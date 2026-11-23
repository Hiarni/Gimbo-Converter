using Microsoft.Win32;

namespace FilmKonverter.App.Services;

public static class Autostart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Gimbo Konverter";
    private const string OldValueName = "GimboKonverter";
    public const string Argument = "--autostart";

    private static string Command => $"\"{Environment.ProcessPath}\" {Argument}";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string;
        }
        catch
        {
            return false;
        }
    }

    public static string? Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
            if (key.GetValue(OldValueName) is not null) key.DeleteValue(OldValueName);
            if (enabled) key.SetValue(ValueName, Command);
            else if (key.GetValue(ValueName) is not null) key.DeleteValue(ValueName);
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public static void Sync(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            var current = key?.GetValue(ValueName) as string;
            if (key?.GetValue(OldValueName) is not null) { Set(enabled); return; }
            if (enabled && current != Command) Set(true);
            if (!enabled && current is not null) Set(false);
        }
        catch
        {
        }
    }
}
