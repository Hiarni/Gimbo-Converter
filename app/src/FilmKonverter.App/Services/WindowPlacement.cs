using System.Runtime.InteropServices;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.App.Services;

public static class WindowPlacement
{
    private const int SwHide = 0;
    private const int SwShowMinimized = 2;
    private const int SwShowMaximized = 3;
    private const int WpfRestoreToMaximized = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct Placement
    {
        public int Length;
        public int Flags;
        public int ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowPlacement(IntPtr hwnd, ref Placement placement);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPlacement(IntPtr hwnd, ref Placement placement);

    public static WindowPlace? Read(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return null;
        var p = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref p)) return null;
        var maximized = p.ShowCmd == SwShowMaximized || (p.ShowCmd == SwShowMinimized && (p.Flags & WpfRestoreToMaximized) != 0);
        var r = p.NormalPosition;
        var place = new WindowPlace(r.Left, r.Top, r.Right, r.Bottom, maximized);
        return place.IsUsable ? place : null;
    }

    public static bool Apply(IntPtr hwnd, WindowPlace place)
    {
        if (hwnd == IntPtr.Zero || !place.IsUsable) return false;
        var p = new Placement { Length = Marshal.SizeOf<Placement>() };
        if (!GetWindowPlacement(hwnd, ref p)) return false;
        p.Flags = 0;
        p.ShowCmd = SwHide;
        p.NormalPosition = new Rect { Left = place.Left, Top = place.Top, Right = place.Right, Bottom = place.Bottom };
        return SetWindowPlacement(hwnd, ref p);
    }
}
