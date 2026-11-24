using System.Diagnostics;
using System.Runtime.InteropServices;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.Services;

internal static partial class Power
{
    private const uint EsContinuous = 0x80000000;
    private const uint EsSystemRequired = 0x00000001;

    [LibraryImport("kernel32.dll")]
    private static partial uint SetThreadExecutionState(uint flags);

    [LibraryImport("powrprof.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetSuspendState([MarshalAs(UnmanagedType.Bool)] bool hibernate, [MarshalAs(UnmanagedType.Bool)] bool force, [MarshalAs(UnmanagedType.Bool)] bool disableWakeEvent);

    public static void KeepAwake(bool on)
    {
        try { SetThreadExecutionState(on ? EsContinuous | EsSystemRequired : EsContinuous); } catch { }
    }

    public static bool Sleep() => Suspend(false);

    public static bool Hibernate() => Suspend(true);

    private static bool Suspend(bool hibernate)
    {
        try { return SetSuspendState(hibernate, false, false); } catch { return false; }
    }

    public static bool Shutdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe", L.T("/s /t 15 /c \"Gimbo Konverter: Arbeit erledigt, der PC wird heruntergefahren.\"", "/s /t 15 /c \"Gimbo Converter: work done, shutting down the PC.\""))
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
