namespace FilmKonverter.Core.Jobs;

public sealed class RunTally
{
    public int Pass { get; private set; } = 1;

    // ---------------- aktueller Durchlauf ----------------
    public int Done { get; private set; }
    public int New { get; private set; }
    public int Ok { get; private set; }
    public int Skipped { get; private set; }
    public int Other { get; private set; }
    public int Errors { get; private set; }
    public long GainBytes { get; private set; }

    // ---------------- seit dem Start des Laufs ----------------
    public int TotalNew { get; private set; }
    public long TotalGainBytes { get; private set; }

    public void Count(FileEnd end)
    {
        Done++;
        switch (end.Outcome)
        {
            case Outcome.Encoded:
            case Outcome.Replaced:
                New++;
                TotalNew++;
                break;
            case Outcome.AlreadyOk: Ok++; break;
            case Outcome.Skipped: Skipped++; break;
            case Outcome.OtherPc: Other++; break;
            case Outcome.Error: Errors++; break;
        }
        if (end.GainBytes > 0)
        {
            GainBytes += end.GainBytes;
            TotalGainBytes += end.GainBytes;
        }
    }

    public void NextPass()
    {
        Pass++;
        Done = New = Ok = Skipped = Other = Errors = 0;
        GainBytes = 0;
    }

    private readonly HashSet<int> _reported = [];

    public bool ShowInHistory(string file, FileEnd end)
    {
        switch (end.Outcome)
        {
            case Outcome.AlreadyOk:
                return Pass == 1;
            case Outcome.Skipped:
            case Outcome.OtherPc:
                var first = _reported.Add(HashCode.Combine(StringComparer.OrdinalIgnoreCase.GetHashCode(file), end.Outcome, end.Message));
                return Pass == 1 || first;
            default:
                return true;
        }
    }

    public void Reset()
    {
        Pass = 1;
        Done = New = Ok = Skipped = Other = Errors = TotalNew = 0;
        GainBytes = TotalGainBytes = 0;
        _reported.Clear();
    }
}
