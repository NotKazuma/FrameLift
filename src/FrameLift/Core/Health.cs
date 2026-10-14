using System.Diagnostics;
using FrameLift.Native;

namespace FrameLift.Core;

public enum HealthStatus { Good, Warning, Problem, Unknown }

/// <summary>What the Fix button does. Fixes only ever go through the journaled engines, so undo keeps working.</summary>
public enum FixKind { ApplyTweak, OpenPage }

/// <param name="Target">A tweak id for <see cref="FixKind.ApplyTweak"/>, or a page name ("cleanup", "startup") for OpenPage.</param>
public sealed record HealthFix(string Label, FixKind Kind, string Target);

public sealed record HealthFinding(string Id, string Title, HealthStatus Status, string Message, HealthFix? Fix = null);

public interface IHealthCheck
{
    string Id { get; }
    string Title { get; }
    /// <summary>Read-only and quick. Runs on a worker thread.</summary>
    HealthFinding Run();
}

public sealed record RamInfo(int Percent, double UsedGb, double TotalGb, double PageFileGb);
public sealed record DriveSpace(string Name, int PercentUsed, double FreeGb, double TotalGb);

public interface ISystemInfo
{
    RamInfo Ram();
    DriveSpace SystemDrive();
}

public sealed class SystemInfo : ISystemInfo
{
    public RamInfo Ram()
    {
        var m = new NativeMethods.MEMORYSTATUSEX { Length = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        if (!NativeMethods.GlobalMemoryStatusEx(ref m))
            throw new InvalidOperationException("Windows would not report memory use (error " + System.Runtime.InteropServices.Marshal.GetLastWin32Error() + ")");
        const double gb = 1073741824.0;
        double total = m.TotalPhys / gb, avail = m.AvailPhys / gb;
        // TotalPageFile is the commit limit: RAM plus the page files at their current size.
        double pageFile = Math.Max(0, (m.TotalPageFile - m.TotalPhys) / gb);
        return new RamInfo((int)m.MemoryLoad, total - avail, total, pageFile);
    }

    public DriveSpace SystemDrive()
    {
        var root = Path.GetPathRoot(Environment.SystemDirectory)!;
        var d = new DriveInfo(root);
        double total = d.TotalSize / 1073741824.0, free = d.AvailableFreeSpace / 1073741824.0;
        return new DriveSpace(root.TrimEnd('\\'), (int)Math.Round((total - free) * 100 / total), free, total);
    }
}

public static class HealthRunner
{
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(20);

    /// <summary>Runs one check off the calling thread. A failing or stuck check becomes an Unknown finding that says why.</summary>
    public static async Task<HealthFinding> RunAsync(IHealthCheck check, TimeSpan? timeout = null)
    {
        var limit = timeout ?? DefaultTimeout;
        var task = Task.Run(check.Run);
        try
        {
            if (await Task.WhenAny(task, Task.Delay(limit)) != task)
                return new HealthFinding(check.Id, check.Title, HealthStatus.Unknown,
                    $"This check did not finish within {limit.TotalSeconds:0} seconds, so it was skipped.");
            return await task;
        }
        catch (Exception e)
        {
            return new HealthFinding(check.Id, check.Title, HealthStatus.Unknown, $"Could not check this: {e.Message}");
        }
    }
}

/// <summary>Runs a fix through the same engines the Tweaks page uses, so "Undo all changes" reverses it.</summary>
public sealed class HealthFixer
{
    private readonly TweakEngine _tweaks;
    public HealthFixer(TweakEngine tweaks) => _tweaks = tweaks;

    /// <summary>Applies a tweak fix and returns a plain-words result. Page fixes are the UI's job and are refused here.</summary>
    public string Apply(HealthFix fix)
    {
        if (fix.Kind != FixKind.ApplyTweak) throw new ArgumentException("Only tweak fixes can be applied here: " + fix.Kind);
        var t = TweakCatalog.Find(fix.Target) ?? throw new ArgumentException("Unknown tweak: " + fix.Target);
        int n = _tweaks.Apply(t);
        return n == 0 ? "Nothing needed changing." : $"{n} change{(n == 1 ? "" : "s")} made. You can undo it from Safety & undo.";
    }
}

// ---- the checks ---------------------------------------------------------------------------------------------

public sealed class PowerPlanCheck : IHealthCheck
{
    private readonly IPower _power;
    private readonly Func<string, bool> _tweakApplied;
    public PowerPlanCheck(IPower power, Func<string, bool> tweakApplied) { _power = power; _tweakApplied = tweakApplied; }

    public string Id => "power-plan";
    public string Title => "Power plan";

