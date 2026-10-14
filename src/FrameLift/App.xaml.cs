using System.Diagnostics;
using System.Windows;
using FrameLift.Core;

namespace FrameLift;

/// <summary>Everything the UI needs, wired once.</summary>
public sealed class AppServices
{
    public IRegistry Registry { get; } = new SystemRegistry();
    public IServiceRunner Services { get; } = new SystemServiceRunner();
    public IPower Power { get; } = new PowercfgPower();
    public IProcessOps Procs { get; } = new SystemProcessOps();
    public IMemoryApi Memory { get; } = new SystemMemoryApi();
    /// <summary>Probed on first use (it spawns nvidia-smi), which only ever happens on a worker thread.</summary>
    public IGpuPower Gpu { get; } = new LazyGpuPower();
    public TempReader Temps { get; } = new();
    public ISystemInfo SysInfo { get; } = new SystemInfo();
    public Journal Journal { get; }
    public Reverter Reverter { get; }
    public TweakEngine Tweaks { get; }
    public HeatGuard Heat { get; }
    public GameBoost Boost { get; }
    public ProfileService Profiles { get; }
    public CleanupService Cleanup { get; } = new();
    public StartupManager Startup { get; }
    public SystemStats Stats { get; } = new();
    public IReadOnlyList<IHealthCheck> HealthChecks { get; }
    public HealthFixer HealthFixer { get; }
    public AppConfig Config { get; private set; }

    public AppServices()
    {
        Config = AppConfig.Load(AppConfig.DefaultPath);
        Journal = new Journal(Journal.DefaultPath);
        Reverter = new Reverter(Journal, Registry, Services, Power, Procs, Gpu);
        Tweaks = new TweakEngine(Registry, Services, Power, Journal, Reverter);
        Heat = new HeatGuard(Power, Gpu, Procs, Journal, Reverter, () => Config);
        Boost = new GameBoost(Procs, Power, Memory, Journal, Reverter, () => Config, Heat, tuner: new EfficiencyTuner(Power, Gpu, Journal));
        Profiles = new ProfileService(Tweaks);
        Startup = new StartupManager(Registry, Journal, Reverter);
        HealthFixer = new HealthFixer(Tweaks);
        HealthChecks = new IHealthCheck[]
        {
            new PowerPlanCheck(Power, Tweaks.IsApplied),
            new CpuIdleCheck(Power, Tweaks.IsApplied),
            new GameModeCheck(Registry, Tweaks.IsApplied),
            new GameDvrCheck(Registry, Tweaks.IsApplied),
            new StartupCheck(Startup.List, new ProcessStartupWeigher()),
            new DiskSpaceCheck(SysInfo),
            new MemoryCheck(SysInfo),
            new JunkCheck(() => CleanupCatalog.Build().Where(c => !c.RecycleBin).Sum(c => Cleanup.Scan(c).Bytes)),
            new ThermalCheck(Temps.Poll, () => Temps.Error, () => Config.Thermal),
        };
    }

    public void SaveConfig() => Config.Save(AppConfig.DefaultPath);
}

public partial class App : Application
{
    private const string ShowEventName = "FrameLift.ShowWindow";

    public static AppServices Svc { get; private set; } = null!;
    public static TrayIcon? Tray { get; private set; }
    /// <summary>True once the user chose Quit (or Windows is signing out): only then may the main window really close.</summary>
    public static bool Quitting { get; private set; }

    private Mutex? _single;
    private EventWaitHandle? _showSignal;
    private readonly ManualResetEvent _quitSignal = new(false);
    private Process? _watchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The window can be hidden to the tray, so closing it must not end the app: only Quit does.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        // Second copy of this exe: wait for the main app to exit for any reason (even a hard kill),
        // then undo any Game Boost changes it left behind. Applied tweaks are intentional and stay.
        if (e.Args.Length >= 3 && e.Args[0] == "--watchdog")
        {
            RunWatchdog(int.Parse(e.Args[1]), e.Args[2]);
            Shutdown();
            return;
        }

