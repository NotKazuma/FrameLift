using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class PowerSettingsTests
{
    [Fact]
    public void Resolve_only_ever_moves_a_setting_toward_efficiency()
    {
        Assert.Equal(5, PowerSettings.Resolve(PowerSettings.MinState, 100, 5));
        Assert.Equal(3, PowerSettings.Resolve(PowerSettings.MinState, 3, 5));         // already lower: kept
        Assert.Equal(25, PowerSettings.Resolve(PowerSettings.Epp, 0, 25));
        Assert.Equal(60, PowerSettings.Resolve(PowerSettings.Epp, 60, 25));           // already more efficient: kept
        Assert.Equal(40, PowerSettings.Resolve(PowerSettings.IdleDemote, 100, 40));
        Assert.Equal(1, PowerSettings.Resolve(PowerSettings.CoolingPolicy, 0, 1));
        Assert.Equal(1, PowerSettings.Resolve(PowerSettings.CoolingPolicy, 1, 1));
    }

    [Fact]
    public void Boost_mode_keeps_turbo_but_in_its_efficient_form_and_never_turns_it_on()
    {
        Assert.Equal(4, PowerSettings.Resolve(PowerSettings.BoostMode, 2, 4)); // Aggressive -> Efficient Aggressive
        Assert.Equal(3, PowerSettings.Resolve(PowerSettings.BoostMode, 1, 4)); // Enabled -> Efficient Enabled
        Assert.Equal(6, PowerSettings.Resolve(PowerSettings.BoostMode, 5, 4));
        Assert.Equal(0, PowerSettings.Resolve(PowerSettings.BoostMode, 0, 4)); // Disabled stays disabled
        Assert.Equal(4, PowerSettings.Resolve(PowerSettings.BoostMode, 4, 4));
    }
}

