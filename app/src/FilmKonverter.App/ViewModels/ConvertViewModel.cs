using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Converting;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;
using Microsoft.Win32;

namespace FilmKonverter.App.ViewModels;

public enum ConvertRowState
{
    Reading,
    Ready,
    NotPossible,
    Queued,
    Running,
    Done,
    Exists,
    Failed,
    Cancelled,
}

public sealed partial class ConvertRow : ObservableObject
{
    public ConvertRow(string source, string? baseDir)
    {
        Source = source;
        BaseDir = baseDir;
    }

    public string Source { get; }
    public string? BaseDir { get; }
    public string Name => Path.GetFileName(Source);
    public string Folder => Path.GetDirectoryName(Source) ?? "";
    public MediaInfo? Media { get; set; }
    public bool Probed { get; set; }

    [ObservableProperty] private string _sourceText = L.T("wird gelesen …", "reading …");
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _warnText = "";
    [ObservableProperty] private string _outputPath = "";

    public string PlannedPath { get; set; } = "";

    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush), nameof(IsActive), nameof(ResultBrush), nameof(CanRemove))]
    private ConvertRowState _state;

    public bool IsActive => State == ConvertRowState.Running;
    public bool CanRemove => State is not (ConvertRowState.Running or ConvertRowState.Queued);

    public Brush StateBrush => State switch
    {
        ConvertRowState.Ready or ConvertRowState.Queued => Palette.Slate,
        ConvertRowState.Running => Palette.Blue,
        ConvertRowState.Done => Palette.Green,
        ConvertRowState.Exists => Palette.Gray,
        ConvertRowState.NotPossible => Palette.Amber,
        ConvertRowState.Failed => Palette.Red,
        _ => Palette.Gray,
    };

    public Brush ResultBrush => State switch
    {
        ConvertRowState.Done => Palette.TextOk,
        ConvertRowState.Failed => Palette.TextErr,
        ConvertRowState.NotPossible => Palette.TextWarn,
        _ => Palette.Resource("TextFillColorSecondaryBrush", Palette.Gray),
    };
}

public sealed partial class TargetOption(ConvertTarget target, string title, string description, Action<TargetOption> onSelect) : ObservableObject
{
    public ConvertTarget Target { get; } = target;
    public string Title { get; } = title;
    public string BaseDescription { get; } = description;

    [ObservableProperty] private string _description = description;
    [ObservableProperty] private bool _isAvailable = true;
    [ObservableProperty] private bool _isSelected;

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) onSelect(this);
    }
}

public sealed partial class ConvertViewModel : ObservableObject, IConvertObserver
{
    private readonly AppState _app;
    private ConvertCaps? _caps;
    private Task<ConvertCaps?>? _capsTask;
    private FfmpegTools? _capsFor;
    private FileConverter? _conv;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly Dictionary<int, ConvertRow> _jobRows = [];
    private bool _loading;

    public static readonly string[] Extensions =
    [
        ".mkv", ".mp4", ".m4v", ".avi", ".divx", ".mov", ".wmv", ".asf", ".ts", ".m2ts", ".mts", ".mpg", ".mpeg", ".vob", ".webm", ".flv", ".3gp", ".ogm",
        ".mka", ".flac", ".mp3", ".m4a", ".wav", ".ac3", ".dts", ".aac", ".ogg", ".opus",
    ];

    public event Action<string, string, bool>? Notify;

    public ObservableCollection<ConvertRow> Rows { get; } = [];
    public List<TargetOption> RemuxTargets { get; }
    public List<TargetOption> DeviceTargets { get; }
    public List<TargetOption> AudioTargets { get; }
    private IEnumerable<TargetOption> AllTargets => RemuxTargets.Concat(DeviceTargets).Concat(AudioTargets);

    public List<IntOption> ParallelOptions { get; } = [new(1, "1"), new(2, "2"), new(3, "3"), new(4, "4")];

