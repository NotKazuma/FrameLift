namespace FrameLift.Core;

public enum Risk { Low, Medium }

public abstract record Change;
public sealed record RegChange(string Path, string Name, string Kind, string Data) : Change;
/// <summary>Sets a service's start type (2 auto, 3 manual, 4 disabled) and stops it if running.</summary>
public sealed record ServiceChange(string Service, int StartType) : Change;
public sealed record PowerChange(Guid Template) : Change;

public sealed record TweakDef(
    string Id, string Category, string Title, string Description, Risk Risk, bool NeedsSignOut, params Change[] Changes);

public static class TweakCatalog
{
    private const string Explorer = @"HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer";
    private const string Svc = @"HKLM\SYSTEM\CurrentControlSet\Services\";

    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");

    public static readonly IReadOnlyList<TweakDef> All = new TweakDef[]
    {
        // Visual effects (applied per user, take effect after sign-out)
        new("fx-transparency", "Visual effects", "Turn off transparency",
            "Removes the glass blur on the taskbar, Start and windows. Saves GPU and memory.", Risk.Low, true,
            new RegChange(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "DWord", "0")),
        new("fx-window-anim", "Visual effects", "Turn off window animations",
            "Windows minimise and maximise instantly instead of animating.", Risk.Low, true,
            new RegChange(@"HKCU\Control Panel\Desktop\WindowMetrics", "MinAnimate", "String", "0")),
        new("fx-taskbar-anim", "Visual effects", "Turn off taskbar animations",
            "Taskbar buttons stop sliding and fading.", Risk.Low, true,
            new RegChange(Explorer + @"\Advanced", "TaskbarAnimations", "DWord", "0")),
        new("fx-menu-delay", "Visual effects", "Instant menus",
            "Removes the short delay before menus open.", Risk.Low, true,
            new RegChange(@"HKCU\Control Panel\Desktop", "MenuShowDelay", "String", "0")),
        new("fx-peek", "Visual effects", "Turn off Aero Peek",
            "Stops the desktop preview when hovering the taskbar corner.", Risk.Low, true,
            new RegChange(@"HKCU\Software\Microsoft\Windows\DWM", "EnableAeroPeek", "DWord", "0")),
        new("fx-shadows", "Visual effects", "Turn off icon label shadows",
            "Removes the shadow behind desktop icon text.", Risk.Low, true,
            new RegChange(Explorer + @"\Advanced", "ListviewShadow", "DWord", "0")),

        // Gaming
        new("game-mode", "Gaming", "Turn on Windows Game Mode",
            "Lets Windows prioritise the game and hold back background updates while you play.", Risk.Low, false,
            new RegChange(@"HKCU\Software\Microsoft\GameBar", "AutoGameModeEnabled", "DWord", "1"),
            new RegChange(@"HKCU\Software\Microsoft\GameBar", "AllowAutoGameMode", "DWord", "1")),
        new("game-dvr", "Gaming", "Turn off background game recording",
            "Stops Xbox Game Bar from recording in the background, which costs FPS on weak PCs.", Risk.Low, false,
            new RegChange(@"HKCU\System\GameConfigStore", "GameDVR_Enabled", "DWord", "0"),
            new RegChange(@"HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled", "DWord", "0")),
        new("power-high", "Gaming", "High Performance power plan",
            "Keeps the CPU from down-clocking mid game. Runs hotter, so watch temperatures on a laptop.", Risk.Low, false,
            new PowerChange(HighPerformance)),

        // Background
        new("bg-apps", "Background", "Stop apps running in the background",
            "Store apps no longer run, sync and update while closed.", Risk.Low, false,
            new RegChange(@"HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications", "GlobalUserDisabled", "DWord", "1")),
        new("bg-startup-delay", "Background", "Remove desktop startup delay",
            "Windows loads your startup apps right away instead of waiting a few seconds.", Risk.Low, true,
            new RegChange(Explorer + @"\Serialize", "StartupDelayInMSec", "DWord", "0")),

        // Services
        new("svc-telemetry", "Services", "Disable telemetry service (DiagTrack)",
            "Stops the data-collection service that uses disk and CPU in the background.", Risk.Low, false,
            new ServiceChange("DiagTrack", 4)),
        new("svc-errorreport", "Services", "Disable Windows Error Reporting",
            "Stops crash reports from being collected and sent.", Risk.Low, false,
            new ServiceChange("WerSvc", 4)),
        new("svc-xbox", "Services", "Set Xbox services to manual",
            "They only start when a game asks for them. Xbox sign-in still works.", Risk.Low, false,
            new ServiceChange("XblAuthManager", 3), new ServiceChange("XblGameSave", 3),
            new ServiceChange("XboxNetApiSvc", 3), new ServiceChange("XboxGipSvc", 3)),
        new("svc-misc", "Services", "Disable Fax, Maps and Retail Demo services",
            "Rarely used services that sit in memory for nothing.", Risk.Low, false,
            new ServiceChange("Fax", 4), new ServiceChange("MapsBroker", 4), new ServiceChange("RetailDemo", 4)),
        new("svc-sysmain", "Services", "Disable SysMain (Superfetch)",
            "Frees RAM and disk activity on an SSD. Leave this off if the game is installed on a hard drive, where it helps loading.", Risk.Medium, false,
            new ServiceChange("SysMain", 4)),
        new("svc-search", "Services", "Set Windows Search to manual",
            "Stops constant file indexing. Start menu and Explorer search become slower.", Risk.Medium, false,
            new ServiceChange("WSearch", 3)),
    };

    public static TweakDef? Find(string id) => All.FirstOrDefault(t => t.Id == id);

    /// <summary>Services that must never be changed by any tweak. Enforced by the engine, not just by the catalog.</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "WinDefend", "wscsvc", "MpsSvc", "BFE", "EventLog", "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM", "SamSs",
        "Dhcp", "Dnscache", "nsi", "Winmgmt", "PlugPlay", "Power", "ProfSvc", "UserManager", "TrustedInstaller",
        "wuauserv", "CryptSvc", "AudioSrv", "Audiosrv", "AudioEndpointBuilder", "Themes", "SENS", "gpsvc",
    };

    public static string ServicePath(string service) => Svc + service;
}

/// <summary>Everything a tweak would do, in plain words, shown before the user applies it.</summary>
public static class TweakPreview
{
    public static IEnumerable<string> Describe(TweakDef t, IRegistry reg)
    {
        foreach (var c in t.Changes)
        {
            switch (c)
            {
                case RegChange r:
                    var cur = reg.Read(r.Path, r.Name);
                    yield return $"{r.Path}\\{r.Name}: {(cur.Exists ? cur.Data : "(not set)")} -> {r.Data}";
                    break;
                case ServiceChange s:
                    var st = reg.Read(TweakCatalog.ServicePath(s.Service), "Start");
                    yield return st.Exists
                        ? $"Service {s.Service}: start type {st.Data} -> {s.StartType}"
                        : $"Service {s.Service}: not installed, skipped";
                    break;
                case PowerChange:
                    yield return "Active power plan -> High Performance";
                    break;
            }
        }
    }
}