        _single = new Mutex(true, "FrameLift.SingleInstance", out var first);
        if (!first)
        {
            // Already running (probably in the tray): ask that copy to show its window, then leave quietly.
            try { using var h = EventWaitHandle.OpenExisting(ShowEventName); h.Set(); }
            catch (WaitHandleCannotBeOpenedException) { MessageBox.Show("FrameLift is already running.", "FrameLift", MessageBoxButton.OK, MessageBoxImage.Information); }
            Shutdown();
            return;
        }

        Svc = new AppServices();
        // A crash or kill last time may have left Game Boost changes behind: undo them now.
        if (Svc.Journal.HasOwner(GameBoost.Owner)) Svc.Reverter.RevertOwner(GameBoost.Owner);

        _watchdog = StartWatchdog();
        SessionEnding += (_, _) => Quitting = true;

        var win = new MainWindow();
        MainWindow = win;
        Tray = new TrayIcon(open: win.ShowFromTray, toggleBoost: win.ToggleBoostFromTray, quit: Quit);
        StartShowListener();

        // Started by the scheduled task at sign-in: no window, just the tray icon and automatic Game Boost.
        if (e.Args.Contains("--tray")) win.StartHidden();
        else win.Show();
    }

    /// <summary>Really exits: OnExit stops Boost, removes the tray icon and closes the sensors.</summary>
    public static void Quit()
    {
        Quitting = true;
        Current.Shutdown();
    }

    private void StartShowListener()
    {
        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        var t = new Thread(() =>
        {
            // Wakes on a second launch (index 0) and ends when the app quits (index 1).
            while (WaitHandle.WaitAny(new WaitHandle[] { _showSignal, _quitSignal }) == 0)
                Dispatcher.BeginInvoke(() => (MainWindow as MainWindow)?.ShowFromTray());
        }) { IsBackground = true, Name = "FrameLift show listener" };
        t.Start();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _quitSignal.Set();
        bool boostLeftBehind = false;
        try
        {
            if (Svc is not null && Svc.Boost.Active) Svc.Boost.Stop();
            boostLeftBehind = Svc is not null && Svc.Journal.HasOwner(GameBoost.Owner);
        }
        catch (Exception ex)
        {
            boostLeftBehind = true;
            Debug.WriteLine("Stopping Game Boost on exit failed: " + ex.Message);
        }
        try { Tray?.Dispose(); } catch (Exception ex) { Debug.WriteLine("Removing the tray icon failed: " + ex.Message); }
        try { Svc?.Temps.Dispose(); } catch (Exception ex) { Debug.WriteLine("Closing the sensors failed: " + ex.Message); }
        // If anything of Boost is still journaled, the watchdog is the safety net: let it run and finish the undo.
        if (!boostLeftBehind)
        {
            try { _watchdog?.Kill(); } catch (Exception ex) { Debug.WriteLine("Stopping the watchdog failed: " + ex.Message); }
        }
        base.OnExit(e);
    }

    private static Process? StartWatchdog()
    {
        var exe = Environment.ProcessPath;
        if (exe is null) return null;
        var psi = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--watchdog");
        psi.ArgumentList.Add(Environment.ProcessId.ToString());
        psi.ArgumentList.Add(Journal.DefaultPath);
        try { return Process.Start(psi); } catch (Exception) { return null; }
    }

    private static void RunWatchdog(int parentPid, string journalPath)
    {
        try
        {
            using var parent = Process.GetProcessById(parentPid);
            parent.WaitForExit();
        }
        catch (ArgumentException) { /* parent already gone */ }

        var journal = new Journal(journalPath);
        // No probing here: recovery only needs to set back a wattage that was journaled earlier.
        new Reverter(journal, new SystemRegistry(), new SystemServiceRunner(), new PowercfgPower(), new SystemProcessOps(),
                NvidiaGpuPower.ForRecovery())
            .RevertOwner(GameBoost.Owner);
    }
}
