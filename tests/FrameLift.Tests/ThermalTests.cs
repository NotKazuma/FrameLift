using System.Diagnostics;
using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class ThermalPolicyTests
{
    [Fact]
    public void Levels_follow_the_highest_of_cpu_and_gpu()
    {
        var p = new ThermalPolicy(new ThermalLimits());
        Assert.Equal(ThermalLevel.Ok, p.Update(gpu: 60, cpu: 60));
        Assert.Equal(ThermalLevel.Warm, p.Update(gpu: 79, cpu: 60));
        Assert.Equal(ThermalLevel.Hot, p.Update(gpu: 60, cpu: 93));
        Assert.Equal(ThermalLevel.Critical, p.Update(gpu: 88, cpu: 93));
    }

    [Fact]
    public void Steps_down_only_after_the_cooldown_margin_and_a_missing_sensor_is_ignored()
    {
        var p = new ThermalPolicy(new ThermalLimits()); // CPU critical 96, cooldown 5
        Assert.Equal(ThermalLevel.Critical, p.Update(gpu: null, cpu: 97));
        Assert.Equal(ThermalLevel.Critical, p.Update(gpu: null, cpu: 93)); // under 96 but not 5 degrees under
        Assert.Equal(ThermalLevel.Hot, p.Update(gpu: null, cpu: 90));      // 6 under critical, still above hot - 5
        Assert.Equal(ThermalLevel.Ok, p.Update(gpu: null, cpu: 50));
    }

    [Fact]
    public void Garbage_sensor_values_are_rejected()
    {
        Assert.Null(TempRange.Valid(0));
        Assert.Null(TempRange.Valid(255));
        Assert.Null(TempRange.Valid(float.NaN));
        Assert.Null(TempRange.Valid(null));
        Assert.Equal(71, TempRange.Valid(70.6f));
    }
}

public sealed class TempReaderTests
{
    private sealed class Src : ITempSource
    {
        public int? CpuTemp { get; set; }
        public int? GpuTemp { get; set; }
        public bool Throw;
        public TimeSpan Delay;
        public int Updates;
        public void Update() { Updates++; if (Delay > TimeSpan.Zero) Thread.Sleep(Delay); if (Throw) throw new InvalidOperationException("sensor read failed"); }
        public void Dispose() { }
    }

    [Fact]
    public void Poll_returns_the_source_readings_and_clears_the_error()
    {
        var src = new Src { CpuTemp = 55, GpuTemp = 48 };
        using var r = new TempReader(() => src);
        var t = r.Poll();
        Assert.Equal((55, 48), (t.Cpu, t.Gpu));
        Assert.Null(r.Error);
    }

    [Fact]
    public void A_source_that_cannot_open_is_reported_once_and_never_retried()
    {
        int opens = 0;
        using var r = new TempReader(() => { opens++; throw new InvalidOperationException("driver blocked"); });
        var t = r.Poll();
        r.Poll(); r.Poll();
        Assert.True(t.Blind);
        Assert.Contains("driver blocked", r.Error);
        Assert.Equal(1, opens);
    }

    [Fact]
    public void A_sensor_read_that_hangs_is_given_up_on_and_never_touched_again()
    {
        var src = new Src { Delay = TimeSpan.FromSeconds(2), CpuTemp = 50 };
        using var r = new TempReader(() => src, timeout: TimeSpan.FromMilliseconds(100));

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var t = r.Poll();
        sw.Stop();
        r.Poll();

        Assert.True(t.Blind);
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(1.5), "Poll must return near the timeout, not wait for the stuck read");
        Assert.Contains("did not respond", r.Error);
        Assert.Equal(1, src.Updates);
    }

    [Fact]
    public void Repeated_read_failures_stop_polling_after_the_limit()
    {
        var src = new Src { Throw = true };
        using var r = new TempReader(() => src);
        for (int i = 0; i < 20; i++) r.Poll();
        Assert.Equal(TempReader.MaxConsecutiveFailures, src.Updates);
        Assert.Contains("sensor read failed", r.Error);
    }
}

