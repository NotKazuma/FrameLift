namespace FrameLift.Core;

/// <summary>One processor power setting and the value we would like it to have. <see cref="AcOnly"/> leaves battery alone.</summary>
public sealed record PowerTarget(Guid Setting, int Value, bool AcOnly = false);

/// <summary>Processor power settings (GUIDs as <c>powercfg /qh</c> lists them) and which way is "more efficient" for each.</summary>
public static class PowerSettings
{
    public static readonly Guid SubProcessor = new("54533251-82be-4824-96c1-47b60b740d00");
    public static readonly Guid MaxState = new("bc5038f7-23e0-4960-96da-33abaf5935ec");
    public static readonly Guid MinState = new("893dee8e-2bef-41e0-89c6-b55d0929964c");
    /// <summary>Energy performance preference, 0 (all speed) to 100 (all efficiency). Steers the CPU's own clock choice.</summary>
    public static readonly Guid Epp = new("36687f9e-e3a5-4dbf-b1dc-15eb381c6863");
    /// <summary>0 Disabled, 1 Enabled, 2 Aggressive, 3 Efficient Enabled, 4 Efficient Aggressive, 5/6 the "at guaranteed" pair.</summary>
    public static readonly Guid BoostMode = new("be337238-0d82-4146-a960-4f3749d470c7");
    /// <summary>Busy-percent thresholds for moving an idle core into a deeper or shallower sleep state. 100 keeps it shallow.</summary>
    public static readonly Guid IdlePromote = new("7b224883-b3cc-4d79-819f-8374152cbe7c");
    public static readonly Guid IdleDemote = new("4b92d758-5a24-4851-a470-815d78aee119");
    /// <summary>0 Passive (slow the CPU before the fan), 1 Active (fan first).</summary>
    public static readonly Guid CoolingPolicy = new("94d3a615-a899-4ac5-ae2b-e4d8f634367f");

    /// <summary>
    /// The value to write: the wanted one only when it costs less power (or, for cooling, uses the fan sooner) than the
    /// current one. A setting the user already has tighter is never loosened.
    /// </summary>
    public static int Resolve(Guid setting, int current, int wanted)
    {
        if (setting == Epp || setting == CoolingPolicy) return Math.Max(current, wanted);
        if (setting == BoostMode) return EfficientBoostMode(current);
        return Math.Min(current, wanted); // states and idle thresholds: lower is cooler
    }

    /// <summary>The same boost mode in its "efficient" form: turbo is still there, but only when the load asks for it.</summary>
    public static int EfficientBoostMode(int current) => current switch { 1 => 3, 2 => 4, 5 => 6, _ => current };
}

/// <summary>Writes power targets to one plan, journaling each setting (AC and battery together) before changing it.</summary>
public static class PowerSettingWriter
{
    /// <summary>
    /// Returns how many settings changed. A setting this PC does not have, or that is refused, is skipped and its reason
    /// added to <paramref name="failures"/>; the rest still apply.
    /// </summary>
    public static int Apply(IPower power, Journal journal, string owner, Guid scheme, IEnumerable<PowerTarget> targets, List<string> failures)
    {
        int n = 0;
        foreach (var t in targets)
        {
            try
            {
                int ac = power.GetSetting(scheme, PowerSettings.SubProcessor, t.Setting, true);
                int dc = power.GetSetting(scheme, PowerSettings.SubProcessor, t.Setting, false);
                int newAc = PowerSettings.Resolve(t.Setting, ac, t.Value);
                int newDc = t.AcOnly ? dc : PowerSettings.Resolve(t.Setting, dc, t.Value);
                if (newAc == ac && newDc == dc) continue;

                // A plan with minimum 100 % and a lower maximum really runs at full speed: the minimum wins. Lowering
                // the minimum alone would let that maximum bite and cut game speed, so keep today's real ceiling.
                if (t.Setting == PowerSettings.MinState) n += KeepCeiling(power, journal, owner, scheme, ac, dc);

                // Read first, journal, then change: a crash in between is harmless, the reverse is not.
                journal.Add(new JournalEntry
                {
                    Owner = owner, Kind = EntryKind.PowerSetting,
                    Name = $"{scheme}|{PowerSettings.SubProcessor}|{t.Setting}", Data = $"{ac}|{dc}",
                });
                power.SetSetting(scheme, PowerSettings.SubProcessor, t.Setting, true, newAc);
                power.SetSetting(scheme, PowerSettings.SubProcessor, t.Setting, false, newDc);
                n++;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                failures.Add($"power setting {t.Setting}: {e.Message}");
            }
        }
        if (n > 0 && power.GetActiveScheme() == scheme) power.Reapply(scheme);
        return n;
    }

    /// <summary>Raises the maximum state to the old minimum where the maximum was below it. Returns 1 if it changed.</summary>
    private static int KeepCeiling(IPower power, Journal journal, string owner, Guid scheme, int minAc, int minDc)
    {
        int maxAc = power.GetSetting(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, true);
        int maxDc = power.GetSetting(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, false);
        int newAc = Math.Max(maxAc, Math.Min(100, minAc)), newDc = Math.Max(maxDc, Math.Min(100, minDc));
        if (newAc == maxAc && newDc == maxDc) return 0;
        journal.Add(new JournalEntry
        {
            Owner = owner, Kind = EntryKind.PowerSetting,
            Name = $"{scheme}|{PowerSettings.SubProcessor}|{PowerSettings.MaxState}", Data = $"{maxAc}|{maxDc}",
        });
        power.SetSetting(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, true, newAc);
        power.SetSetting(scheme, PowerSettings.SubProcessor, PowerSettings.MaxState, false, newDc);
        return 1;
    }
}