    [ObservableProperty] private ConvertTarget _target;
    [ObservableProperty] private bool _toFolder;
    [ObservableProperty] private string _outputDir = "";
    [ObservableProperty] private bool _keepStructure;
    [ObservableProperty] private string _suffix = "";
    [ObservableProperty] private int _parallel;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private string _capsText = "";
    [ObservableProperty] private double _totalPercent;
    [ObservableProperty] private bool _hasRows;

    public bool IsIdle => !IsRunning;

    public ConvertViewModel(AppState app)
    {
        _app = app;
        var s = app.Settings;
        RemuxTargets =
        [
            new(ConvertTarget.RemuxMkv, "MKV", L.T("Alle Spuren bleiben, nichts wird neu kodiert. Ideal für alte AVI-, TS- und MP4-Dateien.", "All tracks stay, nothing is re-encoded. Ideal for old AVI, TS and MP4 files."), Select),
            new(ConvertTarget.RemuxMp4, "MP4", L.T("Video und Ton werden kopiert. Was MP4 nicht kann, wird umgewandelt oder entfällt (wird vorher angezeigt).", "Video and audio are copied. What MP4 cannot hold is converted or dropped (shown beforehand)."), Select),
        ];
        DeviceTargets =
        [
            new(ConvertTarget.Compatible, L.T("Maximal kompatibel", "Maximum compatibility"), L.T("H.264 + AAC in MP4, höchstens 1080p. Läuft auf praktisch jedem Fernseher, Handy und Browser.", "H.264 + AAC in MP4, at most 1080p. Plays on practically every TV, phone and browser."), Select),
            new(ConvertTarget.Mobile, L.T("Handy und Tablet", "Phone and tablet"), L.T("H.264, höchstens 720p, eine Tonspur in Stereo. Klein, zum Mitnehmen und Verschicken.", "H.264, at most 720p, one stereo audio track. Small, for taking along and sending."), Select),
            new(ConvertTarget.Apple, "Apple", L.T("HEVC + AAC in MP4 für iPhone, iPad, Mac und Apple TV. HEVC-Quellen werden nur umverpackt.", "HEVC + AAC in MP4 for iPhone, iPad, Mac and Apple TV. HEVC sources are only repackaged."), Select),
            new(ConvertTarget.Av1, L.T("AV1, maximal klein", "AV1, smallest"), L.T("Moderner Codec, deutlich kleiner bei gleicher Qualität. MKV, alle Spuren bleiben.", "Modern codec, much smaller at the same quality. MKV, all tracks stay."), Select),
        ];
        AudioTargets =
        [
            new(ConvertTarget.AudioMp3, "MP3", L.T("Hauptspur als MP3 in Stereo (ca. 190 kbit/s). Läuft überall.", "Main track as stereo MP3 (approx. 190 kbit/s). Plays everywhere."), Select),
            new(ConvertTarget.AudioM4a, "AAC (M4A)", L.T("Hauptspur als AAC in Stereo, kleiner als MP3 bei gleichem Klang.", "Main track as stereo AAC, smaller than MP3 at the same sound."), Select),
            new(ConvertTarget.AudioFlac, "FLAC", L.T("Verlustfrei, alle Kanäle bleiben.", "Lossless, all channels stay."), Select),
        ];
        _loading = true;
        Target = s.ConvertTarget;
        ToFolder = s.ConvertToFolder;
        OutputDir = s.ConvertOutputDir;
        KeepStructure = s.ConvertKeepStructure;
        Suffix = s.ConvertSuffix;
        Parallel = Math.Clamp(s.ConvertParallel, 1, 4);
        foreach (var t in AllTargets) t.IsSelected = t.Target == Target;
        _loading = false;
        Rows.CollectionChanged += (_, _) => { HasRows = Rows.Count > 0; UpdateSummary(); };
    }

    private void Select(TargetOption opt)
    {
        foreach (var t in AllTargets.Where(t => t != opt)) t.IsSelected = false;
        Target = opt.Target;
    }