public sealed class HeatGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-heat-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly FakeGpu _gpu = new();
    private readonly AppConfig _cfg = new();
    private readonly Journal _journal;
    private readonly Reverter _reverter;
    private readonly List<int> _sleeps = new();
    private DateTime _clock = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private HeatGuard _heat;
    private bool _other;
    private Action? _duringSleep;

    public HeatGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        _reverter = new Reverter(_journal, _reg, new FakeServices(), _power, _proc, _gpu);
        _heat = new HeatGuard(_power, _gpu, _proc, _journal, _reverter, () => _cfg,
            otherGuardRunning: () => _other, sleepMs: ms => { _sleeps.Add(ms); _duringSleep?.Invoke(); }, now: () => _clock);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private TempReading T(int? cpu, int? gpu = null) => new(cpu, gpu, _clock);

    [Fact]
    public void Hot_reading_lowers_cpu_and_gpu_limits_and_cool_reading_puts_both_back()
    {
        _cfg.BoostEfficiency = false; // no Cool Boost GPU baseline: cool means fully back to default
        _cfg.Thermal.SmoothControl = false; // the plain ladder: back to full speed as soon as it is cool
        var scheme = _power.Active;
        _heat.Begin(0);

        Assert.Equal(ThermalLevel.Critical, _heat.Step(T(cpu: 98, gpu: 60)));
        Assert.Equal(30, _power.Caps[(scheme, true)]);
        Assert.Equal(30, _power.Caps[(scheme, false)]);
        Assert.Equal(42, _gpu.LimitWatts); // 70% of the 60 W default
        Assert.Equal(30, _heat.CpuCapPercent);
        Assert.Contains(_journal.Entries, e => e.Kind == EntryKind.PowerCap && e.Owner == GameBoost.Owner);
        Assert.Contains(_journal.Entries, e => e.Kind == EntryKind.GpuPowerLimit && e.Data == "60");

        Assert.Equal(ThermalLevel.Ok, _heat.Step(T(cpu: 50, gpu: 50)));
        Assert.Equal(100, _power.Caps[(scheme, true)]);
        Assert.Equal(60, _gpu.LimitWatts);
        Assert.Empty(_journal.Entries);
        Assert.Equal(100, _heat.CpuCapPercent);
    }

    [Fact]
    public void A_plan_that_is_already_set_lower_than_the_cap_is_never_raised()
    {
        var scheme = _power.Active;
        _power.Caps[(scheme, true)] = 40;
        _power.Caps[(scheme, false)] = 40;
        _heat.Begin(0);

        _heat.Step(T(cpu: 86)); // Warm: cap 80, but the plan was already at 40

        Assert.Equal(40, _power.Caps[(scheme, true)]);
        Assert.Equal(40, _power.Caps[(scheme, false)]);
    }

    [Fact]
    public void The_power_api_is_only_touched_when_the_level_changes()
    {
        _heat.Begin(0);
        _heat.Step(T(cpu: 98));
        int reapplied = _power.Reapplies, gpuSets = _gpu.Sets;

        for (int i = 0; i < 10; i++) _heat.Step(T(cpu: 98));

        Assert.Equal(reapplied, _power.Reapplies);
        Assert.Equal(gpuSets, _gpu.Sets);
    }

    [Fact]
    public void Boost_stop_undoes_the_limits_and_leaves_tweaks_alone()
    {
        var scheme = _power.Active;
        _journal.Add(new JournalEntry { Owner = "tweak:fx-peek", Kind = EntryKind.Registry, Path = "HKCU\\x", Name = "y", Existed = false });
        var boost = new GameBoost(_proc, _power, new FakeMemory(), _journal, _reverter, () => _cfg, _heat);
        boost.Start(null);
        _heat.Step(T(cpu: 98));
        Assert.Equal(30, _power.Caps[(_power.Active, true)]);

        boost.Stop();

        Assert.False(_heat.Active);
        Assert.Equal(100, _power.Caps[(TweakCatalog.HighPerformance, true)]);
        Assert.Equal(60, _gpu.LimitWatts);
        Assert.False(_journal.HasOwner(GameBoost.Owner));
        Assert.True(_journal.HasOwner("tweak:fx-peek"));
        Assert.Equal(scheme, _power.Active);
    }

    [Fact]
    public void A_crash_leaves_journal_entries_that_the_watchdog_path_restores()
    {
        var scheme = _power.Active;
        _heat.Begin(0);
        _heat.Step(T(cpu: 98));

        // New process: a fresh journal read from disk and a reverter, exactly what the watchdog builds.
        var reborn = new Journal(Path.Combine(_dir, "journal.json"));
        new Reverter(reborn, _reg, new FakeServices(), _power, _proc, _gpu).RevertOwner(GameBoost.Owner);

        Assert.Equal(100, _power.Caps[(scheme, true)]);
        Assert.Equal(60, _gpu.LimitWatts);
        Assert.Empty(reborn.Entries);
    }

    [Fact]
    public void A_refused_power_write_is_reported_not_thrown_and_the_journal_still_covers_it()
    {
        _power.FailCapWrites = true;
        _heat.Begin(0);

        var level = _heat.Step(T(cpu: 98));

        Assert.Equal(ThermalLevel.Critical, level);
        Assert.Contains("power write refused", _heat.Note);
        Assert.Contains(_journal.Entries, e => e.Kind == EntryKind.PowerCap); // so a later undo still has the original values
    }

    [Fact]
    public void Switching_power_plan_mid_session_restores_the_old_plan_and_limits_the_new_one()
    {
        var first = _power.Active;
        _heat.Begin(0);
        _heat.Step(T(cpu: 90));  // Hot
        var second = Guid.NewGuid();
        _power.Active = second;

        _heat.Step(T(cpu: 98));  // Critical, on the new plan

        Assert.Equal(100, _power.Caps[(first, true)]);
        Assert.Equal(30, _power.Caps[(second, true)]);
        Assert.Single(_journal.Entries, e => e.Kind == EntryKind.PowerCap);
    }

    [Fact]
    public void Missing_readings_release_the_limits_after_the_blind_limit()
    {
        var scheme = _power.Active;
        _heat.Begin(0);
        _heat.Step(T(cpu: 98));

        for (int i = 0; i < HeatGuard.BlindPollLimit; i++) _heat.Step(T(null));

        Assert.Equal(ThermalLevel.Ok, _heat.Level);
        Assert.Equal(100, _power.Caps[(scheme, true)]);
        Assert.Contains("No temperature readings", _heat.Note);
    }

    [Fact]
    public void Nothing_is_limited_while_the_standalone_ThermalGuard_is_running_or_when_switched_off()
    {
        var scheme = _power.Active;
        _heat.Begin(0);

        _other = true;
        _heat.Step(T(cpu: 98));
        Assert.Equal(ThermalLevel.Ok, _heat.Level);
        Assert.False(_power.Caps.ContainsKey((scheme, true)));
        Assert.Contains("ThermalGuard", _heat.Note);

        _other = false;
        _cfg.ThermalEaseOff = false;
        _heat.Step(T(cpu: 98));
        Assert.False(_power.Caps.ContainsKey((scheme, true)));
    }

    [Fact]
    public void Suspend_is_off_by_default_and_when_on_always_resumes_and_is_rate_limited()
    {
        _proc.P[4242] = (start: 777, pri: ProcessPriorityClass.Normal, eco: false);
        _heat.Begin(4242);
        _heat.Step(T(cpu: 98));
        Assert.Empty(_sleeps); // default: never freezes the game

        _cfg.ThermalSuspendAtCritical = true;
        bool frozenDuringSleep = false, journaledDuringSleep = false;
        _duringSleep = () =>
        {
            frozenDuringSleep = _proc.Suspended.Contains(4242);
            journaledDuringSleep = _journal.Entries.Any(e => e.Kind == EntryKind.ProcessSuspend);
        };
        _heat.Step(T(cpu: 98));

        Assert.Single(_sleeps);
        Assert.True(frozenDuringSleep);
        Assert.True(journaledDuringSleep);
        Assert.Empty(_proc.Suspended);
        Assert.DoesNotContain(_journal.Entries, e => e.Kind == EntryKind.ProcessSuspend);

        _clock = _clock.AddSeconds(2);
        _heat.Step(T(cpu: 98));
        Assert.Single(_sleeps); // within 5 s of the last one

        _clock = _clock.AddSeconds(5);
        _heat.Step(T(cpu: 98));
        Assert.Equal(2, _sleeps.Count);
    }

    [Fact]
    public void Suspend_skips_a_game_that_is_already_gone()
    {
        _cfg.ThermalSuspendAtCritical = true;
        // pid 4242 is unknown to FakeProcs (start time 0): nothing to freeze, nothing journaled.
        _heat.Begin(4242);
        _heat.Step(T(cpu: 98));
        Assert.Empty(_sleeps);
        Assert.DoesNotContain(_journal.Entries, e => e.Kind == EntryKind.ProcessSuspend);
    }
}

