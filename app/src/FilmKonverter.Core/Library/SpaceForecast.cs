using System.Text.Json;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public sealed class AnalysisSnapshot
{
    public string Profile { get; set; } = "";
    public string OutputDir { get; set; } = "";
    public DateTime CreatedUtc { get; set; }
    public int OpenFiles { get; set; }
    public long OpenBytes { get; set; }
    public long EstimatedOutputBytes { get; set; }

    public static AnalysisSnapshot From(string profile, string outputDir, LibraryAnalysis a)
    {
        var open = a.Files.Where(f => f.State == AnalysisState.Open).ToList();
        return new AnalysisSnapshot
        {
            Profile = profile,
            OutputDir = outputDir,
            CreatedUtc = DateTime.UtcNow,
            OpenFiles = open.Count,
            OpenBytes = open.Sum(f => f.SourceBytes),
            EstimatedOutputBytes = open.Sum(f => f.EstimatedBytes),
        };
    }
}

public sealed record SpaceVerdict(bool Warn, long FreeBytes, long NeededBytes, double Fraction, string Text);

public static class SpaceForecast
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static Dictionary<string, AnalysisSnapshot> Load(string file)
    {
        try
        {
            if (File.Exists(file) && JsonSerializer.Deserialize<Dictionary<string, AnalysisSnapshot>>(File.ReadAllText(file), Json) is { } d)
                return new Dictionary<string, AnalysisSnapshot>(d, StringComparer.OrdinalIgnoreCase);
        }
        catch { }
        return new(StringComparer.OrdinalIgnoreCase);
    }

    public static void Save(string file, Dictionary<string, AnalysisSnapshot> data)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, JsonSerializer.Serialize(data, Json));
        }
        catch { }
    }

    public static SpaceVerdict Evaluate(AnalysisSnapshot snap, long freeBytes, long reserveBytes, IEnumerable<StatsRecord> stats)
    {
        var written = stats.Where(r => r.TimeUtc > snap.CreatedUtc && r.IsSuccess).Sum(r => r.OutputBytes);
        var needed = Math.Max(0, snap.EstimatedOutputBytes - written);
        var usable = Math.Max(0, freeBytes - reserveBytes);
        if (freeBytes < 0 || needed == 0) return new SpaceVerdict(false, freeBytes, needed, 1, "");
        var fraction = Math.Min(1, usable / (double)needed);
        if (usable >= needed)
            return new SpaceVerdict(false, freeBytes, needed, 1,
                L.T($"Platz reicht: {Fmt.Size(freeBytes)} frei, für den Rest werden etwa {Fmt.Size(needed)} gebraucht.",
                    $"Space is sufficient: {Fmt.Size(freeBytes)} free, about {Fmt.Size(needed)} needed for the rest."));
        var date = snap.CreatedUtc.ToLocalTime().ToString(L.German ? "dd.MM." : "d MMM", L.Culture);
        return new SpaceVerdict(true, freeBytes, needed, fraction,
            L.T($"Speicherplatz wird knapp: Im Ziel sind {Fmt.Size(freeBytes)} frei" + (reserveBytes > 0 ? $" (davon {Fmt.Size(reserveBytes)} Reserve)" : "")
                + $", für den Rest der Bibliothek werden laut Analyse vom {date} etwa {Fmt.Size(needed)} gebraucht. Das reicht für etwa {fraction * 100:N0} %.",
                $"Disk space is getting tight: {Fmt.Size(freeBytes)} free in the target" + (reserveBytes > 0 ? $" ({Fmt.Size(reserveBytes)} of it reserve)" : "")
                + $", the rest of the library needs about {Fmt.Size(needed)} according to the analysis of {date}. That is enough for about {fraction * 100:N0} %."));
    }
}