    partial void OnTargetChanged(ConvertTarget value)
    {
        if (_loading) return;
        _app.Settings.ConvertTarget = value;
        _app.Save();
        foreach (var t in AllTargets) if (t.IsSelected != (t.Target == value)) t.IsSelected = t.Target == value;
        foreach (var r in Rows.Where(r => r.State is not (ConvertRowState.Running or ConvertRowState.Queued or ConvertRowState.Reading))) PlanRow(r);
        UpdateSummary();
    }

    partial void OnToFolderChanged(bool value) => SaveOptions(outputChanged: true);
    partial void OnOutputDirChanged(string value) => SaveOptions(outputChanged: true);
    partial void OnKeepStructureChanged(bool value) => SaveOptions(outputChanged: true);
    partial void OnSuffixChanged(string value) => SaveOptions(outputChanged: true);
    partial void OnParallelChanged(int value) => SaveOptions(outputChanged: false);

    private void SaveOptions(bool outputChanged)
    {
        if (_loading) return;
        var s = _app.Settings;
        s.ConvertToFolder = ToFolder;
        s.ConvertOutputDir = OutputDir.Trim();
        s.ConvertKeepStructure = KeepStructure;
        s.ConvertSuffix = Suffix.Trim();
        s.ConvertParallel = Parallel;
        _app.Save();
        if (!outputChanged || _caps is null) return;
        var open = Rows.Where(r => r.State is ConvertRowState.Ready or ConvertRowState.Done or ConvertRowState.Exists or ConvertRowState.Failed or ConvertRowState.Cancelled).ToList();
        foreach (var r in open.Where(r => !string.Equals(PreviewPath(r), r.PlannedPath, StringComparison.OrdinalIgnoreCase))) PlanRow(r);
        UpdateSummary();
    }

    // ---------------- Faehigkeiten dieses PCs ----------------

    public async Task<ConvertCaps?> EnsureCapsAsync()
    {
        var tools = await _app.EnsureToolsAsync();
        if (tools is null)
        {
            CapsText = L.T("ffmpeg nicht gefunden (Seite „ffmpeg“).", "ffmpeg not found (“ffmpeg” page).");
            return null;
        }
        if (_capsTask is null || !ReferenceEquals(_capsFor, tools))
        {
            _capsFor = tools;
            _capsTask = DetectCapsAsync(tools);
        }
        return await _capsTask;
    }

    private async Task<ConvertCaps?> DetectCapsAsync(FfmpegTools tools)
    {
        CapsText = L.T("Prüfe, was dieser PC kann …", "Checking what this PC can do …");
        var caps = await Task.Run(() => ConvertCaps.DetectAsync(tools));
        ApplyCaps(caps);
        return caps;
    }

    private void ApplyCaps(ConvertCaps caps)
    {
        _caps = caps;
        foreach (var t in AllTargets)
        {
            t.IsAvailable = caps.Available(t.Target);
            var enc = t.Target switch
            {
                ConvertTarget.Compatible or ConvertTarget.Mobile => caps.H264,
                ConvertTarget.Apple => caps.Hevc,
                ConvertTarget.Av1 => caps.Av1,
                _ => null,
            };
            t.Description = !t.IsAvailable ? L.T("Auf diesem PC nicht verfügbar (kein passender Encoder).", "Not available on this PC (no suitable encoder).")
                : ConvertCaps.IsCpu(enc) ? t.BaseDescription + L.T(" Läuft hier über den Prozessor (langsamer).", " Runs on the CPU here (slower).")
                : t.BaseDescription;
        }
        string Hw(string? e) => e is null ? "–" : e.EndsWith("_nvenc", StringComparison.Ordinal) ? "NVIDIA" : e.EndsWith("_amf", StringComparison.Ordinal) ? "AMD"
            : e.EndsWith("_qsv", StringComparison.Ordinal) ? "Intel" : L.T("Prozessor", "CPU");
        CapsText = L.T($"Dieser PC kodiert H.264 über {Hw(caps.H264)}, HEVC über {Hw(caps.Hevc)}, AV1 über {Hw(caps.Av1)}.",
                       $"This PC encodes H.264 on {Hw(caps.H264)}, HEVC on {Hw(caps.Hevc)}, AV1 on {Hw(caps.Av1)}.");
        foreach (var r in Rows.Where(r => r.Probed && r.State is ConvertRowState.Reading or ConvertRowState.Ready or ConvertRowState.NotPossible)) PlanRow(r);
        UpdateSummary();
    }