public sealed class NewJournalKindTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-kinds-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly Journal _journal;

    public NewJournalKindTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private Reverter R(IGpuPower? gpu = null) => new(_journal, new FakeRegistry(), new FakeServices(), _power, _proc, gpu);

    [Fact]
    public void Power_cap_entry_restores_ac_and_dc_and_reapplies_only_the_active_plan()
    {
        var other = Guid.NewGuid();
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerCap, Name = other.ToString(), Data = "100|70" });
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerCap, Name = _power.Active.ToString(), Data = "90|60" });

        var n = R().RevertOwner("boost");

        Assert.Equal(2, n);
        Assert.Equal(70, _power.Caps[(other, false)]);
        Assert.Equal(90, _power.Caps[(_power.Active, true)]);
        Assert.Equal(1, _power.Reapplies);
        Assert.Empty(R().Failures);
    }

    [Fact]
    public void Malformed_power_cap_entry_is_dropped_and_reported_with_its_reason()
    {
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerCap, Name = "not-a-guid", Data = "x" });
        var r = R();

        var n = r.RevertOwner("boost");

        Assert.Equal(0, n);
        Assert.Empty(_journal.Entries);
        Assert.Contains("Bad power cap entry", Assert.Single(r.Failures));
    }

    [Fact]
    public void Gpu_limit_entry_restores_the_default_and_without_a_gpu_reports_it()
    {
        var gpu = new FakeGpu();
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.GpuPowerLimit, Data = "75" });
        R(gpu).RevertOwner("boost");
        Assert.Equal(75, gpu.LimitWatts);

        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.GpuPowerLimit, Data = "75" });
        var r = R(null);
        r.RevertOwner("boost");
        Assert.Contains("no GPU power control", Assert.Single(r.Failures));
    }

    [Fact]
    public void Suspend_entry_resumes_the_same_process_but_not_a_reused_pid()
    {
        _proc.P[10] = (start: 111, pri: ProcessPriorityClass.Normal, eco: false);
        _proc.Suspended.Add(10);
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.ProcessSuspend, Pid = 10, StartTicks = 111 });
        R().RevertOwner("boost");
        Assert.Empty(_proc.Suspended);

        _proc.Suspended.Add(10);
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.ProcessSuspend, Pid = 10, StartTicks = 999 }); // pid reused
        R().RevertOwner("boost");
        Assert.Contains(10, _proc.Suspended);
    }
}

