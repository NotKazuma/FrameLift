using System.Diagnostics;
using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class FakeMemory : IMemoryApi
{
    public int Purges;
    public MemoryStatus? Read() => new(2048, 300, 8192);
    public bool PurgeStandby() { Purges++; return true; }
}

public sealed class BoostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsb-boost-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly FakeServices _svc = new();
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly FakeMemory _mem = new();
    private readonly Journal _journal;
    private readonly GameBoost _boost;
    private readonly AppConfig _cfg = new();

    public BoostTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        var reverter = new Reverter(_journal, _reg, _svc, _power, _proc);
        _boost = new GameBoost(_proc, _power, _mem, _journal, reverter, () => _cfg);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Start_raises_game_and_switches_power_plan_then_Stop_restores_both()
    {
        var original = _power.Active;
        // Use this test process as the "game": it exists, so the real Tick() snapshot is harmless because
        // FakeProcs only knows about pid 4242.
        _proc.P[4242] = (start: 777, pri: ProcessPriorityClass.Normal, eco: false);

        _boost.Start(new RunningProc(4242, "game"));

        Assert.True(_boost.Active);
        Assert.Equal(ProcessPriorityClass.High, _proc.P[4242].pri);
        Assert.Equal(TweakCatalog.HighPerformance, _power.Active);
        Assert.Equal(1, _mem.Purges);

        _boost.Stop();

        Assert.False(_boost.Active);
        Assert.Equal(ProcessPriorityClass.Normal, _proc.P[4242].pri);
        Assert.Equal(original, _power.Active);
        Assert.False(_journal.HasOwner(GameBoost.Owner));
    }

    [Fact]
    public void Power_plan_and_memory_options_are_respected()
    {
        _cfg.BoostSwitchPowerPlan = false;
        _cfg.BoostPurgeMemory = false;
        var original = _power.Active;

        _boost.Start(null);

        Assert.Equal(original, _power.Active);
        Assert.Equal(0, _mem.Purges);
    }

    [Fact]
    public void Existing_performance_plan_is_never_replaced_by_a_weaker_one()
    {
        _power.ActiveIsPerformance = true; // e.g. Ultimate Performance or a custom performance plan
        var original = _power.Active;

        _boost.Start(null);
        Assert.Equal(original, _power.Active);

        _boost.Stop();
        Assert.Equal(original, _power.Active);
    }

    [Fact]
    public void Boost_entries_survive_a_crash_and_can_be_recovered_without_touching_tweaks()
    {
        var original = _power.Active;
        _journal.Add(new JournalEntry { Owner = "tweak:fx-peek", Kind = EntryKind.Registry, Path = "HKCU\\x", Name = "y", Existed = false });
        _boost.Start(null);
        Assert.Equal(TweakCatalog.HighPerformance, _power.Active);

        // Watchdog / next start: only the boost owner is reverted.
        var reborn = new Journal(Path.Combine(_dir, "journal.json"));
        new Reverter(reborn, _reg, _svc, _power, _proc).RevertOwner(GameBoost.Owner);

        Assert.Equal(original, _power.Active);
        Assert.True(reborn.HasOwner("tweak:fx-peek"));
    }

    [Fact]
    public void Select_skips_game_foreground_own_other_sessions_non_normal_and_protected()
    {
        ProcInfo P(int pid, string n, int s = 1, ProcessPriorityClass c = ProcessPriorityClass.Normal) => new(pid, n, s, c);
        var procs = new[]
        {
            P(1, "game"), P(2, "foreground"), P(3, "me"), P(4, "otherSession", 0), P(5, "high", 1, ProcessPriorityClass.High),
            P(6, "explorer"), P(7, "MsMpEng"), P(8, "EasyAntiCheat.exe"), P(9, "userskip"), P(10, "chrome"),
        };

        var picked = GameBoost.Select(procs, gamePid: 1, foregroundPid: 2, ownPid: 3, userSession: 1,
            userAllow: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "userskip" });

        Assert.Equal(new[] { "chrome" }, picked.Select(p => p.Name));
    }

    [Fact]
    public void Detector_waits_for_the_configured_time_and_ignores_excluded_apps()
    {
        var cfg = new AppConfig { AutoDetectSeconds = 10, Ignored = { "mygame2" } };
        var d = new GameDetector(cfg);
        var t0 = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var game = new RunningProc(50, "mygame.exe");

        Assert.Null(d.Choose(game, t0));
        Assert.Null(d.Choose(game, t0.AddSeconds(9)));
        Assert.Equal(game, d.Choose(game, t0.AddSeconds(10)));

        // Switching to a different app restarts the timer.
        var other = new RunningProc(51, "other");
        Assert.Null(d.Choose(other, t0.AddSeconds(11)));
        Assert.Null(d.Choose(other, t0.AddSeconds(15)));

        Assert.Null(d.Choose(new RunningProc(60, "chrome"), t0.AddSeconds(100)));
        Assert.Null(d.Choose(new RunningProc(61, "MyGame2"), t0.AddSeconds(100)));
        Assert.Null(d.Choose(null, t0.AddSeconds(100)));
    }
}
