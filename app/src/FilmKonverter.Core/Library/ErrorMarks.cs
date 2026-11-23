using System.Text.RegularExpressions;

namespace FilmKonverter.Core.Library;

public sealed record ErrorMark(string File, string Name, long SourceBytes, int Count, DateTime Time, string Machine, string Reason)
{
    public int OwnCount { get; init; }

    public bool Blocks => OwnCount >= ErrorMarks.MaxFailures;
}

public static partial class ErrorMarks
{
    public const int MaxFailures = 2;

    [GeneratedRegex(@"^(?<name>.+)\.fehler_(?<bytes>\d+)\.txt$", RegexOptions.IgnoreCase)]
    private static partial Regex MarkName();

    public static string FileFor(LibraryPaths p, long sourceBytes) => Path.Combine(p.MarkDir, $"{p.Name}.fehler_{sourceBytes}.txt");

    public static bool TryParseName(string file, out string name, out long sourceBytes)
    {
        var m = MarkName().Match(Path.GetFileName(file));
        name = m.Success ? m.Groups["name"].Value : "";
        sourceBytes = 0;
        return m.Success && long.TryParse(m.Groups["bytes"].Value, out sourceBytes);
    }

    public static ErrorMark? Read(string file)
    {
        if (!TryParseName(file, out var name, out var bytes)) return null;
        try
        {
            var lines = File.ReadAllLines(file).Where(l => l.Trim().Length > 0).ToList();
            if (lines.Count == 0) return null;
            var parts = lines[^1].Split(" - ", 4);
            var own = lines.Count(l => l.Split(" - ", 4) is { Length: > 2 } p && p[2].Trim().Equals(FileLock.ComputerName, StringComparison.OrdinalIgnoreCase));
            return new ErrorMark(file, name, bytes, lines.Count, File.GetLastWriteTime(file),
                parts.Length > 2 ? parts[2].Trim() : "", parts.Length > 3 ? parts[3].Trim() : lines[^1].Trim()) { OwnCount = own };
        }
        catch
        {
            return null;
        }
    }

    public static ErrorMark? Get(LibraryPaths p, long sourceBytes)
    {
        var f = FileFor(p, sourceBytes);
        try { return File.Exists(f) ? Read(f) : null; }
        catch { return null; }
    }

    public static int Add(LibraryPaths p, long sourceBytes, string reason)
    {
        try
        {
            Directory.CreateDirectory(p.MarkDir);
            var f = FileFor(p, sourceBytes);
            foreach (var old in Own(p).Where(o => !SamePath(o, f))) TryDelete(old);
            var lines = File.Exists(f) ? File.ReadAllLines(f).Where(l => l.Trim().Length > 0).ToList() : [];
            var text = string.Join(" ", reason.Split('\r', '\n').Select(x => x.Trim()).Where(x => x.Length > 0));
            lines.Add($"{lines.Count + 1} - {DateTime.Now:yyyy-MM-dd HH:mm} - {FileLock.ComputerName} - {text}");
            File.WriteAllLines(f, lines);
            return lines.Count;
        }
        catch
        {
            return 0;
        }
    }

    public static int Clear(LibraryPaths p)
    {
        var n = 0;
        foreach (var f in Own(p)) if (TryDelete(f)) n++;
        return n;
    }

    private static List<string> Own(LibraryPaths p)
    {
        try
        {
            if (!Directory.Exists(p.MarkDir)) return [];
            return Directory.EnumerateFiles(p.MarkDir, p.Name + ".fehler_*.txt")
                .Where(f => TryParseName(f, out var name, out _) && name.Equals(p.Name, StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static bool SamePath(string a, string b) => string.Equals(Path.GetFileName(a), Path.GetFileName(b), StringComparison.OrdinalIgnoreCase);

    private static bool TryDelete(string f)
    {
        try { File.Delete(f); return true; }
        catch { return false; }
    }
}
