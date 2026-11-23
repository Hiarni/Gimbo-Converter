using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.ViewModels;

public sealed record BarItem(string Label, double Height, string Tooltip, bool ShowLabel);

public sealed record RankRow(string Name, string Text, double Percent, string Value);

public sealed record MachineRow(string Machine, string Files, string Gain, string Ratio, string Speed, string Time, string Errors);

public sealed partial class StatsViewModel(AppState app) : ObservableObject
{
    public const double ChartHeight = 150;

    private List<StatsRecord> _records = [];

    public ObservableCollection<BarItem> Bars { get; } = [];
    public ObservableCollection<MachineRow> Machines { get; } = [];
    public ObservableCollection<RankRow> Codecs { get; } = [];
    public ObservableCollection<RankRow> Resolutions { get; } = [];
    public ObservableCollection<RankRow> Groups { get; } = [];
    public ObservableCollection<RankRow> TopSavings { get; } = [];
    public ObservableCollection<RankRow> WorstRatios { get; } = [];

    public IReadOnlyList<SortOption> Periods { get; } =
    [
        new(nameof(StatsPeriod.Week), L.T("Letzte 7 Tage", "Last 7 days")),
        new(nameof(StatsPeriod.Month), L.T("Letzte 30 Tage", "Last 30 days")),
        new(nameof(StatsPeriod.Quarter), L.T("Letzte 90 Tage", "Last 90 days")),
        new(nameof(StatsPeriod.ThisYear), L.T("Dieses Jahr", "This year")),
        new(nameof(StatsPeriod.TwelveMonths), L.T("Letzte 12 Monate", "Last 12 months")),
        new(nameof(StatsPeriod.All), L.T("Gesamter Zeitraum", "All time")),
    ];

    [ObservableProperty] private string _period = nameof(StatsPeriod.Month);
    partial void OnPeriodChanged(string value) => Show();

