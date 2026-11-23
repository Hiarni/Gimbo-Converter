using System.Runtime.InteropServices;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.Services;

public static class UserPresence
{
    private const int QunsBusy = 2;
    private const int QunsRunningD3DFullScreen = 3;
    private const int QunsPresentationMode = 4;

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [StructLayout(LayoutKind.Sequential)]
    private struct LastInputInfo
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LastInputInfo plii);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    public static string? FullscreenApp(string? ignore)
    {
        try
        {
            if (SHQueryUserNotificationState(out var s) != 0) return null;
            if (s == QunsPresentationMode) return "";
            if (s is not (QunsBusy or QunsRunningD3DFullScreen)) return null;
            var name = ForegroundProcess();
            return FullscreenRules.IsIgnored(name, ignore) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    private static string ForegroundProcess()
    {
        try
        {
            var window = GetForegroundWindow();
            if (window == IntPtr.Zero) return "";
            GetWindowThreadProcessId(window, out var pid);
            if (pid == 0) return "";
            using var p = System.Diagnostics.Process.GetProcessById((int)pid);
            return p.ProcessName;
        }
        catch
        {
            return "";
        }
    }

    public static TimeSpan IdleTime()
    {
        try
        {
            var info = new LastInputInfo { cbSize = (uint)Marshal.SizeOf<LastInputInfo>() };
            if (!GetLastInputInfo(ref info)) return TimeSpan.MaxValue;
            var ms = unchecked((uint)Environment.TickCount - info.dwTime);
            return TimeSpan.FromMilliseconds(ms);
        }
        catch
        {
            return TimeSpan.MaxValue;
        }
    }

    public static string? BusyReason(bool fullscreen, bool userActive, int idleMinutes, string? fullscreenIgnore = null)
    {
        if (fullscreen && FullscreenApp(fullscreenIgnore) is { } app)
            return app.Length > 0 ? L.T($"Vollbild-Anwendung läuft ({app})", $"full-screen application running ({app})") : L.T("Vollbild-Anwendung läuft", "full-screen application running");
        if (userActive && IdleTime() < TimeSpan.FromMinutes(Math.Max(1, idleMinutes))) return L.T("PC wird benutzt", "PC is in use");
        return null;
    }
}
