using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class FakeSystemInfo : ISystemInfo
{
    public RamInfo RamValue = new(50, 4, 8, 4);
    public DriveSpace Drive = new("C:", 50, 100, 200);
    public RamInfo Ram() => RamValue;
    public DriveSpace SystemDrive() => Drive;
}

public sealed class FakeWeigher : IStartupWeigher
{
    public readonly Dictionary<string, double> Mb = new(StringComparer.OrdinalIgnoreCase);
    public double? MemoryMb(string command) => Mb.TryGetValue(command, out var v) ? v : null;
}

public sealed class HealthCheckTests
{
    private readonly FakeRegistry _reg = new();
    private readonly FakePower _power = new();

    [Fact]
    public void Power_plan_is_good_on_a_performance_plan_and_a_warning_with_a_fix_otherwise()
    {
        _power.ActiveIsPerformance = true;
        Assert.Equal(HealthStatus.Good, new PowerPlanCheck(_power, _ => false).Run().Status);

        _power.ActiveIsPerformance = false;
        var f = new PowerPlanCheck(_power, _ => false).Run();
        Assert.Equal(HealthStatus.Warning, f.Status);
        Assert.Equal(new HealthFix("Switch to High Performance", FixKind.ApplyTweak, "power-high"), f.Fix);

        Assert.Null(new PowerPlanCheck(_power, id => id == "power-high").Run().Fix); // already applied: a button could not help
    }

