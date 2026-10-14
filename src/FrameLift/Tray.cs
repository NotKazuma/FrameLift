using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using FrameLift.Core;
using WinForms = System.Windows.Forms;

namespace FrameLift;

/// <summary>The notification-area icon: Boost on/off, open the window, quit. Dispose removes the icon, so no ghost is left behind.</summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ToolStripMenuItem _boostItem;
    private readonly Icon _idle = Draw(Color.FromArgb(108, 140, 255), Color.FromArgb(155, 108, 255));
    private readonly Icon _on = Draw(Color.FromArgb(61, 220, 151), Color.FromArgb(39, 183, 168));
    private bool? _lastBoost;

    public TrayIcon(Action open, Action toggleBoost, Action quit)
    {
        _boostItem = new WinForms.ToolStripMenuItem("Start Boost");
        _boostItem.Click += (_, _) => toggleBoost();
        var openItem = new WinForms.ToolStripMenuItem("Open FrameLift") { Font = new Font(WinForms.Control.DefaultFont, FontStyle.Bold) };
        openItem.Click += (_, _) => open();
        var quitItem = new WinForms.ToolStripMenuItem("Quit");
        quitItem.Click += (_, _) => quit();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.AddRange(new WinForms.ToolStripItem[] { openItem, _boostItem, new WinForms.ToolStripSeparator(), quitItem });

        _icon = new WinForms.NotifyIcon { Icon = _idle, Text = "FrameLift", ContextMenuStrip = menu, Visible = true };
        _icon.MouseClick += (_, e) => { if (e.Button == WinForms.MouseButtons.Left) open(); };
    }

    /// <summary>Keeps the menu text, tooltip and icon in step with Game Boost.</summary>
    public void SetBoost(bool on, string? game)
    {
        if (_lastBoost == on) return;
        _lastBoost = on;
        _boostItem.Text = on ? "Stop Boost" : "Start Boost";
        _icon.Icon = on ? _on : _idle;
        // NotifyIcon.Text is limited to 63 characters.
        _icon.Text = on ? ("FrameLift: boosting " + (game ?? "your PC")).Truncate(63) : "FrameLift";
    }

    /// <summary>Once, the first time the window is hidden: say where FrameLift went.</summary>
    public void ShowFirstHideHint(AppConfig cfg, Action save)
    {
        if (cfg.TrayHintShown) return;
        cfg.TrayHintShown = true;
        save();
        _icon.ShowBalloonTip(4000, "FrameLift is still running",
            "It keeps watching for games from the tray. Right-click the icon to quit.", WinForms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.ContextMenuStrip?.Dispose();
        _icon.Dispose();
        _idle.Dispose();
        _on.Dispose();
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr h);

    /// <summary>Draws the icon in-process, so no .ico asset is needed: a rounded gradient tile with an upward arrow.</summary>
    private static Icon Draw(Color a, Color b)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var path = new GraphicsPath();
            const int r = 9;
            path.AddArc(1, 1, r * 2, r * 2, 180, 90);
            path.AddArc(31 - r * 2, 1, r * 2, r * 2, 270, 90);
            path.AddArc(31 - r * 2, 31 - r * 2, r * 2, r * 2, 0, 90);
            path.AddArc(1, 31 - r * 2, r * 2, r * 2, 90, 90);
            path.CloseFigure();
            using var fill = new LinearGradientBrush(new Rectangle(0, 0, 32, 32), a, b, 45f);
            g.FillPath(fill, path);
            using var pen = new Pen(Color.White, 3.2f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
            g.DrawLines(pen, new[] { new PointF(9, 18), new PointF(16, 10), new PointF(23, 18) });
            g.DrawLine(pen, 16, 11, 16, 23);
        }
        IntPtr h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}

internal static class StringExtensions
{
    public static string Truncate(this string s, int max) => s.Length <= max ? s : s[..max];
}
