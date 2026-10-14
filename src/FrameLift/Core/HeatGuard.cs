using System.Diagnostics;

namespace FrameLift.Core;

/// <summary>
/// While Game Boost is on, eases off before the PC throttles or shuts down: lowers the plan's maximum processor state
/// and the GPU power limit as temperatures climb, and puts both back when they fall. It only ever lowers a limit
/// (never raises one above what the plan or GPU already had), and it works on whichever plan is active, so it does
/// not fight Boost's power plan switch. Every change is journaled under the owner "boost", so Boost's Stop, the next
/// start and the watchdog all undo it. It does not cool the PC: sustained high temperatures are a cooling problem.
/// </summary>
public sealed class HeatGuard
{
    /// <summary>Consecutive polls with no valid reading before limits are released rather than held blind.</summary>
    public const int BlindPollLimit = 5;
    private const int SuspendMinGapSeconds = 5;

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

    private readonly List<JournalEntry> _entries = new();
    private ThermalPolicy? _policy;
    private (Guid Scheme, int Ac, int Dc)? _cap;
    private bool _gpuLimited;
    private int _gamePid;
    private int _blind;
    private DateTime _lastSuspend = DateTime.MinValue;

    public bool Active { get; private set; }
    public ThermalLevel Level { get; private set; } = ThermalLevel.Ok;

    /// <summary>Why nothing is being limited right now, or what went wrong. Null when all is well.</summary>
    public string? Note { get; private set; }

    public int CpuCapPercent { get; private set; } = 100;

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
            Level = ThermalLevel.Ok;
            Note = null;
            Active = true;
        }
    }

    /// <summary>Feed one reading. Changes the system only when the level changes, never on every poll.</summary>
    public ThermalLevel Step(TempReading reading)
    {
        lock (_gate)
        {
            if (!Active || _policy is null) return Level;
            var cfg = _cfg();
            if (!cfg.ThermalEaseOff) { ReleaseLocked(); Note = null; return Level; }
            if (_otherGuardRunning())
            {
                ReleaseLocked();
                Note = "The ThermalGuard app is running and already limits heat, so FrameLift leaves that to it.";
                return Level;
            }

            ThermalLevel next;
            if (reading.Blind)
            {
                // Never limit blind: after a few empty polls, go back to normal and say so.
                if (++_blind >= BlindPollLimit) { _policy.Reset(); next = ThermalLevel.Ok; Note = "No temperature readings, so nothing is being limited."; }
                else next = _policy.Current;
            }
            else
            {
                _blind = 0;
                Note = null;
                next = _policy.Update(reading.Gpu, reading.Cpu);
            }

            if (next != Level)
            {
                try
                {
                    if (next == ThermalLevel.Ok) ReleaseLocked();
                    else ApplyLocked(next, cfg.Thermal);
                    Level = next;
                }
                catch (Exception e)
                {
                    // Keep what was journaled so it is undone later; report why the limit did not take.
                    Note = $"Could not lower the power limit at {next}: {e.Message}";
                    Level = next;
                }
            }

            if (Level == ThermalLevel.Critical && cfg.ThermalSuspendAtCritical) PulseSuspendLocked(cfg.Thermal);
            return Level;
        }
    }

    /// <summary>Boost is stopping: put everything this guard changed back.</summary>
    public void End()
    {
        lock (_gate)
        {
            ReleaseLocked();
            _policy = null;
            _gamePid = 0;
            Level = ThermalLevel.Ok;
            Active = false;
        }
    }

    private void ApplyLocked(ThermalLevel level, ThermalLimits limits)
    {
        var scheme = _power.GetActiveScheme();
        if (_cap is { } c && c.Scheme != scheme) ReleaseLocked(); // the plan changed under us: undo on the old one first

        if (_cap is null)
        {
            // Read first, journal, then change: a crash in between is harmless, the reverse is not.
            int ac = _power.GetCpuMaxPercent(scheme, true), dc = _power.GetCpuMaxPercent(scheme, false);
            var e = new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.PowerCap, Name = scheme.ToString(), Data = $"{ac}|{dc}" };
            _journal.Add(e);
            _entries.Add(e);
            _cap = (scheme, ac, dc);
        }

        var (_, origAc, origDc) = _cap.Value;
        int cap = limits.CapFor(level);
        // Math.Min: never raise a plan that was already set lower than our cap.
        _power.SetCpuMaxPercent(scheme, true, Math.Min(origAc, cap));
        _power.SetCpuMaxPercent(scheme, false, Math.Min(origDc, cap));
        if (_power.GetActiveScheme() == scheme) _power.Reapply(scheme);
        CpuCapPercent = Math.Min(Math.Min(origAc, origDc), cap);

        if (_gpu.Supported && _gpu.DefaultWatts is int def)
        {
            if (!_gpuLimited)
            {
                var g = new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.GpuPowerLimit, Data = def.ToString() };
                _journal.Add(g);
                _entries.Add(g);
                _gpuLimited = true;
            }
            _gpu.SetLimitPercent(Math.Min(100, limits.GpuPowerFor(level)));
        }
    }

    private void ReleaseLocked()
    {
        if (_entries.Count > 0)
        {
            var list = _entries.ToList();
            _entries.Clear();
            _reverter.RevertEntries(list);
            if (_reverter.Failures.Count > 0) Note = "Some limits could not be put back: " + string.Join("; ", _reverter.Failures);
        }
        _cap = null;
        _gpuLimited = false;
        CpuCapPercent = 100;
        _policy?.Reset();
        Level = ThermalLevel.Ok;
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
