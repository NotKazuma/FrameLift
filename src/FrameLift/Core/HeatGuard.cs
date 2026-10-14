using System.Diagnostics;

namespace FrameLift.Core;

/// <summary>
/// While Game Boost is on, keeps the PC under its shutdown temperature with as little speed lost as possible. Below
/// Critical it holds a target temperature by stepping the plan's maximum processor state (100, then 99 = turbo off, then
/// down in tens) and the NVIDIA power limit, one small step per window. At Critical it drops straight to the hard cap.
/// It only ever lowers a limit (never raises one above what the plan or GPU already had), and it works on whichever plan
/// is active, so it does not fight Boost's power plan switch. Every change is journaled under the owner "boost", so
/// Boost's Stop, the next start and the watchdog all undo it. It does not cool the PC: sustained high temperatures are
/// a cooling problem.
/// </summary>
public sealed class HeatGuard
{
    /// <summary>Consecutive polls with no valid reading before limits are released rather than held blind.</summary>
    public const int BlindPollLimit = 5;
    private const int SuspendMinGapSeconds = 5;
    /// <summary>99 is the first step: on Intel it turns turbo off, the biggest drop in heat for the least speed.</summary>
    private static readonly int[] CpuSteps = { 100, 99, 90, 80, 70, 60, 50, 40, 30, 20, 10 };

    private readonly IPower _power;
    private readonly IGpuPower _gpu;
    private readonly IProcessOps _ops;
    private readonly Journal _journal;
    private readonly Reverter _reverter;
    private readonly Func<AppConfig> _cfg;
    private readonly Func<bool> _otherGuardRunning;
    private readonly Action<int> _sleepMs;
    private readonly Func<DateTime> _now;
    private readonly object _gate = new();

    private ThermalPolicy? _policy;
    private JournalEntry? _cpuEntry, _minEntry, _gpuEntry;
    private (Guid Scheme, int Ac, int Dc)? _cap;
    private (int Ac, int Dc)? _min;
    private int _wantCpu = 100, _wantGpu = 100, _appliedCpu = 100, _appliedGpu = 100;
    private bool _wasCritical;
    private DateTime _lastStep = DateTime.MinValue;
    /// <summary>Highest CPU cap the controller will climb back to; learned from Critical, relaxed over time.</summary>
    private int _ceiling = 100;
    private DateTime _lastHot = DateTime.MinValue;
    private int _gamePid;
    private int _blind;
    private DateTime _lastSuspend = DateTime.MinValue;

    public bool Active { get; private set; }
    public ThermalLevel Level { get; private set; } = ThermalLevel.Ok;

    /// <summary>Why nothing is being limited right now, or what went wrong. Null when all is well.</summary>
    public string? Note { get; private set; }

    public int CpuCapPercent { get; private set; } = 100;
    /// <summary>GPU power limit in effect, as a percent of its default (100 = untouched).</summary>
    public int GpuPercent { get; private set; } = 100;

    public HeatGuard(IPower power, IGpuPower gpu, IProcessOps ops, Journal journal, Reverter reverter, Func<AppConfig> cfg,
        Func<bool>? otherGuardRunning = null, Action<int>? sleepMs = null, Func<DateTime>? now = null)
    {
        _power = power; _gpu = gpu; _ops = ops; _journal = journal; _reverter = reverter; _cfg = cfg;
        _otherGuardRunning = otherGuardRunning ?? OtherThermalGuardRunning;
        _sleepMs = sleepMs ?? (ms => Thread.Sleep(ms));
        _now = now ?? (() => DateTime.UtcNow);
    }

    /// <summary>The standalone ThermalGuard app does the same job; two limiters at once would fight.</summary>
    private static bool OtherThermalGuardRunning()
    {
        var ps = Process.GetProcessesByName("ThermalGuard");
        try { return ps.Length > 0; }
        finally { foreach (var p in ps) p.Dispose(); }
    }

