using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Rename;
using FilmKonverter.Core.Util;
using Microsoft.Win32;

namespace FilmKonverter.App.ViewModels;

public sealed partial class RenameRow : ObservableObject
{
    private readonly RenameViewModel _owner;
    private bool _syncing;

    public RenameRow(RenameItem item, RenameViewModel owner)
    {
        Item = item;
        _owner = owner;
        _selected = item.Selected;
        _newName = item.NewName;
    }

    public RenameItem Item { get; }
    public string OldPath => Item.RelativeSource;
    public string Extension => Path.GetExtension(Item.Source);
    public RenameStatus Status => Item.Status;
    public string Notes => string.Join(" · ", Item.Notes);
    public bool CanSelect => Item.CanRename;
    public bool HasLibrary => Item.Library is not null;

    [ObservableProperty] private bool _selected;
    [ObservableProperty] private string _newName;

    partial void OnSelectedChanged(bool value)
    {
        if (_syncing) return;
        Item.Selected = value && Item.CanRename;
        _owner.RowChanged();
    }

    partial void OnNewNameChanged(string value)
    {
        if (_syncing) return;
        var clean = RenamePlanner.Sanitize(value ?? "");
        if (clean.Length == 0) clean = Item.OldName;
        Item.NewName = clean;
        Item.Edited = true;
        _owner.Revalidate(this);
    }

    public void Refresh()
    {
        _syncing = true;
        NewName = Item.NewName;
        Selected = Item.Selected;
        _syncing = false;
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(CanSelect));
    }
}

public sealed partial class RenameFilterOption(string key, string label) : ObservableObject
{
    public string Key { get; } = key;
    public string Label { get; } = label;
    [ObservableProperty] private string _text = label;
}

public sealed record JournalItem(string File, string Text, bool Undone);

public sealed record KindOption(RenameKind Kind, string Text);

public sealed record PresetItem(RenamePreset Preset)
{
    public string Display => (Preset.BuiltIn ? "" : "★ ") + Preset.Name + "   ·   " + Preset.Template;
}

public sealed partial class RenameViewModel : ObservableObject
{
    private readonly AppState _app;

    public RenameViewModel(AppState app)
    {
        _app = app;
        View = CollectionViewSource.GetDefaultView(Rows);
        View.Filter = o => o is RenameRow r && Matches(r);
        foreach (var r in app.Settings.RenameRecent) Recent.Add(r);
        LoadPresets();
    }

    public ObservableCollection<RenameRow> Rows { get; } = [];
    public ICollectionView View { get; }
    public ObservableCollection<JournalItem> Journals { get; } = [];

    public ObservableCollection<string> Sources { get; } = [];
    public ObservableCollection<string> Recent { get; } = [];

    public IReadOnlyList<RenameFilterOption> Filters { get; } =
    [
        new("changes", L.T("Änderungen", "Changes")),
        new("check", L.T("Zu prüfen", "To check")),
        new("conflict", L.T("Konflikte", "Conflicts")),
        new("unknown", L.T("Nicht erkannt", "Not recognised")),
        new("unchanged", L.T("Schon richtig", "Already correct")),
        new("all", L.T("Alle", "All")),
    ];

    // ---------------- Vorlagen ----------------

