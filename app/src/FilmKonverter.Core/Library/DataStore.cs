using System.Collections.Concurrent;
using System.Security.Cryptography;

namespace FilmKonverter.Core.Library;

public sealed record SyncResult(bool Restored = false, int MarksRestored = 0, int LogsRestored = 0, int Imported = 0)
{
    public static readonly SyncResult Nothing = new();
}

public enum SharingState
{
    Shared,
    Off,
    NoSource,
    NotWritable,
}

public static class DataStore
{
    public const string SharedFolder = ".gimbo";

    public const string InnerFolder = "metadata";
    private const string MarksName = "Merkliste";
    private const string VerifyName = "Pruefung";
    private const string StatusName = "Status";
    private const string StatsName = "Statistik";
    private const string QueueName = "Warteschlange";
    private const string IdFile = "kennung.txt";

    private static readonly StringComparer PathCmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string LocalBase { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GimboKonverter", "Daten");

    public static string TestBase { get; set; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimboKonverter", "Test");

    public static bool Share { get; set; } = true;

    public static void Configure(string localBase, string testBase, bool share)
    {
        LocalBase = localBase;
        TestBase = testBase;
        Share = share;
    }

    // ---------------- Welche Quelle gehoert zu welchem Ziel ----------------

    private static readonly ConcurrentDictionary<string, string[]> Sources = new(PathCmp);

    private static string Norm(string path) => path.TrimEnd('\\', '/');

    public static void Link(string outputRoot, string inputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || string.IsNullOrWhiteSpace(inputRoot)) return;
        var src = Norm(inputRoot);
        Sources.AddOrUpdate(Norm(outputRoot), _ => [src],
            (_, have) => have.Contains(src, PathCmp) ? have : [.. have, src]);
    }

    public static void Relink(IEnumerable<(string OutputRoot, string InputRoot)> pairs)
    {
        var fresh = new Dictionary<string, List<string>>(PathCmp);
        foreach (var (o, i) in pairs)
        {
            if (string.IsNullOrWhiteSpace(o) || string.IsNullOrWhiteSpace(i)) continue;
            if (!fresh.TryGetValue(Norm(o), out var list)) fresh[Norm(o)] = list = [];
            if (!list.Contains(Norm(i), PathCmp)) list.Add(Norm(i));
        }
        foreach (var key in Sources.Keys) if (!fresh.ContainsKey(key)) Sources.TryRemove(key, out _);
        foreach (var (key, list) in fresh) Sources[key] = [.. list];
    }

    public static string? SourceOf(string outputRoot)
    {
        if (string.IsNullOrWhiteSpace(outputRoot) || !Sources.TryGetValue(Norm(outputRoot), out var list) || list.Length == 0) return null;
        return list.Length == 1
            ? list[0]
            : list.OrderBy(Leaf, StringComparer.OrdinalIgnoreCase).ThenBy(x => x, StringComparer.OrdinalIgnoreCase).First();
    }

    private static string Leaf(string path)
    {
        var p = Norm(path);
        var cut = p.LastIndexOfAny(['\\', '/', ':']);
        return cut >= 0 ? p[(cut + 1)..] : p;
    }

    // ---------------- Orte ----------------

    private static readonly ConcurrentDictionary<string, string> Keys = new(StringComparer.Ordinal);

    public static string Key(string path) => Keys.GetOrAdd(path ?? "", static root =>
    {
        if (string.IsNullOrWhiteSpace(root)) return "ohne-ordner";
        string full;
        try { full = Path.GetFullPath(root); } catch { full = root; }
        full = full.TrimEnd('\\', '/');
        if (OperatingSystem.IsWindows()) full = full.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(full)))[..8].ToLowerInvariant();
        var bad = Path.GetInvalidFileNameChars();
        var leaf = new string(Leaf(root).Select(c => bad.Contains(c) || c == '.' ? '_' : c).ToArray()).Trim();
        if (leaf.Length > 40) leaf = leaf[..40];
        return leaf.Length == 0 ? hash : leaf + "-" + hash;
    });

    public static string LocalRoot(string outputRoot) => Path.Combine(LocalBase, Key(SourceOf(outputRoot) ?? outputRoot));

    public static string? SharedRoot(string outputRoot) =>
        Share && SourceOf(outputRoot) is { } src ? Path.Combine(src, SharedFolder, InnerFolder) : null;

    private static string? SourceOfShared(string shared) => Path.GetDirectoryName(Path.GetDirectoryName(shared));

    public static string? UsableSharedRoot(string outputRoot) =>
        SharedRoot(outputRoot) is { } shared && Usable(shared) ? shared : null;

    public static string Root(string outputRoot) => UsableSharedRoot(outputRoot) ?? LocalRoot(outputRoot);

    public static bool Reachable(string outputRoot)
    {
        if (UsableSharedRoot(outputRoot) is not { } shared) return true;
        try { return Directory.Exists(SourceOfShared(shared)); } catch { return false; }
    }

    public static SharingState State(string outputRoot)
    {
        if (!Share) return SharingState.Off;
        if (SourceOf(outputRoot) is null) return SharingState.NoSource;
        return UsableSharedRoot(outputRoot) is null ? SharingState.NotWritable : SharingState.Shared;
    }

    public static string Marks(string outputRoot) => Path.Combine(Root(outputRoot), MarksName);

    public static string VerifyMarks(string outputRoot) => Path.Combine(Root(outputRoot), VerifyName);

    public static string Status(string outputRoot) => Path.Combine(Root(outputRoot), StatusName);

    public static string Stats(string outputRoot) => Path.Combine(Root(outputRoot), StatsName);

    public static (string Dir, string? Backup) StatsDirs(string outputRoot)
    {
        var local = Path.Combine(LocalRoot(outputRoot), StatsName);
        return UsableSharedRoot(outputRoot) is { } shared ? (Path.Combine(shared, StatsName), local) : (local, null);
    }

    public static string Queue(string outputRoot) => Path.Combine(Root(outputRoot), QueueName);

    public static string TestClips(string outputRoot) => TestBase;

    public static void CleanTestClips(string keepDir, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(TestBase)) return;
            var keep = Norm(Path.GetFullPath(keepDir));
            foreach (var d in Directory.EnumerateDirectories(TestBase))
            {
                if (PathCmp.Equals(Norm(Path.GetFullPath(d)), keep)) continue;
                DeleteIfStale(d, age);
            }
        }
        catch { }
    }

    // ---------------- Ist der gemeinsame Ordner benutzbar? ----------------

    private static readonly ConcurrentDictionary<string, (bool Ok, DateTime CheckedUtc)> UsableCache = new(PathCmp);

    public static TimeSpan RetryAfter { get; set; } = TimeSpan.FromMinutes(2);

    public static TimeSpan RecheckAfter { get; set; } = TimeSpan.FromMinutes(1);

    private static bool Usable(string shared)
    {
        var now = DateTime.UtcNow;
        var known = UsableCache.TryGetValue(shared, out var c);
        if (known && c.Ok && now - c.CheckedUtc < RecheckAfter) return true;
        if (known && !c.Ok && now - c.CheckedUtc < RetryAfter) return false;
        var wasOk = known && c.Ok;
        var probe = Task.Run(() => (wasOk && Directory.Exists(shared)) || Prepare(shared));
        var ok = probe.Wait(TimeSpan.FromSeconds(4)) && probe.Result;
        UsableCache[shared] = (ok, DateTime.UtcNow);
        return ok;
    }

    public static void ResetUsable() => UsableCache.Clear();

    private static bool Prepare(string shared)
    {
        try
        {
            var source = SourceOfShared(shared);
            var hidden = Path.GetDirectoryName(shared);
            if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(hidden) || !Directory.Exists(source)) return false;
            Directory.CreateDirectory(shared);
            var test = Path.Combine(shared, ".schreibtest-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(test, "");
            File.Delete(test);
            if (!File.Exists(Path.Combine(hidden, ".ignore")))
            {
                WriteText(Path.Combine(hidden, ".ignore"), "");
                WriteText(Path.Combine(hidden, ".plexignore"), "*\n");
                WriteText(Path.Combine(hidden, ".nomedia"), "");
            }
            Hide(hidden);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ---------------- Sicherung einzelner Dateien ----------------

    public static void Backup(string outputRoot, string sharedFile)
    {
        try
        {
            var target = LocalTwin(outputRoot, sharedFile);
            if (target is null) return;
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(sharedFile, target, overwrite: true);
        }
        catch
        {
        }
    }

    public static void BackupDelete(string outputRoot, string sharedFile)
    {
        try
        {
            var target = LocalTwin(outputRoot, sharedFile);
            if (target is not null && File.Exists(target)) File.Delete(target);
        }
        catch { }
    }

    private static string? LocalTwin(string outputRoot, string sharedFile)
    {
        if (SharedRoot(outputRoot) is not { } shared) return null;
        var rel = Path.GetRelativePath(shared, sharedFile);
        if (rel.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(rel)) return null;
        return Path.Combine(LocalRoot(outputRoot), rel);
    }

    // ---------------- Abgleich ----------------

    private static readonly object SyncLock = new();
    private static readonly ConcurrentDictionary<string, DateTime> LastMirror = new(PathCmp);

    public static TimeSpan MirrorEvery { get; set; } = TimeSpan.FromMinutes(10);

    public static SyncResult Sync(string outputRoot, bool force = false)
    {
        if (string.IsNullOrWhiteSpace(outputRoot)) return SyncResult.Nothing;
        lock (SyncLock)
        {
            try
            {
                var shared = UsableSharedRoot(outputRoot);
                var imported = Directory.Exists(outputRoot) ? ImportLegacy(outputRoot, shared) : 0;
                return shared is null ? new SyncResult(Imported: imported) : SyncShared(outputRoot, shared, force) with { Imported = imported };
            }
            catch
            {
                return SyncResult.Nothing;
            }
        }
    }

    private static SyncResult SyncShared(string outputRoot, string shared, bool force)
    {
        if (SourceOfShared(shared) is not { Length: > 0 } source || !Directory.Exists(source))
        {
            UsableCache.TryRemove(shared, out _);
            return SyncResult.Nothing;
        }
        var local = LocalRoot(outputRoot);
        var sharedId = ReadText(Path.Combine(shared, IdFile));
        if (sharedId is null)
        {
            WriteText(Path.Combine(shared, IdFile), Guid.NewGuid().ToString("N"));
            sharedId = ReadText(Path.Combine(shared, IdFile));
        }
        var localId = ReadText(Path.Combine(local, IdFile));
        var restore = sharedId is not null && !string.Equals(sharedId, localId, StringComparison.Ordinal);

        var sharedMarks = Path.Combine(shared, MarksName);
        var localMarks = Path.Combine(local, MarksName);
        var marksRestored = restore ? CopyMissing(localMarks, sharedMarks) : 0;
        var logsRestored = SyncStats(Path.Combine(shared, StatsName), Path.Combine(local, StatsName));

        var due = force || restore || !LastMirror.TryGetValue(shared, out var last) || DateTime.UtcNow - last >= MirrorEvery;
        if (due)
        {
            Mirror(sharedMarks, localMarks);
            LastMirror[shared] = DateTime.UtcNow;
        }
        if (restore && sharedId is not null) WriteText(Path.Combine(local, IdFile), sharedId);
        return new SyncResult(restore && (marksRestored > 0 || logsRestored > 0), marksRestored, logsRestored);
    }

    // ---------------- Statistik: Protokolle und Zuruecksetzen ----------------

    public const string ResetFile = "zurueckgesetzt.txt";

    public static DateTime ReadReset(string statsDir)
    {
        var t = ReadText(Path.Combine(statsDir, ResetFile));
        return t is not null && long.TryParse(t.Trim(), out var ticks) && ticks > 0 && ticks < DateTime.MaxValue.Ticks
            ? new DateTime(ticks, DateTimeKind.Utc)
            : DateTime.MinValue;
    }

    public static void WriteReset(string statsDir, DateTime utc) => WriteText(Path.Combine(statsDir, ResetFile), utc.Ticks.ToString());

    private static int SyncStats(string sharedStats, string localStats)
    {
        var resetShared = ReadReset(sharedStats);
        var resetLocal = ReadReset(localStats);
        if (resetShared > resetLocal)
        {
            ArchiveLogs(localStats, resetShared.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss"));
            WriteReset(localStats, resetShared);
        }
        else if (resetLocal > resetShared)
        {
            WriteReset(sharedStats, resetLocal);
        }

        var restored = 0;
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in SafeFiles(sharedStats, "*.jsonl")) names.Add(Path.GetFileName(f));
        foreach (var f in SafeFiles(localStats, "*.jsonl")) names.Add(Path.GetFileName(f));
        foreach (var name in names)
        {
            var s = Path.Combine(sharedStats, name);
            var l = Path.Combine(localStats, name);
            var ls = Length(s);
            var ll = Length(l);
            if (ll > ls) { if (Replace(l, s)) restored++; }
            else if (ls > ll) Replace(s, l);
        }
        return restored;
    }

    public static int ArchiveLogs(string statsDir, string stamp)
    {
        var n = 0;
        var target = Path.Combine(statsDir, "Archiv", stamp);
        foreach (var f in SafeFiles(statsDir, "*.jsonl"))
        {
            try
            {
                Directory.CreateDirectory(target);
                var to = Path.Combine(target, Path.GetFileName(f));
                if (File.Exists(to)) to = Path.Combine(target, Path.GetFileNameWithoutExtension(f) + "." + Guid.NewGuid().ToString("N")[..6] + ".jsonl");
                File.Move(f, to);
                n++;
            }
            catch { }
        }
        return n;
    }

    // ---------------- Alte Ordner frueherer Versionen ----------------

    private static int ImportLegacy(string outputRoot, string? shared)
    {
        var folders = new[] { LibraryPaths.MarkFolder, LibraryPaths.VerifyFolder, LibraryPaths.StatsFolder, LibraryPaths.QueueFolder, LibraryPaths.StatusFolder, LibraryPaths.TestFolder };
        if (!folders.Any(f => Directory.Exists(Path.Combine(outputRoot, f)))) return 0;

        var move = shared is not null;
        var target = shared ?? LocalRoot(outputRoot);
        var n = 0;
        n += Take(Path.Combine(outputRoot, LibraryPaths.MarkFolder), Path.Combine(target, MarksName), move, append: false);
        n += Take(Path.Combine(outputRoot, LibraryPaths.VerifyFolder), Path.Combine(target, VerifyName), move, append: false);
        n += Take(Path.Combine(outputRoot, LibraryPaths.StatsFolder), Path.Combine(target, StatsName), move, append: true);

        var oldQueue = Path.Combine(outputRoot, LibraryPaths.QueueFolder);
        var from = Path.Combine(oldQueue, "queue.json");
        var to = Path.Combine(target, QueueName, "queue.json");
        if (File.Exists(from) && !File.Exists(to))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                if (move) File.Move(from, to); else File.Copy(from, to);
                n++;
            }
            catch { }
        }

        var othersActive = OtherPcSeen(Path.Combine(outputRoot, LibraryPaths.StatusFolder), TimeSpan.FromDays(30));
        if (move)
        {
            DeleteIfStale(oldQueue, TimeSpan.FromHours(24));
            DeleteIfStale(Path.Combine(outputRoot, LibraryPaths.StatusFolder), TimeSpan.FromMinutes(10));
            DeleteIfStale(Path.Combine(outputRoot, LibraryPaths.TestFolder), TimeSpan.FromHours(24));
        }
        else if (!othersActive)
        {
            foreach (var f in folders) DeleteIfStale(Path.Combine(outputRoot, f), TimeSpan.FromMinutes(10));
        }
        return n;
    }

    private static bool OtherPcSeen(string statusDir, TimeSpan within)
    {
        try
        {
            if (!Directory.Exists(statusDir)) return false;
            var limit = DateTime.UtcNow - within;
            var own = FileLock.ComputerName;
            return new DirectoryInfo(statusDir).EnumerateFiles("*.json")
                .Any(f => f.LastWriteTimeUtc > limit && !Path.GetFileNameWithoutExtension(f.Name).Equals(own, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return true;
        }
    }

    private static int Take(string from, string to, bool move, bool append)
    {
        if (!Directory.Exists(from)) return 0;
        var n = 0;
        if (move && !Directory.Exists(to))
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(to)!);
                var count = Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories).Count();
                Directory.Move(from, to);
                return count;
            }
            catch { }
        }
        foreach (var f in SafeFiles(from, "*", recursive: true))
        {
            try
            {
                var t = Path.Combine(to, Path.GetRelativePath(from, f));
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                if (!File.Exists(t))
                {
                    if (move) File.Move(f, t); else File.Copy(f, t);
                    n++;
                }
                else if (move)
                {
                    if (append && f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                    {
                        AppendFile(f, t);
                        n++;
                    }
                    File.Delete(f);
                }
            }
            catch { }
        }
        if (move) RemoveEmptyDirs(from);
        return n;
    }

    private static void AppendFile(string from, string to)
    {
        var bytes = File.ReadAllBytes(from);
        if (bytes.Length == 0) return;
        using var fs = new FileStream(to, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
        if (fs.Length > 0)
        {
            fs.Seek(-1, SeekOrigin.End);
            if (fs.ReadByte() != '\n') { fs.Seek(0, SeekOrigin.End); fs.WriteByte((byte)'\n'); }
        }
        fs.Seek(0, SeekOrigin.End);
        fs.Write(bytes, 0, bytes.Length);
    }

    // ---------------- Hilfen ----------------

    private static int CopyMissing(string from, string to)
    {
        var n = 0;
        foreach (var f in SafeFiles(from, "*", recursive: true))
        {
            try
            {
                var t = Path.Combine(to, Path.GetRelativePath(from, f));
                if (File.Exists(t)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(f, t);
                n++;
            }
            catch { }
        }
        return n;
    }

    private static void Mirror(string from, string to)
    {
        if (!Directory.Exists(from)) return;
        var cmp = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var source = new Dictionary<string, long>(cmp);
        foreach (var fi in new DirectoryInfo(from).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
            source[Path.GetRelativePath(from, fi.FullName)] = fi.Length;
        var have = new Dictionary<string, long>(cmp);
        if (Directory.Exists(to))
            foreach (var fi in new DirectoryInfo(to).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                have[Path.GetRelativePath(to, fi.FullName)] = fi.Length;

        foreach (var (rel, len) in source)
        {
            if (have.TryGetValue(rel, out var l) && l == len) continue;
            try
            {
                var t = Path.Combine(to, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(t)!);
                File.Copy(Path.Combine(from, rel), t, overwrite: true);
            }
            catch { }
        }
        foreach (var rel in have.Keys)
        {
            if (source.ContainsKey(rel)) continue;
            try { File.Delete(Path.Combine(to, rel)); } catch { }
        }
        RemoveEmptyDirs(to, keepRoot: true);
    }

    private static bool Replace(string from, string to)
    {
        var tmp = to + "." + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(from, tmp, overwrite: true);
            File.Move(tmp, to, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    private static long Length(string file)
    {
        try { var fi = new FileInfo(file); return fi.Exists ? fi.Length : 0; } catch { return 0; }
    }

    private static List<string> SafeFiles(string dir, string pattern, bool recursive = false)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern, recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    private static string? ReadText(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var t = File.ReadAllText(file).Trim();
            return t.Length == 0 ? null : t;
        }
        catch
        {
            return null;
        }
    }

    private static void WriteText(string file, string text)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, text);
        }
        catch { }
    }

    private static void Hide(string dir)
    {
        if (!OperatingSystem.IsWindows()) return;
        try
        {
            var a = File.GetAttributes(dir);
            if ((a & FileAttributes.Hidden) == 0) File.SetAttributes(dir, a | FileAttributes.Hidden);
        }
        catch { }
    }

    private static void DeleteIfStale(string dir, TimeSpan age)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            var limit = DateTime.UtcNow - age;
            var info = new DirectoryInfo(dir);
            if (info.EnumerateFiles("*", SearchOption.AllDirectories).Any(f => f.LastWriteTimeUtc > limit)) return;
            Directory.Delete(dir, recursive: true);
        }
        catch { }
    }

    private static void RemoveEmptyDirs(string dir, bool keepRoot = false)
    {
        try
        {
            if (!Directory.Exists(dir)) return;
            foreach (var d in Directory.EnumerateDirectories(dir, "*", SearchOption.AllDirectories).OrderByDescending(d => d.Length))
            {
                try { if (!Directory.EnumerateFileSystemEntries(d).Any()) Directory.Delete(d); } catch { }
            }
            if (!keepRoot && !Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }
}