    /// <param name="gamePid">The boosted game, or 0 for a manual boost with no specific game.</param>
    public void Begin(int gamePid)
    {
        lock (_gate)
        {
            _policy = new ThermalPolicy(_cfg().Thermal);
            _gamePid = gamePid;
            _blind = 0;
            _wasCritical = false;
            _lastStep = DateTime.MinValue;
            _ceiling = 100;
            _lastHot = DateTime.MinValue;
            Level = ThermalLevel.Ok;
            Note = null;
            Active = true;
            _wantCpu = 100;
            _wantGpu = _otherGuardRunning() ? 100 : GpuTop(_cfg()); // ThermalGuard sets the GPU limit itself
            ReconcileSafely();
        }
    }

    /// <summary>The GPU limit to sit at when it is cool: the Cool Boost baseline, or untouched.</summary>
    private static int GpuTop(AppConfig cfg) =>
        cfg.BoostEfficiency ? Math.Clamp(cfg.Thermal.GpuPowerBaseline, cfg.Thermal.GpuPowerFloor, 100) : 100;

    /// <summary>Feed one reading. Writes to the system only when a wanted limit changes, never on every poll.</summary>
    public ThermalLevel Step(TempReading reading)
    {
        lock (_gate)
        {
            if (!Active || _policy is null) return Level;
            var cfg = _cfg();
            var l = cfg.Thermal;
            int gpuTop = GpuTop(cfg);

            if (!cfg.ThermalEaseOff)
            {
                Note = null;
                SetCalm(gpuTop);
                ReconcileSafely();
                return Level;
            }
            if (_otherGuardRunning())
            {
                Note = "The ThermalGuard app is running and already limits heat, so FrameLift leaves that to it.";
                SetCalm(100);
                ReconcileSafely();
                return Level;
            }

            if (reading.Blind)
            {
                // Never limit blind: after a few empty polls, go back to normal and say so. Until then, hold.
                if (++_blind < BlindPollLimit) return Level;
                Note = "No temperature readings, so nothing is being limited.";
                SetCalm(gpuTop);
                ReconcileSafely();
                return Level;
            }
            _blind = 0;
            Note = null;

            var level = _policy.Update(reading.Gpu, reading.Cpu);
            if (level == ThermalLevel.Critical)
            {
                if (!_wasCritical)
                {
                    // Remember what was too much: the climb back stops one step below the speed that overheated.
                    _ceiling = Math.Min(_ceiling, Math.Max(l.CapFloor, Lower(_wantCpu, l.CapFloor)));
                    _lastHot = _now();
                }
                _wantCpu = Math.Min(_wantCpu, l.CapCritical);
                _wantGpu = Math.Min(gpuTop, l.GpuPowerCritical);
                _wasCritical = true;
                Level = ThermalLevel.Critical;
            }
            else if (!l.SmoothControl)
            {
                _wantCpu = l.CapFor(level);
                _wantGpu = Math.Min(gpuTop, l.GpuPowerFor(level));
                Level = level;
            }
            else
            {
                SmoothStep(reading, l, gpuTop);
            }

            ReconcileSafely();
            if (Level == ThermalLevel.Critical && cfg.ThermalSuspendAtCritical) PulseSuspendLocked(l);
            return Level;
        }
    }

    private void SetCalm(int gpuTop)
    {
        _policy?.Reset();
        _wasCritical = false;
        _wantCpu = 100;
        _wantGpu = gpuTop;
        Level = ThermalLevel.Ok;
    }