    public ObservableCollection<PresetItem> Presets { get; } = [];

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(DeletePresetCommand))] private PresetItem? _selectedPreset;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(SavePresetCommand))] private string _presetName = "";
    private bool _syncPreset;

    private void LoadPresets(string? select = null)
    {
        _syncPreset = true;
        Presets.Clear();
        foreach (var p in RenameOptions.BuiltInFor(Kind)) Presets.Add(new PresetItem(p));
        foreach (var p in CustomPresets.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
            Presets.Add(new PresetItem(p with { BuiltIn = false }));
        SelectedPreset = Presets.FirstOrDefault(p => select is not null ? p.Preset.Name == select && !p.Preset.BuiltIn : p.Preset.Template == Template);
        _syncPreset = false;
    }

    partial void OnSelectedPresetChanged(PresetItem? value)
    {
        if (value is null) return;
        PresetName = value.Preset.BuiltIn ? "" : value.Preset.Name;
        if (!_syncPreset) Template = value.Preset.Template;
    }

    private bool CanSavePreset() => PresetName.Trim().Length > 0;

    [RelayCommand(CanExecute = nameof(CanSavePreset))]
    private void SavePreset()
    {
        var name = PresetName.Trim();
        if (RenameOptions.BuiltInFor(Kind).Any(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Message = L.T($"„{name}“ ist eine feste Vorlage. Bitte einen anderen Namen wählen.", $"\"{name}\" is a built-in preset. Please choose another name.");
            return;
        }
        var list = CustomPresets;
        var existing = list.FindIndex(p => p.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var preset = new RenamePreset(name, Template);
        if (existing >= 0) list[existing] = preset; else list.Add(preset);
        _app.Save();
        LoadPresets(name);
        Message = existing >= 0 ? L.T($"Vorlage „{name}“ aktualisiert.", $"Preset \"{name}\" updated.") : L.T($"Vorlage „{name}“ gespeichert.", $"Preset \"{name}\" saved.");
    }

    private bool CanDeletePreset() => SelectedPreset is { Preset.BuiltIn: false };

    [RelayCommand(CanExecute = nameof(CanDeletePreset))]
    private void DeletePreset()
    {
        if (SelectedPreset is not { Preset.BuiltIn: false } sel) return;
        CustomPresets.RemoveAll(p => p.Name.Equals(sel.Preset.Name, StringComparison.OrdinalIgnoreCase));
        _app.Save();
        PresetName = "";
        LoadPresets();
        Message = L.T($"Vorlage „{sel.Preset.Name}“ gelöscht.", $"Preset \"{sel.Preset.Name}\" deleted.");
    }

    public string TemplateWarning => RenamePlanner.TemplateProblem(Template, Kind) ?? "";

    public IReadOnlyList<string> Tokens => RenamePlanner.TokensFor(Kind);

    public string TokenHelp => Kind == RenameKind.Movies
        ? L.T("{Titel} · {Jahr} · {Edition} Fassung (Extended, Director’s Cut, Uncut …) · {Teil} bei mehrteiligen Filmen (sonst automatisch „- CD1“). Leere Platzhalter entfallen samt Klammern und Trennzeichen.",
              "{Titel} title · {Jahr} year · {Edition} edition (Extended, Director’s Cut, Uncut …) · {Teil} part of multi-part movies (otherwise automatically \"- CD1\"). Empty placeholders disappear with brackets and separators.")
        : L.T("{S} {E} Staffel/Folge zweistellig · {Staffel} {Folge} ohne führende Null · {Titel} · {Serie} · {Jahr}. Leere Platzhalter entfallen samt Trennzeichen.",
              "{S} {E} season/episode two digits · {Staffel} {Folge} without leading zero · {Titel} title · {Serie} series · {Jahr} year. Empty placeholders disappear with their separators.");

    // ---------------- Serien oder Filme ----------------

    public IReadOnlyList<KindOption> Kinds { get; } =
    [
        new(RenameKind.Series, L.T("Serien", "Series")),
        new(RenameKind.Movies, L.T("Filme", "Movies")),
    ];

    public RenameKind Kind
    {
        get => _app.Settings.RenameKind;
        set
        {
            if (_app.Settings.RenameKind == value) return;
            _app.Settings.RenameKind = value;
            _app.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Template));
            OnPropertyChanged(nameof(Example));
            OnPropertyChanged(nameof(TemplateWarning));
            OnPropertyChanged(nameof(Tokens));
            OnPropertyChanged(nameof(TokenHelp));
            OnPropertyChanged(nameof(SourceHint));
            OnPropertyChanged(nameof(FilterLabel));
            PresetName = "";
            LoadPresets();
            Rows.Clear();
            UpdateSummary();
            if (Sources.Count > 0) _ = AnalyzeAsync();
        }
    }

    private string Items(int n) => Kind == RenameKind.Movies
        ? RunTexts.Count(n, "Film", "Filme", "movie", "movies")
        : RunTexts.Count(n, "Folge", "Folgen", "episode", "episodes");

    private List<RenamePreset> CustomPresets => Kind == RenameKind.Movies ? _app.Settings.RenameMoviePresets : _app.Settings.RenamePresets;

    public string SourceHint => Kind == RenameKind.Movies
        ? L.T("Quelle: Filmordner, einzelne Filme oder ganze Sammlung", "Source: movie folders, single movies or a whole collection")
        : L.T("Quelle: Serie, Staffel, ganze Bibliothek oder einzelne Folgen", "Source: series, season, whole library or single episodes");

    public string FilterLabel => Kind == RenameKind.Movies ? L.T("Nur mit", "Only with") : L.T("Nur Serien mit", "Only series with");

    public bool HasRecent => Recent.Count > 0;

    [RelayCommand]
    private void ClearRecent()
    {
        _app.Settings.RenameRecent.Clear();
        _app.Save();
        Recent.Clear();
        OnPropertyChanged(nameof(HasRecent));
    }

    [ObservableProperty] private string _filter = "changes";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private string _seriesFilter = "";
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(AnalyzeCommand), nameof(ExecuteCommand), nameof(UndoCommand), nameof(DeleteJournalCommand), nameof(DeleteAllJournalsCommand))] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(ProgressIndeterminate))] private bool _progressKnown;
    public bool ProgressIndeterminate => !ProgressKnown;
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _libraryText = "";

    [ObservableProperty] private bool _schemaExpanded = true;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(UndoCommand), nameof(DeleteJournalCommand))] private JournalItem? _selectedJournal;
    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(ExecuteCommand))] private int _selectedCount;

    partial void OnFilterChanged(string value) => View.Refresh();
    partial void OnSearchChanged(string value) => View.Refresh();

    [RelayCommand]
    private void UseRecent(string? path)
    {
        if (!string.IsNullOrEmpty(path)) AddSources([path]);
    }

    public string SourcesText => Sources.Count == 0
        ? L.T("Noch nichts gewählt: Ordner oder Dateien wählen oder hierher ziehen.", "Nothing chosen yet: pick folders or files, or drag them here.")
        : string.Join("   ·   ", Sources);

    public bool HasSources => Sources.Count > 0;

    // ---------------- Schema ----------------

    public string Template
    {
        get => Kind == RenameKind.Movies ? _app.Settings.RenameMovieTemplate : _app.Settings.RenameTemplate;
        set
        {
            var v = string.IsNullOrWhiteSpace(value) ? RenameOptions.DefaultFor(Kind) : value.Trim();
            if (Template == v) return;
            if (Kind == RenameKind.Movies) _app.Settings.RenameMovieTemplate = v; else _app.Settings.RenameTemplate = v;
            _app.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(Example));
            OnPropertyChanged(nameof(TemplateWarning));
            _syncPreset = true;
            SelectedPreset = SelectedPreset?.Preset.Template == v ? SelectedPreset : Presets.FirstOrDefault(p => p.Preset.Template == v);
            _syncPreset = false;
            _ = RerenderAsync();
        }
    }

    public string Example
    {
        get
        {
            var info = Kind == RenameKind.Movies
                ? new EpisodeInfo { Movie = true, Title = L.T("Film B: Teil Eins", "Film B: Part One"), Year = "2001", Edition = "Extended" }
                : new EpisodeInfo { Series = "Serie A", Year = "2008", Season = 2, Episode = 9, Title = L.T("Der Anruf", "The Call") };
            return L.T("Beispiel: ", "Example: ") + RenamePlanner.BuildName(Template, info) + ".mkv";
        }
    }

    public bool UseNfo
    {
        get => _app.Settings.RenameUseNfo;
        set { _app.Settings.RenameUseNfo = value; _app.Save(); OnPropertyChanged(); }
    }

    public bool IncludeTarget
    {
        get => _app.Settings.RenameIncludeTarget;
        set { _app.Settings.RenameIncludeTarget = value; _app.Save(); OnPropertyChanged(); }
    }

    private RenameOptions Options() => new()
    {
        Kind = Kind,
        Template = Template,
        UseNfo = UseNfo,
        IncludeTarget = IncludeTarget,
        SeriesFilter = SeriesFilter.Trim(),
        Extensions = _app.Settings.Profiles.SelectMany(p => p.Extensions).Append(".mkv").Append(".mp4").Append(".m2ts").Append(".avi")
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
        Libraries = _app.Settings.Profiles
            .Where(p => !string.IsNullOrWhiteSpace(p.InputDir) && !string.IsNullOrWhiteSpace(p.OutputDir))
            .Select(p => new RenameLibrary(p.InputDir, p.OutputDir)).ToList(),
        LocalJournalDir = LocalJournalDir,
    };

    private string LocalJournalDir => Path.Combine(_app.Store.Folder, "Umbenennungen");

    // ---------------- Quellen ----------------

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner wählen (Serie, Staffel oder ganze Bibliothek)", "Choose a folder (series, season or whole library)"), Multiselect = true };
        var start = Sources.LastOrDefault() ?? _app.Settings.FindProfile(_app.Settings.SelectedProfile)?.InputDir;
        if (start is not null && Io.DirExistsQuick(start)) dlg.InitialDirectory = start;
        if (dlg.ShowDialog() == true) AddSources(dlg.FolderNames);
    }

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = Kind == RenameKind.Movies ? L.T("Filme wählen", "Choose movies") : L.T("Folgen wählen", "Choose episodes"),
            Multiselect = true,
            Filter = L.T("Videos|*.mkv;*.mp4;*.m2ts;*.avi;*.ts;*.m4v|Alle Dateien|*.*", "Videos|*.mkv;*.mp4;*.m2ts;*.avi;*.ts;*.m4v|All files|*.*"),
        };
        var start = Sources.LastOrDefault();
        if (start is not null) dlg.InitialDirectory = Io.DirExistsQuick(start, 800) ? start : Path.GetDirectoryName(start);
        if (dlg.ShowDialog() == true) AddSources(dlg.FileNames);
    }

    [RelayCommand]
    private void ClearSources()
    {
        Sources.Clear();
        ClearPreview();
        SourcesChanged();
    }

    private void ClearPreview()
    {
        Rows.Clear();
        LibraryText = "";
        Message = "";
        UpdateSummary();
        View.Refresh();
    }

    public void AddSources(IEnumerable<string> paths, bool add = false)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list.Count == 0) return;
        if (!add)
        {
            Sources.Clear();
            ClearPreview();
        }
        foreach (var p in list)
        {
            if (Sources.Contains(p, StringComparer.OrdinalIgnoreCase)) continue;
            Sources.Add(p);
            var known = ConvertViewModel.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase);
            if (!known && (!Path.HasExtension(p) || Io.DirExistsQuick(p, 800)))
            {
                var r = _app.Settings.RenameRecent;
                r.RemoveAll(x => x.Equals(p, StringComparison.OrdinalIgnoreCase));
                r.Insert(0, p);
                if (r.Count > 10) r.RemoveRange(10, r.Count - 10);
            }
        }
        _app.Save();
        Recent.Clear();
        foreach (var r in _app.Settings.RenameRecent) Recent.Add(r);
        OnPropertyChanged(nameof(HasRecent));
        SourcesChanged();
        if (AnalyzeCommand.CanExecute(null)) AnalyzeCommand.Execute(null);
        else if (IsBusy) _reanalyze = true;
    }

    private void SourcesChanged()
    {
        OnPropertyChanged(nameof(SourcesText));
        OnPropertyChanged(nameof(HasSources));
        AnalyzeCommand.NotifyCanExecuteChanged();
    }

    // ---------------- Analyse und Vorschau ----------------

    private bool Matches(RenameRow r)
    {
        var ok = Filter switch
        {
            "changes" => r.Item.Changes && r.Status is RenameStatus.Ready or RenameStatus.Check or RenameStatus.Conflict,
            "check" => r.Status == RenameStatus.Check,
            "conflict" => r.Status == RenameStatus.Conflict,
            "unknown" => r.Status == RenameStatus.Unknown,
            "unchanged" => r.Status == RenameStatus.Unchanged,
            _ => true,
        };
        if (!ok) return false;
        var q = Search.Trim();
        return q.Length == 0 || r.OldPath.Contains(q, StringComparison.OrdinalIgnoreCase) || r.NewName.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private bool CanAnalyze() => !IsBusy && Sources.Count > 0;

    private bool _reanalyze;

    [RelayCommand(CanExecute = nameof(CanAnalyze))]
    private async Task AnalyzeAsync()
    {
        var srcs = Sources.ToList();
        var missing = await Io.RunAsync(() => srcs.Where(s => !Directory.Exists(s) && !File.Exists(s)).ToList(), srcs, 15000);
        if (missing.Count == Sources.Count)
        {
            Message = L.T("Nicht erreichbar: ", "Not reachable: ") + string.Join(", ", missing);
            return;
        }
        IsBusy = true;
        ProgressKnown = false;
        Message = L.T("Analysiere …", "Analysing …");
        _rerenderPending = false;
        try
        {
            var opt = Options();
            var sources = Sources.ToList();
            var items = await Task.Run(() => RenamePlanner.Analyze(sources, opt));
            if (!Sources.SequenceEqual(sources, StringComparer.OrdinalIgnoreCase)) _reanalyze = true;
            else
            {
                Fill(items);
                if (items.Count > 0) SchemaExpanded = false;
                var inLib = items.Count(i => i.Library is not null);
                LibraryText = !IncludeTarget ? L.T("Nur die gewählten Dateien werden umbenannt (Ziel mitziehen ist aus).", "Only the chosen files are renamed (target sync is off).")
                    : inLib == 0 ? L.T("Liegt in keinem Profil-Quellordner: nur die gewählten Dateien werden umbenannt.", "Not inside any profile source folder: only the chosen files are renamed.")
                    : inLib == items.Count ? L.T("Liegt im Quellordner eines Profils: komprimierte Dateien und Konverter-Vermerke werden mit umbenannt.", "Inside a profile source folder: compressed files and converter notes are renamed too.")
                    : PartlyInLibraryText(inLib, items.Count);
                Message = missing.Count > 0 ? L.T("Nicht erreichbar: ", "Not reachable: ") + string.Join(", ", missing) : "";
                LoadJournals();
            }
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
        if (_reanalyze)
        {
            _reanalyze = false;
            if (Sources.Count > 0) await AnalyzeAsync();
            return;
        }
        if (_rerenderPending) await RerenderAsync();
    }

    private string PartlyInLibraryText(int inLib, int all) =>
        L.T($"{inLib:N0} von {Items(all)} {(inLib == 1 ? "liegt" : "liegen")} in einem Profil-Quellordner: dort werden Ziel und Vermerke mit umbenannt.",
            $"{inLib:N0} of {Items(all)} {(inLib == 1 ? "is" : "are")} in a profile source folder: target and notes are renamed there too.");

    private void Fill(IEnumerable<RenameItem> items)
    {
        Rows.Clear();
        foreach (var it in items) Rows.Add(new RenameRow(it, this));
        UpdateSummary();
        View.Refresh();
    }

    private bool _rerenderPending;

    private async Task RerenderAsync()
    {
        if (IsBusy) { _rerenderPending = true; return; }
        _rerenderPending = false;
        if (Rows.Count == 0) return;
        IsBusy = true;
        try
        {
            var items = Rows.Select(r => r.Item).ToList();
            var t = Template;
            await Task.Run(() =>
            {
                RenamePlanner.Render(items, t);
                RenamePlanner.Validate(items);
            });
            foreach (var r in Rows) r.Refresh();
            UpdateSummary();
            View.Refresh();
        }
        finally
        {
            IsBusy = false;
        }
        if (_rerenderPending)
        {
            _rerenderPending = false;
            await RerenderAsync();
        }
    }

    public void Revalidate(RenameRow row)
    {
        var items = Rows.Select(r => r.Item).ToList();
        RenamePlanner.Render([row.Item], Template);
        RenamePlanner.Validate(items, row.Item);
        if (row.Item.CanRename) row.Item.Selected = true;
        foreach (var r in Rows.Where(r => r == row || r.Status == RenameStatus.Conflict || r.Item.NewName.Equals(row.Item.NewName, StringComparison.OrdinalIgnoreCase)))
            r.Refresh();
        UpdateSummary();
    }

    public void RowChanged() => UpdateSummary();

    private void UpdateSummary()
    {
        int Count(RenameStatus s) => Rows.Count(r => r.Status == s);
        SelectedCount = Rows.Count(r => r.Item.Selected && r.CanSelect);
        var changes = Rows.Count(r => r.Item.Changes && r.Status is RenameStatus.Ready or RenameStatus.Check or RenameStatus.Conflict);
        foreach (var f in Filters)
        {
            var n = f.Key switch
            {
                "changes" => changes,
                "check" => Count(RenameStatus.Check),
                "conflict" => Count(RenameStatus.Conflict),
                "unknown" => Count(RenameStatus.Unknown),
                "unchanged" => Count(RenameStatus.Unchanged),
                _ => Rows.Count,
            };
            f.Text = Rows.Count == 0 ? f.Label : $"{f.Label} ({n:N0})";
        }
        Summary = Rows.Count == 0 ? "" : SummaryText(Rows.Count, Count(RenameStatus.Ready), Count(RenameStatus.Check), Count(RenameStatus.Conflict), Count(RenameStatus.Unknown), Count(RenameStatus.Unchanged));
    }

    private string SummaryText(int all, int ready, int check, int conflicts, int unknown, int unchanged) => L.T(
        $"{Items(all)} · {ready:N0} sicher · {check:N0} prüfen · {RunTexts.Count(conflicts, "Konflikt", "Konflikte", "conflict", "conflicts")} · {unknown:N0} nicht erkannt · {unchanged:N0} schon richtig",
        $"{Items(all)} · {ready:N0} safe · {check:N0} to check · {RunTexts.Count(conflicts, "Konflikt", "Konflikte", "conflict", "conflicts")} · {unknown:N0} not recognised · {unchanged:N0} already correct");

    [RelayCommand]
    private void SelectSafe()
    {
        foreach (var r in Rows)
        {
            r.Item.Selected = r.Status == RenameStatus.Ready && r.Item.Changes;
            r.Refresh();
        }
        UpdateSummary();
    }

    [RelayCommand]
    private void SelectAllVisible()
    {
        foreach (var r in View.Cast<RenameRow>().Where(r => r.CanSelect))
        {
            r.Item.Selected = true;
            r.Refresh();
        }
        UpdateSummary();
    }

    [RelayCommand]
    private void SelectNone()
    {
        foreach (var r in Rows)
        {
            r.Item.Selected = false;
            r.Refresh();
        }
        UpdateSummary();
    }

    [RelayCommand]
    private void ResetRow(RenameRow? row)
    {
        if (row is null) return;
        row.Item.Edited = false;
        RenamePlanner.Render([row.Item], Template);
        RenamePlanner.Validate(Rows.Select(r => r.Item).ToList(), row.Item);
        row.Refresh();
        UpdateSummary();
    }

    [RelayCommand]
    private void OpenRowFolder(RenameRow? row)
    {
        if (row is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{row.Item.Source}\"") { UseShellExecute = true }); }
        catch { }
    }

    // ---------------- Ausfuehren und rueckgaengig ----------------

    private bool CanExecute() => !IsBusy && SelectedCount > 0;

    [ObservableProperty] private bool _isExecuting;

    [RelayCommand(CanExecute = nameof(CanExecute))]
    private async Task ExecuteAsync()
    {
        var selected = Rows.Where(r => r.Item.Selected && r.CanSelect).Select(r => r.Item).ToList();
        var inLib = selected.Any(i => i.Library is not null);
        if (BlockedByRun(selected.SelectMany(RenameGuard.Touched))) return;
        var answer = System.Windows.MessageBox.Show(
            L.T($"{Items(SelectedCount)} umbenennen?\n\nBegleitdateien (.nfo, Untertitel, Bilder){(inLib ? ", die komprimierten Dateien im Ziel und die Konverter-Vermerke" : "")} werden mit umbenannt. Ordner bleiben unverändert.\n\nAlles lässt sich unten über „Rückgängig“ zurücknehmen.",
                $"Rename {Items(SelectedCount)}?\n\nCompanion files (.nfo, subtitles, images){(inLib ? ", the compressed files in the target and the converter notes" : "")} are renamed too. Folders stay unchanged.\n\nEverything can be reverted below via \"Undo\"."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        if (BlockedByRun(selected.SelectMany(RenameGuard.Touched))) return;

        IsBusy = true;
        IsExecuting = true;
        ProgressKnown = true;
        Progress = 0;
        Message = L.T("Benenne um …", "Renaming …");
        string msg;
        try
        {
            var opt = Options();
            var items = Rows.Select(r => r.Item).ToList();
            var prog = new Progress<(int Done, int Total)>(x => Progress = x.Total > 0 ? x.Done * 100d / x.Total : 0);
            var res = await Task.Run(() => RenamePlanner.Execute(items, opt, prog));
            _app.WriteLog($"Umbenennen: {res.Renamed} Folgen, {res.Files} Dateien, {res.Failed} Fehler, Protokoll {res.Journal}");
            foreach (var e in res.Errors) _app.WriteLog("Umbenennen FEHLER " + e);
            msg = res.Failed == 0
                ? L.T($"{Items(res.Renamed)} umbenannt ({RunTexts.Files(res.Files)}).", $"{Items(res.Renamed)} renamed ({RunTexts.Files(res.Files)}).")
                : L.T($"{Items(res.Renamed)} umbenannt, {res.Failed} nicht: ", $"{Items(res.Renamed)} renamed, {res.Failed} not: ") + string.Join(" | ", res.Errors.Take(3));
        }
        catch (Exception ex)
        {
            msg = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsExecuting = false;
            ProgressKnown = false;
        }
        await AnalyzeAsync();
        Message = msg;
    }

    private bool BlockedByRun(IEnumerable<string> files)
    {
        var active = _app.Run.ActiveFolders;
        if (active.Count == 0 || RenameGuard.Conflict(files, active) is not { } hit) return false;
        Message = L.T($"Im Ordner {Path.GetDirectoryName(hit)} arbeitet gerade ein Lauf (siehe Startseite). Dort erst umbenennen oder rückgängig machen, wenn er beendet ist. Andere Ordner gehen jederzeit.",
                      $"A job is working in the folder {Path.GetDirectoryName(hit)} right now (see the start page). Rename or undo there once it has finished. Other folders work anytime.");
        return true;
    }

    public void LoadJournals() => _ = LoadJournalsAsync();

    public async Task LoadJournalsAsync()
    {
        var dirs = new List<string?> { LocalJournalDir };
        dirs.AddRange(_app.Settings.Profiles.Where(p => !string.IsNullOrWhiteSpace(p.OutputDir)).Select(p => Path.Combine(p.OutputDir, RenamePlanner.JournalFolder)));
        var found = await Io.RunAsync(() => RenamePlanner.Journals(dirs), [], 20000);
        Journals.Clear();
        try
        {
            foreach (var j in found)
            {
                Journals.Add(new JournalItem(j.File, JournalText(j.Time, j.Computer, j.Moves, j.Root, j.Undone), j.Undone));
            }
        }
        catch
        {
        }
        SelectedJournal = Journals.FirstOrDefault(j => !j.Undone);
        DeleteAllJournalsCommand.NotifyCanExecuteChanged();
    }

    private bool CanDeleteJournal() => !IsBusy && SelectedJournal is { File.Length: > 0 };
    private bool CanDeleteAllJournals() => !IsBusy && Journals.Any(j => j.File.Length > 0);

    [RelayCommand(CanExecute = nameof(CanDeleteJournal))]
    private async Task DeleteJournalAsync()
    {
        if (SelectedJournal is not { } j) return;
        var answer = System.Windows.MessageBox.Show(
            L.T($"Diesen Lauf aus der Liste löschen?\n\n{j.Text}\n\nDie Dateien bleiben, wie sie sind. Rückgängig machen lässt er sich danach nicht mehr.",
                $"Delete this run from the list?\n\n{j.Text}\n\nThe files stay as they are. It can no longer be undone afterwards."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var n = await Io.RunAsync(() => RenamePlanner.DeleteJournals([j.File]), 0, 15000);
        Message = n == 1 ? L.T("Lauf aus der Liste gelöscht.", "Run deleted from the list.") : L.T("Konnte nicht gelöscht werden (gesperrt oder nicht erreichbar).", "Could not be deleted (locked or not reachable).");
        await LoadJournalsAsync();
    }

    [RelayCommand(CanExecute = nameof(CanDeleteAllJournals))]
    private async Task DeleteAllJournalsAsync()
    {
        var files = Journals.Where(j => j.File.Length > 0).Select(j => j.File).ToList();
        if (files.Count == 0) return;
        var answer = System.Windows.MessageBox.Show(
            L.T($"Alle {files.Count} früheren Läufe aus der Liste löschen?\n\nDie Dateien bleiben, wie sie sind. Rückgängig machen lassen sich diese Läufe danach nicht mehr.",
                $"Delete all {files.Count} previous runs from the list?\n\nThe files stay as they are. These runs can no longer be undone afterwards."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var n = await Io.RunAsync(() => RenamePlanner.DeleteJournals(files), 0, 30000);
        Message = n == files.Count
            ? L.T($"{n} frühere Läufe gelöscht.", $"{n} previous runs deleted.")
            : L.T($"{n} von {files.Count} gelöscht, der Rest ist gesperrt oder nicht erreichbar.", $"{n} of {files.Count} deleted, the rest is locked or not reachable.");
        await LoadJournalsAsync();
    }

    private static string JournalText(DateTime time, string computer, int moves, string root, bool undone) =>
        L.T($"{time:dd.MM.yyyy HH:mm} · {computer} · {RunTexts.Files(moves)} · {root}", $"{time:yyyy-MM-dd HH:mm} · {computer} · {RunTexts.Files(moves)} · {root}")
        + (undone ? L.T(" · rückgängig gemacht", " · undone") : "");

    private bool CanUndo() => !IsBusy && SelectedJournal is { Undone: false };

    [RelayCommand(CanExecute = nameof(CanUndo))]
    private async Task UndoAsync()
    {
        var j = SelectedJournal;
        if (j is null) return;
        List<string>? files = null;
        if (_app.Run.IsBusy)
        {
            files = await Io.RunAsync(() => RenameGuard.JournalFiles(j.File), null, 15000);
            if (files is null)
            {
                Message = L.T("Das Protokoll ist gerade nicht lesbar. Bitte erst nach dem laufenden Lauf rückgängig machen.", "The journal cannot be read right now. Please undo after the running job has finished.");
                return;
            }
            if (BlockedByRun(files)) return;
        }
        var answer = System.Windows.MessageBox.Show(
            L.T($"Diesen Lauf rückgängig machen?\n\n{j.Text}", $"Undo this run?\n\n{j.Text}"),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        if (_app.Run.IsBusy)
        {
            files ??= await Io.RunAsync(() => RenameGuard.JournalFiles(j.File), null, 15000);
            if (files is null || BlockedByRun(files))
            {
                if (files is null) Message = L.T("Das Protokoll ist gerade nicht lesbar. Bitte erst nach dem laufenden Lauf rückgängig machen.", "The journal cannot be read right now. Please undo after the running job has finished.");
                return;
            }
        }
        IsBusy = true;
        IsExecuting = true;
        string msg;
        try
        {
            var res = await Task.Run(() => RenamePlanner.Undo(j.File));
            _app.WriteLog($"Umbenennen rueckgaengig: {res.Renamed} Dateien, {res.Failed} uebersprungen ({j.File})");
            msg = res.Failed == 0
                ? L.T($"{RunTexts.Files(res.Renamed)} zurückbenannt.", $"{RunTexts.Files(res.Renamed)} renamed back.")
                : L.T($"{RunTexts.Files(res.Renamed)} zurückbenannt, {res.Failed} übersprungen: ", $"{RunTexts.Files(res.Renamed)} renamed back, {res.Failed} skipped: ") + string.Join(" | ", res.Errors.Take(3));
        }
        catch (Exception ex)
        {
            msg = ex.Message;
        }
        finally
        {
            IsBusy = false;
            IsExecuting = false;
        }
        if (Sources.Count > 0) await AnalyzeAsync(); else LoadJournals();
        Message = msg;
    }

    public void FillMovieDemo()
    {
        Sources.Clear();
        Kind = RenameKind.Movies;
        Sources.Add(DemoText.T(@"\\NAS\Filme"));
        SourcesChanged();
        var samples = new (string Rel, string Folder, string NfoTitle, string NfoYear)[]
        {
            (@"Film A (2009)\film.a.2009.german.dl.1080p.bluray.x264-grp.mkv", "Film A (2009)", "", ""),
            (DemoText.T(@"Sammlung\Film.B.Teil.Eins.2001.Extended.German.DL.1080p.mkv"), "", DemoText.T("Film B: Teil Eins"), "2001"),
            (DemoText.T(@"Sammlung\Film.C.1982.Final.Cut.German.DTS.DL.1080p.mkv"), "", "", ""),
            (DemoText.T(@"Sammlung\Film.D.German.DL.1080p.mkv"), "", "", ""),
            (DemoText.T(@"Sammlung\Film.E.1972.German.cd1.mkv"), "", "", ""),
            (DemoText.T(@"Sammlung\film-f-sample.mkv"), "", "", ""),
        };
        var items = new List<RenameItem>();
        foreach (var (rel, folder, nfoTitle, nfoYear) in samples)
        {
            var info = EpisodeParser.ParseMovie(Path.GetFileNameWithoutExtension(rel));
            if (folder.Length > 0) { var f = EpisodeParser.ParseMovie(folder); info = info with { Title = f.Title, Year = f.Year }; }
            if (nfoTitle.Length > 0) info = info with { Title = nfoTitle, Year = nfoYear, TitleSource = "nfo" };
            if (rel.Contains("sample")) info = info with { Title = "" };
            var it = new RenameItem { Source = DemoText.T(@"\\NAS\Filme\") + rel, RelativeSource = rel, Info = info, Library = new RenameLibrary(DemoText.T(@"\\NAS\Filme"), DemoText.T(@"\\NAS\Komprimiert-Filme")) };
            if (!info.Recognized) it.AnalysisNotes.Add(L.T("Sample oder Trailer: wird nicht umbenannt", "Sample or trailer: not renamed"));
            else if (info.Year.Length == 0) it.AnalysisNotes.Add(L.T("kein Jahr gefunden", "no year found"));
            items.Add(it);
        }
        RenamePlanner.Render(items, Template, initial: true);
        SchemaExpanded = false;
        Filter = "all";
        Fill(items);
        LibraryText = "";
        Journals.Clear();
        Journals.Add(new JournalItem("", JournalText(new DateTime(2026, 11, 9, 20, 15, 0), "PC-1", 312, DemoText.T(@"\\NAS\Filme"), false), false));
        SelectedJournal = Journals[0];
    }

    public void FillDemo()
    {
        Sources.Clear();
        Kind = RenameKind.Series;
        if (!_app.Settings.RenamePresets.Any(p => p.Name == L.T("Meine Vorlage", "My preset")))
            _app.Settings.RenamePresets.Add(new RenamePreset(L.T("Meine Vorlage", "My preset"), "S{S}E{E} · {Titel}"));
        LoadPresets();
        Sources.Add(DemoText.T(@"\\NAS\Serien\Serie A"));
        Sources.Add(DemoText.T(@"\\NAS\Serien\Serie D\Staffel 1"));
        Sources.Add(DemoText.T(@"D:\Downloads\Serie C.S08E12.German.DL.1080p.BluRay.x264.mkv"));
        SourcesChanged();
        var samples = new (string Rel, string Folder, string Series, string NfoTitle, bool Lib)[]
        {
            (DemoText.T(@"Serie A\Staffel 2\Serie.A.S02E09.Der.Anruf.German.DL.AC3.5.1.1080p.Bluray.x264-iND.mkv"), DemoText.T("Staffel 2"), DemoText.T("Serie A"), "", true),
            (DemoText.T(@"Serie A\Staffel 4\Folge 10 (Heimkehr).mkv"), DemoText.T("Staffel 4"), DemoText.T("Serie A"), "", true),
            (DemoText.T(@"Serie A\Staffel 4\Serie.A.S04E11.German.DL.1080p.mkv"), DemoText.T("Staffel 4"), DemoText.T("Serie A"), DemoText.T("Neue Wege"), true),
            (DemoText.T(@"Serie C\Serie C.S08E12.German.DL.1080p.BluRay.x264.mkv"), "", DemoText.T("Serie C"), DemoText.T("Das Ende"), false),
            (DemoText.T(@"Serie D\Staffel 1\Serie.D.S01E04.German.DL.720p.mkv"), DemoText.T("Staffel 1"), DemoText.T("Serie D"), "", true),
            (DemoText.T(@"Serie D\Staffel 1\S01E05 - Der Fremde.mkv"), DemoText.T("Staffel 1"), DemoText.T("Serie D"), DemoText.T("Der Fremde"), true),
            (DemoText.T(@"Serie D\Staffel 1\Making-of.mkv"), DemoText.T("Staffel 1"), DemoText.T("Serie D"), "", true),
        };
        var items = new List<RenameItem>();
        foreach (var (rel, folder, series, nfo, lib) in samples)
        {
            var info = EpisodeParser.Parse(Path.GetFileNameWithoutExtension(rel), folder.Length > 0 ? folder : null) with { Series = series };
            if (nfo.Length > 0) info = info with { Title = nfo, TitleSource = "nfo" };
            var it = new RenameItem { Source = DemoText.T(@"\\NAS\Serien\") + rel, RelativeSource = rel, Info = info, Library = lib ? new RenameLibrary(DemoText.T(@"\\NAS\Serien"), DemoText.T(@"\\NAS\Komprimiert")) : null };
            if (info.Season is null || info.Episode is null) it.AnalysisNotes.Add(L.T("Staffel/Folge nicht erkennbar", "Season/episode not recognised"));
            else
            {
                if (info.SeasonFromFolder) it.AnalysisNotes.Add(L.T("Staffel aus dem Ordnernamen", "Season taken from the folder name"));
                if (info.Title.Length == 0) it.AnalysisNotes.Add(L.T("kein Folgentitel gefunden", "no episode title found"));
            }
            items.Add(it);
        }
        RenamePlanner.Render(items, Template, initial: true);
        SchemaExpanded = false;
        Filter = "all";
        Fill(items);
        LibraryText = PartlyInLibraryText(6, 7);
        Journals.Clear();
        Journals.Add(new JournalItem("", JournalText(new DateTime(2026, 11, 11, 15, 30, 0), "PC-2", 1284, DemoText.T(@"\\NAS\Serien"), false), false));
        SelectedJournal = Journals[0];
    }
}
