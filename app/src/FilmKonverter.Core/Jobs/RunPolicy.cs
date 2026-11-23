namespace FilmKonverter.Core.Jobs;

public static class RunPolicy
{
    public static int RetryMinutes(int failuresInARow, int rescanMinutes)
    {
        var quick = failuresInARow switch { <= 1 => 1, 2 => 2, _ => 5 };
        return Math.Min(Math.Max(1, rescanMinutes), quick);
    }

    public static bool NeedsAwake(bool running, bool fileInProgress, bool windowClosed, bool waitingIdle) =>
        running && (fileInProgress || (!windowClosed && !waitingIdle));
}