    /// <summary>
    /// One controller step, at most one notch per window. Climbing back never passes the learned ceiling: the speed
    /// that last ran into Critical, minus one step. On a laptop that goes from 67 to 100 °C in seconds at full turbo,
    /// jumping straight back to full speed after cooling just repeats the overheat.
    /// </summary>
    private void SmoothStep(TempReading r, ThermalLimits l, int gpuTop)
    {
        var now = _now();
        if (_wasCritical)
        {
            // Just out of Critical: start from the floor and climb.
            _wasCritical = false;
            _wantCpu = Math.Max(_wantCpu, l.CapFloor);
            _wantGpu = Math.Max(_wantGpu, Math.Min(gpuTop, l.GpuPowerFloor));
            _lastStep = now;
        }

        // A long spell without overheating: allow one more step of speed, in case the load got lighter.
        if (_ceiling < 100 && (now - _lastHot).TotalSeconds >= l.CeilingRelaxSeconds)
        {
            _ceiling = Higher(_ceiling);
            _lastHot = now;
        }

        // Well under target there is no risk in climbing faster: sitting at 1.2 GHz while at 68 °C just wastes frames.
        bool wellUnder = (r.Cpu is null || r.Cpu <= l.CpuTarget - l.FastClimbMargin) && (r.Gpu is null || r.Gpu <= l.GpuTarget - l.FastClimbMargin);
        double window = wellUnder ? l.StepSeconds / 2.0 : l.StepSeconds;
        if ((now - _lastStep).TotalSeconds >= window)
        {
            int cpu = _wantCpu, gpu = _wantGpu;
            if (r.Cpu is int c)
            {
                if (c >= l.CpuTarget + l.StepDownMargin) { cpu = Lower(cpu, l.CapFloor); _lastHot = now; }
                else if (c <= l.CpuTarget - l.StepUpMargin) cpu = Math.Min(Higher(cpu), _ceiling);
            }
            else cpu = Math.Min(Higher(cpu), _ceiling);
            if (r.Gpu is int g)
            {
                if (g >= l.GpuTarget + l.StepDownMargin) gpu = Math.Max(Math.Min(gpuTop, l.GpuPowerFloor), gpu - l.GpuPowerStep);
                else if (g <= l.GpuTarget - l.StepUpMargin) gpu = Math.Min(gpuTop, gpu + l.GpuPowerStep);
            }
            else gpu = Math.Min(gpuTop, gpu + l.GpuPowerStep);

            if (cpu != _wantCpu || gpu != _wantGpu) _lastStep = now;
            _wantCpu = cpu;
            _wantGpu = gpu;
        }

        Level = _wantCpu >= 100 && _wantGpu >= gpuTop ? ThermalLevel.Ok
              : _wantCpu <= l.CapFloor ? ThermalLevel.Hot
              : ThermalLevel.Warm;
    }

    private static int Lower(int cap, int floor)
    {
        foreach (var s in CpuSteps) if (s < cap) return Math.Max(s, Math.Min(cap, floor));
        return cap;
    }

    private static int Higher(int cap)
    {
        for (int i = CpuSteps.Length - 1; i >= 0; i--) if (CpuSteps[i] > cap) return CpuSteps[i];
        return 100;
    }

    /// <summary>Boost is stopping: put everything this guard changed back.</summary>
    public void End()
    {
        lock (_gate)
        {
            ReleaseCpu();
            ReleaseGpu();
            _policy = null;
            _gamePid = 0;
            Level = ThermalLevel.Ok;
            Active = false;
        }
    }

    private void ReconcileSafely()
    {
        try { Reconcile(); }
        catch (Exception e)
        {
            // Keep what was journaled so it is undone later; report why the limit did not take.
            Note = $"Could not change the power limit ({Level}): {e.Message}";
        }
    }

    /// <summary>Brings the system to the wanted limits. Touches nothing when they are already in effect.</summary>
    private void Reconcile()
    {
        if (_wantCpu != _appliedCpu)
        {
            if (_wantCpu >= 100) ReleaseCpu();
            else ApplyCpu(_wantCpu);
        }
        if (_wantGpu != _appliedGpu)
        {
            if (_wantGpu >= 100) ReleaseGpu();
            else ApplyGpu(_wantGpu);
        }
    }

    private void ApplyCpu(int want)
    {
        _appliedCpu = want; // even if the write below fails: retrying every poll is what stalled the power API
        var scheme = _power.GetActiveScheme();
        if (_cap is { } c && c.Scheme != scheme) { ReleaseCpu(); _appliedCpu = want; } // plan changed under us: undo the old one first

        if (_cap is null)
        {
            // Read first, journal, then change: a crash in between is harmless, the reverse is not.
            int ac = _power.GetCpuMaxPercent(scheme, true), dc = _power.GetCpuMaxPercent(scheme, false);
            _cpuEntry = new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.PowerCap, Name = scheme.ToString(), Data = $"{ac}|{dc}" };
            _journal.Add(_cpuEntry);
            _cap = (scheme, ac, dc);
        }

