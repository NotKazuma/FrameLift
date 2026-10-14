namespace FrameLift.Core;

/// <summary>A named bundle of system tweaks, picked by id.</summary>
public sealed record Profile(string Id, string Name, string Tagline, IReadOnlyList<string> TweakIds);

public sealed record PlannedStep(string Text, bool Risky);

public static class ProfileCatalog
{
    private static readonly string[] Light =
    {
        "fx-transparency", "fx-menu-delay", "game-mode", "game-dvr",
    };

    private static readonly string[] Medium = Light.Concat(new[]
    {
        "fx-window-anim", "fx-taskbar-anim", "fx-peek", "fx-shadows", "bg-apps", "bg-startup-delay",
        "svc-telemetry", "svc-errorreport",
    }).ToArray();

    private static readonly string[] Heavy = Medium.Concat(new[]
    {
        "power-high", "svc-xbox", "svc-misc",
    }).ToArray();

    // Everything, including the two medium-risk service tweaks.
    private static readonly string[] Maximum = Heavy.Concat(new[]
    {
        "svc-sysmain", "svc-search",
    }).ToArray();

    public static readonly IReadOnlyList<Profile> All = new Profile[]
    {
        new("light", "Light", "Cosmetic savings only. Nothing you'd notice is lost.", Light),
        new("balanced", "Balanced", "Turns off animations and background noise.", Medium),
        new("heavy", "Heavy", "Adds the High Performance power plan and more services off. Runs warmer.", Heavy),
        new("maximum", "Maximum", "Everything on, including the two medium-risk service tweaks.", Maximum),
    };

    public static Profile? Find(string id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>Works out and applies what a profile would do on this PC. Every step goes through the normal journaled engines.</summary>
public sealed class ProfileService
{
    private readonly TweakEngine _tweaks;

    public ProfileService(TweakEngine tweaks) => _tweaks = tweaks;

    public IReadOnlyList<PlannedStep> Plan(Profile p)
    {
        var steps = new List<PlannedStep>();
        foreach (var id in p.TweakIds)
        {
            var t = TweakCatalog.Find(id);
            if (t is null || _tweaks.IsApplied(id) || !_tweaks.IsApplicable(t)) continue;
            steps.Add(new PlannedStep(t.Title + (t.NeedsSignOut ? " (after sign-out)" : ""), t.Risk == Risk.Medium));
        }
        return steps;
    }

    public sealed record Result(int Applied, IReadOnlyList<string> Skipped);

    /// <summary>Applies everything in the plan. A failing step is reported and skipped, never allowed to stop the rest.</summary>
    public Result Apply(Profile p)
    {
        int applied = 0;
        var skipped = new List<string>();
        foreach (var id in p.TweakIds)
        {
            var t = TweakCatalog.Find(id);
            if (t is null || _tweaks.IsApplied(id) || !_tweaks.IsApplicable(t)) continue;
            try { if (_tweaks.Apply(t) > 0) applied++; }
            catch (Exception e) { skipped.Add($"{t.Title}: {e.Message}"); }
        }
        return new Result(applied, skipped);
    }
}
