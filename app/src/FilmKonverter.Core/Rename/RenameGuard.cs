using System.Text.Json;
using FilmKonverter.Core.Library;

namespace FilmKonverter.Core.Rename;

public static class RenameGuard
{
    public static IEnumerable<string> Touched(RenameItem item)
    {
        yield return item.Source;
        if (item.Library is { } lib) yield return new LibraryPaths(lib.InputRoot, lib.OutputRoot, item.Source).Output;
    }

    public static List<string>? JournalFiles(string journalFile)
    {
        try
        {
            var files = new List<string>();
            foreach (var line in File.ReadAllLines(journalFile).Skip(1))
            {
                if (JsonSerializer.Deserialize<FileMove>(line) is not { } m) continue;
                if (!string.IsNullOrEmpty(m.To)) files.Add(m.To);
                if (!string.IsNullOrEmpty(m.From)) files.Add(m.From);
            }
            return files;
        }
        catch
        {
            return null;
        }
    }

    public static string? Conflict(IEnumerable<string> files, IReadOnlyCollection<string> runFolders)
    {
        if (runFolders.Count == 0) return null;
        foreach (var file in files)
            foreach (var folder in runFolders)
                if (Scanner.IsInside(file, folder)) return file;
        return null;
    }
}