    private StatsPeriod PeriodValue => Enum.TryParse<StatsPeriod>(Period, out var p) ? p : StatsPeriod.Month;

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    private async Task ResetAsync()
    {
        var ok = System.Windows.MessageBox.Show(
            L.T("Statistik zurücksetzen? Die Protokolle aller PCs werden archiviert (nicht gelöscht), die Statistik beginnt bei null. Auch Fehler und Qualitäts-Hinweise in der Problemliste verschwinden.",
                "Reset statistics? The logs of all PCs are archived (not deleted), statistics start from zero. Errors and quality notes in the problem list disappear too."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        IsBusy = true;
        try
        {
            var roots = Roots;
            var n = await Task.Run(() => StatsLog.Archive(roots));
            app.Stats.BytesSaved = 0;
            app.Stats.FilesEncoded = 0;
            app.Stats.FilesReplaced = 0;
            app.Stats.EncodeSeconds = 0;
            app.Stats.FirstRun = null;
            app.SaveStats();
            InfoText = L.T($"Zurückgesetzt, {n} Protokolle archiviert.", $"Reset, {n} logs archived.");
        }
        finally
        {
            IsBusy = false;
        }
        var info = InfoText;
        await RefreshAsync();
        InfoText = info;
    }

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshCommand), nameof(ResetCommand))] private bool _isBusy;
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private bool _isEmpty = true;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _infoText = "";

    [ObservableProperty] private string _gainText = "–";
    [ObservableProperty] private string _gainDetail = "";
    [ObservableProperty] private string _filesText = "–";
    [ObservableProperty] private string _filesDetail = "";
    [ObservableProperty] private string _ratioText = "–";
    [ObservableProperty] private string _ratioDetail = "";
    [ObservableProperty] private string _timeText = "–";
    [ObservableProperty] private string _timeDetail = "";
    [ObservableProperty] private string _forecast = "";
    [ObservableProperty] private string _chartTitle = "";
    [ObservableProperty] private string _chartMax = "";
    [ObservableProperty] private PointCollection _cumulative = [];
    [ObservableProperty] private string _cumulativeText = "";
    [ObservableProperty] private string _footer = "";

    private bool CanRefresh() => !IsBusy;

    private List<string> Roots => app.Settings.Profiles.Select(p => p.OutputDir).Where(d => !string.IsNullOrWhiteSpace(d))
        .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    [RelayCommand(CanExecute = nameof(CanRefresh))]
    public async Task RefreshAsync()
    {
        IsBusy = true;
        Message = "";
        InfoText = "";
        try
        {
            var roots = Roots;
            _records = await Task.Run(() =>
            {
                foreach (var r in roots) FilmKonverter.Core.Library.DataStore.Sync(r);
                return StatsLog.ReadAll(roots);
            });
            Show();
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Show()
    {
        var s = StatsSummary.Build(_records, DateTime.Now, PeriodValue);
        HasData = _records.Count > 0;
        IsEmpty = !HasData;
        var legacy = app.Stats;

        GainText = Fmt.Size(s.GainBytes);
        var periodText = Periods.First(x => x.Key == Period).Text;
        GainDetail = s.Period == StatsPeriod.All && s.First is { } f
            ? L.T($"gewonnen seit {f:dd.MM.yyyy}", $"saved since {f:d}")
            : L.T($"gewonnen · {periodText}", $"saved · {periodText}");
        if (s.Period == StatsPeriod.All && legacy.BytesSaved > s.GainBytes)
            GainDetail += L.T($" · dieser PC insgesamt {Fmt.Size(legacy.BytesSaved)}", $" · this PC in total {Fmt.Size(legacy.BytesSaved)}");
        FilesText = s.Files.ToString("N0", Fmt.De);
        FilesDetail = L.T($"Dateien komprimiert · davon {s.AudioOnly:N0} nur Ton · {s.Skipped:N0} übersprungen", $"files compressed · {s.AudioOnly:N0} audio only · {s.Skipped:N0} skipped")
                      + (s.Cleaned > 0 ? L.T($" · {s.Cleaned:N0} nachgebessert", $" · {s.Cleaned:N0} touched up") : "");
        RatioText = s.SourceBytes > 0 ? Fmt.Percent(s.Ratio, 0) : "–";
        RatioDetail = s.SourceBytes > 0 ? L.T($"der Originalgröße bleiben: {Fmt.Size(s.SourceBytes)} » {Fmt.Size(s.OutputBytes)}", $"of the original size remains: {Fmt.Size(s.SourceBytes)} » {Fmt.Size(s.OutputBytes)}") : L.T("Größe nachher / vorher", "size after / before");
        TimeText = FormatHours(s.EncodeSeconds);
        TimeDetail = L.T($"Rechenzeit · {s.DurationSeconds / 3600:N0} Stunden Video · Fehlerquote {Fmt.Percent(s.ErrorRate, 1)}", $"encoding time · {s.DurationSeconds / 3600:N0} hours of video · error rate {Fmt.Percent(s.ErrorRate, 1)}");

        var open = app.Library.LastOpenBytes;
        Forecast = s.SourceBytesPerDay <= 0
            ? L.T("Prognose: sobald in den letzten 14 Tagen gearbeitet wurde.", "Forecast: as soon as there was work in the last 14 days.")
            : open is null
                ? L.T($"Tempo der letzten 14 Tage: {Fmt.Size(s.SourceBytesPerDay)} Quelle pro Tag, {Fmt.Size(s.GainBytesPerDay)} gewonnen pro Tag. Für eine Prognose einmal die Bibliothek analysieren.",
                      $"Pace of the last 14 days: {Fmt.Size(s.SourceBytesPerDay)} of source per day, {Fmt.Size(s.GainBytesPerDay)} saved per day. Analyse the library once for a forecast.")
                : s.DaysLeft(open.Value) is double dl && dl > 3650
                    ? L.T("Bei diesem Tempo dauert der Rest mehr als zehn Jahre.", "At this pace the rest takes more than ten years.")
                : s.DaysLeft(open.Value) is double d
                    ? L.T($"Bei diesem Tempo ({Fmt.Size(s.SourceBytesPerDay)} Quelle pro Tag, alle PCs zusammen) ist der Rest von {Fmt.Size(open.Value)} in etwa {d:N0} Tagen erledigt, also um den {DateTime.Now.AddDays(d):dd.MM.yyyy}.",
                          $"At this pace ({Fmt.Size(s.SourceBytesPerDay)} of source per day, all PCs together) the remaining {Fmt.Size(open.Value)} will be done in about {d:N0} days, around {DateTime.Now.AddDays(d):d}.")
                    : L.T("Laut letzter Analyse ist nichts mehr offen.", "According to the last analysis nothing is left open.");

        Bars.Clear();
        var points = s.Buckets;
        var max = Math.Max(1, points.Count == 0 ? 1 : points.Max(p => p.GainBytes));
        ChartTitle = s.Grain switch
        {
            StatsGrain.Week => L.T("Gewonnen pro Woche", "Saved per week"),
            StatsGrain.Month => L.T("Gewonnen pro Monat", "Saved per month"),
            StatsGrain.Year => L.T("Gewonnen pro Jahr", "Saved per year"),
            _ => L.T("Gewonnen pro Tag", "Saved per day"),
        };
        ChartMax = Fmt.Size(max);
        var every = Math.Max(1, (int)Math.Ceiling(points.Count / 8d));
        for (var i = 0; i < points.Count; i++)
        {
            var p = points[i];
            var week = System.Globalization.ISOWeek.GetWeekOfYear(p.Date);
            var label = s.Grain switch
            {
                StatsGrain.Week => L.T($"KW {week}", $"W{week}"),
                StatsGrain.Month => p.Date.ToString(points.Count > 12 ? "MM/yy" : "MMM", L.Culture),
                StatsGrain.Year => p.Date.ToString("yyyy", L.Culture),
                _ => p.Date.ToString("dd.MM.", L.Culture),
            };
            var head = s.Grain switch
            {
                StatsGrain.Week => L.T($"Woche ab {p.Date:dd.MM.yyyy} (KW {week})", $"Week from {p.Date:d} (W{week})"),
                StatsGrain.Month => p.Date.ToString("MMMM yyyy", L.Culture),
                StatsGrain.Year => p.Date.ToString("yyyy", L.Culture),
                _ => p.Date.ToString("ddd dd.MM.yyyy", L.Culture),
            };
            var tip = head + L.T($"\n{Fmt.Size(p.GainBytes)} gewonnen · {p.Files} Dateien", $"\n{Fmt.Size(p.GainBytes)} saved · {p.Files} files");
            var show = (points.Count - 1 - i) % every == 0;
            Bars.Add(new BarItem(label, Math.Max(p.GainBytes > 0 ? 2 : 0, p.GainBytes / (double)max * ChartHeight), tip, show));
        }

        var cum = new PointCollection();
        if (s.Cumulative.Count > 1)
        {
            var cmax = Math.Max(1, s.Cumulative[^1].GainBytes);
            cum.Add(new Point(0, 100));
            for (var i = 0; i < s.Cumulative.Count; i++)
                cum.Add(new Point(i * 1000d / (s.Cumulative.Count - 1), 100 - s.Cumulative[i].GainBytes * 100d / cmax));
            cum.Add(new Point(1000, 100));
            CumulativeText = L.T($"{s.Cumulative[0].Date:dd.MM.yyyy} bis heute · {Fmt.Size(cmax)}", $"{s.Cumulative[0].Date:d} until today · {Fmt.Size(cmax)}");
        }
        else CumulativeText = "";
        cum.Freeze();
        Cumulative = cum;

        Machines.Clear();
        foreach (var m in s.Machines)
            Machines.Add(new MachineRow(m.Key, m.Files.ToString("N0", Fmt.De), Fmt.Size(m.GainBytes), m.SourceBytes > 0 ? Fmt.Percent(m.Ratio, 0) : "–",
                m.Speed > 0 ? m.Speed.ToString("N1", Fmt.De) + "x" : "–", FormatHours(m.EncodeSeconds), m.Errors.ToString("N0", Fmt.De)));

        Fill(Codecs, s.Codecs);
        Fill(Resolutions, s.Resolutions);
        Fill(Groups, s.Groups);

        TopSavings.Clear();
        var topMax = Math.Max(1, s.TopSavings.FirstOrDefault()?.GainBytes ?? 1);
        foreach (var r in s.TopSavings)
            TopSavings.Add(new RankRow(Path.GetFileName(r.File), $"{Fmt.Size(r.SourceBytes)} » {Fmt.Size(r.OutputBytes)} · {r.TimeUtc.ToLocalTime():dd.MM.yyyy} · {r.Machine}", r.GainBytes * 100d / topMax, Fmt.Size(r.GainBytes)));
        WorstRatios.Clear();
        foreach (var r in s.WorstRatios)
            WorstRatios.Add(new RankRow(Path.GetFileName(r.File), $"{Fmt.Size(r.SourceBytes)} » {Fmt.Size(r.OutputBytes)} · {LibraryCodec(r.SourceCodec)} · {r.Profile}", r.Ratio * 100, Fmt.Percent(r.Ratio, 0)));

        Footer = HasData
            ? L.T($"{_records.Count:N0} Einträge aus {string.Join(", ", s.Machines.Select(m => m.Key))}. Die Protokolle sind im Datenordner dieses PCs gesichert.",
                  $"{_records.Count:N0} entries from {string.Join(", ", s.Machines.Select(m => m.Key))}. The logs are backed up in this PC's data folder.")
            : "";
    }

    private static string LibraryCodec(string c) => Core.Library.LibraryAnalyzer.CodecName(c);

    private static void Fill(ObservableCollection<RankRow> target, List<StatsBucket> buckets)
    {
        target.Clear();
        var max = Math.Max(1, buckets.Count == 0 ? 1 : buckets.Max(b => b.GainBytes));
        foreach (var b in buckets)
            target.Add(new RankRow(b.Key,
                L.T($"{b.Files:N0} Dateien · bleiben {Fmt.Percent(b.Ratio, 0)}", $"{b.Files:N0} files · {Fmt.Percent(b.Ratio, 0)} remains"),
                b.GainBytes * 100d / max, Fmt.Size(b.GainBytes)));
    }

    private static string FormatHours(double seconds) =>
        seconds >= 3600 * 48 ? (seconds / 86400).ToString("N1", Fmt.De) + L.T(" Tage", " days")
        : seconds >= 3600 ? (seconds / 3600).ToString("N1", Fmt.De) + L.T(" Std.", " h")
        : (seconds / 60).ToString("N0", Fmt.De) + L.T(" Min.", " min");

    public void FillDemo()
    {
        var rnd = new Random(3);
        var list = new List<StatsRecord>();
        var groups = new[] { (DemoText.T("Serie A"), "h264", 1080, 4.1e9), (DemoText.T("Serie B"), "h264", 1080, 3.6e9), (DemoText.T("Serie D"), "h264", 720, 1.6e9), (DemoText.T("Serie F"), "mpeg2video", 576, 2.1e9), (DemoText.T("Serie C"), "hevc", 2160, 6.0e9), (DemoText.T("Serie E"), "h264", 720, 0.9e9) };
        for (var day = 75; day >= 0; day--)
        {
            var n = day % 7 == 3 ? 0 : rnd.Next(4, 16);
            for (var i = 0; i < n; i++)
            {
                var (g, codec, h, size) = groups[rnd.Next(groups.Length)];
                var src = (long)(size * (0.7 + rnd.NextDouble() * 0.6));
                var audioOnly = codec == "hevc";
                var ratio = audioOnly ? 0.82 : codec == "mpeg2video" ? 0.22 : h == 720 ? 0.45 : 0.3;
                var outB = (long)(src * ratio * (0.8 + rnd.NextDouble() * 0.4));
                var machine = rnd.Next(3) == 0 ? "PC-2" : "PC-1";
                var dur = 2700 + rnd.Next(-400, 500);
                list.Add(new StatsRecord
                {
                    TimeUtc = DateTime.UtcNow.AddDays(-day).AddMinutes(-rnd.Next(0, 600)), Machine = machine, Profile = machine == "PC-2" ? "AMD" : "NVIDIA",
                    File = DemoText.T($@"{g}\Staffel {rnd.Next(1, 6)}\{g.Replace(' ', '.')}.S0{rnd.Next(1, 6)}E{rnd.Next(1, 22):00}.mkv"), Group = g, Outcome = rnd.Next(60) == 0 ? "Error" : "Encoded",
                    SourceBytes = src, OutputBytes = outB, GainBytes = src - outB, DurationSeconds = dur,
                    EncodeSeconds = audioOnly ? dur / 40d : dur / (machine == "PC-2" ? 1.6 : 4.2), SourceCodec = codec, SourceHeight = h, OutputHeight = Math.Min(h, 1080), AudioOnly = audioOnly,
                });
            }
        }
        _records = list.OrderBy(r => r.TimeUtc).ToList();
        Show();
    }
}