    public HealthFinding Run()
    {
        if (_power.IsPerformancePlan(_power.GetActiveScheme()))
            return new(Id, Title, HealthStatus.Good, "A performance power plan is active, so the CPU will not slow down mid-game.");
        // If the tweak is already on, applying it again would do nothing, so offer no button that cannot work.
        var fix = _tweakApplied("power-high") ? null : new HealthFix("Switch to High Performance", FixKind.ApplyTweak, "power-high");
        return new(Id, Title, HealthStatus.Warning,
            "Your power plan is not a performance plan, so Windows may slow the CPU down while you play. On a laptop this runs hotter.", fix);
    }
}

public sealed class GameModeCheck : IHealthCheck
{
    private const string Key = @"HKCU\Software\Microsoft\GameBar";
    private readonly IRegistry _reg;
    private readonly Func<string, bool> _tweakApplied;
    public GameModeCheck(IRegistry reg, Func<string, bool> tweakApplied) { _reg = reg; _tweakApplied = tweakApplied; }

    public string Id => "game-mode";
    public string Title => "Windows Game Mode";

    public HealthFinding Run()
    {
        // Game Mode is on unless a value says otherwise, so a missing value counts as on.
        var v = _reg.Read(Key, "AutoGameModeEnabled");
        bool off = v.Exists && v.Data == "0";
        if (!off) return new(Id, Title, HealthStatus.Good, "Game Mode is on, so Windows holds back background work while you play.");
        var fix = _tweakApplied("game-mode") ? null : new HealthFix("Turn on Game Mode", FixKind.ApplyTweak, "game-mode");
        return new(Id, Title, HealthStatus.Warning, "Game Mode is off, so Windows may run updates and other work in the background during games.", fix);
    }
}

public sealed class GameDvrCheck : IHealthCheck
{
    private readonly IRegistry _reg;
    private readonly Func<string, bool> _tweakApplied;
    public GameDvrCheck(IRegistry reg, Func<string, bool> tweakApplied) { _reg = reg; _tweakApplied = tweakApplied; }

    public string Id => "game-dvr";
    public string Title => "Background game recording";

    public HealthFinding Run()
    {
        var capture = _reg.Read(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        var store = _reg.Read(@"HKCU\System\GameConfigStore", "GameDVR_Enabled");
        // Recording counts as on when the capture switch is on, or when it is unset and the store switch is not off.
        bool on = capture.Exists ? capture.Data != "0" : !(store.Exists && store.Data == "0");
        if (!on) return new(Id, Title, HealthStatus.Good, "Xbox Game Bar is not recording in the background.");
        var fix = _tweakApplied("game-dvr") ? null : new HealthFix("Turn off recording", FixKind.ApplyTweak, "game-dvr");
        return new(Id, Title, HealthStatus.Warning, "Xbox Game Bar can record in the background, which costs frames on a weak PC.", fix);
    }
}

public interface IStartupWeigher
{
    /// <summary>Private memory in MB used right now by the running app behind a startup command, or null if it is not running.</summary>
    double? MemoryMb(string command);
}

public sealed class ProcessStartupWeigher : IStartupWeigher
{
    public static string? ProcessNameOf(string command)
    {
        var c = command.Trim();
        if (c.Length == 0) return null;
        string exe;
        if (c[0] == '"')
        {
            int end = c.IndexOf('"', 1);
            if (end < 0) return null;
            exe = c[1..end];
        }
        else
        {
            int i = c.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            exe = i < 0 ? c.Split(' ')[0] : c[..(i + 4)];
        }
        var name = Path.GetFileNameWithoutExtension(exe);
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public double? MemoryMb(string command)
    {
        var name = ProcessNameOf(command);
        if (name is null) return null;
        var procs = Process.GetProcessesByName(name);
        try
        {
            if (procs.Length == 0) return null;
            double total = 0;
            foreach (var p in procs)
            {
                try { total += p.PrivateMemorySize64 / 1048576.0; } // private memory: summing working sets would count shared pages once per process
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { /* exited meanwhile */ }
            }
            return total;
        }
        finally { foreach (var p in procs) p.Dispose(); }
    }
}

public sealed class StartupCheck : IHealthCheck
{
    private readonly Func<IReadOnlyList<StartupItem>> _list;
    private readonly IStartupWeigher _weigher;
    public StartupCheck(Func<IReadOnlyList<StartupItem>> list, IStartupWeigher weigher) { _list = list; _weigher = weigher; }

    public string Id => "startup";
    public string Title => "Startup apps";

    public HealthFinding Run()
    {
        var on = _list().Where(i => i.Enabled).ToList();
        var heaviest = on.Select(i => (i.Name, Mb: _weigher.MemoryMb(i.Command)))
            .Where(x => x.Mb is > 1)
            .OrderByDescending(x => x.Mb).Take(3)
            .Select(x => $"{x.Name} ({x.Mb:0} MB)").ToList();
        var detail = heaviest.Count > 0 ? " Using the most memory right now: " + string.Join(", ", heaviest) + "." : "";
        var status = on.Count <= 5 ? HealthStatus.Good : on.Count <= 10 ? HealthStatus.Warning : HealthStatus.Problem;
        if (status == HealthStatus.Good)
            return new(Id, Title, status, $"{on.Count} app{(on.Count == 1 ? "" : "s")} start with Windows.{detail}");
        return new(Id, Title, status, $"{on.Count} apps start with Windows, which slows start-up and uses memory all day.{detail}",
            new HealthFix("Review startup apps", FixKind.OpenPage, "startup"));
    }
}

public sealed class DiskSpaceCheck : IHealthCheck
{
    private readonly ISystemInfo _sys;
    public DiskSpaceCheck(ISystemInfo sys) => _sys = sys;

