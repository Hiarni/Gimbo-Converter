namespace FilmKonverter.App;

internal static class Io
{
    public static async Task<bool> ExistsAsync(string? path, int timeoutMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var t = Task.Run(() => Directory.Exists(path) || File.Exists(path));
        return await Task.WhenAny(t, Task.Delay(timeoutMs)) == t && t.Result;
    }

    public static async Task<bool> DirExistsAsync(string? path, int timeoutMs = 4000)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var t = Task.Run(() => Directory.Exists(path));
        return await Task.WhenAny(t, Task.Delay(timeoutMs)) == t && t.Result;
    }

    public static bool DirExistsQuick(string? path, int timeoutMs = 1500)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var t = Task.Run(() => Directory.Exists(path));
        return t.Wait(timeoutMs) && t.Result;
    }

    public static async Task<T> RunAsync<T>(Func<T> work, T fallback, int timeoutMs = 10000)
    {
        var t = Task.Run(work);
        try { return await Task.WhenAny(t, Task.Delay(timeoutMs)) == t ? await t : fallback; }
        catch { return fallback; }
    }
}
