using System.Diagnostics;
using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class FakeRegistry : IRegistry
{
    public readonly Dictionary<(string, string), RegSnapshot> Values = new();
    private static (string, string) K(string p, string n) => (p.ToLowerInvariant(), n.ToLowerInvariant());

    public RegSnapshot Read(string path, string name) => Values.TryGetValue(K(path, name), out var v) ? v : new(false, "", "");
    public void Write(string path, string name, string kind, string data) => Values[K(path, name)] = new(true, kind, data);
    public void DeleteValue(string path, string name) => Values.Remove(K(path, name));
    public IReadOnlyList<string> ValueNames(string path) =>
        Values.Where(kv => kv.Key.Item1 == path.ToLowerInvariant()).Select(kv => kv.Key.Item2).ToList();
    public bool KeyExists(string path) => Values.Keys.Any(k => k.Item1 == path.ToLowerInvariant());
    public void Seed(string path, string name, string kind, string data) => Write(path, name, kind, data);
}

public sealed class FakeServices : IServiceRunner
{
    public readonly HashSet<string> Running = new(StringComparer.OrdinalIgnoreCase);
    public readonly List<string> Stopped = new(), Started = new();
    public bool IsRunning(string name) => Running.Contains(name);
    public bool Stop(string name) { Running.Remove(name); Stopped.Add(name); return true; }
    public bool Start(string name) { Running.Add(name); Started.Add(name); return true; }
}

public sealed class FakePower : IPower
{
    public Guid Active = Guid.NewGuid();
    public Guid GetActiveScheme() => Active;
    public void SetActiveScheme(Guid scheme) => Active = scheme;
    public Guid EnsureScheme(Guid template) => template;
    public bool ActiveIsPerformance;
    public bool IsPerformancePlan(Guid scheme) => ActiveIsPerformance && scheme == Active;

    public readonly Dictionary<(Guid, bool), int> Caps = new();
    public int Reapplies;
    public bool FailCapWrites;
    public int GetCpuMaxPercent(Guid scheme, bool ac) => Caps.TryGetValue((scheme, ac), out var v) ? v : 100;
    public void SetCpuMaxPercent(Guid scheme, bool ac, int percent)
    {
        if (FailCapWrites) throw new InvalidOperationException("power write refused");
        Caps[(scheme, ac)] = percent;
    }
    public void Reapply(Guid scheme) => Reapplies++;

    /// <summary>Starting values for every plan: the user's real "KernelOS" plan, which never lets the CPU rest.</summary>
    public readonly Dictionary<Guid, int> SettingDefaults = new()
    {
        [PowerSettings.MinState] = 100, [PowerSettings.Epp] = 0, [PowerSettings.BoostMode] = 2,
        [PowerSettings.IdlePromote] = 100, [PowerSettings.IdleDemote] = 100, [PowerSettings.CoolingPolicy] = 0,
    };
    public readonly Dictionary<(Guid Scheme, Guid Setting, bool Ac), int> Settings = new();
    /// <summary>Settings this "PC" does not have: reads and writes fail like the real API does.</summary>
    public readonly HashSet<Guid> MissingSettings = new();
    public int SettingWrites;

    public int GetSetting(Guid scheme, Guid sub, Guid setting, bool ac)
    {
        if (MissingSettings.Contains(setting)) throw new InvalidOperationException("setting not on this plan");
        if (setting == PowerSettings.MaxState) return GetCpuMaxPercent(scheme, ac);
        return Settings.TryGetValue((scheme, setting, ac), out var v) ? v : SettingDefaults.GetValueOrDefault(setting);
    }

    public void SetSetting(Guid scheme, Guid sub, Guid setting, bool ac, int value)
    {
        if (MissingSettings.Contains(setting)) throw new InvalidOperationException("setting not on this plan");
        SettingWrites++;
        if (setting == PowerSettings.MaxState) { SetCpuMaxPercent(scheme, ac, value); return; }
        Settings[(scheme, setting, ac)] = value;
    }
}

public sealed class FakeGpu : IGpuPower
{
    public bool Supported { get; set; } = true;
    public int? DefaultWatts { get; set; } = 60;
    public int? LimitWatts;
    public int Sets;
    public void SetLimitPercent(int percent) { LimitWatts = (int)Math.Round(DefaultWatts!.Value * percent / 100.0); Sets++; }
    public void SetLimitWatts(int watts) { LimitWatts = watts; Sets++; }

    public bool ClockLockSupported { get; set; } = true;
    public bool FailClockLock;
    public int? LockedMhz;
    public int ClockResets;
    public void LockMaxClock(int mhz)
    {
        if (FailClockLock) throw new InvalidOperationException("clock lock refused");
        LockedMhz = mhz;
    }
    public void ResetClocks() { LockedMhz = null; ClockResets++; }
}

public sealed class FakeProcs : IProcessOps
{
    public readonly Dictionary<int, (long start, ProcessPriorityClass pri, bool eco)> P = new();
    public long GetStartTicks(int pid) => P.TryGetValue(pid, out var v) ? v.start : 0;
    public ProcessPriorityClass GetPriority(int pid) => P[pid].pri;
    public void SetPriority(int pid, ProcessPriorityClass c) => P[pid] = (P[pid].start, c, P[pid].eco);
    public void SetEcoQos(int pid, bool on) { if (P.ContainsKey(pid)) P[pid] = (P[pid].start, P[pid].pri, on); }
    public readonly HashSet<int> Suspended = new();
    public void Suspend(int pid) { if (!P.ContainsKey(pid)) throw new ArgumentException("no such process"); Suspended.Add(pid); }
    public void Resume(int pid) => Suspended.Remove(pid);
}
