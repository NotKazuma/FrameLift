namespace FrameLift.Core;

/// <summary>
/// "Cool Boost": removes heat that makes no frames, for the length of a Game Boost session. On the active plan it lets
/// idle cores clock down and sleep deeply between frames, makes turbo efficient instead of always-on, and asks for the
/// fan before throttling (on mains only). On an NVIDIA GPU that accepts it, it caps the core clock below the top boost
/// steps, which cost the most voltage. Settings only ever move toward efficiency, and everything is journaled under the
/// owner "boost", so Boost's Stop and the watchdog put it all back.
/// </summary>
public sealed class EfficiencyTuner
{
    public static readonly IReadOnlyList<PowerTarget> BoostTargets = new PowerTarget[]
    {
        new(PowerSettings.MinState, 5),
        new(PowerSettings.Epp, 25),
        new(PowerSettings.BoostMode, 4),
        new(PowerSettings.IdleDemote, 40),
        new(PowerSettings.IdlePromote, 60),
        new(PowerSettings.CoolingPolicy, 1, AcOnly: true), // on battery, passive cooling saves charge
    };

    private readonly IPower _power;
    private readonly IGpuPower _gpu;
    private readonly Journal _journal;

    public EfficiencyTuner(IPower power, IGpuPower gpu, Journal journal)
    {
        _power = power; _gpu = gpu; _journal = journal;
    }

    /// <summary>What could not be applied in the last <see cref="Apply"/>, in plain words. Null when everything took.</summary>
    public string? Note { get; private set; }

    /// <summary>Applies to the active plan and GPU. Returns how many things changed. Never throws: failures go to <see cref="Note"/>.</summary>
    public int Apply(AppConfig cfg)
    {
        var failures = new List<string>();
        int n = 0;
        try
        {
            n += PowerSettingWriter.Apply(_power, _journal, GameBoost.Owner, _power.GetActiveScheme(), BoostTargets, failures);
        }
        catch (Exception e) { failures.Add("power plan: " + e.Message); }

        if (cfg.BoostGpuClockMhz > 0)
        {
            try
            {
                if (_gpu.ClockLockSupported)
                {
                    _journal.Add(new JournalEntry { Owner = GameBoost.Owner, Kind = EntryKind.GpuClockLock });
                    _gpu.LockMaxClock(cfg.BoostGpuClockMhz);
                    n++;
                }
            }
            catch (Exception e) { failures.Add("GPU clock: " + e.Message); }
        }

        Note = failures.Count == 0 ? null : "Cool Boost could not change everything: " + string.Join("; ", failures);
        return n;
    }
}
