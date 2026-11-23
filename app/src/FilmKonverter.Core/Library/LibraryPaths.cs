using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public sealed class LibraryPaths
{
    public const string MarkFolder = "_Merkliste";
    public const string VerifyFolder = "_Pruefung";
    public const string TestFolder = "_Test";
    public const string BrokenFolder = "_Defekt";
    public const string StatusFolder = "_Status";
    public const string StatsFolder = "_Statistik";
    public const string QueueFolder = "_Warteschlange";

    public string Source { get; }
    public string Name { get; }
    public string RelativeDir { get; }
    public string RelativeFile { get; }
    public string TargetDir { get; }
    public string Output { get; }
    public string Temp { get; }
    public string Lock { get; }
    public string MarkDir { get; }
    public string OutputRoot { get; }

    public LibraryPaths(string inputRoot, string outputRoot, string source)
    {
        Source = source;
        OutputRoot = outputRoot.TrimEnd('\\', '/');
        DataStore.Link(OutputRoot, inputRoot);
        Name = Path.GetFileNameWithoutExtension(source);
        var rel = Path.GetRelativePath(inputRoot, Path.GetDirectoryName(source) ?? inputRoot);
        RelativeDir = rel == "." ? "" : rel;
        TargetDir = RelativeDir.Length == 0 ? OutputRoot : Path.Combine(OutputRoot, RelativeDir);
        Output = Path.Combine(TargetDir, Name + ".mkv");
        Temp = Path.Combine(TargetDir, Name + ".part.mkv");
        Lock = Path.Combine(TargetDir, Name + ".lock");
        MarkDir = RelativeDir.Length == 0 ? DataStore.Marks(OutputRoot) : Path.Combine(DataStore.Marks(OutputRoot), RelativeDir);
        var relSource = Path.GetRelativePath(inputRoot, source);
        RelativeFile = relSource;
    }

    public string RelativeOutput => Path.GetRelativePath(OutputRoot, Output);
}

public sealed class ScanFilter
{
    public IEnumerable<string> Extensions { get; init; } = [".mkv"];
    public string ExcludePatterns { get; init; } = "";
    public long MinBytes { get; init; }
    public TimeSpan SkipNewerThan { get; init; }
    public FileOrder Order { get; init; } = FileOrder.Name;
    public string? ExcludeDir { get; init; }

    public Func<string, long, DateTime, double?>? RateOf { get; init; }

    public static ScanFilter FromProfile(Profile p, Func<string, long, DateTime, double?>? rateOf = null) => new()
    {
        RateOf = rateOf,
        Extensions = p.Extensions,
        ExcludePatterns = p.ExcludePatterns,
        MinBytes = Math.Max(0, p.MinSizeMb) * 1024L * 1024L,
        SkipNewerThan = TimeSpan.FromMinutes(Math.Max(0, p.SkipNewerThanMinutes)),
        Order = p.Order,
        ExcludeDir = Scanner.IsInside(p.OutputDir, p.InputDir) ? p.OutputDir : null,
    };
}

public sealed record ScanResult(List<string> Files, int Excluded, int TooSmall, int TooNew, int Duplicates = 0)
{
    public List<string> DroppedDuplicates { get; init; } = [];

    public int Filtered => Excluded + TooSmall + TooNew + Duplicates;

    public string FilterText
    {
        get
        {
            var parts = new List<string>();
            if (Excluded > 0) parts.Add(L.T($"{Excluded} per Muster", $"{Excluded} by pattern"));
            if (TooSmall > 0) parts.Add(L.T($"{TooSmall} zu klein", $"{TooSmall} too small"));
            if (TooNew > 0) parts.Add(L.T($"{TooNew} zu neu", $"{TooNew} too new"));
            if (Duplicates > 0) parts.Add(L.T($"{Duplicates} mit gleichem Namen (nur die größte Quelle zählt)", $"{Duplicates} with the same name (only the largest source counts)"));
            return string.Join(", ", parts);
        }
    }
}

public static class Scanner
{
    private static readonly string[] ExcludedFolders = [LibraryPaths.MarkFolder, LibraryPaths.VerifyFolder, LibraryPaths.TestFolder, LibraryPaths.BrokenFolder, LibraryPaths.StatusFolder, LibraryPaths.StatsFolder, LibraryPaths.QueueFolder, DataStore.SharedFolder, "_Umbenennung", "_Originale"];

    public static DateTime ChangedAt(FileInfo fi)
    {
        var w = fi.LastWriteTime;
        var c = fi.CreationTime;
        return c > w ? c : w;
    }

    public static DateTime ChangedAt(string file) => ChangedAt(new FileInfo(file));

    public static bool IsFresh(FileInfo fi, TimeSpan window)
    {
        if (window <= TimeSpan.Zero) return false;
        var newest = DateTime.Now - window;
        if (fi.LastWriteTime > newest) return true;
        return fi.CreationTime > newest && StillBeingWritten(fi.FullName);
    }

    public static bool IsFresh(string file, TimeSpan window) => IsFresh(new FileInfo(file), window);

    public static bool StillBeingWritten(string file)
    {
        try
        {
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
            return false;
        }
        catch
        {
            return true;
        }
    }

    public static bool IsTemp(string file)
    {
        var stem = Path.GetFileNameWithoutExtension(file);
        return stem.EndsWith(".part", StringComparison.Ordinal) || stem.Contains(".reparatur.tmp", StringComparison.OrdinalIgnoreCase);
    }

