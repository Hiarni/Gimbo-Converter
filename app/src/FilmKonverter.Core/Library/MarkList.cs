namespace FilmKonverter.Core.Library;

public static class MarkList
{
    public static string MarkFile(LibraryPaths p, string tag) => Path.Combine(p.MarkDir, $"{p.Name}.{tag}.txt");

    public static bool IsMarked(LibraryPaths p, string tag)
    {
        if (File.Exists(MarkFile(p, tag))) return true;
        if (!Directory.Exists(p.MarkDir)) return false;
        try
        {
            return Directory.EnumerateFiles(p.MarkDir, $"{p.Name}.CQ*_{tag}.txt").Any()
                || Directory.EnumerateFiles(p.MarkDir, $"{p.Name}.QP*_{tag}.txt").Any();
        }
        catch
        {
            return false;
        }
    }

    public static void Mark(LibraryPaths p, string tag, string reason, string settingsText, int limit)
    {
        try
        {
            Directory.CreateDirectory(p.MarkDir);
            File.WriteAllText(MarkFile(p, tag),
                $"{reason} - {settingsText} - Grenze {limit} - {FileLock.ComputerName} - Quelle {p.Source}");
            DataStore.Backup(p.OutputRoot, MarkFile(p, tag));
        }
        catch
        {
        }
    }

    public static string OkFile(LibraryPaths p, string tag, long sourceBytes, long outputBytes) =>
        Path.Combine(p.MarkDir, $"{p.Name}.ok_{tag}_{sourceBytes}_{outputBytes}.txt");

    public static bool IsOk(LibraryPaths p, string tag, long sourceBytes, long outputBytes) =>
        File.Exists(OkFile(p, tag, sourceBytes, outputBytes));

    public static void MarkOk(LibraryPaths p, string tag, long sourceBytes, long outputBytes)
    {
        try
        {
            Directory.CreateDirectory(p.MarkDir);
            File.WriteAllText(OkFile(p, tag, sourceBytes, outputBytes), $"erfuellt die Vorgaben - {DateTime.Now:yyyy-MM-dd HH:mm} - {FileLock.ComputerName}");
            DataStore.Backup(p.OutputRoot, OkFile(p, tag, sourceBytes, outputBytes));
        }
        catch { }
    }

    public static string VerifyFile(string outputRoot, string outputFile, long size)
    {
        var rel = Path.GetRelativePath(outputRoot, outputFile);
        return Path.Combine(DataStore.VerifyMarks(outputRoot), rel + "." + size + ".ok");
    }

    public static bool IsVerified(string outputRoot, string outputFile, long size) => File.Exists(VerifyFile(outputRoot, outputFile, size));

    public static void MarkVerified(string outputRoot, string outputFile, long size, string how)
    {
        try
        {
            var f = VerifyFile(outputRoot, outputFile, size);
            Directory.CreateDirectory(Path.GetDirectoryName(f)!);
            File.WriteAllText(f, $"geprueft {how} - {DateTime.Now:yyyy-MM-dd HH:mm} - {FileLock.ComputerName}");
        }
        catch { }
    }

    public static IEnumerable<string> OwnMarks(string dir, string name)
    {
        if (!Directory.Exists(dir)) return [];
        var rx = new System.Text.RegularExpressions.Regex("^" + System.Text.RegularExpressions.Regex.Escape(name)
            + @"\.(G\d+|CQ\d+_G\d+|QP[\d-]+_G\d+|ok_G\d+_\d+_\d+|fehler_\d+)\.txt$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        try { return Directory.EnumerateFiles(dir, name + ".*.txt").Where(f => rx.IsMatch(Path.GetFileName(f))).ToList(); }
        catch { return []; }
    }

    public static int Clear(string outputRoot, string outputFile)
    {
        var rel = Path.GetDirectoryName(Path.GetRelativePath(outputRoot, outputFile)) ?? "";
        var dir = rel.Length == 0 ? DataStore.Marks(outputRoot) : Path.Combine(DataStore.Marks(outputRoot), rel);
        if (!Directory.Exists(dir)) return 0;
        var name = Path.GetFileNameWithoutExtension(outputFile);
        var n = 0;
        foreach (var f in OwnMarks(dir, name))
        {
            try { File.Delete(f); n++; DataStore.BackupDelete(outputRoot, f); } catch { }
        }
        return n;
    }
}