    [Fact]
    public void Game_mode_counts_a_missing_value_as_on_and_offers_a_fix_only_when_it_is_off()
    {
        Assert.Equal(HealthStatus.Good, new GameModeCheck(_reg, _ => false).Run().Status);

        _reg.Seed(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", "DWord", "0");
        var f = new GameModeCheck(_reg, _ => false).Run();
        Assert.Equal(HealthStatus.Warning, f.Status);
        Assert.Equal("game-mode", f.Fix?.Target);
        Assert.Null(new GameModeCheck(_reg, id => id == "game-mode").Run().Fix);
    }

    [Fact]
    public void Game_dvr_reads_the_capture_switch_first_and_defaults_to_on()
    {
        Assert.Equal(HealthStatus.Warning, new GameDvrCheck(_reg, _ => false).Run().Status); // nothing set: Windows default is on

        _reg.Seed(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", "DWord", "0");
        Assert.Equal(HealthStatus.Good, new GameDvrCheck(_reg, _ => false).Run().Status);

        _reg.Seed(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "DWord", "1");
        var f = new GameDvrCheck(_reg, _ => false).Run();
        Assert.Equal(HealthStatus.Warning, f.Status);
        Assert.Equal("game-dvr", f.Fix?.Target);
    }

    private static StartupItem Item(string name, bool enabled = true) => new(name, name + ".exe", "Current user", enabled, "ap", name);

    [Fact]
    public void Startup_status_follows_the_count_of_enabled_apps_and_names_the_heaviest()
    {
        var weigher = new FakeWeigher();
        weigher.Mb["a.exe"] = 300; weigher.Mb["b.exe"] = 90; weigher.Mb["c.exe"] = 700; weigher.Mb["d.exe"] = 40;
        var items = new List<StartupItem> { Item("a"), Item("b"), Item("c"), Item("d"), Item("e"), Item("f"), Item("off", false) };

        var f = new StartupCheck(() => items, weigher).Run();

        Assert.Equal(HealthStatus.Warning, f.Status); // 6 enabled; the disabled one does not count
        Assert.Contains("6 apps", f.Message);
        Assert.True(f.Message.IndexOf("c (700 MB)", StringComparison.Ordinal) < f.Message.IndexOf("a (300 MB)", StringComparison.Ordinal));
        Assert.DoesNotContain("d (40 MB)", f.Message); // only the top three
        Assert.Equal(new HealthFix("Review startup apps", FixKind.OpenPage, "startup"), f.Fix);

        Assert.Equal(HealthStatus.Good, new StartupCheck(() => items.Take(3).ToList(), weigher).Run().Status);
        var many = Enumerable.Range(0, 12).Select(i => Item("x" + i)).ToList();
        Assert.Equal(HealthStatus.Problem, new StartupCheck(() => many, weigher).Run().Status);
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\App\\app.exe\" --silent", "app")]
    [InlineData("C:\\Tools\\thing.exe /min", "thing")]
    [InlineData("C:\\Tools\\thing.EXE", "thing")]
    [InlineData("notepad", "notepad")]
    public void Process_name_is_taken_from_the_startup_command(string command, string expected) =>
        Assert.Equal(expected, ProcessStartupWeigher.ProcessNameOf(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\"unterminated")]
    public void Unreadable_startup_commands_give_no_process_name(string command) =>
        Assert.Null(ProcessStartupWeigher.ProcessNameOf(command));

    [Fact]
    public void Disk_space_is_a_problem_at_94_percent_full_and_points_to_cleanup()
    {
        var sys = new FakeSystemInfo { Drive = new("C:", 94, 14, 238) };
        var f = new DiskSpaceCheck(sys).Run();
        Assert.Equal(HealthStatus.Problem, f.Status);
        Assert.Contains("94% full", f.Message);
        Assert.Equal(new HealthFix("Open Cleanup", FixKind.OpenPage, "cleanup"), f.Fix);

        sys.Drive = new("C:", 85, 40, 250);
        Assert.Equal(HealthStatus.Warning, new DiskSpaceCheck(sys).Run().Status);
        sys.Drive = new("C:", 50, 500, 1000);
        var good = new DiskSpaceCheck(sys).Run();
        Assert.Equal(HealthStatus.Good, good.Status);
        Assert.Null(good.Fix);
        sys.Drive = new("D:", 70, 4, 13); // lots of percent free is not enough when only 4 GB remain
        Assert.Equal(HealthStatus.Problem, new DiskSpaceCheck(sys).Run().Status);
    }

    [Fact]
    public void Memory_status_follows_use_and_a_missing_page_file_on_a_small_machine_is_flagged()
    {
        var sys = new FakeSystemInfo { RamValue = new(95, 7.5, 8, 4) };
        Assert.Equal(HealthStatus.Problem, new MemoryCheck(sys).Run().Status);
        sys.RamValue = new(82, 6.5, 8, 4);
        Assert.Equal(HealthStatus.Warning, new MemoryCheck(sys).Run().Status);
        sys.RamValue = new(40, 3, 8, 4);
        Assert.Equal(HealthStatus.Good, new MemoryCheck(sys).Run().Status);

        sys.RamValue = new(40, 3, 8, 0);
        var f = new MemoryCheck(sys).Run();
        Assert.Equal(HealthStatus.Warning, f.Status);
        Assert.Contains("page file", f.Message);

        sys.RamValue = new(40, 6, 32, 0); // plenty of RAM: no page file is fine
        Assert.Equal(HealthStatus.Good, new MemoryCheck(sys).Run().Status);
    }

    [Theory]
    [InlineData(0L, HealthStatus.Good)]
    [InlineData(100L << 20, HealthStatus.Good)]
    [InlineData(600L << 20, HealthStatus.Warning)]
    [InlineData(6L << 30, HealthStatus.Problem)]
    public void Junk_status_follows_the_size_found(long bytes, HealthStatus expected)
    {
        var f = new JunkCheck(() => bytes).Run();
        Assert.Equal(expected, f.Status);
        Assert.Equal(expected == HealthStatus.Good, f.Fix is null);
    }

    [Fact]
    public void Temperature_is_unknown_without_a_reading_and_otherwise_graded_against_the_limits()
    {
        var limits = new ThermalLimits();
        TempReading R(int? c, int? g) => new(c, g, DateTime.UtcNow);

        var none = new ThermalCheck(() => R(null, null), () => "Temperature sensors unavailable: driver blocked", () => limits).Run();
        Assert.Equal(HealthStatus.Unknown, none.Status);
        Assert.Contains("driver blocked", none.Message);

        Assert.Equal(HealthStatus.Good, new ThermalCheck(() => R(50, 45), () => null, () => limits).Run().Status);
        Assert.Equal(HealthStatus.Warning, new ThermalCheck(() => R(86, 45), () => null, () => limits).Run().Status);
        var hot = new ThermalCheck(() => R(50, 84), () => null, () => limits).Run();
        Assert.Equal(HealthStatus.Problem, hot.Status);
        Assert.Contains("GPU 84", hot.Message);
        Assert.Equal(HealthStatus.Good, new ThermalCheck(() => R(55, null), () => null, () => limits).Run().Status); // cpu only
    }
}

public sealed class HealthRunnerTests
{
    private sealed class Check : IHealthCheck
    {
        public Func<HealthFinding> Body = () => new("x", "X", HealthStatus.Good, "ok");
        public string Id => "x";
        public string Title => "X";
        public HealthFinding Run() => Body();
    }

