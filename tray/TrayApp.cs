using System.Diagnostics;
using System.Drawing.Drawing2D;
using Microsoft.Win32;

namespace FreeVram;

public sealed class TrayApp : ApplicationContext
{
    const int RefreshMs = 2000;
    const int MenuRows = 12;
    const int WarnPercent = 90;
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "FreeVram";

    readonly NotifyIcon _tray;
    readonly System.Windows.Forms.Timer _timer;
    readonly ContextMenuStrip _menu = new();
    Icon? _icon;
    bool _warned;

    public TrayApp()
    {
        _tray = new NotifyIcon { Visible = true, ContextMenuStrip = _menu, Text = "FreeVram" };
        _menu.Opening += (_, _) => BuildMenu();
        _tray.DoubleClick += (_, _) => Restart();

        _timer = new System.Windows.Forms.Timer { Interval = RefreshMs };
        _timer.Tick += (_, _) => Refresh();
        _timer.Start();
        Refresh();
    }

    void Refresh()
    {
        var t = VramMonitor.GetTotals();
        SetIcon(t.Percent);
        _tray.Text = $"VRAM {t.UsedMB / 1024:0.0} / {t.TotalMB / 1024:0.0} GB ({t.Percent}%)";

        if (t.Percent >= WarnPercent && !_warned)
        {
            _warned = true;
            _tray.ShowBalloonTip(4000, "VRAM nearly full", $"{t.Percent}% in use. Right-click to see what is holding it.", ToolTipIcon.Warning);
        }
        else if (t.Percent < WarnPercent - 10) _warned = false;
    }

    void SetIcon(int percent)
    {
        var old = _icon;
        _icon = DrawMeter(percent);
        _tray.Icon = _icon;
        if (old is not null) { Native.DestroyIcon(old.Handle); old.Dispose(); }
    }

    /// <summary>Vertical fill bar coloured by pressure. Text is unreadable at 16px, a meter is not.</summary>
    static Icon DrawMeter(int percent)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        var fill = percent >= 90 ? Color.FromArgb(230, 70, 70)
                 : percent >= 70 ? Color.FromArgb(240, 180, 40)
                 : Color.FromArgb(80, 200, 120);

        var outer = new Rectangle(6, 2, 20, 28);
        using var border = new Pen(Color.FromArgb(200, 200, 200), 2);
        g.DrawRectangle(border, outer);

        int h = (int)Math.Round((outer.Height - 4) * Math.Clamp(percent, 0, 100) / 100.0);
        if (h > 0)
        {
            using var brush = new SolidBrush(fill);
            g.FillRectangle(brush, outer.X + 2, outer.Bottom - 2 - h, outer.Width - 4, h);
        }

        return Icon.FromHandle(bmp.GetHicon());
    }

    void BuildMenu()
    {
        _menu.Items.Clear();
        var t = VramMonitor.GetTotals();
        _menu.Items.Add(new ToolStripMenuItem($"VRAM: {t.UsedMB:N0} / {t.TotalMB:N0} MB  ({t.Percent}%)")
        {
            Enabled = false,
            Font = new Font(_menu.Font, FontStyle.Bold),
        });
        _menu.Items.Add(new ToolStripSeparator());

        foreach (var p in VramMonitor.GetProcesses().Take(MenuRows))
        {
            var label = $"{p.DedicatedMB,7:N0} MB   {p.Name}";
            if (p.Protected) label += "   (system)";
            var item = new ToolStripMenuItem(label)
            {
                Enabled = !p.Protected,
                ToolTipText = $"PID {p.Pid}   shared {p.SharedMB:N0} MB   -   click to kill",
            };
            if (p.Fragile) item.ForeColor = Color.DarkGoldenrod;
            item.Click += (_, _) => Kill(p);
            _menu.Items.Add(item);
        }

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Restart graphics driver  (Win+Ctrl+Shift+B)", null, (_, _) => Restart());

        var startup = new ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled(), CheckOnClick = true };
        startup.CheckedChanged += (_, _) => SetStartup(startup.Checked);
        _menu.Items.Add(startup);

        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => ExitThread());
    }

    void Kill(ProcInfo p)
    {
        var msg = $"Kill {p.Name} (PID {p.Pid})?\n\nThis frees about {p.DedicatedMB:N0} MB of VRAM. Unsaved work in that app is lost.";
        if (p.Fragile) msg += "\n\nThis is a Windows shell process. It usually restarts on its own, but the desktop may blink.";
        var r = MessageBox.Show(msg, "FreeVram", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2);
        if (r != DialogResult.Yes) return;

        try
        {
            using var proc = Process.GetProcessById(p.Pid);
            proc.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Could not kill {p.Name}: {ex.Message}", "FreeVram", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        Refresh();
    }

    void Restart()
    {
        var before = VramMonitor.GetTotals().UsedMB;
        Native.RestartGraphicsDriver();
        // The driver takes a moment to come back; measure after it settles.
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += (_, _) =>
        {
            t.Stop();
            t.Dispose();
            var after = VramMonitor.GetTotals().UsedMB;
            _tray.ShowBalloonTip(3000, "Graphics driver restarted",
                $"VRAM {before:N0} MB -> {after:N0} MB ({after - before:+#,0;-#,0;0} MB)", ToolTipIcon.Info);
            Refresh();
        };
        t.Start();
    }

    static bool IsStartupEnabled()
    {
        using var k = Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(RunValue) is string;
    }

    static void SetStartup(bool enable)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (enable) k.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else k.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _menu.Dispose();
            if (_icon is not null) { Native.DestroyIcon(_icon.Handle); _icon.Dispose(); }
        }
        base.Dispose(disposing);
    }
}
