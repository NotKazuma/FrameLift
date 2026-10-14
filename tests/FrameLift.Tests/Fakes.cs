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
}

public sealed class FakeGpu : IGpuPower
{
    public bool Supported { get; set; } = true;
    public int? DefaultWatts { get; set; } = 60;
    public int? LimitWatts;
    public int Sets;
    public void SetLimitPercent(int percent) { LimitWatts = (int)Math.Round(DefaultWatts!.Value * percent / 100.0); Sets++; }
    public void SetLimitWatts(int watts) { LimitWatts = watts; Sets++; }
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
