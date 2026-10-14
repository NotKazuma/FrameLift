using System.Diagnostics;
using System.Globalization;

namespace FrameLift.Core;

/// <summary>nvidia-smi, shipped with every NVIDIA driver. Every call is bounded by a timeout.</summary>
public static class NvidiaSmi
{
    public static string? Find()
    {
        foreach (var p in new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "NVIDIA Corporation", "NVSMI", "nvidia-smi.exe"),
            Path.Combine(Environment.SystemDirectory, "nvidia-smi.exe"),
        })
            if (File.Exists(p)) return p;
        return null;
    }

    /// <summary>Runs nvidia-smi. Returns null if it could not start or did not finish in time (and was killed).</summary>
    public static (string Stdout, string Stderr)? Run(string smiPath, string args, int timeoutMs = 4000)
    {
        try
        {
            var psi = new ProcessStartInfo(smiPath, args)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
            };
            using var p = Process.Start(psi);
            if (p is null) return null;
            // Read both streams asynchronously so a full pipe cannot block the wait below.
            var o = p.StandardOutput.ReadToEndAsync();
            var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs))
            {
                try { p.Kill(true); } catch (Exception) { /* exited between the check and the kill */ }
                return null;
            }
            return (o.GetAwaiter().GetResult(), e.GetAwaiter().GetResult());
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }
}

/// <summary>No controllable GPU (not NVIDIA, no nvidia-smi, or the card rejects power limits).</summary>
public sealed class NoGpuPower : IGpuPower
{
    public bool Supported => false;
    public int? DefaultWatts => null;
    public void SetLimitPercent(int percent) { }
    public void SetLimitWatts(int watts) => throw new InvalidOperationException("no GPU power control available");
}

/// <summary>Probes the GPU the first time it is used, so app start-up never waits on nvidia-smi.</summary>
public sealed class LazyGpuPower : IGpuPower
{
    private readonly Lazy<IGpuPower> _inner;

    public LazyGpuPower(Func<IGpuPower>? probe = null) =>
        _inner = new Lazy<IGpuPower>(probe ?? (() =>
        {
            var smi = NvidiaSmi.Find();
            return smi is null ? new NoGpuPower() : new NvidiaGpuPower(smi);
        }));

    public bool Supported => _inner.Value.Supported;
    public int? DefaultWatts => _inner.Value.DefaultWatts;
    public void SetLimitPercent(int percent) => _inner.Value.SetLimitPercent(percent);
    public void SetLimitWatts(int watts) => _inner.Value.SetLimitWatts(watts);
}

/// <summary>
/// NVIDIA power limit through <c>nvidia-smi -pl</c>. Some laptop GPUs report a valid range but reject every set while
/// still exiting 0, so <see cref="Supported"/> is decided by a real no-op set (to the default) and nvidia-smi's own text.
/// </summary>
public sealed class NvidiaGpuPower : IGpuPower
{
    private readonly string _smi;
    private readonly int _minW, _maxW;

    public bool Supported { get; }
    public int? DefaultWatts { get; }

    /// <summary>Probes the GPU. Slow (spawns nvidia-smi twice), so create it once, off the UI thread.</summary>
    public NvidiaGpuPower(string smiPath)
    {
        _smi = smiPath;
        try
        {
            var q = NvidiaSmi.Run(smiPath, "--query-gpu=power.min_limit,power.max_limit,power.default_limit --format=csv,noheader,nounits");
            if (q is null) return;
            var parts = q.Value.Stdout.Split(new[] { ',', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (parts.Length < 3) return;
            _minW = (int)Math.Round(ParseW(parts[0]));
            _maxW = (int)Math.Round(ParseW(parts[1]));
            int def = (int)Math.Round(ParseW(parts[2]));
            if (def <= 0 || _maxW < _minW) return;
            DefaultWatts = def;
            Supported = SetAccepted(def);
        }
        catch (FormatException) { Supported = false; } // "[N/A]" and friends: this GPU has no power limit control
    }

    private NvidiaGpuPower(string smiPath, bool recovery) { _smi = smiPath; Supported = true; }

    /// <summary>For crash recovery: no probing, just enough to set a wattage that was journaled earlier.</summary>
    public static NvidiaGpuPower? ForRecovery()
    {
        var smi = NvidiaSmi.Find();
        return smi is null ? null : new NvidiaGpuPower(smi, recovery: true);
    }

    public void SetLimitPercent(int percent)
    {
        if (!Supported || DefaultWatts is not int def) return;
        SetLimitWatts(Math.Clamp((int)Math.Round(def * percent / 100.0), _minW, _maxW));
    }

    public void SetLimitWatts(int watts)
    {
        if (!Supported) return;
        if (!SetAccepted(watts)) throw new InvalidOperationException($"nvidia-smi did not accept a {watts} W power limit");
    }

    private bool SetAccepted(int watts)
    {
        var r = NvidiaSmi.Run(_smi, $"-pl {watts}");
        if (r is null) return false;
        var text = r.Value.Stdout + r.Value.Stderr;
        return !text.Contains("not supported", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("not permitted", StringComparison.OrdinalIgnoreCase)
            && !text.Contains("unable to set", StringComparison.OrdinalIgnoreCase);
    }

    private static float ParseW(string s) => float.Parse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture);
}
