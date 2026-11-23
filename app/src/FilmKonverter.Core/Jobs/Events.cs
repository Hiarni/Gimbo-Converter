using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public enum Outcome
{
    Encoded,
    Replaced,
    AlreadyOk,
    Skipped,
    OtherPc,
    Error,
    Cancelled,
}

public enum LineKind
{
    Info,
    Ok,
    Warn,
    Error,
    Dim,
}

public sealed record FileStart(int Number, int Total, string RelativePath, string FullPath, long SourceBytes = 0, int LimitPercent = 0, int Slot = 0);

public sealed record InfoLine(string Label, string Text, LineKind Kind = LineKind.Info);

public sealed record FileEnd(Outcome Outcome, string Message, long GainBytes = 0)
{
    public string? Label { get; init; }

    public string Badge => Label ?? Outcome switch
    {
        Outcome.Encoded => L.T("KOMPRIMIERT", "COMPRESSED"),
        Outcome.Replaced => L.T("ZIEL ERNEUERT", "TARGET REPLACED"),
        Outcome.AlreadyOk => L.T("VORHANDEN", "EXISTS"),
        Outcome.Skipped => L.T("ÜBERSPRUNGEN", "SKIPPED"),
        Outcome.OtherPc => L.T("ANDERER PC", "OTHER PC"),
        Outcome.Cancelled => L.T("ABGEBROCHEN", "CANCELLED"),
        _ => L.T("FEHLER", "ERROR"),
    };
}

public interface IRunObserver
{
    void ScanStarted(string folder);
    void ScanDone(int total);
    void FileStarted(FileStart start);
    void Line(InfoLine line);
    void Progress(string stage, ProgressInfo info);
    void FileFinished(FileStart start, FileEnd end);
    void RunFinished(RunCounters counters, bool cancelled);

    void Status(string text) { }

    void Line(int slot, InfoLine line) => Line(line);

    void Progress(int slot, string stage, ProgressInfo info) => Progress(stage, info);
}

public sealed class RunCounters
{
    public int Total { get; set; }
    public int Processed { get; set; }
    public int Encoded { get; set; }
    public int Replaced { get; set; }
    public int AlreadyOk { get; set; }
    public int Skipped { get; set; }
    public int OtherPc { get; set; }
    public int Errors { get; set; }
    public int Filtered { get; set; }
    public string FilterText { get; set; } = "";
    public int TooNew { get; set; }
    public int WouldChange { get; set; }
    public bool DiskFull { get; set; }
    public long GainBytes { get; set; }
    public double EncodeSeconds { get; set; }
    public DateTime Started { get; } = DateTime.Now;
    public Dictionary<string, int> SkipReasons { get; } = new();

    private readonly object _lock = new();

    public void AddEncodeSeconds(double s)
    {
        lock (_lock) EncodeSeconds += s;
    }

    public void Count(FileEnd e, string? skipReason = null)
    {
        lock (_lock) CountCore(e, skipReason);
    }

    private void CountCore(FileEnd e, string? skipReason)
    {
        Processed++;
        switch (e.Outcome)
        {
            case Outcome.Encoded: Encoded++; GainBytes += e.GainBytes; break;
            case Outcome.Replaced: Replaced++; GainBytes += e.GainBytes; break;
            case Outcome.AlreadyOk: AlreadyOk++; break;
            case Outcome.Skipped:
                Skipped++;
                if (skipReason is not null) SkipReasons[skipReason] = SkipReasons.GetValueOrDefault(skipReason) + 1;
                break;
            case Outcome.OtherPc: OtherPc++; break;
            case Outcome.Error: Errors++; break;
        }
    }
}
