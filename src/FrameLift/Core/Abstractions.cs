namespace FrameLift.Core;

/// <summary>A registry value as it was found: <see cref="Kind"/> is a RegistryValueKind name, <see cref="Data"/> is text (binary is base64).</summary>
public sealed record RegSnapshot(bool Exists, string Kind, string Data);

public interface IRegistry
{
    /// <summary>Paths look like <c>HKCU\Software\...</c> or <c>HKLM\SYSTEM\...</c>.</summary>
    RegSnapshot Read(string path, string name);
    void Write(string path, string name, string kind, string data);
    void DeleteValue(string path, string name);
    /// <summary>Names of the values under a key (empty if the key is missing).</summary>
    IReadOnlyList<string> ValueNames(string path);
    bool KeyExists(string path);
}

public interface IServiceRunner
{
    bool IsRunning(string name);
    /// <summary>Best effort; returns false if it could not be stopped.</summary>
    bool Stop(string name);
    bool Start(string name);
}

public interface IPower
{
    Guid GetActiveScheme();
    void SetActiveScheme(Guid scheme);
    /// <summary>Makes sure a scheme exists (creating it from its template if needed) and returns the GUID to activate.</summary>
    Guid EnsureScheme(Guid template);
    /// <summary>True for Ultimate/High/custom "performance" plans, which Game Boost must not replace with a weaker one.</summary>
    bool IsPerformancePlan(Guid scheme);
    /// <summary>The plan's "maximum processor state" in percent, for mains (<paramref name="ac"/>) or battery.</summary>
    int GetCpuMaxPercent(Guid scheme, bool ac);
    void SetCpuMaxPercent(Guid scheme, bool ac, int percent);
    /// <summary>Makes the plan's current values take effect (only needed when it is the active plan).</summary>
    void Reapply(Guid scheme);
}

/// <summary>GPU power limit, as watts. Only NVIDIA through nvidia-smi is supported.</summary>
public interface IGpuPower
{
    /// <summary>True only after a real runtime probe succeeded, never assumed from the vendor name.</summary>
    bool Supported { get; }
    /// <summary>The GPU's own default limit in watts, the value to restore to.</summary>
    int? DefaultWatts { get; }
    void SetLimitPercent(int percent);
    void SetLimitWatts(int watts);
}

public interface IProcessOps
{
    /// <summary>Process start time in ticks, or 0 if gone. Guards against pid reuse.</summary>
    long GetStartTicks(int pid);
    System.Diagnostics.ProcessPriorityClass GetPriority(int pid);
    void SetPriority(int pid, System.Diagnostics.ProcessPriorityClass c);
    /// <summary>EcoQoS on (efficiency mode) or back to system-managed.</summary>
    void SetEcoQos(int pid, bool on);
    /// <summary>Freezes every thread of the process. Always pair with <see cref="Resume"/>.</summary>
    void Suspend(int pid);
    void Resume(int pid);
}