        var (_, origAc, origDc) = _cap.Value;
        // A minimum above the cap overrides it (the CPU runs at the minimum), so bring the minimum down with it.
        LowerMinimumTo(scheme, want);
        // Math.Min: never raise a plan that was already set lower than our cap.
        _power.SetCpuMaxPercent(scheme, true, Math.Min(origAc, want));
        _power.SetCpuMaxPercent(scheme, false, Math.Min(origDc, want));
        if (_power.GetActiveScheme() == scheme) _power.Reapply(scheme);
        // What really applies is the higher of maximum and minimum, so report that.
        var (minAc, minDc) = _min!.Value;
        CpuCapPercent = Math.Min(want, Math.Max(Math.Min(origAc, origDc), Math.Min(minAc, minDc)));
    }

    private void LowerMinimumTo(Guid scheme, int want)
    {
        var sub = PowerSettings.SubProcessor;
        _min ??= (_power.GetSetting(scheme, sub, PowerSettings.MinState, true), _power.GetSetting(scheme, sub, PowerSettings.MinState, false));
        var (ac, dc) = _min.Value;
        if (_minEntry is null)
        {
            if (ac <= want && dc <= want) return; // the minimum is already under the cap: nothing to do
            _minEntry = new JournalEntry
            {
                Owner = GameBoost.Owner, Kind = EntryKind.PowerSetting,
                Name = $"{scheme}|{sub}|{PowerSettings.MinState}", Data = $"{ac}|{dc}",
            };
            _journal.Add(_minEntry);
        }
        _power.SetSetting(scheme, sub, PowerSettings.MinState, true, Math.Min(ac, want));
        _power.SetSetting(scheme, sub, PowerSettings.MinState, false, Math.Min(dc, want));
    }

    private void ApplyGpu(int want)
    {
        _appliedGpu = want;
        if (!_gpu.Supported || _gpu.DefaultWatts is not int def) return;
        if (_gpuEntry is null)
        {
            _gpuEntry = new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.GpuPowerLimit, Data = def.ToString() };
            _journal.Add(_gpuEntry);
        }
        _gpu.SetLimitPercent(want);
        GpuPercent = want;
    }

    private void ReleaseCpu()
    {
        if (_minEntry is not null) RevertOne(_minEntry);
        if (_cpuEntry is not null) RevertOne(_cpuEntry);
        _minEntry = null;
        _cpuEntry = null;
        _cap = null;
        _min = null;
        _appliedCpu = 100;
        CpuCapPercent = 100;
    }

    private void ReleaseGpu()
    {
        if (_gpuEntry is not null) RevertOne(_gpuEntry);
        _gpuEntry = null;
        _appliedGpu = 100;
        GpuPercent = 100;
    }

    private void RevertOne(JournalEntry e)
    {
        _reverter.RevertEntries(new[] { e });
        if (_reverter.Failures.Count > 0) Note = "Some limits could not be put back: " + string.Join("; ", _reverter.Failures);
    }

    /// <summary>Briefly freezes the game. The journal is written first and the resume always runs.</summary>
    private void PulseSuspendLocked(ThermalLimits limits)
    {
        if (_gamePid == 0) return;
        if ((_now() - _lastSuspend).TotalSeconds < SuspendMinGapSeconds) return;
        var ticks = _ops.GetStartTicks(_gamePid);
        if (ticks == 0) return;

        var e = new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.ProcessSuspend, Pid = _gamePid, StartTicks = ticks };
        _journal.Add(e);
        try
        {
            _ops.Suspend(_gamePid);
            _sleepMs(Math.Clamp(limits.SuspendMs, 100, 3000));
        }
        catch (Exception ex) { Note = "Could not pause the game briefly: " + ex.Message; }
        finally
        {
            _reverter.RevertEntries(new[] { e }); // resumes the game and removes the entry: the same path crash recovery uses
            _lastSuspend = _now();
        }
    }
}
