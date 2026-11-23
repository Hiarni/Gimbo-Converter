using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.ViewModels;

public sealed partial class SlotViewModel : ObservableObject
{
    public SlotViewModel(int slot) => Slot = slot;

    public int Slot { get; }

    public ObservableCollection<LineItem> Lines { get; } = [];

    public FileStart? Current { get; private set; }

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _currentName = "";
    [ObservableProperty] private string _currentPosition = "";
    [ObservableProperty] private string _stageName = "";
    [ObservableProperty] private double _stagePercent;
    [ObservableProperty] private bool _stageIndeterminate;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private string _timeText = "";
    [ObservableProperty] private string _etaText = "";
    [ObservableProperty] private string _fpsText = "";
    [ObservableProperty] private string _speedText = "";
    [ObservableProperty] private string _tempoText = "";
    [ObservableProperty] private string _bitrateText = "";
    [ObservableProperty] private string _sizeText = "";
    [ObservableProperty] private string _estimateText = "";
    [ObservableProperty] private bool _estimateWarn;
    [ObservableProperty] private bool _hasStage;

    public double Fraction { get; private set; }
    public double Speed { get; private set; }
    public double EtaSeconds { get; private set; } = -1;

    public void Start(FileStart start)
    {
        Current = start;
        Lines.Clear();
        HasStage = false;
        Busy = true;
        Fraction = 0;
        Speed = 0;
        EtaSeconds = -1;
        CurrentName = start.RelativePath;
        CurrentPosition = L.T($"Datei {start.Number.ToString("N0", Fmt.De)} / {start.Total.ToString("N0", Fmt.De)}", $"File {start.Number.ToString("N0", Fmt.De)} / {start.Total.ToString("N0", Fmt.De)}");
    }

    public void Finish()
    {
        Busy = false;
        HasStage = false;
        Fraction = 0;
    }

    public void Reset()
    {
        Current = null;
        Lines.Clear();
        Busy = false;
        HasStage = false;
        CurrentName = "";
        CurrentPosition = "";
        Fraction = 0;
    }

    public void Progress(string stage, ProgressInfo pi)
    {
        HasStage = true;
        StageName = stage switch
        {
            "Encoding" => L.T("Kodieren", "Encoding"),
            "Decode" => L.T("Prüfen", "Checking"),
            _ => stage,
        };
        StageIndeterminate = pi.Fraction < 0;
        StagePercent = pi.Fraction < 0 ? 0 : pi.Fraction * 100;
        Fraction = stage == "Encoding" && pi.Fraction > 0 ? pi.Fraction : Fraction;
        Speed = pi.Speed;
        EtaSeconds = pi.EtaSeconds;
        PercentText = pi.Fraction < 0 ? "" : Fmt.Percent(pi.Fraction);
        TimeText = $"{Fmt.Time(pi.PositionSeconds)} / {Fmt.Time(pi.DurationSeconds)}";
        EtaText = pi.EtaSeconds < 0 || double.IsNaN(pi.EtaSeconds) ? "--:--:--" : Fmt.Time(pi.EtaSeconds);
        FpsText = pi.Fps > 0 ? pi.Fps.ToString("N0", Fmt.De) + " fps" : "";
        SpeedText = RunTexts.Speed(pi.Speed);
        TempoText = string.Join(FileCard.Sep, new[] { FpsText, SpeedText }.Where(x => x.Length > 0));
        BitrateText = pi.BitrateKbit > 0 ? Fmt.Mbit(pi.BitrateKbit) + " Mbit/s" : "";
        SizeText = pi.SizeBytes > 0 ? Fmt.Size(pi.SizeBytes) : "";
        if (pi.EstimatedFinalBytes > 0 && Current is not null)
        {
            var src = Current.SourceBytes;
            EstimateText = FileCard.Estimate((long)pi.EstimatedFinalBytes, src);
            var limit = Current.LimitPercent;
            EstimateWarn = src > 0 && limit > 0 && pi.EstimatedFinalBytes > src * limit / 100d;
        }
        else if (pi.Final)
        {
            EstimateText = "";
        }
    }

    public PeerFile ToPeer() => new()
    {
        Name = CurrentName,
        Percent = Math.Round(StagePercent, 1),
        Speed = Math.Round(Speed, 2),
        EtaSeconds = Math.Round(EtaSeconds),
        Stage = HasStage ? StageName : "",
    };
}
