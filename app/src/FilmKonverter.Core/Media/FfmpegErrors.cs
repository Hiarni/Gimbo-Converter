namespace FilmKonverter.Core.Media;

public static class FfmpegErrors
{
    private static readonly string[] ReadPatterns =
    [
        "Error during demuxing",
        "Read error",
        "I/O error",
        "Input/output error",
        "Error retrieving a packet",
        "error reading",
    ];

    private static readonly string[] DiskFullPatterns =
    [
        "No space left on device",
        "not enough space on the disk",
        "Disk quota exceeded",
        "nicht genügend Speicherplatz",
    ];

    public static bool IsReadError(string line) =>
        ReadPatterns.Any(p => line.Contains(p, StringComparison.OrdinalIgnoreCase));

    public static List<string> ReadErrors(IEnumerable<string> lines) => lines.Where(IsReadError).ToList();

    public static bool IsDiskFull(IEnumerable<string> lines) =>
        lines.Any(l => DiskFullPatterns.Any(p => l.Contains(p, StringComparison.OrdinalIgnoreCase)));
}
