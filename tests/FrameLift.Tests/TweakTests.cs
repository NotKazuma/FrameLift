using System.Diagnostics;
using FrameLift.Core;

namespace FrameLift.Tests;

public sealed class TweakTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "lsb-test-" + Guid.NewGuid().ToString("N"));
    private readonly FakeRegistry _reg = new();
    private readonly FakeServices _svc = new();
    private readonly FakePower _power = new();
    private readonly FakeProcs _proc = new();
    private readonly Journal _journal;
    private readonly TweakEngine _engine;
    private readonly Reverter _reverter;
    private string JournalPath => Path.Combine(_dir, "journal.json");

    private const string Personalize = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    public TweakTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(JournalPath);
        _reverter = new Reverter(_journal, _reg, _svc, _power, _proc);
        _engine = new TweakEngine(_reg, _svc, _power, _journal, _reverter);
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Registry_tweak_applies_and_restores_existing_value()
    {
        var t = TweakCatalog.Find("fx-transparency")!;
        _reg.Seed(Personalize, "EnableTransparency", "DWord", "1");

        Assert.Equal(1, _engine.Apply(t));
        Assert.Equal("0", _reg.Read(Personalize, "EnableTransparency").Data);
        Assert.True(_engine.IsApplied(t.Id));

        _engine.Revert(t.Id);
        Assert.Equal("1", _reg.Read(Personalize, "EnableTransparency").Data);
        Assert.False(_engine.IsApplied(t.Id));
    }

    [Fact]
    public void Registry_tweak_deletes_value_that_did_not_exist_before()
    {
        var t = TweakCatalog.Find("fx-peek")!;
        _engine.Apply(t);
        Assert.True(_reg.Read(@"HKCU\Software\Microsoft\Windows\DWM", "EnableAeroPeek").Exists);

        _engine.Revert(t.Id);
        Assert.False(_reg.Read(@"HKCU\Software\Microsoft\Windows\DWM", "EnableAeroPeek").Exists);
    }

    [Fact]
    public void Service_tweak_sets_start_type_stops_service_and_restarts_on_revert()
    {
        var key = TweakCatalog.ServicePath("DiagTrack");
        _reg.Seed(key, "Start", "DWord", "2");
        _svc.Running.Add("DiagTrack");

        _engine.Apply(TweakCatalog.Find("svc-telemetry")!);
        Assert.Equal("4", _reg.Read(key, "Start").Data);
        Assert.Contains("DiagTrack", _svc.Stopped);

        _engine.Revert("svc-telemetry");
        Assert.Equal("2", _reg.Read(key, "Start").Data);
        Assert.Contains("DiagTrack", _svc.Started);
    }

    [Fact]
    public void Service_that_is_not_installed_is_skipped_not_created()
    {
        var n = _engine.Apply(TweakCatalog.Find("svc-misc")!);
        Assert.Equal(0, n);
        Assert.False(_reg.KeyExists(TweakCatalog.ServicePath("Fax")));
    }

    [Fact]
    public void Protected_services_are_never_touched()
    {
        var key = TweakCatalog.ServicePath("WinDefend");
        _reg.Seed(key, "Start", "DWord", "2");
        var t = new TweakDef("x", "x", "x", "x", Risk.Low, false, new ServiceChange("WinDefend", 4));

        Assert.Equal(0, _engine.Apply(t));
        Assert.Equal("2", _reg.Read(key, "Start").Data);
    }

    [Fact]
    public void Power_tweak_restores_previous_plan()
    {
        var original = _power.Active;
        _engine.Apply(TweakCatalog.Find("power-high")!);
        Assert.Equal(TweakCatalog.HighPerformance, _power.Active);

        _engine.Revert("power-high");
        Assert.Equal(original, _power.Active);
    }

    [Fact]
    public void Applying_twice_does_not_overwrite_the_original_in_the_journal()
    {
        var t = TweakCatalog.Find("fx-transparency")!;
        _reg.Seed(Personalize, "EnableTransparency", "DWord", "1");
        _engine.Apply(t);
        Assert.Equal(0, _engine.Apply(t));

        _engine.Revert(t.Id);
        Assert.Equal("1", _reg.Read(Personalize, "EnableTransparency").Data);
    }

    [Fact]
    public void Journal_survives_restart_and_can_be_reverted_by_a_new_instance()
    {
        _reg.Seed(Personalize, "EnableTransparency", "DWord", "1");
        _engine.Apply(TweakCatalog.Find("fx-transparency")!);
        _engine.Apply(TweakCatalog.Find("power-high")!);

        // Simulate a crash: a fresh Journal object reads the file the watchdog would read.
        var reborn = new Journal(JournalPath);
        var n = new Reverter(reborn, _reg, _svc, _power, _proc).RevertAll();

        Assert.Equal(2, n);
        Assert.Equal("1", _reg.Read(Personalize, "EnableTransparency").Data);
        Assert.Empty(reborn.Entries);
        Assert.False(File.Exists(JournalPath));
    }

    [Fact]
    public void Process_entry_is_not_restored_when_pid_was_reused()
    {
        _proc.P[100] = (start: 111, pri: ProcessPriorityClass.High, eco: false);
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.Process, Pid = 100, StartTicks = 999, Data = "Normal" });

        _reverter.RevertOwner("boost");
        Assert.Equal(ProcessPriorityClass.High, _proc.P[100].pri); // untouched: different process now
    }

    [Fact]
    public void Process_entry_is_restored_when_same_process()
    {
        _proc.P[100] = (start: 111, pri: ProcessPriorityClass.High, eco: true);
        _journal.Add(new JournalEntry { Owner = "boost", Kind = EntryKind.Process, Pid = 100, StartTicks = 111, Data = "Normal" });

        _reverter.RevertOwner("boost");
        Assert.Equal(ProcessPriorityClass.Normal, _proc.P[100].pri);
        Assert.False(_proc.P[100].eco);
    }

    [Fact]
    public void Preview_shows_current_and_new_values()
    {
        var t = TweakCatalog.Find("fx-transparency")!;
        _reg.Seed(Personalize, "EnableTransparency", "DWord", "1");
        var line = TweakPreview.Describe(t, _reg).Single();
        Assert.Contains("1 -> 0", line);
    }

    [Fact]
    public void Catalog_ids_are_unique_and_never_target_protected_services()
    {
        Assert.Equal(TweakCatalog.All.Count, TweakCatalog.All.Select(t => t.Id).Distinct().Count());
        foreach (var s in TweakCatalog.All.SelectMany(t => t.Changes).OfType<ServiceChange>())
            Assert.DoesNotContain(s.Service, TweakCatalog.ProtectedServices);
    }
}