    // ---------------- Dateien ----------------

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Dateien zum Konvertieren wählen", "Choose files to convert"),
            Multiselect = true,
            Filter = L.T("Video und Ton|", "Video and audio|") + string.Join(";", Extensions.Select(e => "*" + e)) + L.T("|Alle Dateien|*.*", "|All files|*.*"),
        };
        var last = Rows.LastOrDefault()?.Folder;
        if (last is not null && Io.DirExistsQuick(last, 800)) dlg.InitialDirectory = last;
        if (dlg.ShowDialog() == true) _ = AddSourcesAsync(dlg.FileNames);
    }

    // ---------------- Lauf ----------------

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner wählen (alle Video- und Tondateien darin)", "Choose a folder (all video and audio files in it)"), Multiselect = true };
        if (dlg.ShowDialog() == true) _ = AddSourcesAsync(dlg.FolderNames);
    }

    [RelayCommand]
    private void PickOutputDir()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Zielordner wählen", "Choose the target folder") };
        if (Io.DirExistsQuick(OutputDir, 800)) dlg.InitialDirectory = OutputDir;
        if (dlg.ShowDialog() != true) return;
        OutputDir = dlg.FolderName;
        ToFolder = true;
    }

    public async Task AddSourcesAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        Message = "";
        List<(string File, string? Base)> found;
        try
        {
            found = await Task.Run(() =>
            {
                var r = new List<(string, string?)>();
                foreach (var p in list)
                {
                    if (Directory.Exists(p))
                    {
                        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(p)) ?? p;
                        foreach (var f in Scanner.Find(p, Extensions)) r.Add((f, parent));
                    }
                    else if (File.Exists(p) && Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) r.Add((p, null));
                }
                return r;
            });
        }
        catch (Exception ex)
        {
            Message = ex.Message;
            return;
        }
        var known = new HashSet<string>(Rows.Select(r => r.Source), StringComparer.OrdinalIgnoreCase);
        var added = new List<ConvertRow>();
        foreach (var (file, b) in found)
        {
            if (FileConverter.IsTempName(file) || !known.Add(file)) continue;
            var row = new ConvertRow(file, b);
            Rows.Add(row);
            added.Add(row);
        }
        if (added.Count == 0)
        {
            Message = found.Count == 0 ? L.T("Keine Video- oder Tondateien gefunden.", "No video or audio files found.") : L.T("Schon in der Liste.", "Already in the list.");
            return;
        }
        await AnalyzeAsync(added);
    }

    private async Task AnalyzeAsync(List<ConvertRow> rows)
    {
        var tools = await _app.EnsureToolsAsync();
        if (tools is null)
        {
            Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found.");
            foreach (var r in rows) { r.Probed = true; r.SourceText = ""; r.State = ConvertRowState.NotPossible; r.WarnText = Message; }
            UpdateSummary();
            return;
        }
        await _app.LoadProbeCacheAsync();
        var capsTask = EnsureCapsAsync();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                var media = await Task.Run(async () =>
                {
                    try
                    {
                        return ProbeCache.Shared is { } cache ? await cache.GetOrProbeAsync(tools, row.Source, CancellationToken.None) : await tools.ProbeAsync(row.Source);
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        return null;
                    }
                });
                row.Media = media;
                row.Probed = true;
                row.SourceText = media is null ? L.T("nicht lesbar", "not readable") : $"{ConvertPlanner.Describe(media)} · {Fmt.Size(media.SizeBytes)}";
                if (_caps is not null) PlanRow(row);
            }
            finally
            {
                gate.Release();
            }
        }));
        if (ProbeCache.Shared is { } pc) await Task.Run(pc.Save);
        if (await capsTask is null)
            foreach (var r in rows.Where(r => r.State == ConvertRowState.Reading)) { r.State = ConvertRowState.NotPossible; r.WarnText = L.T("ffmpeg nicht gefunden.", "ffmpeg not found."); }
        foreach (var r in rows.Where(r => r.State == ConvertRowState.Reading)) PlanRow(r);
        UpdateSummary();
    }

    private string PreviewPath(ConvertRow r) =>
        ConvertPlanner.OutputPath(r.Source, r.BaseDir, ToFolder ? OutputDir.Trim() : null, KeepStructure, Target, Suffix);

    private void PlanRow(ConvertRow r)
    {
        r.Percent = 0;
        r.ProgressText = "";
        r.ResultText = "";
        if (_caps is null) { r.State = ConvertRowState.Reading; return; }
        if (r.Media is null)
        {
            r.State = ConvertRowState.NotPossible;
            r.PlanText = "";
            r.WarnText = L.T("Datei nicht lesbar.", "File not readable.");
            return;
        }
        var plan = ConvertPlanner.Plan(r.Media, Target, _caps, r.Source);
        r.PlanText = plan.Ok ? string.Join(" · ", plan.Changes) : "";
        r.WarnText = plan.Error ?? string.Join(" · ", plan.Warnings);
        r.PlannedPath = plan.Ok ? PreviewPath(r) : "";
        r.OutputPath = r.PlannedPath;
        r.State = plan.Ok ? ConvertRowState.Ready : ConvertRowState.NotPossible;
    }

    [RelayCommand]
    private void RemoveRow(ConvertRow? row)
    {
        if (row is not null && row.CanRemove) Rows.Remove(row);
    }

    [RelayCommand]
    private void RemoveFinished()
    {
        foreach (var r in Rows.Where(r => r.State is ConvertRowState.Done or ConvertRowState.Exists).ToList()) Rows.Remove(r);
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var r in Rows.Where(r => r.CanRemove).ToList()) Rows.Remove(r);
        Message = "";
    }

    [RelayCommand]
    private void OpenRow(ConvertRow? row)
    {
        if (row is null) return;
        var path = row.State is ConvertRowState.Done or ConvertRowState.Exists && File.Exists(row.OutputPath) ? row.OutputPath : row.Source;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch { }
    }

    private void UpdateSummary()
    {
        if (Rows.Count == 0) { Summary = ""; return; }
        int Count(ConvertRowState s) => Rows.Count(r => r.State == s);
        Summary = SummaryText(Rows.Count,
            ready: Count(ConvertRowState.Ready) + Count(ConvertRowState.Failed) + Count(ConvertRowState.Cancelled),
            reading: Count(ConvertRowState.Reading), running: Count(ConvertRowState.Running), queued: Count(ConvertRowState.Queued),
            notPossible: Count(ConvertRowState.NotPossible), done: Count(ConvertRowState.Done) + Count(ConvertRowState.Exists),
            bytes: Rows.Sum(r => r.Media?.SizeBytes ?? 0));
    }

    private static string SummaryText(int total, int ready, int reading, int running, int queued, int notPossible, int done, double bytes)
    {
        var parts = new List<string> { RunTexts.Files(total) };
        if (ready > 0) parts.Add(L.T($"{ready} bereit", $"{ready} ready"));
        if (reading > 0) parts.Add(reading == 1 ? L.T("1 wird gelesen", "1 being read") : L.T($"{reading} werden gelesen", $"{reading} being read"));
        if (running > 0) parts.Add(L.T($"{running} in Arbeit", $"{running} in progress"));
        if (queued > 0) parts.Add(queued == 1 ? L.T("1 wartet", "1 waiting") : L.T($"{queued} warten", $"{queued} waiting"));
        if (notPossible > 0) parts.Add(L.T($"{notPossible} nicht möglich", $"{notPossible} not possible"));
        if (done > 0) parts.Add(L.T($"{done} fertig", $"{done} done"));
        if (bytes > 0) parts.Add(Fmt.Size(bytes));
        return string.Join(" · ", parts);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        Message = "";
        var tools = await _app.EnsureToolsAsync();
        var caps = await EnsureCapsAsync();
        if (tools is null || caps is null) { Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found."); return; }
        if (ToFolder && string.IsNullOrWhiteSpace(OutputDir)) { Message = L.T("Bitte zuerst einen Zielordner wählen.", "Please choose a target folder first."); return; }
        foreach (var r in Rows.Where(r => r.State is ConvertRowState.Failed or ConvertRowState.Cancelled)) PlanRow(r);
        var rows = Rows.Where(r => r.State == ConvertRowState.Ready && r.Media is not null).ToList();
        if (rows.Count == 0)
        {
            Message = Rows.Count == 0 ? L.T("Zuerst Dateien oder Ordner hinzufügen (oder hierher ziehen).", "Add files or folders first (or drag them here).")
                : L.T("Nichts mehr zu tun: alle Dateien sind fertig oder nicht möglich.", "Nothing left to do: all files are done or not possible.");
            return;
        }
        if (ToFolder && !await Io.DirExistsAsync(Path.GetPathRoot(OutputDir.Trim()) ?? OutputDir, 5000))
        {
            Message = L.T($"Zielordner nicht erreichbar: {OutputDir}", $"Target folder not reachable: {OutputDir}");
            return;
        }

        _jobRows.Clear();
        var jobs = new List<ConvertJob>();
        foreach (var r in rows)
        {
            var job = new ConvertJob(jobs.Count, r.Source, r.BaseDir, r.Media);
            _jobRows[job.Id] = r;
            jobs.Add(job);
            r.State = ConvertRowState.Queued;
            r.ResultText = L.T("wartet", "waiting");
        }
        var opt = new ConvertOptions
        {
            Target = Target, OutputDir = ToFolder ? OutputDir.Trim() : null, KeepStructure = KeepStructure, Suffix = Suffix,
            Parallel = Parallel, Priority = _app.FfmpegPriority,
        };
        _conv = new FileConverter(tools, caps);
        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        TotalPercent = 0;
        _autoPause.Reset();
        _app.Run.UpdateKeepAwake();
        try
        {
            var conv = _conv;
            var token = _cts.Token;
            _runTask = Task.Run(() => conv.RunAsync(jobs, opt, this, token));
            await _runTask;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            foreach (var r in rows.Where(r => r.State is ConvertRowState.Queued or ConvertRowState.Running))
            {
                r.State = ConvertRowState.Ready;
                r.ResultText = "";
            }
            IsRunning = false;
            IsPaused = false;
            _autoPause.Reset();
            _app.Run.UpdateKeepAwake();
            _cts.Dispose();
            _cts = null;
            _conv = null;
            Finish(rows);
        }
    }

    private void Finish(List<ConvertRow> rows)
    {
        var done = rows.Where(r => r.State == ConvertRowState.Done).ToList();
        var exists = rows.Count(r => r.State == ConvertRowState.Exists);
        var failed = rows.Count(r => r.State is ConvertRowState.Failed or ConvertRowState.NotPossible);
        var cancelled = rows.Any(r => r.State == ConvertRowState.Cancelled || r.State == ConvertRowState.Ready);
        var parts = new List<string> { L.T($"{done.Count} umgewandelt", $"{done.Count} converted") };
        if (exists > 0) parts.Add(L.T($"{exists} schon vorhanden", $"{exists} already existed"));
        if (failed > 0) parts.Add(L.T($"{failed} mit Fehler", $"{failed} failed"));
        var title = cancelled ? L.T("Konvertieren abgebrochen", "Conversion cancelled") : L.T("Konvertieren fertig", "Conversion finished");
        Message = title + ": " + string.Join(", ", parts) + ".";
        TotalPercent = 0;
        UpdateSummary();
        if (!cancelled) Notify?.Invoke(title, string.Join(", ", parts), failed > 0);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_conv is null) return;
        _autoPause.UserToggled();
        if (IsPaused) { _conv.Resume(); IsPaused = false; Message = ""; }
        else { _conv.Pause(); IsPaused = true; }
    }

    private readonly AutoPauseState _autoPause = new();

    public bool AutoPaused => _autoPause.AutoPaused;

    public void AutoPauseTick(bool enabled, string? reason, DateTime now)
    {
        var conv = _conv;
        switch (_autoPause.Decide(enabled, conv is not null, IsPaused, reason is not null, now))
        {
            case AutoPauseAction.Pause:
                conv!.Pause();
                _autoPause.MarkPaused();
                IsPaused = true;
                Message = L.T($"Automatisch pausiert: {reason}. Geht danach von selbst weiter.", $"Paused automatically: {reason}. Continues on its own afterwards.");
                break;
            case AutoPauseAction.Resume:
                conv!.Resume();
                _autoPause.MarkResumed();
                IsPaused = false;
                Message = "";
                break;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    public void CancelAndWait(TimeSpan timeout)
    {
        try
        {
            _cts?.Cancel();
            _runTask?.Wait(timeout);
        }
        catch
        {
        }
    }

    // ---------------- Rueckmeldungen (aus Hintergrund-Threads) ----------------

    private static void Ui(Action a)
    {
        var d = System.Windows.Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    public void Started(ConvertJob job, string outputPath) => Ui(() =>
    {
        if (!_jobRows.TryGetValue(job.Id, out var r)) return;
        r.State = ConvertRowState.Running;
        r.OutputPath = outputPath;
        r.ResultText = "";
        r.ProgressText = L.T("startet …", "starting …");
    });

    public void Progress(ConvertJob job, ProgressInfo pi) => Ui(() =>
    {
        if (!_jobRows.TryGetValue(job.Id, out var r) || r.State != ConvertRowState.Running) return;
        r.Percent = Math.Clamp(pi.Fraction * 100, 0, 100);
        r.ProgressText = ProgressLine(pi.Fraction, pi.EtaSeconds, pi.Speed);
        UpdateTotal();
    });

    public void Finished(ConvertJob job, ConvertResult res) => Ui(() =>
    {
        if (!_jobRows.TryGetValue(job.Id, out var r)) return;
        r.State = res.Outcome switch
        {
            Outcome.Encoded => ConvertRowState.Done,
            Outcome.AlreadyOk => ConvertRowState.Exists,
            Outcome.Skipped => ConvertRowState.NotPossible,
            Outcome.Cancelled => ConvertRowState.Cancelled,
            _ => ConvertRowState.Failed,
        };
        r.Percent = r.State == ConvertRowState.Done ? 100 : 0;
        r.ProgressText = "";
        r.ResultText = res.Message;
        if (res.OutputPath is not null) r.OutputPath = res.OutputPath;
        if (r.State == ConvertRowState.Failed) _app.WriteLog($"Konvertieren fehlgeschlagen: {r.Source}: {res.Message}");
        UpdateTotal();
        UpdateSummary();
    });

    private static string ProgressLine(double fraction, double etaSeconds, double speed)
    {
        var parts = new List<string> { fraction >= 0 ? (fraction * 100).ToString("N0", Fmt.De) + " %" : "…" };
        if (etaSeconds > 0) parts.Add(RunTexts.Eta(etaSeconds));
        if (speed > 0) parts.Add(RunTexts.Speed(speed));
        return string.Join(" · ", parts);
    }

    private void UpdateTotal()
    {
        if (_jobRows.Count == 0) return;
        var sum = _jobRows.Values.Sum(r => r.State switch
        {
            ConvertRowState.Running => r.Percent,
            ConvertRowState.Queued => 0,
            _ => 100,
        });
        TotalPercent = sum / _jobRows.Count;
    }

    // ---------------- Screenshot-Demo ----------------

    public void FillDemo()
    {
        Rows.Clear();
        _loading = true;
        Target = ConvertTarget.Compatible;
        foreach (var t in AllTargets) t.IsSelected = t.Target == Target;
        ToFolder = true;
        OutputDir = DemoText.T(@"D:\Konvertiert");
        KeepStructure = true;
        _loading = false;
        ApplyCaps(new ConvertCaps { H264 = "h264_nvenc", Hevc = "hevc_nvenc", Av1 = "av1_nvenc", Mp3 = true, Tonemap = true });
        ConvertRow Row(string src, string info, string plan, string warn, ConvertRowState state, string result = "", double pct = 0, string progress = "")
        {
            var r = new ConvertRow(src, @"D:\Videos") { SourceText = info, PlanText = plan, WarnText = warn, ResultText = result, Percent = pct, ProgressText = progress };
            r.State = state;
            return r;
        }
        const double gb = 1024d * 1024 * 1024, mb = 1024d * 1024;
        var subs = L.T("3 UT", "3 subs");
        Rows.Add(Row(@"D:\Videos\Clips\Video A.MOV", $"H.264 4K · AAC Stereo · 00:04:12 · {Fmt.Size(2.1 * gb)}", L.T("Video → H.264 · verkleinert auf höchstens 1080p · Ton kopiert", "video → H.264 · scaled down to at most 1080p · audio copied"), "", ConvertRowState.Done, $"{Fmt.Size(2.1 * gb)} » {Fmt.Size(412 * mb)}", 100));
        Rows.Add(Row(@"D:\Videos\Clips\Video B.mkv", $"HEVC 1080p · HDR10 · TrueHD 7.1 · {subs} · 01:52:40 · {Fmt.Size(14.8 * gb)}", L.T("Video → H.264 · HDR → SDR umgerechnet · Ton TrueHD → AAC 5.1", "video → H.264 · HDR → SDR converted · audio TrueHD → AAC 5.1"), L.T("2 Bild-Untertitel entfallen (MP4 kann keine PGS/VobSub)", "2 image subtitles are dropped (MP4 cannot hold PGS/VobSub)"), ConvertRowState.Running, "", 63, ProgressLine(0.63, 591, 4.2)));
        Rows.Add(Row(DemoText.T(@"D:\Videos\Archiv\Video C (DVD).vob"), $"MPEG-2 576p · AC3 5.1 · 01:12:05 · {Fmt.Size(3.9 * gb)}", L.T("Video → H.264 · Halbbilder entfernt · Ton AC3 → AAC 5.1", "video → H.264 · deinterlaced · audio AC3 → AAC 5.1"), "", ConvertRowState.Queued, L.T("wartet", "waiting")));
        Rows.Add(Row(DemoText.T(@"D:\Videos\Archiv\Video D.avi"), $"MPEG-4 480p · MP3 Stereo · 00:38:20 · {Fmt.Size(699 * mb)}", L.T("Video → H.264 · Ton MP3 → AAC", "video → H.264 · audio MP3 → AAC"), "", ConvertRowState.Queued, L.T("wartet", "waiting")));
        Rows.Add(Row(DemoText.T(@"D:\Videos\Serien\Video E.mp4"), $"H.264 1080p · AAC Stereo · 00:44:10 · {Fmt.Size(1.2 * gb)}", L.T("Video kopiert (schon H.264) · Ton kopiert", "video copied (already H.264) · audio copied"), "", ConvertRowState.Exists, L.T("Gibt es schon, nichts überschrieben.", "Already exists, nothing overwritten.")));
        Rows.Add(Row(DemoText.T(@"D:\Videos\Musik\Audio A.flac"), $"FLAC Stereo · 00:48:02 · {Fmt.Size(312 * mb)}", "", L.T("Kein Video gefunden. Für reine Tondateien „Nur Ton“ wählen.", "No video found. For audio-only files choose “Audio only”."), ConvertRowState.NotPossible));
        IsRunning = true;
        TotalPercent = 44;
        Summary = SummaryText(6, ready: 0, reading: 0, running: 1, queued: 2, notPossible: 1, done: 2, bytes: 22.99 * gb);
    }
}