public sealed class StandbyMemoryTests
{
    private sealed class Mem : IMemoryApi
    {
        public MemoryStatus? Status = new(2048, 300, 8192);
        public int Purges;
        public bool PurgeResult = true;
        public MemoryStatus? Read() => Status;
        public bool PurgeStandby() { Purges++; return PurgeResult; }
    }

    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-standby-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly Mem _mem = new();
    private readonly AppConfig _cfg = new();
    private DateTime _clock = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
    private readonly GameBoost _boost;

    public StandbyMemoryTests()
    {
        Directory.CreateDirectory(_dir);
        var journal = new Journal(Path.Combine(_dir, "journal.json"));
        var reverter = new Reverter(journal, new FakeRegistry(), new FakeServices(), _power, _proc);
        _boost = new GameBoost(_proc, _power, _mem, journal, reverter, () => _cfg, now: () => _clock);
    }

    [Fact]
    public void Tick_frees_standby_again_only_when_it_is_large_free_is_low_and_the_cooldown_passed()
    {
        _boost.Start(null);
        Assert.Equal(1, _mem.Purges); // the one at the start

        _clock = _clock.AddSeconds(30);
        _boost.Tick();
        Assert.Equal(1, _mem.Purges); // inside the 60 s cooldown

        _clock = _clock.AddSeconds(31);
        _boost.Tick();
        Assert.Equal(2, _mem.Purges);
        Assert.Equal(2, _boost.Purges);

        _mem.Status = new MemoryStatus(2048, 4000, 8192); // plenty free: no need
        _clock = _clock.AddSeconds(120);
        _boost.Tick();
        Assert.Equal(2, _mem.Purges);

        _mem.Status = new MemoryStatus(200, 300, 8192); // standby too small to matter
        _boost.Tick();
        Assert.Equal(2, _mem.Purges);

        _boost.Stop();
    }

