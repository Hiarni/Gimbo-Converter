using System.Text.RegularExpressions;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public enum ProblemKind
{
    SkipList,
    Error,
    Quality,
    Broken,
}

public sealed record ProblemItem(ProblemKind Kind, string Relative, string Reason, DateTime? Time, string? SourcePath, string? MarkFile, string Machine = "");

public static partial class ProblemFinder
{
    [GeneratedRegex(@"^(?<name>.+)\.(?<tag>G\d+|CQ\d+_G\d+|QP[\d-]+_G\d+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkName();

    public static string ReasonText(string raw) => raw.Trim() switch
    {
        "Fruehabbruch" => L.T("Früh abgebrochen: wäre nicht klein genug geworden", "Aborted early: would not have become small enough"),
        "nicht kleiner als Quelle" => L.T("Neue Datei war nicht kleiner als die Quelle", "New file was not smaller than the source"),
        "ueber der Grenze" => L.T("Neue Datei lag über der Grenze", "New file was above the limit"),
        "nicht kleiner als vorhandene Datei" => L.T("Neue Datei war nicht kleiner als die vorhandene", "New file was not smaller than the existing one"),
        var x => x,
    };

    public static List<ProblemItem> Find(Profile p, IReadOnlyList<StatsRecord> stats, CancellationToken ct = default)
    {
        var list = new List<ProblemItem>();
        if (string.IsNullOrWhiteSpace(p.OutputDir) || !Directory.Exists(p.OutputDir)) return list;

        var markRoot = DataStore.Marks(p.OutputDir);
        var failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in SafeFiles(markRoot, "*.txt"))
        {
            ct.ThrowIfCancellationRequested();
            if (ErrorMarks.TryParseName(f, out _, out _))
            {
                if (FailureItem(p, markRoot, f) is { } item)
                {
                    list.Add(item);
                    failed.Add(Key(item.Relative));
                }
                continue;
            }
            var m = MarkName().Match(Path.GetFileName(f));
            if (!m.Success) continue;
            var tag = m.Groups["tag"].Value;
            if (!tag.Equals(p.MarkTag, StringComparison.OrdinalIgnoreCase) && !tag.EndsWith("_" + p.MarkTag, StringComparison.OrdinalIgnoreCase)) continue;
            var relDir = Path.GetRelativePath(markRoot, Path.GetDirectoryName(f)!);
            if (relDir == ".") relDir = "";
            var name = m.Groups["name"].Value;
            string reason = "", machine = "";
            try
            {
                var parts = File.ReadAllText(f).Split(" - ");
                reason = ReasonText(parts[0]);
                if (parts.Length > 3) machine = parts[3].Trim();
            }
            catch { }
            var source = FindSource(p, relDir, name);
            list.Add(new ProblemItem(ProblemKind.SkipList, Path.Combine(relDir, name), reason, File.GetLastWriteTime(f), source, f, machine));
        }

        foreach (var g in stats.GroupBy(r => r.File, StringComparer.OrdinalIgnoreCase))
        {
            var last = g.OrderBy(r => r.TimeUtc).Last();
            var src = Path.Combine(p.InputDir, last.File);
            var outFile = new LibraryPaths(p.InputDir, p.OutputDir, src).Output;
            if (last.Outcome == "Error" && failed.Contains(Key(last.File))) continue;
            if (last.Outcome == "Error" && !File.Exists(outFile))
                list.Add(new ProblemItem(ProblemKind.Error, last.File, last.Reason, last.TimeUtc.ToLocalTime(), File.Exists(src) ? src : null, null, last.Machine));
            else if (last.IsSuccess && last.SsimLow && File.Exists(outFile) && new FileInfo(outFile).Length == last.OutputBytes)
                list.Add(new ProblemItem(ProblemKind.Quality, last.File,
                    L.T($"SSIM {last.Ssim?.ToString("0.000", Fmt.De)} unter der Grenze", $"SSIM {last.Ssim?.ToString("0.000", Fmt.De)} below the limit"),
                    last.TimeUtc.ToLocalTime(), File.Exists(src) ? src : null, null, last.Machine));
        }

        var brokenRoot = Path.Combine(p.OutputDir, LibraryPaths.BrokenFolder);
        foreach (var f in SafeFiles(brokenRoot, "*.mkv"))
        {
            var rel = Path.GetRelativePath(brokenRoot, f);
            var relDir = Path.GetDirectoryName(rel) ?? "";
            var source = FindSource(p, relDir, Path.GetFileNameWithoutExtension(f));
            list.Add(new ProblemItem(ProblemKind.Broken, rel, L.T("Beim Prüflauf als beschädigt aussortiert", "Moved aside as damaged by the verification"), File.GetLastWriteTime(f), source, null));
        }
        return list.OrderBy(i => i.Kind).ThenByDescending(i => i.Time).ToList();
    }

    private static string Key(string relative)
    {
        var r = relative.Replace('\\', '/');
        var dir = r.Contains('/') ? r[..(r.LastIndexOf('/') + 1)] : "";
        return dir + Path.GetFileNameWithoutExtension(r[dir.Length..]);
    }

    private static ProblemItem? FailureItem(Profile p, string markRoot, string file)
    {
        if (ErrorMarks.Read(file) is not { } mark) return null;
        var relDir = Path.GetRelativePath(markRoot, Path.GetDirectoryName(file)!);
        if (relDir == ".") relDir = "";
        var source = FindSource(p, relDir, mark.Name);
        try { if (source is null || new FileInfo(source).Length != mark.SourceBytes) return null; }
        catch { return null; }
        var reason = mark.Blocks
            ? L.T($"{mark.Count}-mal fehlgeschlagen, wird nicht mehr von selbst versucht. Zuletzt: {mark.Reason}", $"Failed {mark.Count} times, no longer tried automatically. Last: {mark.Reason}")
            : L.T($"{mark.Count}-mal fehlgeschlagen, dieser PC versucht es noch einmal. Zuletzt: {mark.Reason}", $"Failed {mark.Count} times, this PC tries once more. Last: {mark.Reason}");
        return new ProblemItem(ProblemKind.Error, Path.GetRelativePath(p.InputDir, source), reason, mark.Time, source, file, mark.Machine);
    }

    private static string? FindSource(Profile p, string relDir, string name)
    {
        try
        {
            var dir = relDir.Length == 0 ? p.InputDir : Path.Combine(p.InputDir, relDir);
            if (!Directory.Exists(dir)) return null;
            var exts = new HashSet<string>(p.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
            return Directory.EnumerateFiles(dir, name + ".*").FirstOrDefault(f => exts.Contains(Path.GetExtension(f))
                && Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static IEnumerable<string> SafeFiles(string dir, string pattern)
    {
        try
        {
            return Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir, pattern, new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static bool Reset(ProblemItem item)
    {
        if (item.MarkFile is null) return false;
        try
        {
            File.Delete(item.MarkFile);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