public sealed class CoolBoostTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-cool-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new() { ActiveIsPerformance = true }; // like KernelOS: Boost keeps the plan
    private readonly FakeProcs _proc = new();
    private readonly FakeGpu _gpu = new();
    private readonly AppConfig _cfg = new();
    private readonly Journal _journal;
    private readonly Reverter _reverter;
    private readonly EfficiencyTuner _tuner;
    private readonly GameBoost _boost;

    public CoolBoostTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        _reverter = new Reverter(_journal, new FakeRegistry(), new FakeServices(), _power, _proc, _gpu);
        _tuner = new EfficiencyTuner(_power, _gpu, _journal);
        _boost = new GameBoost(_proc, _power, new FakeMemory(), _journal, _reverter, () => _cfg, tuner: _tuner);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private int S(Guid setting, bool ac = true) => _power.GetSetting(_power.Active, PowerSettings.SubProcessor, setting, ac);

    [Fact]
    public void Boost_start_lets_the_cpu_rest_and_stop_puts_every_value_back()
    {
        _boost.Start(null);

        Assert.Equal(5, S(PowerSettings.MinState));
        Assert.Equal(5, S(PowerSettings.MinState, ac: false));
        Assert.Equal(25, S(PowerSettings.Epp));
        Assert.Equal(4, S(PowerSettings.BoostMode));
        Assert.Equal(60, S(PowerSettings.IdlePromote));
        Assert.Equal(40, S(PowerSettings.IdleDemote));
        Assert.Equal(1, S(PowerSettings.CoolingPolicy));
        Assert.Equal(0, S(PowerSettings.CoolingPolicy, ac: false)); // battery keeps passive cooling
        Assert.Equal(1700, _gpu.LockedMhz);
        Assert.True(_power.Reapplies > 0);
        Assert.Equal(6, _journal.Entries.Count(e => e.Kind == EntryKind.PowerSetting && e.Owner == GameBoost.Owner));
        Assert.Null(_boost.EfficiencyNote);

        _boost.Stop();

        Assert.Equal(100, S(PowerSettings.MinState));
        Assert.Equal(0, S(PowerSettings.Epp));
        Assert.Equal(2, S(PowerSettings.BoostMode));
        Assert.Equal(100, S(PowerSettings.IdlePromote));
        Assert.Equal(0, S(PowerSettings.CoolingPolicy));
        Assert.Null(_gpu.LockedMhz);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public void A_plan_whose_low_maximum_was_hidden_by_a_100_minimum_keeps_its_real_top_speed()
    {
        // The user's KernelOS plan: max 50 % but min 100 %, which really runs at full turbo. Lowering only the minimum
        // was measured to drop the CPU from ~3 GHz to 1.3 GHz under load.
        _power.Caps[(_power.Active, true)] = 50;
        _power.Caps[(_power.Active, false)] = 50;

        _boost.Start(null);
        Assert.Equal(5, S(PowerSettings.MinState));
        Assert.Equal(100, S(PowerSettings.MaxState));
        Assert.Equal(100, S(PowerSettings.MaxState, ac: false));

        _boost.Stop();
        Assert.Equal(100, S(PowerSettings.MinState));
        Assert.Equal(50, S(PowerSettings.MaxState));
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public void Settings_the_user_already_has_tighter_are_left_alone()
    {
        foreach (var ac in new[] { true, false })
        {
            _power.Settings[(_power.Active, PowerSettings.MinState, ac)] = 3;
            _power.Settings[(_power.Active, PowerSettings.Epp, ac)] = 60;
            _power.Settings[(_power.Active, PowerSettings.BoostMode, ac)] = 0;
        }

        _boost.Start(null);

        Assert.Equal(3, S(PowerSettings.MinState));
        Assert.Equal(60, S(PowerSettings.Epp));
        Assert.Equal(0, S(PowerSettings.BoostMode));
        var touched = _journal.Entries.Where(e => e.Kind == EntryKind.PowerSetting).Select(e => e.Name).ToList();
        Assert.DoesNotContain(touched, n => n.EndsWith(PowerSettings.MinState.ToString()));
        Assert.DoesNotContain(touched, n => n.EndsWith(PowerSettings.Epp.ToString()));
        _boost.Stop();
    }

    [Fact]
    public void The_off_switch_and_a_gpu_without_clock_locks_change_nothing_extra()
    {
        _cfg.BoostEfficiency = false;
        _boost.Start(null);
        Assert.DoesNotContain(_journal.Entries, e => e.Kind is EntryKind.PowerSetting or EntryKind.GpuClockLock);
        Assert.Equal(100, S(PowerSettings.MinState));
        _boost.Stop();

        _cfg.BoostEfficiency = true;
        _gpu.ClockLockSupported = false;
        _boost.Start(null);
        Assert.DoesNotContain(_journal.Entries, e => e.Kind == EntryKind.GpuClockLock);
        Assert.Null(_gpu.LockedMhz);
        Assert.Null(_boost.EfficiencyNote); // unsupported is not an error, just skipped
        _boost.Stop();
    }

    [Fact]
    public void A_missing_setting_or_refused_clock_lock_is_reported_and_the_rest_still_applies()
    {
        _power.MissingSettings.Add(PowerSettings.Epp);
        _gpu.FailClockLock = true;

        _boost.Start(null);

        Assert.True(_boost.Active);
        Assert.Equal(5, S(PowerSettings.MinState));
        Assert.Contains("setting not on this plan", _boost.EfficiencyNote);
        Assert.Contains("clock lock refused", _boost.EfficiencyNote);

        _boost.Stop();
        Assert.Empty(_journal.Entries);
        Assert.Equal(1, _gpu.ClockResets); // the journaled lock is reset even though it was refused
    }

    [Fact]
    public void After_a_crash_the_watchdog_path_restores_cool_boost()
    {
        _boost.Start(null);

        // New process: a fresh journal read from disk and a reverter, exactly what the watchdog builds.
        var reborn = new Journal(Path.Combine(_dir, "journal.json"));
        var r = new Reverter(reborn, new FakeRegistry(), new FakeServices(), _power, _proc, _gpu);
        r.RevertOwner(GameBoost.Owner);

        Assert.Empty(r.Failures);
        Assert.Equal(100, S(PowerSettings.MinState));
        Assert.Equal(0, S(PowerSettings.Epp));
        Assert.Null(_gpu.LockedMhz);
        Assert.Empty(reborn.Entries);
    }

    [Fact]
    public void Bad_or_unrestorable_entries_are_dropped_and_reported_with_their_reason()
    {
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerSetting, Name = "x|y", Data = "1|2" });
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.GpuClockLock });
        var r = new Reverter(_journal, new FakeRegistry(), new FakeServices(), _power, _proc, gpu: null);

        Assert.Equal(0, r.RevertOwner("boost"));

        Assert.Empty(_journal.Entries);
        Assert.Contains(r.Failures, f => f.Contains("Bad power setting entry"));
        Assert.Contains(r.Failures, f => f.Contains("no GPU control"));
    }

    [Fact]
    public void New_kinds_survive_a_save_and_reload_as_text()
    {
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.PowerSetting, Name = "a|b|c", Data = "1|2" });
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.GpuClockLock });

        Assert.Contains("\"GpuClockLock\"", File.ReadAllText(Path.Combine(_dir, "journal.json")));
        var again = new Journal(Path.Combine(_dir, "journal.json"));
        Assert.Equal(new[] { EntryKind.PowerSetting, EntryKind.GpuClockLock }, again.Entries.Select(e => e.Kind));
    }
}

