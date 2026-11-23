using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.ViewModels;

public sealed class GroupRow(GroupSummary g, double maxSaving)
{
    public GroupSummary Data { get; } = g;
    public string Name => Data.Name;
    public string FilesText => Data.Files.ToString("N0", Fmt.De);
    public string SizeText => Fmt.Size(Data.SourceBytes);
    public string OpenText => Data.Open > 0 ? $"{Data.Open:N0} · {Fmt.Size(Data.OpenBytes)}"
        : Data.Skip > 0 && Data.Done == 0 && MainSkipReason is { } why ? L.T("übersprungen: ", "skipped: ") + why
        : "–";

    private string? MainSkipReason => Data.SkipReasons.OrderByDescending(r => r.Value).Select(r => r.Key.TrimEnd('.')).FirstOrDefault();
    public double DonePercent => Data.DonePercent;
    public string DoneText => Data.Files == 0 ? "" : Fmt.Percent(Data.Done / (double)Data.Files, 0);
    public string RateText => Data.AvgMbit > 0 ? Data.AvgMbit.ToString("N1", Fmt.De) + " Mbit/s" : "?";
    public string CodecText => Data.CodecText;
    public string SavingText => Data.EstimatedSaving > 0 ? "~" + Fmt.Size(Data.EstimatedSaving) : "–";
    public double SavingBar => maxSaving > 0 ? Data.EstimatedSaving * 100d / maxSaving : 0;
    public bool CanEncode => Data.Open > 0;
    public string Tooltip => L.T(
        $"{Data.Done} fertig ({Fmt.Size(Data.SavedBytes)} gewonnen), {Data.Open} offen, {Data.Skip} übersprungen" + (Data.InProgress > 0 ? $", {Data.InProgress} in Arbeit" : "") + (Data.Unreadable > 0 ? $", {Data.Unreadable} nicht lesbar" : ""),
        $"{Data.Done} done ({Fmt.Size(Data.SavedBytes)} saved), {Data.Open} open, {Data.Skip} skipped" + (Data.InProgress > 0 ? $", {Data.InProgress} in progress" : "") + (Data.Unreadable > 0 ? $", {Data.Unreadable} not readable" : ""))
        + string.Concat(Data.SkipReasons.OrderByDescending(r => r.Value).Select(r => $"\n{L.T("Übersprungen", "Skipped")} ({r.Value}): {r.Key}"));
}

public sealed record ShareRow(string Name, string Text, double Percent);

public sealed record SortOption(string Key, string Text);

public sealed record DuplicateFileRow(string Name, string Folder, string Size, string Details, string Path, string Decision, string DecisionColor);

public sealed record DuplicateRow(string Title, string Subtitle, string Note, List<DuplicateFileRow> Files);

public sealed record MissingRow(string Group, string Season, string Have, string Missing);

public sealed record ProblemRow(ProblemItem Item, string KindText, string Color, string TimeText, bool CanReset, bool CanEncode)
{
    public string Relative => Item.Relative;
    public string Reason => Item.Reason + (Item.Machine.Length > 0 ? " · " + Item.Machine : "");
}

public sealed partial class LibraryViewModel : ObservableObject
{
    private readonly AppState _app;
    private CancellationTokenSource? _cts;
    private LibraryAnalysis? _analysis;
    private Profile? _analysisProfile;
    private List<DuplicateDecision>? _plan;
    private int _generation;
    private bool _problemsLoaded;