    [Fact]
    public void A_failed_purge_or_unreadable_memory_changes_nothing_and_the_option_can_turn_it_off()
    {
        _cfg.BoostPurgeMemory = false;
        _boost.Start(null);
        _clock = _clock.AddSeconds(300);
        _boost.Tick();
        Assert.Equal(0, _mem.Purges);
        _boost.Stop();

        _cfg.BoostPurgeMemory = true;
        _mem.PurgeResult = false;
        _boost.Start(null);
        _clock = _clock.AddSeconds(300);
        _boost.Tick();
        Assert.Equal(0, _boost.Purges);
        _boost.Stop();

        _mem.Status = null;
        _boost.Start(null);
        _clock = _clock.AddSeconds(300);
        var before = _mem.Purges;
        _boost.Tick();
        Assert.Equal(before, _mem.Purges);
        _boost.Stop();
    }
}

public sealed class RealSystemInfoTests
{
    [Fact]
    public void System_info_reports_sane_memory_and_drive_numbers_on_this_PC()
    {
        var sys = new SystemInfo();
        var ram = sys.Ram();
        var drive = sys.SystemDrive();

        Assert.InRange(ram.Percent, 1, 100);
        Assert.True(ram.TotalGb > 0 && ram.UsedGb > 0 && ram.UsedGb <= ram.TotalGb);
        Assert.True(ram.PageFileGb >= 0);
        Assert.InRange(drive.PercentUsed, 0, 100);
        Assert.True(drive.TotalGb > drive.FreeGb && drive.FreeGb >= 0);
        Assert.EndsWith(":", drive.Name);
    }

    [Fact]
    public void Startup_weigher_measures_a_running_app_and_gives_null_for_one_that_is_not_running()
    {
        var self = Environment.ProcessPath!;
        var w = new ProcessStartupWeigher();

        Assert.True(w.MemoryMb($"\"{self}\" --silent") > 1);
        Assert.Null(w.MemoryMb(@"C:\Nowhere\definitely-not-running-app.exe"));
        Assert.Null(w.MemoryMb(""));
    }

    [Fact]
    public void New_journal_kinds_survive_a_save_and_reload_as_text()
    {
        var dir = Path.Combine(Path.GetTempPath(), "fl-kindjson-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "j.json");
            var j = new Journal(path);
            j.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerCap, Name = Guid.Empty.ToString(), Data = "100|70" });
            j.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.GpuPowerLimit, Data = "60" });
            j.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.ProcessSuspend, Pid = 4, StartTicks = 9 });

            Assert.Contains("\"PowerCap\"", File.ReadAllText(path)); // readable names, so an old build never misreads a number
            var again = new Journal(path);
            Assert.Equal(new[] { EntryKind.PowerCap, EntryKind.GpuPowerLimit, EntryKind.ProcessSuspend }, again.Entries.Select(e => e.Kind));
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
