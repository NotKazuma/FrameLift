using System.Diagnostics;
using System.Runtime.InteropServices;
using FrameLift.Native;

namespace FrameLift.Core;

public sealed record ProcInfo(int Pid, string Name, int SessionId, ProcessPriorityClass Priority);

public sealed record MemoryStatus(long StandbyMb, long FreeMb, long TotalMb);

public interface IMemoryApi
{
    MemoryStatus? Read();
    bool PurgeStandby();
}

public sealed class SystemMemoryApi : IMemoryApi
{
    private bool _privileged;

    private bool EnsurePrivilege()
    {
        if (_privileged) return true;
        if (!NativeMethods.OpenProcessToken(NativeMethods.GetCurrentProcess(), 0x28, out var tok)) return false;
        try
        {
            if (!NativeMethods.LookupPrivilegeValue(null, "SeProfileSingleProcessPrivilege", out var luid)) return false;
            var tp = new NativeMethods.TOKEN_PRIVILEGES { Count = 1, Luid = luid, Attributes = 2 };
            NativeMethods.AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
            _privileged = Marshal.GetLastWin32Error() == 0;
        }
        finally { NativeMethods.CloseHandle(tok); }
        return _privileged;
    }

    public MemoryStatus? Read()
    {
        // SYSTEM_MEMORY_LIST_INFORMATION: Zero, Free, Modified, ModifiedNoWrite, Bad, PageCountByPriority[8], ...
        const int size = 256;
        var buf = Marshal.AllocHGlobal(size);
        try
        {
            if (NativeMethods.NtQuerySystemInformation(NativeMethods.SystemMemoryListInformation, buf, size, out _) != 0)
                return null;
            long page = Environment.SystemPageSize, ptr = IntPtr.Size;
            long Get(int i) => Marshal.ReadIntPtr(buf, (int)(i * ptr)).ToInt64();
            long standby = 0;
            for (int i = 5; i < 13; i++) standby += Get(i);
            long free = Get(0) + Get(1);
            long total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024 * 1024);
            return new MemoryStatus(standby * page / (1024 * 1024), free * page / (1024 * 1024), total);
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    public bool PurgeStandby()
    {
        if (!EnsurePrivilege()) return false;
        var buf = Marshal.AllocHGlobal(4);
        try
        {
            Marshal.WriteInt32(buf, NativeMethods.MemoryPurgeStandbyList);
            return NativeMethods.NtSetSystemInformation(NativeMethods.SystemMemoryListInformation, buf, 4) == 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}

/// <summary>
/// One Game Boost session: High Performance power plan, game at High priority, background apps lowered,
/// standby RAM purged. Everything is journaled under the owner "boost" so it can always be undone.
/// </summary>
public sealed class GameBoost
{
    public const string Owner = "boost";

    // Never lowered, whatever the user does: the shell, security software, anti-cheat, and capture/overlay tools.
    private static readonly HashSet<string> NeverTouch = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "csrss", "winlogon", "wininit", "services", "lsass", "smss", "svchost", "system", "idle",
        "sihost", "taskhostw", "fontdrvhost", "ctfmon", "audiodg", "searchhost", "startmenuexperiencehost", "shellexperiencehost",
        "msmpeng", "nissrv", "securityhealthservice", "securityhealthsystray",
        "easyanticheat", "easyanticheat_eos", "beservice", "battleye", "vgc", "vgtray", "faceitclient", "eaanticheat",
        "obs64", "obs32", "nvcontainer", "nvdisplay.container", "nvidia share", "rtss", "msiafterburner", "dsound",
        "steam", "steamwebhelper", "discord", "voicemeeter", "voicemeeterpro", "razer synapse", "lghub", "icue",
        "framelift", "stutterfix", "thermalguard",
    };

    private readonly IProcessOps _ops;
    private readonly IPower _power;
    private readonly IMemoryApi _mem;
    private readonly Journal _journal;
    private readonly Reverter _reverter;
    private readonly Func<AppConfig> _cfg;
    private readonly HeatGuard? _heat;
    private readonly Func<DateTime> _now;
    private readonly HashSet<int> _suppressed = new();
    private DateTime _lastPurge = DateTime.MinValue;

    public bool Active { get; private set; }
    public int GamePid { get; private set; }
    public string? GameName { get; private set; }
    public int SuppressedCount => _suppressed.Count;
    public long FreedMb { get; private set; }
    /// <summary>How many times standby memory was freed during this session, including the one at the start.</summary>
    public int Purges { get; private set; }

    public GameBoost(IProcessOps ops, IPower power, IMemoryApi mem, Journal journal, Reverter reverter, Func<AppConfig> cfg,
        HeatGuard? heat = null, Func<DateTime>? now = null)
    {
        _ops = ops; _power = power; _mem = mem; _journal = journal; _reverter = reverter; _cfg = cfg;
        _heat = heat;
        _now = now ?? (() => DateTime.UtcNow);
    }

    private static string Normalize(string n) => n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? n[..^4] : n;

    /// <summary>Pure selection of what to lower, separated from the Win32 calls so it is testable.</summary>
    public static IReadOnlyList<ProcInfo> Select(IEnumerable<ProcInfo> procs, int gamePid, int foregroundPid,
        int ownPid, int userSession, ISet<string> userAllow) =>
        procs.Where(p =>
            p.Pid != gamePid && p.Pid != foregroundPid && p.Pid != ownPid &&
            p.SessionId == userSession &&
            p.Priority == ProcessPriorityClass.Normal &&
            !NeverTouch.Contains(Normalize(p.Name)) &&
            !userAllow.Contains(Normalize(p.Name))).ToList();

    public static List<ProcInfo> Snapshot()
    {
        var list = new List<ProcInfo>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add(new ProcInfo(p.Id, p.ProcessName, p.SessionId, p.PriorityClass)); }
            catch (Exception) { /* protected or exited: not ours to touch */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    /// <param name="game">The game to raise, or null for a manual boost with no specific game.</param>
    public void Start(RunningProc? game)
    {
        if (Active) return;
        Active = true;
        var cfg = _cfg();
        _suppressed.Clear();
        FreedMb = 0;
        Purges = 0;
        _lastPurge = DateTime.MinValue;

        if (cfg.BoostSwitchPowerPlan)
        {
            var current = _power.GetActiveScheme();
            // Already on Ultimate/High/custom performance plan: switching could only make it weaker.
            var target = _power.IsPerformancePlan(current) ? current : _power.EnsureScheme(TweakCatalog.HighPerformance);
            if (current != target)
            {
                _journal.Add(new JournalEntry { Owner = Owner, Kind = EntryKind.PowerPlan, Data = current.ToString() });
                _power.SetActiveScheme(target);
            }
        }

        if (game is not null)
        {
            var ticks = _ops.GetStartTicks(game.Pid);
            if (ticks != 0)
            {
                try
                {
                    var orig = _ops.GetPriority(game.Pid);
                    _journal.Add(new JournalEntry
                    {
                        Owner = Owner, Kind = EntryKind.Process, Pid = game.Pid, StartTicks = ticks, Data = orig.ToString(),
                    });
                    _ops.SetPriority(game.Pid, ProcessPriorityClass.High); // never Realtime: it can starve input and audio
                    GamePid = game.Pid;
                    GameName = game.Name;
                }
                catch (Exception) { /* exited or access denied */ }
            }
        }

        Suppress();

        if (cfg.BoostPurgeMemory)
        {
            var before = _mem.Read();
            if (_mem.PurgeStandby())
            {
                if (before is not null) FreedMb = before.StandbyMb;
                Purges++;
                _lastPurge = _now();
            }
        }

        _heat?.Begin(GamePid);
    }

    /// <summary>
    /// Call periodically while active: lowers newly started background apps and, when standby memory has grown large
    /// and free memory is low, frees it again (at most once per cooldown). Returns how many apps were lowered.
    /// </summary>
    public int Tick()
    {
        if (!Active) return 0;
        int n = Suppress();
        if (_cfg().BoostPurgeMemory) MaybePurge();
        return n;
    }

    private void MaybePurge()
    {
        var cfg = _cfg();
        var now = _now();
        if ((now - _lastPurge).TotalSeconds < cfg.StandbyCooldownSeconds) return;
        var s = _mem.Read();
        if (s is null || s.StandbyMb < cfg.StandbyThresholdMb || s.FreeMb >= cfg.FreeBelowMb) return;
        if (!_mem.PurgeStandby()) return;
        _lastPurge = now;
        FreedMb += s.StandbyMb;
        Purges++;
    }

    private int Suppress()
    {
        using var self = Process.GetCurrentProcess();
        var allow = new HashSet<string>(_cfg().Ignored.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        var targets = Select(Snapshot(), GamePid, WindowProbe.ForegroundPid(), Environment.ProcessId, self.SessionId, allow)
            .Where(t => !_suppressed.Contains(t.Pid)).ToList();

        int n = 0;
        foreach (var t in targets)
        {
            var ticks = _ops.GetStartTicks(t.Pid);
            if (ticks == 0) continue;
            // Journal first: a crash between the save and the change is harmless, the reverse is not.
            _journal.Add(new JournalEntry
            {
                Owner = Owner, Kind = EntryKind.Process, Pid = t.Pid, StartTicks = ticks, Data = t.Priority.ToString(),
            });
            _suppressed.Add(t.Pid);
            try
            {
                _ops.SetPriority(t.Pid, ProcessPriorityClass.BelowNormal);
                _ops.SetEcoQos(t.Pid, true);
                n++;
            }
            catch (Exception) { /* access denied or exited: the journal entry restores to the same value */ }
        }
        return n;
    }

    public bool GameStillRunning() => GamePid == 0 || _ops.GetStartTicks(GamePid) != 0;

    public void Stop()
    {
        _heat?.End(); // undoes its own limits first; whatever else is left under "boost" goes next
        _reverter.RevertOwner(Owner);
        _suppressed.Clear();
        GamePid = 0;
        GameName = null;
        Active = false;
    }
}
