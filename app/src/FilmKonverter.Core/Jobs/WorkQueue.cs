using System.Text.Json;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed class QueueEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..10];
    public string Relative { get; set; } = "";
    public string AddedBy { get; set; } = "";
    public DateTime AddedUtc { get; set; }

    public string Name => Relative.Length == 0 ? L.T("(ganze Quelle)", "(whole source)") : Relative;
}

public static class WorkQueue
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string Folder(string outputRoot) => DataStore.Queue(outputRoot);
    public static string FileOf(string outputRoot) => Path.Combine(Folder(outputRoot), "queue.json");

    public static List<QueueEntry> Read(string outputRoot) => TryRead(outputRoot, out var l) ? l : [];

    private enum ReadState { Ok, Corrupt, Failed }

    public static bool TryRead(string outputRoot, out List<QueueEntry> list) => ReadCore(outputRoot, out list) == ReadState.Ok;

    private static ReadState ReadCore(string outputRoot, out List<QueueEntry> list)
    {
        list = [];
        try
        {
            using var fs = new FileStream(FileOf(outputRoot), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            list = (JsonSerializer.Deserialize<List<QueueEntry?>>(fs, Json) ?? []).Where(e => e is not null).Select(e => e!).ToList();
            return ReadState.Ok;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return DataStore.Reachable(outputRoot) ? ReadState.Ok : ReadState.Failed;
        }
        catch (JsonException)
        {
            return ReadState.Corrupt;
        }
        catch
        {
            return ReadState.Failed;
        }
    }

    private static readonly Dictionary<string, (string Content, DateTime SeenUtc)> LockSeen = new(StringComparer.OrdinalIgnoreCase);

    private static string LockContent(string lockDir)
    {
        try
        {
            using var fs = new FileStream(Path.Combine(lockDir, "owner.txt"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var r = new StreamReader(fs);
            return r.ReadToEnd();
        }
        catch { return ""; }
    }

    private static string LockIdentity(string lockDir)
    {
        long created;
        try { created = Directory.GetCreationTimeUtc(lockDir).Ticks; } catch { created = 0; }
        return LockContent(lockDir) + "|" + created;
    }

    public static DateTime Stamp(string outputRoot)
    {
        try { return File.GetLastWriteTimeUtc(FileOf(outputRoot)); }
        catch { return DateTime.MinValue; }
    }

    public static bool Modify(string outputRoot, Action<List<QueueEntry>> change)
    {
        if (string.IsNullOrWhiteSpace(outputRoot)) return false;
        var dir = Folder(outputRoot);
        var lockDir = Path.Combine(dir, "queue.lock");
        try { Directory.CreateDirectory(dir); }
        catch { return false; }

        var deadline = DateTime.UtcNow.AddSeconds(8);
        var me = $"{FileLock.ComputerName} {Guid.NewGuid():N}";
        while (true)
        {
            bool got = false;
            try { got = Native.TryCreateDirectoryExclusive(lockDir); }
            catch { }
            if (got)
            {
                try { File.WriteAllText(Path.Combine(lockDir, "owner.txt"), me); } catch { }
                lock (LockSeen) LockSeen.Remove(lockDir);
                break;
            }
            if (!Directory.Exists(lockDir))
            {
                lock (LockSeen) LockSeen.Remove(lockDir);
            }
            else
            {
                var identity = LockIdentity(lockDir);
                lock (LockSeen)
                {
                    if (!LockSeen.TryGetValue(lockDir, out var seen) || seen.Content != identity) LockSeen[lockDir] = (identity, DateTime.UtcNow);
                    else if (DateTime.UtcNow - seen.SeenUtc > TimeSpan.FromMinutes(2))
                    {
                        var grave = lockDir + ".alt-" + Guid.NewGuid().ToString("N")[..6];
                        try
                        {
                            Directory.Move(lockDir, grave);
                            if (LockIdentity(grave).Split('|')[0] == identity.Split('|')[0]) Directory.Delete(grave, true);
                            else if (!Directory.Exists(lockDir)) Directory.Move(grave, lockDir);
                        }
                        catch { }
                        LockSeen.Remove(lockDir);
                    }
                }
            }
            if (DateTime.UtcNow > deadline) return false;
            Thread.Sleep(150);
        }
        try
        {
            var file = FileOf(outputRoot);
            var state = ReadCore(outputRoot, out var list);
            if (state == ReadState.Failed) return false;
            if (state == ReadState.Corrupt)
            {
                try { File.Copy(file, file + ".defekt", overwrite: true); } catch { }
                list = [];
            }
            change(list);
            var tmp = file + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
            try
            {
                using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    JsonSerializer.Serialize(fs, list, Json);
                    fs.Flush(true);
                }
                File.Move(tmp, file, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            }
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (LockContent(lockDir) == me) Directory.Delete(lockDir, true); } catch { }
        }
    }

    public static int Add(string outputRoot, string inputRoot, IEnumerable<string> paths, string machine)
    {
        DataStore.Link(outputRoot, inputRoot);
        var rels = paths.Where(p => Scanner.IsInside(p, inputRoot))
            .Select(p => Path.GetRelativePath(inputRoot, p.TrimEnd('\\', '/')))
            .Select(r => r == "." ? "" : r)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var added = 0;
        var ok = Modify(outputRoot, list =>
        {
            foreach (var r in rels)
            {
                if (list.Any(e => e.Relative.Equals(r, StringComparison.OrdinalIgnoreCase))) continue;
                list.Add(new QueueEntry { Relative = r, AddedBy = machine, AddedUtc = DateTime.UtcNow });
                added++;
            }
        });
        return ok ? added : -1;
    }

    public static bool Remove(string outputRoot, string id) => Modify(outputRoot, l => l.RemoveAll(e => e.Id == id));

    public static bool Clear(string outputRoot) => Modify(outputRoot, l => l.Clear());

    public static bool Move(string outputRoot, string id, int delta) => Modify(outputRoot, l =>
    {
        var i = l.FindIndex(e => e.Id == id);
        if (i < 0) return;
        var j = Math.Clamp(i + delta, 0, l.Count - 1);
        if (i == j) return;
        var e = l[i];
        l.RemoveAt(i);
        l.Insert(j, e);
    });
}