    public static List<string> Find(string root, IEnumerable<string> extensions) =>
        Find(root, new ScanFilter { Extensions = extensions }).Files;

    public static ScanResult Find(string root, ScanFilter filter)
    {
        var exts = new HashSet<string>(filter.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var patterns = ParsePatterns(filter.ExcludePatterns);
        var opts = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System };
        var excludeDir = filter.ExcludeDir is { Length: > 0 } d ? Path.GetFullPath(d).TrimEnd('\\', '/') : null;
        var list = new List<FileInfo>();
        int excluded = 0, small = 0, fresh = 0;
        foreach (var f in Directory.EnumerateFiles(root, "*", opts))
        {
            if (!exts.Contains(Path.GetExtension(f))) continue;
            if (IsTemp(f)) continue;
            var rel = Path.GetRelativePath(root, f);
            var first = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (ExcludedFolders.Any(x => rel.Contains(Path.DirectorySeparatorChar + x + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                                          || first.Equals(x, StringComparison.OrdinalIgnoreCase))) continue;
            if (excludeDir is not null && IsInside(f, excludeDir)) continue;
            if (patterns.Count > 0 && patterns.Any(p => WildcardMatch(Path.GetFileName(f), p))) { excluded++; continue; }
            var fi = new FileInfo(f);
            long len;
            try { len = fi.Length; } catch { continue; }
            if (filter.MinBytes > 0 && len < filter.MinBytes) { small++; continue; }
            bool isFresh;
            try { isFresh = IsFresh(fi, filter.SkipNewerThan); } catch { continue; }
            if (isFresh) { fresh++; continue; }
            list.Add(fi);
        }

        var dupes = 0;
        var dropped = new List<string>();
        list = list.GroupBy(x => Path.Combine(x.DirectoryName ?? "", Path.GetFileNameWithoutExtension(x.Name)), StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                dupes += g.Count() - 1;
                if (g.Count() > 1)
                {
                    var keep = g.OrderByDescending(x => x.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).First();
                    dropped.AddRange(g.Where(x => x != keep).Select(x => x.FullName));
                }
                return g.OrderByDescending(x => x.Length).ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase).First();
            })
            .ToList();

        IEnumerable<FileInfo> sorted = filter.Order switch
        {
            FileOrder.LargestFirst => list.OrderByDescending(x => x.Length).ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase),
            FileOrder.NewestFirst => list.OrderByDescending(x => x.LastWriteTimeUtc).ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase),
            FileOrder.OldestFirst => list.OrderBy(x => x.LastWriteTimeUtc).ThenBy(x => x.FullName, StringComparer.OrdinalIgnoreCase),
            FileOrder.HighestRateFirst => ByRate(list, filter.RateOf),
            _ => list.OrderBy(x => x.FullName, StringComparer.OrdinalIgnoreCase),
        };
        return new ScanResult(sorted.Select(x => x.FullName).ToList(), excluded, small, fresh, dupes) { DroppedDuplicates = dropped };
    }

    private static IEnumerable<FileInfo> ByRate(List<FileInfo> list, Func<string, long, DateTime, double?>? rateOf)
    {
        var rated = list.Select(x => (F: x, R: rateOf?.Invoke(x.FullName, x.Length, x.LastWriteTimeUtc))).ToList();
        return rated.Where(x => x.R is not null).OrderByDescending(x => x.R).ThenBy(x => x.F.FullName, StringComparer.OrdinalIgnoreCase).Select(x => x.F)
            .Concat(rated.Where(x => x.R is null).OrderByDescending(x => x.F.Length).ThenBy(x => x.F.FullName, StringComparer.OrdinalIgnoreCase).Select(x => x.F));
    }

    public static List<string> ParsePatterns(string text) =>
        (text ?? "").Split(new[] { ';', ',', '|' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => p.Contains('*') || p.Contains('?') ? p : "*" + p + "*").ToList();

    public static bool WildcardMatch(string text, string pattern)
    {
        int t = 0, p = 0, star = -1, mark = 0;
        while (t < text.Length)
        {
            if (p < pattern.Length && (pattern[p] == '?' || char.ToUpperInvariant(pattern[p]) == char.ToUpperInvariant(text[t]))) { t++; p++; }
            else if (p < pattern.Length && pattern[p] == '*') { star = p++; mark = t; }
            else if (star >= 0) { p = star + 1; t = ++mark; }
            else return false;
        }
        while (p < pattern.Length && pattern[p] == '*') p++;
        return p == pattern.Length;
    }

    public static bool IsInside(string path, string folder)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrWhiteSpace(folder)) return false;
        try
        {
            var a = Path.GetFullPath(path).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var b = Path.GetFullPath(folder).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            return a.StartsWith(b, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        }
        catch
        {
            return false;
        }
    }
}

public static class Sidecars
{
    public static readonly string[] Extensions = [".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".nfo", ".jpg", ".jpeg", ".png", ".webp"];

    public static int Copy(string source, string targetDir)
    {
        var dir = Path.GetDirectoryName(source);
        if (dir is null || !Directory.Exists(dir)) return 0;
        var name = Path.GetFileNameWithoutExtension(source);
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, name + ".*"))
        {
            var fn = Path.GetFileName(f);
            if (!Extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            var rest = fn[name.Length..];
            if (!(rest.StartsWith('.') || rest.Length == 0)) continue;
            var target = Path.Combine(targetDir, fn);
            if (File.Exists(target)) continue;
            try { File.Copy(f, target); n++; } catch { }
        }
        return n;
    }
}
