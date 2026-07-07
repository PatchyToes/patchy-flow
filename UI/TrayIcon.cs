using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;
using Microsoft.Win32;

namespace PatchyFlow;

/// <summary>System tray icon and menu — the app's entire chrome.</summary>
public sealed class TrayIcon : IDisposable
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "PatchyFlow";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _startWithWindows;
    private readonly ToolStripMenuItem _header;

    public event Action? ReloadRequested;
    public event Action? ExitRequested;

    public TrayIcon(string hotkeyDescription)
    {
        _header = new ToolStripMenuItem($"PatchyFlow — hold {hotkeyDescription}") { Enabled = false };

        var menu = new ContextMenuStrip();
        menu.Items.Add(_header);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open dictionary", null, (_, _) => OpenInNotepad(Paths.DictionaryPath));
        menu.Items.Add("Open settings", null, (_, _) => OpenInNotepad(Paths.SettingsPath));
        menu.Items.Add("Reload settings && dictionary", null, (_, _) => ReloadRequested?.Invoke());
        menu.Items.Add("Open log", null, (_, _) => OpenInNotepad(Log.LogPath));
        menu.Items.Add(new ToolStripSeparator());

        _startWithWindows = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = IsAutoStartEnabled(),
        };
        _startWithWindows.CheckedChanged += (_, _) => SetAutoStart(_startWithWindows.Checked);
        menu.Items.Add(_startWithWindows);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = DrawIcon(),
            Text = $"PatchyFlow — hold {hotkeyDescription} to talk",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _icon.DoubleClick += (_, _) => OpenInNotepad(Paths.DictionaryPath);
    }

    public void UpdateHotkeyDescription(string hotkeyDescription)
    {
        _header.Text = $"PatchyFlow — hold {hotkeyDescription}";
        _icon.Text = $"PatchyFlow — hold {hotkeyDescription} to talk";
    }

    public void Balloon(string title, string message) =>
        _icon.ShowBalloonTip(4000, title, message, ToolTipIcon.Info);

    private static void OpenInNotepad(string path)
    {
        try
        {
            if (!File.Exists(path)) File.WriteAllText(path, "");
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Write($"Open file failed: {ex.Message}");
        }
    }

    private static bool IsAutoStartEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
        return key?.GetValue(RunValueName) != null;
    }

    private static void SetAutoStart(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
            if (key == null) return;
            if (enabled)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
        catch (Exception ex)
        {
            Log.Write($"Autostart toggle failed: {ex.Message}");
        }
    }

    private static Icon DrawIcon()
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var body = new SolidBrush(Color.White);
        using var pen = new Pen(Color.White, 2.5f);

        // mic capsule
        g.FillPath(body, RoundedRect(11, 3, 10, 15, 5));
        // cradle arc
        g.DrawArc(pen, 7, 8, 18, 16, 30, 120);
        // stem + base
        g.DrawLine(pen, 16, 24, 16, 28);
        g.DrawLine(pen, 11, 28, 21, 28);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        var p = new GraphicsPath();
        p.AddArc(x, y, r * 2, r * 2, 180, 90);
        p.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        p.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        p.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        p.CloseFigure();
        return p;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