    public LibraryViewModel(AppState app)
    {
        _app = app;
        app.Profiles.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ProfilesViewModel.Selected)) OnProfileChanged();
        };
    }

    public event Action? NavigateToDashboard;

    public long? LastOpenBytes => _analysis?.OpenBytes;

    public ObservableCollection<GroupRow> Groups { get; } = [];
    public ObservableCollection<ShareRow> Codecs { get; } = [];
    public ObservableCollection<ShareRow> Resolutions { get; } = [];
    public ObservableCollection<DuplicateRow> Duplicates { get; } = [];
    public ObservableCollection<MissingRow> Missing { get; } = [];
    public ObservableCollection<ProblemRow> Problems { get; } = [];
    private List<ProblemRow> _allProblems = [];

    [ObservableProperty] private string _duplicatesHeader = L.T("Doppelte", "Duplicates");
    [ObservableProperty] private string _missingHeader = L.T("Fehlende Folgen", "Missing episodes");
    [ObservableProperty] private string _problemsHeader = L.T("Probleme", "Problems");
    [ObservableProperty] private string _duplicatesSummary = L.T("Erst analysieren.", "Analyse first.");
    [ObservableProperty] private string _missingSummary = L.T("Erst analysieren.", "Analyse first.");
    [ObservableProperty] private string _problemsSummary = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(RefreshProblemsCommand))] private bool _problemsBusy;
    [ObservableProperty] private string _problemFilter = "all";
    partial void OnProblemFilterChanged(string value) => FillProblems();

    public IReadOnlyList<SortOption> ProblemFilters { get; } =
    [
        new("all", L.T("Alle", "All")),
        new(nameof(ProblemKind.SkipList), L.T("Ausnahmeliste (nicht klein genug)", "Skip list (not small enough)")),
        new(nameof(ProblemKind.Error), L.T("Fehler", "Errors")),
        new(nameof(ProblemKind.Quality), L.T("Qualität auffällig", "Quality flagged")),
        new(nameof(ProblemKind.Broken), L.T("Beschädigt aussortiert", "Moved aside as damaged")),
    ];

    public IReadOnlyList<SortOption> Sorts { get; } =
    [
        new("saving", L.T("Größtes Sparpotenzial", "Largest saving")),
        new("rate", L.T("Höchste Datenrate", "Highest bitrate")),
        new("size", L.T("Größte zuerst", "Largest first")),
        new("open", L.T("Meiste offene Dateien", "Most open files")),
        new("name", L.T("Name", "Name")),
    ];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(CancelCommand), nameof(EncodeAllCommand), nameof(CheckDuplicatesCommand), nameof(DeleteDuplicatesCommand))]
    private bool _isBusy;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _hasResult;

    [ObservableProperty] private string _totalText = "–";
    [ObservableProperty] private string _totalDetail = "";
    [ObservableProperty] private string _doneText = "–";
    [ObservableProperty] private string _doneDetail = "";
    [ObservableProperty] private string _openText = "–";
    [ObservableProperty] private string _openDetail = "";
    [ObservableProperty] private string _savingText = "–";
    [ObservableProperty] private string _savingDetail = "";
    [ObservableProperty] private string _footer = "";

    [ObservableProperty] private string _sort = "saving";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _onlyOpen;

    partial void OnSortChanged(string value) => FillGroups();
    partial void OnSearchChanged(string value) => FillGroups();
    partial void OnOnlyOpenChanged(bool value) => FillGroups();

    private Profile? Profile => _app.Profiles.Selected is { } s ? _app.Settings.FindProfile(s.Name) : _app.Settings.Profiles.FirstOrDefault();

    public string ProfileText => Profile is { } p ? L.T($"Profil {p.Name}: {p.InputDir} → {p.OutputDir}", $"Profile {p.Name}: {p.InputDir} → {p.OutputDir}") : "";

    private bool CanAnalyze() => !IsBusy;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        OnPropertyChanged(nameof(ProfileText));
        Message = "";
        var gen = _generation;
        var p = Profile?.Clone();
        if (p is null) { Message = L.T("Kein Profil vorhanden.", "No profile available."); return; }
        if (!await Io.DirExistsAsync(p.InputDir, 8000)) { Message = L.T($"Quellordner nicht erreichbar: {p.InputDir}", $"Source folder not reachable: {p.InputDir}"); return; }
        var tools = await _app.EnsureToolsAsync();
        if (tools is null) { Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found."); return; }
        await _app.LoadProbeCacheAsync();
        var cache = ProbeCache.Shared ?? new ProbeCache(_app.Store.ProbeCacheFile);
        ProbeCache.Shared ??= cache;

        if (gen != _generation || IsBusy) return;
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressText = L.T("Durchsuche den Quellordner …", "Scanning the source folder …");
        var progress = new Progress<AnalyzeProgress>(pr =>
        {
            ProgressIndeterminate = pr.Total == 0;
            Progress = pr.Total > 0 ? pr.Done * 100d / pr.Total : 0;
            ProgressText = L.T($"Analysiere {pr.Done:N0} / {pr.Total:N0} …", $"Analysing {pr.Done:N0} / {pr.Total:N0} …") + (pr.Current.Length > 0 ? "  " + pr.Current : "");
        });
        try
        {
            var parallel = p.InputDir.StartsWith(@"\\", StringComparison.Ordinal) ? 4 : 8;
            var analysis = await LibraryAnalyzer.AnalyzeAsync(p, tools, cache, progress, cts.Token, parallel);
            if (gen != _generation) return;
            _analysis = analysis;
            _analysisProfile = p;
            _plan = null;
            Show(_analysis);
            _app.SaveSnapshot(AnalysisSnapshot.From(p.Name, p.OutputDir, analysis));
            _app.Run.CheckSpace();
            var outDir = p.OutputDir;
            var free = await Io.RunAsync(() => Native.FreeBytes(outDir), -1L);
            var verdict = SpaceForecast.Evaluate(AnalysisSnapshot.From(p.Name, p.OutputDir, analysis), free, Math.Max(0, p.MinFreeSpaceGb) * 1024L * 1024L * 1024L, []);
            if (verdict.Warn && gen == _generation) Message = verdict.Text;
        }
        catch (OperationCanceledException)
        {
            if (gen == _generation) Message = L.T("Analyse abgebrochen. Bereits gelesene Dateien sind gespeichert, der nächste Versuch geht schneller.", "Analysis cancelled. Files read so far are saved, the next attempt is faster.");
            await Task.Run(cache.Save);
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    private void EndBusy()
    {
        IsBusy = false;
        ProgressText = "";
        _cts?.Dispose();
        _cts = null;
    }

    private bool CanCancel() => IsBusy && _cts is not null;

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() => _cts?.Cancel();

    private void OnProfileChanged()
    {
        OnPropertyChanged(nameof(ProfileText));
        if (App.ScreenshotFolder is not null) return;
        var had = _analysis is not null;
        _generation++;
        _cts?.Cancel();
        _analysis = null;
        _analysisProfile = null;
        _plan = null;
        HasResult = false;
        TotalText = DoneText = OpenText = SavingText = "–";
        TotalDetail = DoneDetail = OpenDetail = SavingDetail = Footer = "";
        Codecs.Clear();
        Resolutions.Clear();
        Groups.Clear();
        Duplicates.Clear();
        Missing.Clear();
        DuplicatesHeader = L.T("Doppelte", "Duplicates");
        MissingHeader = L.T("Fehlende Folgen", "Missing episodes");
        DuplicatesSummary = MissingSummary = L.T("Erst analysieren.", "Analyse first.");
        Message = had ? L.T("Anderes Profil gewählt: die Analyse galt für das vorige Profil. Bitte neu analysieren.", "Another profile selected: the analysis was for the previous profile. Please analyse again.") : "";
        EncodeAllCommand.NotifyCanExecuteChanged();
        CheckDuplicatesCommand.NotifyCanExecuteChanged();
        DeleteDuplicatesCommand.NotifyCanExecuteChanged();
        _allProblems = [];
        FillProblems();
        if (_problemsLoaded && RefreshProblemsCommand.CanExecute(null)) _ = RefreshProblemsAsync();
    }

    private void Show(LibraryAnalysis a)
    {
        HasResult = true;
        var files = a.Files.Count;
        TotalText = Fmt.Size(a.SourceBytes);
        TotalDetail = L.T($"{files:N0} Dateien · {a.DurationSeconds / 3600:N0} Stunden · {a.Groups.Count:N0} Serien/Filme", $"{files:N0} files · {a.DurationSeconds / 3600:N0} hours · {a.Groups.Count:N0} series/movies");
        var done = a.Count(AnalysisState.Done);
        DoneText = Fmt.Size(a.SavedBytes);
        DoneDetail = L.T($"gewonnen · {done:N0} von {files:N0} Dateien fertig ({(files > 0 ? done * 100d / files : 0):N0} %)", $"saved · {done:N0} of {files:N0} files done ({(files > 0 ? done * 100d / files : 0):N0} %)");
        var open = a.Count(AnalysisState.Open);
        OpenText = Fmt.Size(a.OpenBytes);
        var skip = a.Count(AnalysisState.Skip);
        OpenDetail = L.T($"{open:N0} Dateien offen · {skip:N0} werden übersprungen", $"{open:N0} files open · {skip:N0} will be skipped");
        SavingText = "~" + Fmt.Size(a.EstimatedSaving);
        SavingDetail = a.CalibrationSamples >= 5
            ? L.T($"noch zu gewinnen · geschätzt, gelernt aus {a.CalibrationSamples:N0} fertigen Dateien", $"still to be saved · estimated, learned from {a.CalibrationSamples:N0} finished files")
            : L.T("noch zu gewinnen · grob geschätzt (wird genauer, sobald Dateien fertig sind)", "still to be saved · rough estimate (gets better once files are finished)");

        var total = Math.Max(1, a.SourceBytes);
        Codecs.Clear();
        foreach (var c in a.CodecShare.Take(6))
            Codecs.Add(new ShareRow(c.Codec, L.T($"{c.Files:N0} Dateien · {Fmt.Size(c.Bytes)}", $"{c.Files:N0} files · {Fmt.Size(c.Bytes)}"), c.Bytes * 100d / total));
        Resolutions.Clear();
        foreach (var r in a.ResolutionShare.Take(6))
            Resolutions.Add(new ShareRow(r.Name, L.T($"{r.Files:N0} Dateien · {Fmt.Size(r.Bytes)}", $"{r.Files:N0} files · {Fmt.Size(r.Bytes)}"), r.Bytes * 100d / total));

        var unreadable = a.Count(AnalysisState.Unreadable);
        var busy = a.Count(AnalysisState.InProgress);
        ShowDuplicatesAndMissing(a);
        Footer = L.T($"Analysiert in {Fmt.Time(a.Took.TotalSeconds)}, {a.FromCache:N0} aus dem Zwischenspeicher", $"Analysed in {Fmt.Time(a.Took.TotalSeconds)}, {a.FromCache:N0} from the cache")
                 + (busy > 0 ? L.T($" · {busy} gerade in Arbeit", $" · {busy} in progress") : "")
                 + (unreadable > 0 ? L.T($" · {unreadable} nicht lesbar", $" · {unreadable} not readable") : "")
                 + L.T(". Schätzung = typische HEVC-Datenrate für die Ausgabeauflösung plus Ton.", ". Estimate = typical HEVC bitrate for the output resolution plus audio.");
        FillGroups();
    }

    private static string FilesText(int n) => n == 1 ? L.T("1 Datei", "1 file") : L.T($"{n:N0} Dateien", $"{n:N0} files");

    private static (string Text, string Color) DecisionText(DuplicateDecision? d) => d switch
    {
        null => ("", "#8B949E"),
        { WillDelete: true } => (L.T("Wird gelöscht: ", "Will be deleted: ") + d.Why, "#F85149"),
        { IsKept: true } => (L.T("Wird behalten: ", "Will be kept: ") + d.Reason, "#3FB950"),
        { Gone: true } => (L.T("Die Datei gibt es nicht mehr.", "The file no longer exists."), "#8B949E"),
        _ => (L.T("Bleibt: ", "Stays: ") + d.Reason, "#E3B341"),
    };

    private void ShowDuplicatesAndMissing(LibraryAnalysis a)
    {
        var decisions = new Dictionary<(string, string, string), DuplicateDecision>();
        foreach (var d in _plan ?? [])
            if (d.Set is not null) decisions[(d.Set.Group, d.Set.Label, d.File)] = d;
        Duplicates.Clear();
        foreach (var d in a.Duplicates)
        {
            var title = d.Group == d.Label ? d.Label : $"{d.Group} · {d.Label}";
            var folders = d.Files.Select(f => Path.GetDirectoryName(f.Path) ?? "").Distinct(StringComparer.OrdinalIgnoreCase).Count();
            Duplicates.Add(new DuplicateRow(title,
                L.T($"{d.Files.Count} Fassungen · {Fmt.Size(d.Bytes)}", $"{d.Files.Count} versions · {Fmt.Size(d.Bytes)}"),
                folders > 1 ? L.T("Liegt in mehreren Ordnern. Verglichen und gelöscht wird nur innerhalb eines Ordners.", "Spread over several folders. Files are only compared and deleted within one folder.") : "",
                d.Files.Select(f =>
                {
                    var (text, color) = DecisionText(decisions.GetValueOrDefault((d.Group, d.Label, f.Path)));
                    var folder = Path.GetDirectoryName(f.Relative) ?? "";
                    return new DuplicateFileRow(Path.GetFileName(f.Path),
                        L.T("Ordner: ", "Folder: ") + (folder.Length > 0 ? folder : L.T("(direkt im Quellordner)", "(directly in the source folder)")),
                        Fmt.Size(f.SourceBytes),
                        $"{f.Codec} · {f.ResolutionText}" + (f.DurationSeconds > 0 ? " · " + Fmt.Time(f.DurationSeconds) : "") + (f.State == AnalysisState.Done ? L.T(" · komprimiert", " · compressed") : "")
                        + (f.Reason.Length > 0 && f.State == AnalysisState.Skip ? " · " + f.Reason : ""), f.Path, text, color);
                }).ToList()));
        }
        DuplicatesHeader = L.T($"Doppelte ({a.Duplicates.Count})", $"Duplicates ({a.Duplicates.Count})");
        DuplicatesSummary = DuplicatesText(a);
        CheckDuplicatesCommand.NotifyCanExecuteChanged();
        DeleteDuplicatesCommand.NotifyCanExecuteChanged();

        Missing.Clear();
        foreach (var m in a.Missing)
            Missing.Add(new MissingRow(m.Group, L.T($"Staffel {m.Season}", $"Season {m.Season}"),
                L.T($"{m.Have} von {m.Max}", $"{m.Have} of {m.Max}"), m.MissingText));
        var count = a.Missing.Sum(m => m.Missing.Count);
        MissingHeader = L.T($"Fehlende Folgen ({count})", $"Missing episodes ({count})");
        MissingSummary = a.Missing.Count == 0
            ? L.T("Keine Lücken gefunden.", "No gaps found.")
            : L.T($"{(count == 1 ? "1 Folge fehlt" : $"{count} Folgen fehlen")} in {(a.Missing.Count == 1 ? "1 Staffel" : $"{a.Missing.Count} Staffeln")} (zwischen Folge 1 und der höchsten vorhandenen). Specials und Staffeln mit sehr vielen Lücken werden nicht gezählt.",
                  $"{(count == 1 ? "1 episode is" : $"{count} episodes are")} missing in {(a.Missing.Count == 1 ? "1 season" : $"{a.Missing.Count} seasons")} (between episode 1 and the highest one present). Specials and seasons with very many gaps are not counted.");
    }

    // ---------------- Probleme ----------------

    private bool CanRefreshProblems() => !ProblemsBusy;

    [RelayCommand(CanExecute = nameof(CanRefreshProblems))]
    public async Task RefreshProblemsAsync()
    {
        var p = Profile?.Clone();
        if (p is null) return;
        _problemsLoaded = true;
        var gen = _generation;
        ProblemsBusy = true;
        try
        {
            var items = await Task.Run(() => ProblemFinder.Find(p, StatsLog.ReadAll([p.OutputDir])));
            if (gen != _generation) return;
            _allProblems = items.Select(ToRow).ToList();
            FillProblems();
        }
        catch (Exception ex)
        {
            ProblemsSummary = ex.Message;
        }
        finally
        {
            ProblemsBusy = false;
        }
        if (gen != _generation) await RefreshProblemsAsync();
    }

    private static ProblemRow ToRow(ProblemItem i) => i.Kind switch
    {
        ProblemKind.SkipList => new ProblemRow(i, L.T("AUSNAHME", "SKIP LIST"), "#9E6A03", i.Time?.ToString("dd.MM.yy HH:mm") ?? "", true, i.SourcePath is not null),
        ProblemKind.Error => new ProblemRow(i, L.T("FEHLER", "ERROR"), "#DA3633", i.Time?.ToString("dd.MM.yy HH:mm") ?? "", i.MarkFile is not null, i.SourcePath is not null),
        ProblemKind.Quality => new ProblemRow(i, L.T("QUALITÄT", "QUALITY"), "#8957E5", i.Time?.ToString("dd.MM.yy HH:mm") ?? "", false, false),
        _ => new ProblemRow(i, L.T("DEFEKT", "DAMAGED"), "#6E7681", i.Time?.ToString("dd.MM.yy HH:mm") ?? "", false, i.SourcePath is not null),
    };

    private void FillProblems()
    {
        Problems.Clear();
        foreach (var r in _allProblems.Where(r => ProblemFilter == "all" || r.Item.Kind.ToString() == ProblemFilter)) Problems.Add(r);
        ProblemsHeader = L.T($"Probleme ({_allProblems.Count})", $"Problems ({_allProblems.Count})");
        int C(ProblemKind k) => _allProblems.Count(r => r.Item.Kind == k);
        ProblemsSummary = _allProblems.Count == 0
            ? L.T("Keine Probleme. Ausnahmeliste, Fehler, Qualitäts-Ausreißer und aussortierte Dateien würden hier stehen.", "No problems. Skip list, errors, quality outliers and files moved aside would show up here.")
            : L.T($"{C(ProblemKind.SkipList)} auf der Ausnahmeliste · {C(ProblemKind.Error)} Fehler · {C(ProblemKind.Quality)} Qualität auffällig · {C(ProblemKind.Broken)} beschädigt aussortiert",
                  $"{C(ProblemKind.SkipList)} on the skip list · {C(ProblemKind.Error)} errors · {C(ProblemKind.Quality)} quality flagged · {C(ProblemKind.Broken)} moved aside as damaged");
    }

    [RelayCommand]
    private async Task ResetProblemAsync(ProblemRow? row)
    {
        if (row is null || !await Io.RunAsync(() => ProblemFinder.Reset(row.Item), false)) return;
        _allProblems.Remove(row);
        FillProblems();
    }

    [RelayCommand]
    private async Task ResetAllSkipListAsync()
    {
        var rows = _allProblems.Where(r => r.Item.Kind == ProblemKind.SkipList && (ProblemFilter == "all" || ProblemFilter == nameof(ProblemKind.SkipList))).ToList();
        if (rows.Count == 0) return;
        var ok = System.Windows.MessageBox.Show(
            L.T($"{rows.Count} Einträge von der Ausnahmeliste nehmen? Der nächste Lauf versucht diese Dateien erneut. Sinnvoll nach einer Änderung von Qualität oder Grenze.",
                $"Remove {rows.Count} entries from the skip list? The next run tries these files again. Useful after changing quality or limit."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        var done = await Io.RunAsync(() => rows.Where(r => ProblemFinder.Reset(r.Item)).ToList(), [], 120000);
        foreach (var r in done) _allProblems.Remove(r);
        FillProblems();
    }

    [RelayCommand]
    private async Task EncodeProblemAsync(ProblemRow? row)
    {
        if (row?.Item.SourcePath is not { } src) return;
        if (row.Item.Kind == ProblemKind.SkipList) await ResetProblemAsync(row);
        var run = _app.Run.StartSelectionAsync([src]);
        NavigateToDashboard?.Invoke();
        await run;
    }

    [RelayCommand]
    private void OpenProblem(ProblemRow? row)
    {
        if (row is null) return;
        var path = row.Item.SourcePath ?? row.Item.MarkFile;
        if (path is not null) ShowInExplorer(path);
    }

    [RelayCommand]
    private void OpenDuplicate(DuplicateFileRow? row)
    {
        if (row is not null) ShowInExplorer(row.Path);
    }

    private static void ShowInExplorer(string path)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private void FillGroups()
    {
        Groups.Clear();
        if (_analysis is null) return;
        IEnumerable<GroupSummary> g = _analysis.Groups;
        if (Search.Trim().Length > 0) g = g.Where(x => x.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase));
        if (OnlyOpen) g = g.Where(x => x.Open > 0);
        g = Sort switch
        {
            "rate" => g.OrderByDescending(x => x.AvgMbit),
            "size" => g.OrderByDescending(x => x.SourceBytes),
            "open" => g.OrderByDescending(x => x.Open).ThenByDescending(x => x.OpenBytes),
            "name" => g.OrderBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => g.OrderByDescending(x => x.EstimatedSaving).ThenByDescending(x => x.OpenBytes),
        };
        var list = g.ToList();
        var max = list.Count == 0 ? 0 : list.Max(x => x.EstimatedSaving);
        foreach (var x in list) Groups.Add(new GroupRow(x, max));
    }

    [RelayCommand]
    private async Task EncodeGroupAsync(GroupRow? row)
    {
        if (row is null) return;
        var run = _app.Run.StartSelectionAsync([row.Data.SourcePath]);
        NavigateToDashboard?.Invoke();
        await run;
    }

    private bool CanEncodeAll() => !IsBusy && _analysis is not null;

    private string DuplicatesText(LibraryAnalysis a)
    {
        var n = a.Duplicates.Count;
        if (n == 0) return L.T("Keine doppelten Folgen oder Filme gefunden.", "No duplicate episodes or movies found.");
        var found = n == 1
            ? L.T("1 Folge bzw. Film liegt mehrfach vor.", "1 episode or movie exists more than once.")
            : L.T($"{n:N0} Folgen bzw. Filme liegen mehrfach vor.", $"{n:N0} episodes or movies exist more than once.");
        if (_plan is null)
            return found + " " + L.T(
                "Schritt 1 „Prüfen“ vergleicht die Dateien, die im selben Ordner liegen (Laufzeit, Auflösung, Ton, Sprachen und das Bild selbst), und schreibt zu jeder Datei, was mit ihr passieren würde. Dabei wird nichts gelöscht. Schritt 2 „Löschen …“ löscht nach einer Rückfrage nur, was überflüssig ist: einen kurzen Ausschnitt (Sample) neben dem Original, eine unvollständige Kopie oder die größere von zwei gleichwertigen Fassungen.",
                "Step 1 “Check” compares the files that are in the same folder (duration, resolution, audio, languages and the picture itself) and notes for every file what would happen to it. Nothing is deleted yet. Step 2 “Delete …” deletes, after asking, only what is superfluous: a short excerpt (sample) next to its original, an incomplete copy or the larger of two equivalent versions.");
        var del = PlannedDeletes();
        return del.Count == 0
            ? found + " " + L.T("Geprüft: nichts zu löschen. Bei jeder Datei steht, warum sie bleibt.", "Checked: nothing to delete. Every file shows why it stays.")
            : found + " " + L.T(
                $"Geprüft: {FilesText(del.Count)} ({Fmt.Size(del.Sum(d => d.Bytes))}) {(del.Count == 1 ? "ist" : "sind")} überflüssig, alle anderen bleiben. Bei jeder Datei steht, was mit ihr passiert und warum. Gelöscht ist noch nichts: dafür Schritt 2 „Löschen …“.",
                $"Checked: {FilesText(del.Count)} ({Fmt.Size(del.Sum(d => d.Bytes))}) {(del.Count == 1 ? "is" : "are")} superfluous, all others stay. Every file shows what happens to it and why. Nothing has been deleted yet: that is step 2 “Delete …”.");
    }

    private List<DuplicateDecision> PlannedDeletes() => (_plan ?? []).Where(d => d.WillDelete).DistinctBy(d => d.File, StringComparer.OrdinalIgnoreCase).ToList();

    private bool CanCheckDuplicates() => !IsBusy && _analysis is { Duplicates.Count: > 0 };

    private bool CanDeleteDuplicates() => !IsBusy && _analysis is not null && (_plan?.Any(d => d.WillDelete) ?? false);

    [RelayCommand(CanExecute = nameof(CanCheckDuplicates))]
    private async Task CheckDuplicatesAsync()
    {
        if (_analysis is not { } a || _analysisProfile is not { } p) return;
        Message = "";
        if (_app.Run.IsBusy) { Message = L.T("Bitte erst nach dem laufenden Lauf bereinigen.", "Please clean up after the running job has finished."); return; }
        var tools = await _app.EnsureToolsAsync();
        if (tools is null) { Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found."); return; }
        if (IsBusy || !ReferenceEquals(a, _analysis)) return;
        var gen = _generation;
        var cts = _cts = new CancellationTokenSource();
        IsBusy = true;
        ProgressIndeterminate = false;
        Progress = 0;
        ProgressText = L.T("Prüfe die doppelten Dateien …", "Checking the duplicate files …");
        var progress = new Progress<AnalyzeProgress>(pr =>
        {
            if (!ReferenceEquals(_cts, cts) || pr.Current.Length == 0) return;
            Progress = pr.Total > 0 ? pr.Done * 100d / pr.Total : 0;
            ProgressText = L.T($"Prüfe Gruppe {pr.Done + 1:N0} von {pr.Total:N0}: {pr.Current}", $"Checking group {pr.Done + 1:N0} of {pr.Total:N0}: {pr.Current}");
        });
        try
        {
            var sets = a.Duplicates.ToList();
            var fresh = TimeSpan.FromMinutes(Math.Max(0, p.SkipNewerThanMinutes));
            var plan = await Task.Run(() => DuplicateCleaner.PlanAsync(sets, tools, cts.Token, p.InputDir, p.OutputDir, progress, fresh));
            if (gen != _generation) return;
            _plan = plan;
            ShowDuplicatesAndMissing(a);
        }
        catch (OperationCanceledException)
        {
            if (gen == _generation) Message = L.T("Prüfung abgebrochen. Es wurde nichts gelöscht.", "Check cancelled. Nothing was deleted.");
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanDeleteDuplicates))]
    private async Task DeleteDuplicatesAsync()
    {
        if (_analysis is not { } a || _analysisProfile is not { } p || _plan is not { } plan) return;
        Message = "";
        var del = PlannedDeletes();
        if (del.Count == 0) return;
        const string runBusy = "Bitte erst nach dem laufenden Lauf bereinigen.";
        if (_app.Run.IsBusy) { Message = L.T(runBusy, "Please clean up after the running job has finished."); return; }

        var bytes = del.Sum(d => d.Bytes);
        var folders = del.GroupBy(d => Path.GetDirectoryName(Path.GetRelativePath(a.InputDir, d.File)) ?? "", StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase).ToList();
        var folderLines = string.Join("\n", folders.Take(8).Select(g => $"– {(g.Key.Length > 0 ? g.Key : L.T("(direkt im Quellordner)", "(directly in the source folder)"))}: {FilesText(g.Count())}"))
            + (folders.Count == 9 ? "\n" + L.T("… und 1 weiterer Ordner", "… and 1 more folder")
                : folders.Count > 9 ? "\n" + L.T($"… und {folders.Count - 8:N0} weitere Ordner", $"… and {folders.Count - 8:N0} more folders") : "");
        var withTarget = !string.IsNullOrWhiteSpace(p.OutputDir);
        var ok = System.Windows.MessageBox.Show(
            L.T($"{FilesText(del.Count)} endgültig löschen ({Fmt.Size(bytes)})?\n\n"
                + "Das lässt sich nicht rückgängig machen: die Dateien kommen nicht in den Papierkorb.\n\n"
                + "Gelöscht wird nur, was die Prüfung als überflüssig erkannt hat: ein kurzer Ausschnitt (Sample) neben dem Original, eine unvollständige Kopie oder die größere von zwei gleichwertigen Fassungen. Jede betroffene Datei steht mit Ordner und Grund in der Liste („Wird gelöscht: …“).\n\n"
                + "Betroffene Ordner:\n" + folderLines
                + (withTarget ? "\n\nHat eine gelöschte Datei schon eine komprimierte Fassung im Zielordner, wird diese mitgelöscht. Die behaltene Datei und ihre komprimierte Fassung bleiben unberührt." : ""),
                $"Delete {FilesText(del.Count)} permanently ({Fmt.Size(bytes)})?\n\n"
                + "This cannot be undone: the files do not go to the recycle bin.\n\n"
                + "Only what the check found superfluous is deleted: a short excerpt (sample) next to its original, an incomplete copy or the larger of two equivalent versions. Every affected file is shown with its folder and the reason in the list (“Will be deleted: …”).\n\n"
                + "Affected folders:\n" + folderLines
                + (withTarget ? "\n\nIf a deleted file already has a compressed version in the target folder, that one is deleted too. The kept file and its compressed version stay untouched." : "")),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No);
        if (ok != System.Windows.MessageBoxResult.Yes) return;
        if (_app.Run.IsBusy) { Message = L.T(runBusy, "Please clean up after the running job has finished."); return; }
        if (IsBusy || !ReferenceEquals(a, _analysis) || !ReferenceEquals(plan, _plan)) return;

        var gen = _generation;
        IsBusy = true;
        ProgressIndeterminate = true;
        ProgressText = L.T($"Lösche {FilesText(del.Count)} …", $"Deleting {FilesText(del.Count)} …");
        try
        {
            var res = await Task.Run(() => DuplicateCleaner.Execute(del, p.InputDir, p.OutputDir));
            foreach (var d in res.Deleted) _app.WriteLog($"Doppelte: geloescht {d.File} ({Fmt.Size(d.Bytes)}; {d.Why}); behalten {d.Keep}");
            foreach (var o in res.Outputs) _app.WriteLog($"Doppelte: komprimierte Fassung geloescht {o}");
            foreach (var e in res.Errors) _app.WriteLog("Doppelte: NICHT geloescht " + e);
            var text = (res.Deleted.Count == 0
                           ? L.T("Nichts gelöscht.", "Nothing deleted.")
                           : L.T($"{FilesText(res.Deleted.Count)} gelöscht, {Fmt.Size(res.Bytes)} frei.", $"{FilesText(res.Deleted.Count)} deleted, {Fmt.Size(res.Bytes)} freed."))
                       + (res.Outputs.Count > 0 ? " " + L.T($"Im Zielordner mitgelöscht: {FilesText(res.Outputs.Count)}.", $"Also deleted in the target folder: {FilesText(res.Outputs.Count)}.") : "")
                       + (res.Errors.Count > 0 ? " " + L.T("Nicht gelöscht: ", "Not deleted: ") + string.Join(" | ", res.Errors.Take(3)) + (res.Errors.Count > 3 ? " …" : "") : "")
                       + (res.Deleted.Count > 0 ? " " + L.T("Für genaue Zahlen in der Übersicht neu analysieren.", "Analyse again for exact numbers in the overview.") : "");
            if (gen != _generation) { Message = text; return; }
            var gone = new HashSet<string>(res.Deleted.Select(d => d.File), StringComparer.OrdinalIgnoreCase);
            var failed = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (d, why) in res.Failed) failed[d.File] = why;
            _plan = plan.Where(d => !gone.Contains(d.File))
                .Select(d => d.WillDelete && failed.TryGetValue(d.File, out var why) ? d with { Reason = why } : d).ToList();
            _analysis = DuplicateCleaner.WithoutDeleted(a, res.Deleted);
            Show(_analysis);
            Message = text;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            EndBusy();
        }
    }

    [RelayCommand(CanExecute = nameof(CanEncodeAll))]
    private async Task EncodeAllAsync()
    {
        if (_analysis is null) return;
        var sources = _analysis.Groups.Where(g => g.Open > 0).OrderByDescending(g => g.EstimatedSaving).Select(g => g.SourcePath).ToList();
        if (sources.Count == 0) { Message = L.T("Nichts offen.", "Nothing open."); return; }
        var run = _app.Run.StartSelectionAsync(sources, fullRun: true);
        NavigateToDashboard?.Invoke();
        await run;
    }

    [RelayCommand]
    private void OpenGroupFolder(GroupRow? row)
    {
        if (row is null) return;
        try
        {
            var path = row.Data.SourcePath;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", Directory.Exists(path) ? $"\"{path}\"" : $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch
        {
        }
    }

    public void FillDemo()
    {
        var rnd = new Random(7);
        var files = new List<AnalyzedFile>();
        void Add(string group, int n, string codec, int height, double mbit, int doneN, double ratio)
        {
            for (var i = 0; i < n; i++)
            {
                var dur = 2700 + rnd.Next(-300, 600);
                var bytes = (long)(mbit * 1e6 / 8 * dur * (0.85 + rnd.NextDouble() * 0.3));
                var done = i < doneN;
                files.Add(new AnalyzedFile(DemoText.T($@"\\NAS\Serien\{group}\Staffel 1\x{i}.mkv"), DemoText.T($@"{group}\Staffel 1\x{i}.mkv"), group, bytes, dur, codec, height,
                    done ? AnalysisState.Done : codec == "HEVC" && i % 3 != 0 ? AnalysisState.Skip : AnalysisState.Open,
                    done ? (long)(bytes * ratio) : 0, (long)(bytes * ratio), ""));
            }
        }
        Add(DemoText.T("Serie A"), 62, "H.264", 1080, 11.5, 14, 0.27);
        Add(DemoText.T("Serie B"), 73, "H.264", 1080, 9.8, 0, 0.31);
        Add(DemoText.T("Serie C"), 10, "HEVC", 2160, 14.2, 2, 0.24);
        Add(DemoText.T("Serie D"), 121, "H.264", 720, 4.1, 40, 0.42);
        Add(DemoText.T("Serie E"), 188, "H.264", 720, 2.9, 0, 0.55);
        Add(DemoText.T("Serie F"), 46, "MPEG-2", 576, 6.2, 0, 0.21);
        Add(DemoText.T("Serie G"), 26, "HEVC", 2160, 9.3, 0, 0.45);
        Add(DemoText.T("Serie H"), 92, "H.264", 1080, 7.4, 92, 0.38);

        const long gb = 1024L * 1024 * 1024;
        AnalyzedFile Twin(string rel, double sizeGb, double seconds, string codec, int height) =>
            new(DemoText.T(@"\\NAS\Serien\") + rel, rel, rel.Split('\\')[0], (long)(sizeGb * gb), seconds, codec, height, AnalysisState.Skip, 0, 0, "");
        DuplicateSet serieD = new(DemoText.T("Serie D"), "S02E07",
        [
            Twin(DemoText.T(@"Serie D\Staffel 2\Serie D - S02E07 - Neuer Anfang.mkv"), 1.31, 2601, "HEVC", 1080),
            Twin(DemoText.T(@"Serie D\Staffel 2\Serie.D.S02E07.German.DL.1080p.BluRay.x264.mkv"), 4.62, 2601, "H.264", 1080),
        ]);
        DuplicateSet filmF = new("Film F (1995)", "Film F (1995)",
        [
            Twin(@"Film F (1995)\Film F (1995).mkv", 6.12, 10221, "HEVC", 1080),
            Twin(@"Film F (1995)\Film.F.1995.German.DL.1080p.BluRay.x264.mkv", 17.84, 10221, "H.264", 1080),
        ]);
        DuplicateSet serieE = new(DemoText.T("Serie E"), "S02E13",
        [
            Twin(DemoText.T(@"Serie E\Staffel 2\Serie.E.S02E13.German.1080p.WEB.h264.mkv"), 1.92, 1301, "H.264", 1080),
            Twin(DemoText.T(@"Serie E\Staffel 2\Serie.E.S02E13.German.720p.HDTV.x264.mkv"), 0.74, 1301, "H.264", 720),
        ]);
        var smallest = L.T("die kleinste vollständige Fassung", "the smallest complete version");
        var larger = L.T("größer, sonst gleichwertig (Laufzeit, Auflösung, Ton, Sprachen, Untertitel und Bild verglichen)", "larger, otherwise equivalent (duration, resolution, audio, languages, subtitles and picture compared)");
        DuplicateDecision Kept(DuplicateSet set, int i) => new(set.Files[i].Path, set.Files[i].Path, set.Files[i].SourceBytes, smallest) { IsKept = true, Set = set };
        DuplicateDecision Gone(DuplicateSet set, int i, int keep) => new(set.Files[i].Path, set.Files[keep].Path, set.Files[i].SourceBytes, null) { Why = larger, Set = set };
        _plan =
        [
            Kept(serieD, 0), Gone(serieD, 1, 0), Kept(filmF, 0), Gone(filmF, 1, 0), Kept(serieE, 1),
            new(serieE.Files[0].Path, serieE.Files[1].Path, serieE.Files[0].SourceBytes, L.T("andere Auflösung (1920x1080 statt 1280x720): zwei Fassungen", "different resolution (1920x1080 instead of 1280x720): two versions")) { Set = serieE },
        ];
        _analysis = new LibraryAnalysis
        {
            InputDir = DemoText.T(@"\\NAS\Serien"),
            Files = files,
            Groups = LibraryAnalyzer.Summarize(DemoText.T(@"\\NAS\Serien"), files),
            Calibration = 0.92,
            CalibrationSamples = 148,
            Took = TimeSpan.FromSeconds(41),
            FromCache = 512,
            Duplicates = [filmF, serieD, serieE],
            Missing = [new(DemoText.T("Serie F"), 3, 8, 10, [7, 8]), new(DemoText.T("Serie E"), 2, 20, 22, [13])],
        };
        Show(_analysis);
        _allProblems =
        [
            ToRow(new ProblemItem(ProblemKind.Error, DemoText.T(@"Serie D\Staffel 1\Serie.D.S01E04.German.DL.720p.mkv"), L.T("Prüfung: Datei ist beschädigt (Fehler beim Dekodieren).", "Check: file is damaged (decoding errors)."), DateTime.Now.AddHours(-3), "x", null, "PC-2")),
            ToRow(new ProblemItem(ProblemKind.Quality, DemoText.T(@"Serie G\Staffel 1\Serie.G.S01E03.mkv"), "SSIM 0,941", DateTime.Now.AddDays(-1), null, null, "PC-1")),
            ToRow(new ProblemItem(ProblemKind.SkipList, DemoText.T(@"Serie E\Staffel 2\Serie.E.S02E03.mkv"), L.T("Früh abgebrochen: wäre nicht klein genug geworden", "Aborted early: would not have become small enough"), DateTime.Now.AddDays(-2), "x", "x", "PC-1")),
            ToRow(new ProblemItem(ProblemKind.SkipList, DemoText.T(@"Serie E\Staffel 2\Serie.E.S02E04.mkv"), L.T("Neue Datei war nicht kleiner als die Quelle", "New file was not smaller than the source"), DateTime.Now.AddDays(-2), "x", "x", "PC-2")),
        ];
        FillProblems();
    }
}
