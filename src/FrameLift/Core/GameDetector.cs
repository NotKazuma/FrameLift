using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace FrameLift.Core;

public sealed record RunningProc(int Pid, string Name);

/// <summary>Decides when a fullscreen foreground app counts as a game. Pure logic, so the timing rules are testable.</summary>
public sealed class GameDetector
{
    private static readonly HashSet<string> BuiltInExclusions = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "chrome", "msedge", "firefox", "brave", "opera", "vlc", "mpv", "wmplayer",
        "steam", "steamwebhelper", "epicgameslauncher", "eadesktop", "origin", "battle.net", "galaxyclient",
        "discord", "obs64", "code", "devenv", "powershell", "windowsterminal", "cmd", "framelift", "stutterfix",
        "thermalguard", "applicationframehost", "shellexperiencehost", "searchhost", "lockapp",
    };

    private readonly AppConfig _cfg;
    private int _candidatePid;
    private DateTime _candidateSince;

    public GameDetector(AppConfig cfg) => _cfg = cfg;

    public static string Normalize(string name) =>
        name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;

    public bool IsExcluded(string name) =>
        BuiltInExclusions.Contains(Normalize(name)) ||
        _cfg.Ignored.Any(i => string.Equals(Normalize(i), Normalize(name), StringComparison.OrdinalIgnoreCase));

    /// <param name="foreground">The foreground process when it is fullscreen, else null.</param>
    /// <returns>The game once it has been fullscreen in front for <c>AutoDetectSeconds</c>, else null.</returns>
    public RunningProc? Choose(RunningProc? foreground, DateTime now)
    {
        if (foreground is null || IsExcluded(foreground.Name))
        {
            _candidatePid = 0;
            return null;
        }
        if (_candidatePid != foreground.Pid) { _candidatePid = foreground.Pid; _candidateSince = now; }
        return (now - _candidateSince).TotalSeconds >= _cfg.AutoDetectSeconds ? foreground : null;
    }
}

/// <summary>Win32 facts for the detector: the foreground app, if it is fullscreen.</summary>
public static class WindowProbe
{
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int L, T, R, B; }
    [StructLayout(LayoutKind.Sequential)] private struct MONITORINFO { public int Size; public RECT Monitor, Work; public int Flags; }

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT r);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromWindow(IntPtr h, uint flags);
    [DllImport("user32.dll")] private static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr h, StringBuilder s, int max);

    /// <summary>The foreground process if its window covers its whole monitor (borderless or exclusive fullscreen).</summary>
    public static RunningProc? FullscreenForeground()
    {
        var h = GetForegroundWindow();
        if (h == IntPtr.Zero) return null;

        var cls = new StringBuilder(64);
        GetClassName(h, cls, cls.Capacity);
        if (cls.ToString() is "Progman" or "WorkerW") return null;

        if (!GetWindowRect(h, out var w)) return null;
        var mi = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(MonitorFromWindow(h, 2), ref mi)) return null;
        var m = mi.Monitor;
        if (w.L > m.L || w.T > m.T || w.R < m.R || w.B < m.B) return null;

        GetWindowThreadProcessId(h, out var pid);
        try { using var p = Process.GetProcessById((int)pid); return new RunningProc(p.Id, p.ProcessName); }
        catch (ArgumentException) { return null; }
    }

    public static int ForegroundPid()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }
}
