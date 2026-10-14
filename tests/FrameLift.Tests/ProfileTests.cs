using FrameLift.Core;

namespace FrameLift.Tests;

/// <summary>A registry that refuses to write under one key, to prove one failing step never stops the rest.</summary>
public sealed class RefusingRegistry : IRegistry
{
    private readonly FakeRegistry _inner = new();
    private readonly string _refusePath;
    public RefusingRegistry(string refusePath) => _refusePath = refusePath;

    public RegSnapshot Read(string path, string name) => _inner.Read(path, name);
    public void Write(string path, string name, string kind, string data)
    {
        if (path.Contains(_refusePath, StringComparison.OrdinalIgnoreCase)) throw new UnauthorizedAccessException("access denied by policy");
        _inner.Write(path, name, kind, data);
    }
    public void DeleteValue(string path, string name) => _inner.DeleteValue(path, name);
    public IReadOnlyList<string> ValueNames(string path) => _inner.ValueNames(path);
    public bool KeyExists(string path) => _inner.KeyExists(path);
}

public sealed class ProfileTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "fl-prof-" + Guid.NewGuid().ToString("N"));
    private readonly Journal _journal;

    public ProfileTests()
    {
        Directory.CreateDirectory(_dir);
        _journal = new Journal(Path.Combine(_dir, "journal.json"));
    }

    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private (ProfileService Svc, TweakEngine Engine, Reverter Reverter) Build(IRegistry reg)
    {
        var power = new FakePower();
        var svc = new FakeServices();
        var reverter = new Reverter(_journal, reg, svc, power, new FakeProcs());
        var engine = new TweakEngine(reg, svc, power, _journal, reverter);
        return (new ProfileService(engine), engine, reverter);
    }

    [Fact]
    public void Each_profile_contains_the_one_before_it_and_every_id_exists()
    {
        var ids = ProfileCatalog.All.Select(p => p.TweakIds.ToHashSet()).ToList();
        for (int i = 1; i < ids.Count; i++) Assert.True(ids[i - 1].IsSubsetOf(ids[i]), $"profile {i} drops tweaks of profile {i - 1}");
        foreach (var id in ProfileCatalog.All.SelectMany(p => p.TweakIds)) Assert.NotNull(TweakCatalog.Find(id));
    }

    [Fact]
    public void Only_the_Maximum_profile_includes_medium_risk_tweaks()
    {
        foreach (var p in ProfileCatalog.All)
        {
            bool hasMedium = p.TweakIds.Any(id => TweakCatalog.Find(id)!.Risk == Risk.Medium);
            Assert.Equal(p.Id == "maximum", hasMedium);
        }
    }

    [Fact]
    public void Apply_runs_everything_once_and_Plan_is_then_empty()
    {
        var (svc, _, _) = Build(new FakeRegistry());
        var p = ProfileCatalog.Find("balanced")!;

        Assert.NotEmpty(svc.Plan(p));
        var r = svc.Apply(p);
        Assert.True(r.Applied > 0);
        Assert.Empty(r.Skipped);
        Assert.Empty(svc.Plan(p));
        Assert.Equal(0, svc.Apply(p).Applied); // a second run changes nothing
    }

    [Fact]
    public void One_failing_step_is_reported_and_the_rest_still_apply()
    {
        var (svc, engine, _) = Build(new RefusingRegistry(@"Themes\Personalize")); // the transparency tweak writes here

        var r = svc.Apply(ProfileCatalog.Find("balanced")!);

        Assert.Single(r.Skipped);
        Assert.Contains("transparency", r.Skipped[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains("access denied by policy", r.Skipped[0]);
        Assert.True(engine.IsApplied("fx-menu-delay"));
    }

    [Fact]
    public void Undo_all_reverts_a_whole_profile()
    {
        var (svc, engine, reverter) = Build(new FakeRegistry());
        svc.Apply(ProfileCatalog.Find("balanced")!);
        Assert.True(engine.IsApplied("fx-transparency"));

        reverter.RevertAll();

        Assert.False(engine.IsApplied("fx-transparency"));
        Assert.Empty(_journal.Entries);
    }
}
