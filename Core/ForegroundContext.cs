using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace PatchyFlow;

public sealed record ForegroundInfo(string App, string Title)
{
    public string Describe() => string.IsNullOrEmpty(Title) ? App : $"{App} — {Title}";
}

/// <summary>Captures which app/window the user is dictating into, at chord-down.</summary>
public static class ForegroundContext
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    public static ForegroundInfo? Capture()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return null;

            string app = "";
            GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid != 0)
            {
                try { app = Process.GetProcessById((int)pid).ProcessName; }
                catch { /* process may have exited */ }
            }

            var sb = new StringBuilder(256);
            GetWindowTextW(hwnd, sb, sb.Capacity);
            var title = sb.ToString().Trim();
            if (title.Length > 120) title = title[..120];

            if (app.Length == 0 && title.Length == 0) return null;
            return new ForegroundInfo(app, title);
        }
        catch (Exception ex)
        {
            Log.Write($"Foreground capture failed: {ex.Message}");
            return null;
        }
    }
}