public sealed class SmoothHeatControlTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-smooth-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly FakeGpu _gpu = new(); // 60 W default
    private readonly AppConfig _cfg = new();  // CPU target 88: down at 90+, up at 84-, all off at 78-
    private readonly Journal _journal;
    private readonly HeatGuard _heat;
    private DateTime _clock = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    public SmoothHeatControlTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        var reverter = new Reverter(_journal, new FakeRegistry(), new FakeServices(), _power, _proc, _gpu);
        _heat = new HeatGuard(_power, _gpu, _proc, _journal, reverter, () => _cfg,
            otherGuardRunning: () => false, sleepMs: _ => { }, now: () => _clock);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private int Cap => _power.GetCpuMaxPercent(_power.Active, true);

    private void StepAfter(int seconds, int? cpu, int? gpu = null)
    {
        _clock = _clock.AddSeconds(seconds);
        _heat.Step(new TempReading(cpu, gpu, _clock));
    }

    [Fact]
    public void Holds_the_target_with_one_small_step_per_window_and_turbo_off_first()
    {
        _heat.Begin(0);

        StepAfter(0, 91, 70);
        Assert.Equal(99, Cap); // first step only turns turbo off
        StepAfter(2, 91, 70);
        Assert.Equal(99, Cap); // inside the 6 s window: no second step
        StepAfter(6, 91, 70);
        Assert.Equal(90, Cap);
        StepAfter(6, 89, 70);
        Assert.Equal(90, Cap); // in the band between 84 and 90: hold
        StepAfter(6, 84, 70);
        Assert.Equal(99, Cap); // cooled: one step back up
        Assert.Equal(ThermalLevel.Warm, _heat.Level);

        StepAfter(6, 60, 60);
        Assert.Equal(100, Cap); // never overheated, so nothing stops it climbing back to full speed
        Assert.Equal(ThermalLevel.Ok, _heat.Level);
    }

    [Fact]
    public void After_an_overheat_it_climbs_back_only_to_one_step_below_and_relaxes_later()
    {
        _heat.Begin(0);
        StepAfter(0, 97);                  // full speed ran into Critical
        Assert.Equal(30, Cap);

        StepAfter(1, 80);
        Assert.Equal(40, Cap);             // out of Critical: from the floor
        for (int i = 0; i < 10; i++) StepAfter(6, 80);
        Assert.Equal(99, Cap);             // climbed back, but not to the 100 % that overheated

        StepAfter(30, 80);                 // 90 s since the overheat with no new one
        Assert.Equal(100, Cap);
    }

    [Fact]
    public void Well_under_target_the_climb_runs_at_twice_the_rate()
    {
        _heat.Begin(0);
        StepAfter(0, 97);
        StepAfter(1, 70);
        Assert.Equal(40, Cap);
        StepAfter(3, 70);
        Assert.Equal(50, Cap);             // 3 s, not 6, because 70 is far under the 88 target
        StepAfter(3, 80);
        Assert.Equal(50, Cap);             // closer to target: back to the normal 6 s window
    }

    [Fact]
    public void Stops_at_the_floor_before_critical_and_critical_still_cuts_hard_then_climbs_back()
    {
        _heat.Begin(0);
        for (int i = 0; i < 12; i++) StepAfter(6, 95);
        Assert.Equal(40, Cap);
        Assert.Equal(ThermalLevel.Hot, _heat.Level);

        StepAfter(1, 97);
        Assert.Equal(30, Cap);
        Assert.Equal(ThermalLevel.Critical, _heat.Level);

        StepAfter(1, 85);
        Assert.Equal(40, Cap); // out of Critical: back to the floor, not straight to full speed
        StepAfter(6, 83);
        Assert.Equal(40, Cap); // even the floor overheated, so that is the ceiling for now
        StepAfter(90, 83);
        Assert.Equal(50, Cap); // after a long cool spell it may try one step more
    }

    [Fact]
    public void Cool_Boost_keeps_a_gpu_baseline_for_the_session_and_end_removes_it()
    {
        _heat.Begin(0);
        Assert.Equal(54, _gpu.LimitWatts); // 90 % of 60 W from the start
        Assert.Contains(_journal.Entries, e => e.Kind == EntryKind.GpuPowerLimit && e.Data == "60");

        StepAfter(0, 50, 50);
        Assert.Equal(54, _gpu.LimitWatts); // cool: stays at the baseline, never above
        StepAfter(6, 60, 83);
        Assert.Equal(51, _gpu.LimitWatts); // hot GPU: one 5 % step

        _heat.End();
        Assert.Equal(60, _gpu.LimitWatts);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public void Holding_steady_writes_nothing()
    {
        _heat.Begin(0);
        StepAfter(0, 91, 70);
        int reapplies = _power.Reapplies, gpuSets = _gpu.Sets;

        for (int i = 0; i < 10; i++) StepAfter(6, 88, 78);

        Assert.Equal(reapplies, _power.Reapplies);
        Assert.Equal(gpuSets, _gpu.Sets);
    }

    [Fact]
    public void A_cap_also_brings_down_a_minimum_that_would_override_it_and_release_restores_both()
    {
        _cfg.BoostEfficiency = false; // nothing else has lowered the plan's 100 % minimum
        var scheme = _power.Active;
        _power.Caps[(scheme, true)] = 50;  // the user's plan: max 50 % hidden behind min 100 %
        _power.Caps[(scheme, false)] = 50;
        _heat.Begin(0);

        StepAfter(0, 91);
        int Min() => _power.GetSetting(scheme, PowerSettings.SubProcessor, PowerSettings.MinState, true);
        Assert.Equal(99, Min());          // without this the 100 % minimum wins and the cap does nothing
        Assert.Equal(99, _heat.CpuCapPercent);

        StepAfter(1, 97);                 // Critical
        Assert.Equal(30, Min());
        Assert.Equal(30, Cap);

        _heat.End();
        Assert.Equal(100, Min());
        Assert.Equal(50, Cap);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public void With_smooth_control_off_the_old_ladder_applies()
    {
        _cfg.Thermal.SmoothControl = false;
        _heat.Begin(0);
        StepAfter(0, 86);
        Assert.Equal(80, Cap);
        Assert.Equal(ThermalLevel.Warm, _heat.Level);
    }
}

public sealed class CpuRestTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-rest-" + Guid.NewGuid().ToString("N"));
    private readonly FakePower _power = new();
    private readonly Journal _journal;
    private readonly TweakEngine _tweaks;

    public CpuRestTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        var reverter = new Reverter(_journal, new FakeRegistry(), new FakeServices(), _power, new FakeProcs());
        _tweaks = new TweakEngine(new FakeRegistry(), new FakeServices(), _power, _journal, reverter);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private int S(Guid setting) => _power.GetSetting(_power.Active, PowerSettings.SubProcessor, setting, true);

    [Fact]
    public void A_plan_that_never_rests_is_flagged_and_the_fix_is_journaled_and_undoable()
    {
        var check = new CpuIdleCheck(_power, _tweaks.IsApplied);

        var f = check.Run();
        Assert.Equal(HealthStatus.Problem, f.Status);
        Assert.Contains("100%", f.Message);
        Assert.Equal("cpu-rest", f.Fix?.Target);

        new HealthFixer(_tweaks).Apply(f.Fix!);
        Assert.Equal(5, S(PowerSettings.MinState));
        Assert.Equal(60, S(PowerSettings.IdlePromote));
        Assert.Equal(HealthStatus.Good, check.Run().Status);
        Assert.Equal(100, S(PowerSettings.MaxState)); // full speed is untouched

        _tweaks.Revert("cpu-rest");
        Assert.Equal(100, S(PowerSettings.MinState));
        Assert.Equal(100, S(PowerSettings.IdlePromote));
    }

    [Fact]
    public void The_fix_keeps_a_hidden_low_maximum_from_slowing_games_and_undo_restores_it()
    {
        _power.Caps[(_power.Active, true)] = 50;
        _power.Caps[(_power.Active, false)] = 50;

        _tweaks.Apply(TweakCatalog.Find("cpu-rest")!);
        Assert.Equal(5, S(PowerSettings.MinState));
        Assert.Equal(100, S(PowerSettings.MaxState));

        _tweaks.Revert("cpu-rest");
        Assert.Equal(50, S(PowerSettings.MaxState));
        Assert.Equal(100, S(PowerSettings.MinState));
    }

    [Fact]
    public void A_resting_plan_is_good_and_an_applied_tweak_offers_no_dead_button()
    {
        _power.SettingDefaults[PowerSettings.MinState] = 5;
        _power.SettingDefaults[PowerSettings.IdlePromote] = 60;
        Assert.Equal(HealthStatus.Good, new CpuIdleCheck(_power, _ => false).Run().Status);

        _power.SettingDefaults[PowerSettings.MinState] = 100;
        Assert.Null(new CpuIdleCheck(_power, _ => true).Run().Fix);
    }

    [Fact]
    public async Task An_unreadable_plan_becomes_an_Unknown_finding_with_the_reason()
    {
        _power.MissingSettings.Add(PowerSettings.MinState);
        var f = await HealthRunner.RunAsync(new CpuIdleCheck(_power, _ => false));
        Assert.Equal(HealthStatus.Unknown, f.Status);
        Assert.Contains("setting not on this plan", f.Message);
    }
}