    public string Id => "disk-space";
    public string Title => "Free disk space";

    public HealthFinding Run()
    {
        var d = _sys.SystemDrive();
        double freePct = 100 - d.PercentUsed;
        var text = $"{d.Name} is {d.PercentUsed}% full, with {d.FreeGb:0.#} GB free.";
        if (freePct < 10 || d.FreeGb < 5)
            return new(Id, Title, HealthStatus.Problem, text + " Windows and games slow down badly when the system drive is nearly full.",
                new HealthFix("Open Cleanup", FixKind.OpenPage, "cleanup"));
        if (freePct < 20 || d.FreeGb < 15)
            return new(Id, Title, HealthStatus.Warning, text + " Getting low.", new HealthFix("Open Cleanup", FixKind.OpenPage, "cleanup"));
        return new(Id, Title, HealthStatus.Good, text);
    }
}

public sealed class MemoryCheck : IHealthCheck
{
    private readonly ISystemInfo _sys;
    public MemoryCheck(ISystemInfo sys) => _sys = sys;

    public string Id => "memory";
    public string Title => "Memory and page file";

    public HealthFinding Run()
    {
        var r = _sys.Ram();
        var text = $"{r.UsedGb:0.0} of {r.TotalGb:0.0} GB in use ({r.Percent}%), page file {r.PageFileGb:0.0} GB.";
        var status = r.Percent >= 90 ? HealthStatus.Problem : r.Percent >= 80 ? HealthStatus.Warning : HealthStatus.Good;
        string advice = status != HealthStatus.Good ? " Close apps you are not using before playing." : "";
        if (r.PageFileGb < 0.5 && r.TotalGb < 16)
        {
            if (status == HealthStatus.Good) status = HealthStatus.Warning;
            advice += " There is almost no page file, so a game can crash when memory runs out. Turn on 'Automatically manage paging file size' in Windows' advanced system settings.";
        }
        return new(Id, Title, status, text + advice);
    }
}

public sealed class JunkCheck : IHealthCheck
{
    private readonly Func<long> _junkBytes;
    public JunkCheck(Func<long> junkBytes) => _junkBytes = junkBytes;

    public string Id => "junk";
    public string Title => "Junk files";

    public HealthFinding Run()
    {
        long b = _junkBytes();
        var size = b >= 1L << 30 ? $"{b / 1073741824.0:0.0} GB" : $"{b / 1048576.0:0} MB";
        if (b >= 5L << 30) return new(Id, Title, HealthStatus.Problem, $"{size} of junk files can be removed.", new HealthFix("Open Cleanup", FixKind.OpenPage, "cleanup"));
        if (b >= 500L << 20) return new(Id, Title, HealthStatus.Warning, $"{size} of junk files can be removed.", new HealthFix("Open Cleanup", FixKind.OpenPage, "cleanup"));
        return new(Id, Title, HealthStatus.Good, b < 1L << 20 ? "No junk files worth cleaning." : $"Only {size} of junk files.");
    }
}

public sealed class ThermalCheck : IHealthCheck
{
    private readonly Func<TempReading> _reading;
    private readonly Func<string?> _error;
    private readonly Func<ThermalLimits> _limits;

    public ThermalCheck(Func<TempReading> reading, Func<string?> error, Func<ThermalLimits> limits)
    {
        _reading = reading; _error = error; _limits = limits;
    }

    public string Id => "temperature";
    public string Title => "Temperature";

    public HealthFinding Run()
    {
        var t = _reading();
        if (t.Blind)
            return new(Id, Title, HealthStatus.Unknown, _error() ?? "No temperature reading yet. Try again in a few seconds.");
        var l = _limits();
        var parts = new List<string>();
        if (t.Cpu is int c) parts.Add($"CPU {c}°C");
        if (t.Gpu is int g) parts.Add($"GPU {g}°C");
        var text = string.Join(", ", parts) + " right now.";
        bool hot = (t.Cpu ?? 0) >= l.CpuHot || (t.Gpu ?? 0) >= l.GpuHot;
        bool warm = (t.Cpu ?? 0) >= l.CpuWarm || (t.Gpu ?? 0) >= l.GpuWarm;
        if (hot) return new(Id, Title, HealthStatus.Problem, text + " That is too hot for a PC that is not busy. Dust, dried thermal paste or a blocked fan is the likely cause, and software cannot fix that.");
        if (warm) return new(Id, Title, HealthStatus.Warning, text + " Warm for the current load. Check that the fans and vents are clear.");
        return new(Id, Title, HealthStatus.Good, text + " Heat that only shows up during games points to cooling, which Game Boost can ease but not cure.");
    }
}