    [Fact]
    public async Task A_finished_check_returns_its_finding()
    {
        var f = await HealthRunner.RunAsync(new Check());
        Assert.Equal(HealthStatus.Good, f.Status);
    }

    [Fact]
    public async Task A_throwing_check_becomes_unknown_with_the_reason()
    {
        var f = await HealthRunner.RunAsync(new Check { Body = () => throw new InvalidOperationException("registry blocked") });
        Assert.Equal(HealthStatus.Unknown, f.Status);
        Assert.Contains("registry blocked", f.Message);
        Assert.Equal("x", f.Id);
    }

    [Fact]
    public async Task A_stuck_check_is_given_up_on_after_the_timeout()
    {
        var f = await HealthRunner.RunAsync(new Check { Body = () => { Thread.Sleep(1500); return new("x", "X", HealthStatus.Good, "late"); } },
            TimeSpan.FromMilliseconds(100));
        Assert.Equal(HealthStatus.Unknown, f.Status);
        Assert.Contains("did not finish", f.Message);
    }
}

public sealed class HealthFixerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-health-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly Journal _journal;
    private readonly TweakEngine _tweaks;
    private readonly HealthFixer _fixer;

    public HealthFixerTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
        var power = new FakePower();
        var svc = new FakeServices();
        var reverter = new Reverter(_journal, _reg, svc, power, new FakeProcs());
        _tweaks = new TweakEngine(_reg, svc, power, _journal, reverter);
        _fixer = new HealthFixer(_tweaks);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void A_tweak_fix_is_journaled_turns_the_check_good_and_can_be_undone()
    {
        var check = new GameDvrCheck(_reg, _tweaks.IsApplied);
        var fix = check.Run().Fix!;

        var msg = _fixer.Apply(fix);

        Assert.Contains("made", msg);
        Assert.True(_journal.HasOwner("tweak:game-dvr"));
        Assert.Equal(HealthStatus.Good, check.Run().Status);

        _tweaks.Revert("game-dvr");
        Assert.Equal(HealthStatus.Warning, check.Run().Status);
        Assert.Empty(_journal.Entries);
    }

    [Fact]
    public void Applying_the_same_fix_twice_changes_nothing_the_second_time()
    {
        var fix = new HealthFix("x", FixKind.ApplyTweak, "game-mode");
        _fixer.Apply(fix);
        var count = _journal.Entries.Count;
        Assert.Equal("Nothing needed changing.", _fixer.Apply(fix));
        Assert.Equal(count, _journal.Entries.Count);
    }

    [Fact]
    public void Unknown_tweaks_and_page_fixes_are_refused()
    {
        Assert.Throws<ArgumentException>(() => _fixer.Apply(new HealthFix("x", FixKind.ApplyTweak, "no-such-tweak")));
        Assert.Throws<ArgumentException>(() => _fixer.Apply(new HealthFix("x", FixKind.OpenPage, "cleanup")));
    }
}
