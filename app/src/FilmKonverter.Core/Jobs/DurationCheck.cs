using FilmKonverter.Core.Media;

namespace FilmKonverter.Core.Jobs;

public static class DurationCheck
{
    public const double CopyTolerance = 0.5;

    public static double EncodeTolerance(MediaInfo source) => source.DurationFromTimestamps
        ? Math.Max(3, source.DurationSeconds * 0.015)
        : Math.Max(1, source.DurationSeconds * 0.002);

    public static bool Matches(MediaInfo source, double outputSeconds, double tolerance) =>
        Math.Abs(source.DurationSeconds - outputSeconds) <= tolerance
        || (source.StartSeconds > 0 && Math.Abs(source.DurationSeconds - source.StartSeconds - outputSeconds) <= tolerance);
}
