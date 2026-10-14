using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using FrameLift.Core;

namespace FrameLift;

public partial class MainWindow : Window
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    private static AppServices S => App.Svc;

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly GameDetector _detector;
    private readonly Dictionary<string, CheckBox> _tweakSwitches = new();
    private readonly List<(CleanupCategory Cat, CheckBox Box, TextBlock Size)> _cleanRows = new();
    private string _tweakTab = "";
    private int _ticks;
    private bool _autoStarted, _busy, _loading = true, _polling;
    private readonly Dictionary<string, HealthRow> _healthRows = new();
    private int _healthRun;
    private DateTime _lastHealth = DateTime.MinValue;

    private sealed record HealthRow(Border Card, Ellipse Dot, TextBlock Message, Button Fix);

    public MainWindow()
    {
        InitializeComponent();
        _detector = new GameDetector(S.Config);
        SourceInitialized += (_, _) => UseDarkTitleBar();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => _timer.Stop();
    }

    /// <summary>The window's close button only hides it: FrameLift keeps watching for games from the tray. Quit is in the tray menu.</summary>
    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (App.Quitting) return;
        e.Cancel = true;
        HideToTray();
    }

    public void HideToTray()
    {
        Hide();
        App.Tray?.ShowFirstHideHint(S.Config, S.SaveConfig);
    }

    private bool _parked;

    /// <summary>Starts with no visible window (autostart): the window is created and shown once off-screen so its timer and
    /// automatic Game Boost run, then hidden. It is moved to the screen centre when first opened from the tray.</summary>
    public void StartHidden()
    {
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = Top = -20000;
        ShowInTaskbar = false;
        ShowActivated = false;
        _parked = true;
        Show();
        Hide();
    }

    public void ShowFromTray()
    {
        if (_parked)
        {
            var wa = SystemParameters.WorkArea;
            Left = wa.Left + Math.Max(0, (wa.Width - Width) / 2);
            Top = wa.Top + Math.Max(0, (wa.Height - Height) / 2);
            ShowActivated = true;
            _parked = false;
        }
        ShowInTaskbar = true;
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true; Topmost = false; // bring it in front of whatever is on top
    }

    public bool BoostIsOn => S.Boost.Active;

    /// <summary>Same as pressing the Boost button, for the tray menu.</summary>
    public void ToggleBoostFromTray() => Boost_Click(this, new RoutedEventArgs());

    private void UseDarkTitleBar()
    {
        int on = 1;
        var h = new WindowInteropHelper(this).Handle;
        // 20 is DWMWA_USE_IMMERSIVE_DARK_MODE on current Windows 10/11; 19 on early 20H1 builds.
        if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref on, sizeof(int));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        AutoBoostCheck.IsChecked = S.Config.AutoBoost;
        OptEfficiency.IsChecked = S.Config.BoostEfficiency;
        OptPower.IsChecked = S.Config.BoostSwitchPowerPlan;
        OptMemory.IsChecked = S.Config.BoostPurgeMemory;
        OptHeat.IsChecked = S.Config.ThermalEaseOff;
        OptSuspend.IsChecked = S.Config.ThermalSuspendAtCritical;
        IgnoreBox.Text = string.Join(Environment.NewLine, S.Config.Ignored);

        BuildTweaks();
        BuildCleanup();
        BuildStartup();
        RefreshUndoText();
        UpdateBoostUi();
        _loading = false;

        _timer.Tick += OnTick;
        _timer.Start();
        OnTick(null, EventArgs.Empty);
        _ = QuickScanAsync();
    }

    // ---- navigation -------------------------------------------------------------------------------------

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (PageDash is null) return; // fires during InitializeComponent
        PageDash.Visibility = NavDash.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageHealth.Visibility = NavHealth.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavHealth.IsChecked == true && (DateTime.UtcNow - _lastHealth).TotalSeconds > 30) _ = RunHealthAsync();
        PageProfiles.Visibility = NavProfiles.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavProfiles.IsChecked == true) BuildProfiles();
        PageCleanup.Visibility = NavCleanup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageTweaks.Visibility = NavTweaks.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageStartup.Visibility = NavStartup.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PageSettings.Visibility = NavSettings.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        if (NavStartup.IsChecked == true) BuildStartup();
        if (NavSettings.IsChecked == true) RefreshUndoText();
    }

    private void GoCleanup_Click(object sender, RoutedEventArgs e) => NavCleanup.IsChecked = true;

    // ---- timer: stats, auto detection, boost upkeep ----------------------------------------------------

    private async void OnTick(object? sender, EventArgs e)
    {
        _ticks++;
        CpuText.Text = S.Stats.CpuPercent() + "%";
        CpuBar.Value = int.Parse(CpuText.Text.TrimEnd('%'));
        var (rp, used, total) = S.Stats.Ram();
        RamText.Text = rp + "%"; RamBar.Value = rp;
        RamDetail.Text = $"{used:0.0} of {total:0.0} GB in use";
        var (dp, free, dtotal) = S.Stats.SystemDrive();
        DiskText.Text = dp + "%"; DiskBar.Value = dp;
        DiskDetail.Text = $"{free:0} GB free of {dtotal:0} GB";

        // Sensors are only read while someone can see them or Boost needs them, so a hidden idle app costs nothing.
        if (_ticks % 2 == 0 && !_polling && (IsVisible || S.Boost.Active)) _ = PollTempsAsync();

        if (_busy) return;

        if (S.Boost.Active)
        {
            if (_autoStarted && !S.Boost.GameStillRunning())
            {
                await RunBoost(() => S.Boost.Stop());
                _autoStarted = false;
            }
            else if (_ticks % 5 == 0)
            {
                _busy = true;
                try { await Task.Run(() => S.Boost.Tick()); } finally { _busy = false; }
                UpdateBoostUi();
            }
        }
        else if (S.Config.AutoBoost && _ticks % 2 == 0)
        {
            var game = _detector.Choose(WindowProbe.FullscreenForeground(), DateTime.UtcNow);
            if (game is not null)
            {
                await RunBoost(() => S.Boost.Start(game));
                _autoStarted = S.Boost.Active;
            }
        }
    }

    private async Task RunBoost(Action action)
    {
        _busy = true;
        try { await Task.Run(action); }
        finally { _busy = false; }
        UpdateBoostUi();
    }

    // ---- temperature and heat protection ----------------------------------------------------------------

    /// <summary>Reads the sensors off the UI thread. While boosting, the same reading drives the heat guard.</summary>
    private async Task PollTempsAsync()
    {
        _polling = true;
        try
        {
            var reading = await Task.Run(S.Temps.Poll);
            ShowTemps(reading);
            if (S.Boost.Active) await Task.Run(() => S.Heat.Step(reading));
            ShowHeatStatus(reading);
        }
        catch (Exception ex)
        {
            HeatText.Text = "Temperature check failed: " + ex.Message;
        }
        finally { _polling = false; }
    }

    private Brush TempBrush(int? t, int warm, int hot) =>
        (Brush)FindResource(t is null ? "Muted" : t >= hot ? "Bad" : t >= warm ? "Warn" : "Text");

    private void ShowTemps(TempReading r)
    {
        var l = S.Config.Thermal;
        CpuTempText.Text = r.Cpu is int c ? c + "°C" : "--";
        GpuTempText.Text = r.Gpu is int g ? g + "°C" : "--";
        CpuTempText.Foreground = TempBrush(r.Cpu, l.CpuWarm, l.CpuHot);
        GpuTempText.Foreground = TempBrush(r.Gpu, l.GpuWarm, l.GpuHot);
    }

    private void ShowHeatStatus(TempReading r)
    {
        var h = S.Heat;
        if (r.Blind && S.Temps.Error is string err) { HeatTitle.Text = "Heat protection is off"; HeatText.Text = err; return; }
        if (!S.Boost.Active)
        {
            HeatTitle.Text = "Heat protection";
            HeatText.Text = S.Config.ThermalEaseOff
                ? "Eases off during Game Boost before the PC overheats."
                : "Turned off in Safety & undo.";
            return;
        }
        if (h.Note is string note) { HeatTitle.Text = "Heat protection"; HeatText.Text = note; return; }
        var gpu = h.GpuPercent < 100 ? $", GPU power {h.GpuPercent}%" : "";
        switch (h.Level)
        {
            case ThermalLevel.Ok:
                HeatTitle.Text = "Heat protection: watching";
                HeatText.Text = "Temperatures are fine. CPU at full speed" + gpu + ".";
                break;
            case ThermalLevel.Critical:
                HeatTitle.Text = "Heat protection: near shutdown";
                HeatText.Text = $"CPU cut to {h.CpuCapPercent}%{gpu} until it cools. Constant heat like this usually means dust or dried thermal paste.";
                break;
            default:
                HeatTitle.Text = "Heat protection: holding";
                HeatText.Text = $"Holding about {S.Config.Thermal.CpuTarget}°C: CPU at {h.CpuCapPercent}%{gpu}.";
                break;
        }
    }

    // ---- health -----------------------------------------------------------------------------------------

    private void BuildHealthRows()
    {
        if (_healthRows.Count > 0) return;
        foreach (var check in S.HealthChecks)
        {
            var dot = new Ellipse { Width = 12, Height = 12, Fill = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 5, 14, 0) };
            var text = Texts(check.Title, "Checking...");
            var msg = (TextBlock)text.Children[1];
            var fix = new Button { Style = (Style)FindResource("PrimaryButton"), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150 };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            g.Children.Add(dot);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Grid.SetColumn(fix, 2);
            g.Children.Add(fix);
            var card = Card(g);
            System.Windows.Automation.AutomationProperties.SetAutomationId(card, "Health-" + check.Id);
            HealthHost.Children.Add(card);
            _healthRows[check.Id] = new HealthRow(card, dot, msg, fix);
        }
    }

    private Brush StatusBrush(HealthStatus s) => (Brush)FindResource(s switch
    {
        HealthStatus.Good => "Good", HealthStatus.Warning => "Warn", HealthStatus.Problem => "Bad", _ => "Muted",
    });

    private readonly Dictionary<string, HealthFinding> _findings = new();

    private void ShowFinding(HealthFinding f, IHealthCheck check)
    {
        if (!_healthRows.TryGetValue(f.Id, out var row)) return;
        _findings[f.Id] = f;
        row.Dot.Fill = StatusBrush(f.Status);
        row.Message.Text = f.Message;
        row.Fix.IsEnabled = true;
        if (row.Fix.Tag is RoutedEventHandler old) row.Fix.Click -= old; // rows are reused between checks
        row.Fix.Tag = null;
        if (f.Fix is null) { row.Fix.Visibility = Visibility.Collapsed; return; }
        row.Fix.Visibility = Visibility.Visible;
        row.Fix.Content = f.Fix.Label;
        row.Fix.Style = (Style)FindResource(f.Status == HealthStatus.Problem ? "PrimaryButton" : "GhostButton");
        RoutedEventHandler h = async (_, _) => await RunFixAsync(f.Fix, check, row.Fix);
        row.Fix.Tag = h;
        row.Fix.Click += h;
    }

    private async Task RunHealthAsync()
    {
        BuildHealthRows();
        int run = ++_healthRun;
        _lastHealth = DateTime.UtcNow;
        HealthRecheck.IsEnabled = false;
        HealthSummary.Text = "Checking your PC...";
        _findings.Clear();
        foreach (var r in _healthRows.Values) { r.Dot.Fill = (Brush)FindResource("Muted"); r.Message.Text = "Checking..."; r.Fix.Visibility = Visibility.Collapsed; }

        await Task.WhenAll(S.HealthChecks.Select(async c =>
        {
            var f = await HealthRunner.RunAsync(c);
            if (run == _healthRun) ShowFinding(f, c);
        }));
        if (run != _healthRun) return;
        SummarizeHealth();
        HealthRecheck.IsEnabled = true;
    }

    private void SummarizeHealth()
    {
        int problems = _findings.Values.Count(f => f.Status == HealthStatus.Problem);
        int warnings = _findings.Values.Count(f => f.Status == HealthStatus.Warning);
        HealthSummary.Text = problems + warnings == 0
            ? "Everything checked looks fine."
            : $"{problems} problem{(problems == 1 ? "" : "s")} and {warnings} warning{(warnings == 1 ? "" : "s")} found.";
    }

    private async void HealthRecheck_Click(object sender, RoutedEventArgs e) => await RunHealthAsync();

    private async Task RunFixAsync(HealthFix fix, IHealthCheck check, Button btn)
    {
        if (fix.Kind == FixKind.OpenPage)
        {
            if (fix.Target == "cleanup") NavCleanup.IsChecked = true;
            else if (fix.Target == "startup") NavStartup.IsChecked = true;
            return;
        }

        btn.IsEnabled = false;
        try
        {
            if (!await OfferRestorePointAsync()) return;
            await Task.Run(() => S.HealthFixer.Apply(fix));
            BuildTweaks();
            RefreshUndoText();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "That fix could not be applied: " + ex.Message, "FrameLift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally { btn.IsEnabled = true; }

        ShowFinding(await HealthRunner.RunAsync(check), check); // show what is really in effect now
        SummarizeHealth();
    }

    // ---- dashboard --------------------------------------------------------------------------------------

    private async void Boost_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (S.Boost.Active) { await RunBoost(() => S.Boost.Stop()); _autoStarted = false; }
        else { await RunBoost(() => S.Boost.Start(null)); _autoStarted = false; }
    }

    private void AutoBoost_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Config.AutoBoost = AutoBoostCheck.IsChecked == true;
        S.SaveConfig();
    }

    private void UpdateBoostUi()
    {
        var b = S.Boost;
        App.Tray?.SetBoost(b.Active, b.GameName);
        if (b.Active)
        {
            HeroTitle.Text = b.GameName is null ? "Game Boost is on" : $"Boosting {b.GameName}";
            HeroText.Text = $"{b.SuppressedCount} background apps slowed" +
                            (b.FreedMb > 0 ? $", {b.FreedMb:N0} MB of standby memory freed" : "") +
                            ". Everything goes back to normal when you stop." +
                            (b.EfficiencyNote is string note ? " " + note : "");
            BoostButton.Content = "Stop Boost";
            SidebarStatus.Text = "Boost is on";
            SidebarStatus.Foreground = (Brush)FindResource("Good");
            SidebarDetail.Text = b.GameName ?? "Manual boost";
        }
        else
        {
            HeroTitle.Text = "Game Boost is off";
            HeroText.Text = "Uses a fast power plan, puts your game first, slows background apps and frees memory. All undone when you stop.";
            BoostButton.Content = "Start Boost";
            SidebarStatus.Text = "Boost is off";
            SidebarStatus.Foreground = (Brush)FindResource("Text");
            SidebarDetail.Text = "Nothing changed on your PC";
        }
    }

    private async Task QuickScanAsync()
    {
        var results = await Task.Run(() => CleanupCatalog.Build().Where(c => !c.RecycleBin).Select(S.Cleanup.Scan).ToList());
        long bytes = results.Sum(r => r.Bytes);
        QuickCleanText.Text = bytes > 0
            ? $"About {Format(bytes)} of junk files can be removed."
            : "Your PC is already clean.";
    }

    // ---- cleanup ----------------------------------------------------------------------------------------

    private void BuildCleanup()
    {
        CleanupHost.Children.Clear();
        _cleanRows.Clear();
        foreach (var c in CleanupCatalog.Build())
        {
            var box = new CheckBox { IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            var size = new TextBlock { Text = "Not scanned", Foreground = (Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center };
            var row = Card(new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = GridLength.Auto },
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            });
            var g = (Grid)((Border)row).Child;
            g.Children.Add(box);
            var text = Texts(c.Title, c.Description);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            Grid.SetColumn(size, 2);
            g.Children.Add(size);
            CleanupHost.Children.Add(row);
            _cleanRows.Add((c, box, size));
        }
    }

    private async void Scan_Click(object sender, RoutedEventArgs e)
    {
        ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        CleanStatus.Text = "Scanning...";
        long total = 0;
        foreach (var (cat, _, size) in _cleanRows)
        {
            var r = await Task.Run(() => S.Cleanup.Scan(cat));
            size.Text = Format(r.Bytes);
            total += r.Bytes;
        }
        CleanStatus.Text = $"{Format(total)} found";
        ScanButton.IsEnabled = CleanButton.IsEnabled = true;
    }

    private async void Clean_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _cleanRows.Where(r => r.Box.IsChecked == true).ToList();
        if (chosen.Count == 0) return;
        ScanButton.IsEnabled = CleanButton.IsEnabled = false;
        CleanStatus.Text = "Cleaning...";
        long freed = 0;
        foreach (var (cat, _, size) in chosen)
        {
            freed += await Task.Run(() => S.Cleanup.Clean(cat));
            size.Text = Format((await Task.Run(() => S.Cleanup.Scan(cat))).Bytes);
        }
        CleanStatus.Text = $"Freed {Format(freed)}. Files that are in use were skipped.";
        ScanButton.IsEnabled = CleanButton.IsEnabled = true;
        _ = QuickScanAsync();
    }

    // ---- tweaks -----------------------------------------------------------------------------------------

    private void BuildTweaks()
    {
        TweaksHost.Children.Clear();
        TweakTabs.Children.Clear();
        _tweakSwitches.Clear();
        var groups = TweakCatalog.All.GroupBy(t => t.Category).ToList();
        if (!groups.Any(g => g.Key == _tweakTab)) _tweakTab = groups[0].Key;

        // One category at a time, so the page never becomes a wall of switches.
        foreach (var gr in groups)
        {
            int on = gr.Count(t => S.Tweaks.IsApplied(t.Id));
            var tab = new RadioButton
            {
                Style = (Style)FindResource("PillTab"), GroupName = "tweakTabs", IsChecked = gr.Key == _tweakTab,
                Content = on > 0 ? $"{gr.Key}  ({on} on)" : gr.Key,
            };
            var key = gr.Key;
            tab.Checked += (_, _) => { if (_tweakTab != key) { _tweakTab = key; BuildTweaks(); } };
            TweakTabs.Children.Add(tab);
        }

        foreach (var group in groups.Where(g => g.Key == _tweakTab))
        {
            foreach (var t in group)
            {
                var sw = new CheckBox
                {
                    Style = (Style)FindResource("Switch"), IsChecked = S.Tweaks.IsApplied(t.Id), Tag = t,
                };
                sw.Click += Tweak_Click;
                _tweakSwitches[t.Id] = sw;

                var desc = t.Description + (t.NeedsSignOut ? " Takes effect after you sign out." : "");
                var text = Texts(t.Title, desc);
                if (t.Risk == Risk.Medium)
                {
                    ((StackPanel)text).Children.Insert(1, new TextBlock
                    {
                        Text = "MEDIUM RISK, read before enabling", FontSize = 10.5, FontWeight = FontWeights.SemiBold,
                        Foreground = (Brush)FindResource("Warn"), Margin = new Thickness(0, 2, 0, 1),
                    });
                }

                var g = new Grid
                {
                    ColumnDefinitions =
                    {
                        new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                        new ColumnDefinition { Width = GridLength.Auto },
                    },
                };
                g.Children.Add(text);
                Grid.SetColumn(sw, 1);
                g.Children.Add(sw);
                var card = Card(g);
                card.ToolTip = MakeTip(string.Join(Environment.NewLine, TweakPreview.Describe(t, S.Registry)));
                TweaksHost.Children.Add(card);
            }
        }
    }

    private async void Tweak_Click(object sender, RoutedEventArgs e)
    {
        var sw = (CheckBox)sender;
        var t = (TweakDef)sw.Tag;
        sw.IsEnabled = false;
        try
        {
            if (sw.IsChecked == true)
            {
                if (!await OfferRestorePointAsync()) { sw.IsChecked = false; return; }
                await Task.Run(() => S.Tweaks.Apply(t));
            }
            else
            {
                await Task.Run(() => S.Tweaks.Revert(t.Id));
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, "That change could not be made: " + ex.Message, "FrameLift", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            sw.IsChecked = S.Tweaks.IsApplied(t.Id); // show what is really in effect
            sw.IsEnabled = true;
            RefreshUndoText();
        }
    }

    /// <summary>Before the first change ever made, offer a Windows restore point. Returns false if the user cancels.</summary>
    private async Task<bool> OfferRestorePointAsync()
    {
        if (S.Config.RestorePointOffered) return true;
        var answer = MessageBox.Show(this,
            "Before the first change, FrameLift can create a Windows restore point so you can roll your whole PC back if needed.\n\nCreate one now?",
            "FrameLift", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (answer == MessageBoxResult.Cancel) return false;
        S.Config.RestorePointOffered = true;
        S.SaveConfig();
        if (answer == MessageBoxResult.Yes)
        {
            var problem = await Task.Run(() => RestorePoint.Create("FrameLift - before changes"));
            if (problem is not null)
                MessageBox.Show(this, problem + "\n\nYour changes can still be undone from the Safety & undo page.", "FrameLift",
                    MessageBoxButton.OK, MessageBoxImage.Information);
        }
        return true;
    }

    // ---- profiles ---------------------------------------------------------------------------------------


    private void BuildProfiles()
    {
        ProfilesHost.Children.Clear();
        foreach (var p in ProfileCatalog.All)
        {
            var plan = S.Profiles.Plan(p);
            var text = Texts(p.Name, p.Tagline);
            ((TextBlock)((StackPanel)text).Children[0]).FontSize = 17;
            ((StackPanel)text).Children.Add(new TextBlock
            {
                Text = plan.Count == 0 ? "Everything in this profile is already on." : $"{plan.Count} change{(plan.Count == 1 ? "" : "s")} to make",
                Foreground = (Brush)FindResource(plan.Count == 0 ? "Good" : "Accent"), FontSize = 12.5, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            });
            var btn = new Button
            {
                Content = plan.Count == 0 ? "Applied" : "Review and apply", Style = (Style)FindResource(plan.Count == 0 ? "GhostButton" : "PrimaryButton"),
                IsEnabled = plan.Count > 0, VerticalAlignment = VerticalAlignment.Center, MinWidth = 150,
            };
            var profile = p;
            btn.Click += async (_, _) => await ApplyProfile(profile, btn);
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            g.Children.Add(text);
            Grid.SetColumn(btn, 1);
            g.Children.Add(btn);
            var card = Card(g);
            card.Padding = new Thickness(22, 18, 22, 18);
            ProfilesHost.Children.Add(card);
        }
    }

    private async Task ApplyProfile(Profile p, Button btn)
    {
        var plan = S.Profiles.Plan(p);
        if (plan.Count == 0) return;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"{p.Name} will make these {plan.Count} changes:").AppendLine();
        foreach (var s in plan.Take(25)) sb.AppendLine((s.Risky ? "  ! " : "  - ") + s.Text);
        if (plan.Count > 25) sb.AppendLine($"  ...and {plan.Count - 25} more");
        if (plan.Any(s => s.Risky)) sb.AppendLine().AppendLine("Items marked ! are medium risk.");
        sb.AppendLine().AppendLine("Everything can be undone from the Safety page. Continue?");
        if (MessageBox.Show(this, sb.ToString(), "FrameLift", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        if (!await OfferRestorePointAsync()) return;

        btn.IsEnabled = false;
        var result = await Task.Run(() => S.Profiles.Apply(p));
        var msg = $"{result.Applied} change{(result.Applied == 1 ? "" : "s")} applied.";
        if (result.Skipped.Count > 0)
            msg += $"\n\n{result.Skipped.Count} skipped:\n" + string.Join("\n", result.Skipped.Take(8).Select(x => "  - " + x));
        MessageBox.Show(this, msg, "FrameLift", MessageBoxButton.OK, MessageBoxImage.Information);
        BuildProfiles();
        RefreshUndoText();
    }

    // ---- startup ----------------------------------------------------------------------------------------

    private void BuildStartup()
    {
        StartupHost.Children.Clear();
        var items = S.Startup.List();
        if (items.Count == 0)
        {
            StartupHost.Children.Add(new TextBlock { Text = "No startup apps found.", Style = (Style)FindResource("MutedText") });
            return;
        }
        foreach (var item in items)
        {
            var sw = new CheckBox { Style = (Style)FindResource("Switch"), IsChecked = item.Enabled, Tag = item };
            sw.Click += (_, _) =>
            {
                try { S.Startup.SetEnabled(item, sw.IsChecked == true); }
                catch (Exception ex)
                {
                    sw.IsChecked = !sw.IsChecked;
                    MessageBox.Show(this, "Could not change this startup app: " + ex.Message, "FrameLift",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                RefreshUndoText();
            };
            var g = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto },
                },
            };
            var text = Texts(item.Name, item.Source + "  |  " + item.Command);
            ((TextBlock)((StackPanel)text).Children[1]).TextTrimming = TextTrimming.CharacterEllipsis;
            ((TextBlock)((StackPanel)text).Children[1]).TextWrapping = TextWrapping.NoWrap;
            g.Children.Add(text);
            Grid.SetColumn(sw, 1);
            g.Children.Add(sw);
            StartupHost.Children.Add(Card(g));
        }
    }

    // ---- safety -----------------------------------------------------------------------------------------

    private void RefreshUndoText()
    {
        int tweaks = S.Journal.Owners().Count(o => o.StartsWith("tweak:") || o.StartsWith("startup:") || o.StartsWith("game:"));
        UndoText.Text = tweaks == 0
            ? "No changes are in effect. Your PC is as Windows left it."
            : $"{tweaks} change{(tweaks == 1 ? " is" : "s are")} in effect. This puts every one of them back exactly as it was.";
    }

    private async void UndoAll_Click(object sender, RoutedEventArgs e)
    {
        if (MessageBox.Show(this, "Undo every change FrameLift has made?", "FrameLift",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await Task.Run(() =>
        {
            if (S.Boost.Active) S.Boost.Stop();
            S.Reverter.RevertAll();
        });
        _autoStarted = false;
        BuildTweaks();
        BuildStartup();
        UpdateBoostUi();
        RefreshUndoText();
    }

    private async void RestorePoint_Click(object sender, RoutedEventArgs e)
    {
        RestoreStatus.Text = "Creating restore point...";
        var problem = await Task.Run(() => RestorePoint.Create("FrameLift"));
        RestoreStatus.Text = problem ?? "Restore point created.";
        if (problem is null) { S.Config.RestorePointOffered = true; S.SaveConfig(); }
    }

    private void Option_Click(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        S.Config.BoostEfficiency = OptEfficiency.IsChecked == true;
        S.Config.BoostSwitchPowerPlan = OptPower.IsChecked == true;
        S.Config.BoostPurgeMemory = OptMemory.IsChecked == true;
        S.Config.ThermalEaseOff = OptHeat.IsChecked == true;
        S.Config.ThermalSuspendAtCritical = OptSuspend.IsChecked == true;
        S.SaveConfig();
    }

    private void Ignore_LostFocus(object sender, RoutedEventArgs e)
    {
        S.Config.Ignored = IgnoreBox.Text
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(GameDetector.Normalize).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        S.SaveConfig();
    }

    // ---- small UI helpers -------------------------------------------------------------------------------

    private Border Card(UIElement child)
    {
        var b = new Border
        {
            Background = (Brush)FindResource("Card"),
            CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 14, 18, 14), Margin = new Thickness(0, 0, 0, 8),
            Child = child,
        };
        b.MouseEnter += (_, _) => b.Background = (Brush)FindResource("CardHover");
        b.MouseLeave += (_, _) => b.Background = (Brush)FindResource("Card");
        return b;
    }

    /// <summary>A dark tooltip that wraps at a sensible width, so long text never stretches across the screen.</summary>
    private ToolTip MakeTip(string text) => new()
    {
        Background = (Brush)FindResource("Panel"), BorderBrush = (Brush)FindResource("Line"), Padding = new Thickness(12, 9, 12, 9),
        Content = new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460, FontSize = 12 },
    };

    private StackPanel Texts(string title, string detail)
    {
        var sp = new StackPanel { Margin = new Thickness(0, 0, 18, 0), VerticalAlignment = VerticalAlignment.Center };
        sp.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold });
        sp.Children.Add(new TextBlock { Text = detail, Style = (Style)FindResource("MutedText"), Margin = new Thickness(0, 2, 0, 0) });
        return sp;
    }

    private static string Format(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / 1073741824.0:0.0} GB"
        : bytes >= 1L << 20 ? $"{bytes / 1048576.0:0} MB"
        : bytes >= 1L << 10 ? $"{bytes / 1024.0:0} KB"
        : $"{bytes} B";
}
