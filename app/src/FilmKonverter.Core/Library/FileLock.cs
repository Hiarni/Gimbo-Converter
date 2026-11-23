using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public sealed class FileLock : IDisposable
{
    private static readonly TimeSpan Heartbeat = TimeSpan.FromMinutes(10);

    public string Folder { get; }
    private readonly string _token;
    private readonly string _purpose;
    private readonly Timer _timer;
    private bool _released;

    private FileLock(string folder, string token, string purpose)
    {
        Folder = folder;
        _token = token;
        _purpose = purpose;
        _timer = new Timer(_ => Beat(), null, Heartbeat, Heartbeat);
    }

    public static string ComputerName => Environment.MachineName;

    private static readonly TimeSpan HeartbeatStale = TimeSpan.FromHours(1);

    private static string? ReadShared(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    public bool StillOwned()
    {
        if (_released) return false;
        var text = ReadShared(Path.Combine(Folder, "info.txt"));
        return text is null ? Directory.Exists(Folder) : text.Contains(_token, StringComparison.Ordinal);
    }

    private void Beat()
    {
        if (_released) return;
        var text = ReadShared(Path.Combine(Folder, "info.txt"));
        if (text is not null && !text.Contains(_token, StringComparison.Ordinal)) { _timer.Change(Timeout.Infinite, Timeout.Infinite); return; }
        if (_released) return;
        WriteInfo(Folder, _token, _purpose);
    }

    private static readonly string ProcessTag = MakeProcessTag();

    private static string MakeProcessTag()
    {
        try
        {
            using var me = System.Diagnostics.Process.GetCurrentProcess();
            return $"pid={me.Id}:{me.StartTime.ToUniversalTime().Ticks}";
        }
        catch
        {
            return $"pid={Environment.ProcessId}:0";
        }
    }

    private static bool OwnerProcessAlive(string infoText)
    {
        var m = System.Text.RegularExpressions.Regex.Match(infoText, @"pid=(?<pid>\d+):(?<t>\d+)");
        if (!m.Success) return false;
        System.Diagnostics.Process p;
        try { p = System.Diagnostics.Process.GetProcessById(int.Parse(m.Groups["pid"].Value)); }
        catch { return false; }
        using (p)
        {
            try
            {
                var t = long.Parse(m.Groups["t"].Value);
                return t == 0 || p.StartTime.ToUniversalTime().Ticks == t;
            }
            catch
            {
                return true;
            }
        }
    }

    public static FileLock? TryAcquire(string lockFolder, int staleHours, out string owner, string purpose = "")
    {
        lock (Held)
        {
            if (Held.Contains(Key(lockFolder)))
            {
                owner = ReadOwner(lockFolder) is { Length: > 0 } o ? o : ComputerName;
                return null;
            }
            var l = TryAcquireCore(lockFolder, staleHours, out owner, purpose);
            if (l is not null) Held.Add(Key(lockFolder));
            return l;
        }
    }

    private static readonly HashSet<string> Held = new(StringComparer.OrdinalIgnoreCase);

    private static string Key(string folder)
    {
        try { return Path.GetFullPath(folder).TrimEnd('\\', '/'); }
        catch { return folder; }
    }

    private static FileLock? TryAcquireCore(string lockFolder, int staleHours, out string owner, string purpose)
    {
        owner = "";
        if (Native.TryCreateDirectoryExclusive(lockFolder)) return Take(lockFolder, purpose);

        owner = ReadOwner(lockFolder);
        if (IsStale(lockFolder, owner, staleHours))
        {
            var seen = ReadShared(Path.Combine(lockFolder, "info.txt")) ?? "";
            var grave = lockFolder + ".alt-" + Guid.NewGuid().ToString("N")[..8];
            try
            {
                Directory.Move(lockFolder, grave);
                var moved = ReadShared(Path.Combine(grave, "info.txt")) ?? "";
                var isLock = Directory.GetDirectories(grave).Length == 0
                             && Directory.GetFiles(grave).All(f => Path.GetFileName(f).Equals("info.txt", StringComparison.OrdinalIgnoreCase));
                if (moved != seen || !isLock)
                {
                    if (!Directory.Exists(lockFolder)) Directory.Move(grave, lockFolder);
                    owner = ReadOwner(lockFolder);
                    if (owner.Length == 0) owner = L.T("anderem PC", "another PC");
                    return null;
                }
                try { Directory.Delete(grave, recursive: true); } catch { }
            }
            catch
            {
            }
            if (Native.TryCreateDirectoryExclusive(lockFolder)) return Take(lockFolder, purpose);
            owner = ReadOwner(lockFolder);
        }
        if (owner.Length == 0) owner = L.T("anderem PC", "another PC");
        return null;
    }

    private static FileLock Take(string folder, string purpose)
    {
        var token = "#" + Guid.NewGuid().ToString("N")[..10];
        WriteInfo(folder, token, purpose);
        return new FileLock(folder, token, purpose);
    }

    private static void WriteInfo(string folder, string token, string purpose)
    {
        try
        {
            var text = $"{ComputerName} {DateTime.Now:yyyy-MM-dd HH:mm}" + (purpose.Length > 0 ? " " + purpose : "") + " " + token + " " + ProcessTag;
            File.WriteAllText(Path.Combine(folder, "info.txt"), text);
        }
        catch
        {
        }
    }

    public static string ReadOwner(string folder)
    {
        var text = ReadShared(Path.Combine(folder, "info.txt"));
        if (text is null) return "";
        var first = text.Split('\n').FirstOrDefault() ?? "";
        return first.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
    }

    private static DateTime LastSign(string folder)
    {
        var t = Directory.GetCreationTime(folder);
        var info = Path.Combine(folder, "info.txt");
        if (File.Exists(info))
        {
            var w = File.GetLastWriteTime(info);
            if (w > t) t = w;
        }
        return t;
    }

    private static bool IsStale(string folder, string owner, int staleHours)
    {
        var info = ReadShared(Path.Combine(folder, "info.txt")) ?? "";
        if (owner.Length > 0 && owner.Equals(ComputerName, StringComparison.OrdinalIgnoreCase))
        {
            if (info.Contains(ProcessTag, StringComparison.Ordinal)) return true;
            if (!OwnerProcessAlive(info)) return true;
        }
        try
        {
            var silent = DateTime.Now - LastSign(folder);
            if (info.Contains("pid=", StringComparison.Ordinal) && silent >= HeartbeatStale) return true;
            if (info.Length == 0 && silent >= HeartbeatStale) return true;
            return staleHours > 0 && silent.TotalHours >= staleHours;
        }
        catch
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_released) return;
        _released = true;
        using (var done = new ManualResetEvent(false))
        {
            if (_timer.Dispose(done)) done.WaitOne(TimeSpan.FromSeconds(5));
        }
        try
        {
            var text = ReadShared(Path.Combine(Folder, "info.txt"));
            if (text is not null && !text.Contains(_token, StringComparison.Ordinal)) return;
            for (var i = 0; ; i++)
            {
                try { Directory.Delete(Folder, recursive: true); break; }
                catch (DirectoryNotFoundException) { break; }
                catch when (i < 3) { Thread.Sleep(200); }
            }
        }
        catch
        {
        }
        finally
        {
            lock (Held) Held.Remove(Key(Folder));
        }
    }
}
