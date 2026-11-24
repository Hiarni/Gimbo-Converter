using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Converting;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;
using Microsoft.Win32;

namespace FilmKonverter.App.ViewModels;

public enum RepairRowState
{
    Reading,
    Ready,
    Fine,
    NotPossible,
    Queued,
    Running,
    Repaired,
    Skipped,
    Failed,
    Cancelled,
}

public sealed partial class RepairRow(string source) : ObservableObject
{
    public string Source { get; } = source;
    public string Name => Path.GetFileName(Source);
    public string Folder => Path.GetDirectoryName(Source) ?? "";
    public MediaInfo? Media { get; set; }

    [ObservableProperty] private string _sourceText = L.T("wird gelesen …", "reading …");
    [ObservableProperty] private string _planText = "";
    [ObservableProperty] private string _noteText = "";
    [ObservableProperty] private double _percent;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _resultText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StateBrush), nameof(IsActive), nameof(ResultBrush), nameof(CanRemove))]
    private RepairRowState _state;

    public bool IsActive => State == RepairRowState.Running;
    public bool CanRemove => State is not (RepairRowState.Running or RepairRowState.Queued);

    public Brush StateBrush => State switch
    {
        RepairRowState.Ready or RepairRowState.Queued => Palette.Amber,
        RepairRowState.Running => Palette.Blue,
        RepairRowState.Repaired or RepairRowState.Fine => Palette.Green,
        RepairRowState.NotPossible or RepairRowState.Skipped => Palette.Slate,
        RepairRowState.Failed => Palette.Red,
        _ => Palette.Gray,
    };

    public Brush ResultBrush => State switch
    {
        RepairRowState.Repaired => Palette.TextOk,
        RepairRowState.Failed => Palette.TextErr,
        RepairRowState.Skipped or RepairRowState.NotPossible => Palette.TextWarn,
        _ => Palette.Resource("TextFillColorSecondaryBrush", Palette.Gray),
    };
}

public sealed partial class AudioCodecOption(string codec, string text, bool isChecked, Action changed) : ObservableObject
{
    public string Codec { get; } = codec;
    public string Text { get; } = text;

    [ObservableProperty] private bool _isChecked = isChecked;

    partial void OnIsCheckedChanged(bool value) => changed();
}

public sealed partial class RepairViewModel : ObservableObject, IRepairObserver
{
    private readonly AppState _app;
    private RepairRun? _run;
    private CancellationTokenSource? _cts;
    private Task? _runTask;
    private readonly Dictionary<string, RepairRow> _active = new(StringComparer.OrdinalIgnoreCase);
    private bool _loading;

    public event Action<string, string, bool>? Notify;

    public ObservableCollection<RepairRow> Rows { get; } = [];
    public List<AudioCodecOption> AllowedAudio { get; }
    public List<IntOption> BitrateOptions { get; } = [new(448, "448k"), new(640, "640k"), new(768, "768k"), new(1024, "1024k")];

    [ObservableProperty] private bool _targetAc3;
    [ObservableProperty] private int _bitrate;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    private bool _isRunning;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string _summary = "";
    [ObservableProperty] private double _totalPercent;
    [ObservableProperty] private bool _hasRows;

    public bool IsIdle => !IsRunning;

    public RepairViewModel(AppState app)
    {
        _app = app;
        var s = app.Settings;
        var allowed = new HashSet<string>(s.RepairAllowedAudio.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries), StringComparer.OrdinalIgnoreCase);
        AllowedAudio =
        [
            new("aac", "AAC", allowed.Contains("aac"), OptionsChanged),
            new("ac3", "AC3 (Dolby Digital)", allowed.Contains("ac3"), OptionsChanged),
            new("eac3", "E-AC3 (Dolby Digital Plus)", allowed.Contains("eac3"), OptionsChanged),
            new("mp3", "MP3", allowed.Contains("mp3"), OptionsChanged),
            new("mp2", L.T("MP2 (TV-Aufnahmen)", "MP2 (TV recordings)"), allowed.Contains("mp2"), OptionsChanged),
        ];
        _loading = true;
        TargetAc3 = s.RepairTarget != AudioTarget.Eac3;
        Bitrate = s.RepairBitrate is 448 or 640 or 768 or 1024 ? s.RepairBitrate : 640;
        _loading = false;
        Rows.CollectionChanged += (_, _) => { HasRows = Rows.Count > 0; UpdateSummary(); };
    }

    partial void OnTargetAc3Changed(bool value) => OptionsChanged();
    partial void OnBitrateChanged(int value) => OptionsChanged();

    private void OptionsChanged()
    {
        if (_loading) return;
        var s = _app.Settings;
        s.RepairAllowedAudio = string.Join(" ", AllowedAudio.Where(a => a.IsChecked).Select(a => a.Codec));
        s.RepairTarget = TargetAc3 ? AudioTarget.Ac3 : AudioTarget.Eac3;
        s.RepairBitrate = Bitrate;
        _app.Save();
        if (IsRunning) return;
        foreach (var r in Rows.Where(r => r.Media is not null && r.State is not (RepairRowState.Running or RepairRowState.Queued))) PlanRow(r);
        UpdateSummary();
    }

    public RepairOptions Options() => new()
    {
        AllowedAudio = AllowedAudio.Where(a => a.IsChecked).Select(a => a.Codec).ToList(),
        Target = TargetAc3 ? AudioTarget.Ac3 : AudioTarget.Eac3,
        Bitrate51 = Bitrate,
        Priority = _app.FfmpegPriority,
    };

    // ---------------- Dateien ----------------

    [RelayCommand]
    private void AddFiles()
    {
        var dlg = new OpenFileDialog
        {
            Title = L.T("Dateien zum Reparieren wählen", "Choose files to repair"),
            Multiselect = true,
            Filter = L.T("Videos (MKV, MP4)|", "Videos (MKV, MP4)|") + string.Join(";", RepairPlanner.Extensions.Select(e => "*" + e)),
        };
        var last = Rows.LastOrDefault()?.Folder;
        if (last is not null && Io.DirExistsQuick(last, 800)) dlg.InitialDirectory = last;
        if (dlg.ShowDialog() == true) _ = AddSourcesAsync(dlg.FileNames);
    }

    [RelayCommand]
    private void AddFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner wählen (alle MKV- und MP4-Dateien darin, mit Unterordnern)", "Choose a folder (all MKV and MP4 files in it, including subfolders)"), Multiselect = true };
        if (dlg.ShowDialog() == true) _ = AddSourcesAsync(dlg.FolderNames);
    }

    public async Task AddSourcesAsync(IEnumerable<string> paths)
    {
        var list = paths.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        Message = "";
        List<string> found;
        try
        {
            found = await Task.Run(() =>
            {
                var r = new List<string>();
                foreach (var p in list)
                {
                    if (Directory.Exists(p)) r.AddRange(Scanner.Find(p, RepairPlanner.Extensions));
                    else if (File.Exists(p) && RepairPlanner.Extensions.Contains(Path.GetExtension(p), StringComparer.OrdinalIgnoreCase)) r.Add(p);
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
        var added = new List<RepairRow>();
        foreach (var file in found.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            if (FileConverter.IsTempName(file) || Scanner.IsTemp(file) || !known.Add(file)) continue;
            var row = new RepairRow(file);
            Rows.Add(row);
            added.Add(row);
        }
        if (added.Count == 0)
        {
            Message = found.Count == 0 ? L.T("Keine MKV- oder MP4-Dateien gefunden.", "No MKV or MP4 files found.") : L.T("Schon in der Liste.", "Already in the list.");
            return;
        }
        await AnalyzeAsync(added);
    }

    private async Task AnalyzeAsync(List<RepairRow> rows)
    {
        var tools = await _app.EnsureToolsAsync();
        if (tools is null)
        {
            Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found.");
            foreach (var r in rows) { r.SourceText = ""; r.State = RepairRowState.NotPossible; r.NoteText = Message; }
            UpdateSummary();
            return;
        }
        await _app.LoadProbeCacheAsync();
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(rows.Select(async row =>
        {
            await gate.WaitAsync();
            try
            {
                row.Media = await Task.Run(async () =>
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
                PlanRow(row);
            }
            finally
            {
                gate.Release();
            }
        }));
        if (ProbeCache.Shared is { } pc) await Task.Run(pc.Save);
        UpdateSummary();
    }

    private void PlanRow(RepairRow r)
    {
        r.Percent = 0;
        r.ProgressText = "";
        r.ResultText = "";
        if (r.Media?.Video is null)
        {
            r.SourceText = r.Media is null ? L.T("nicht lesbar", "not readable") : L.T("kein Video", "no video");
            r.PlanText = "";
            r.NoteText = "";
            r.State = RepairRowState.NotPossible;
            return;
        }
        r.SourceText = $"{ConvertPlanner.Describe(r.Media)} · {Fmt.Size(r.Media.SizeBytes)}";
        var plan = RepairPlanner.Plan(r.Media, Options());
        r.PlanText = plan.NeedsWork ? string.Join(" · ", plan.Changes) : L.T("Nichts zu tun: Level und Ton passen.", "Nothing to do: level and audio are fine.");
        r.NoteText = string.Join(" · ", plan.Notes);
        r.State = plan.NeedsWork ? RepairRowState.Ready : RepairRowState.Fine;
    }

    [RelayCommand]
    private void RemoveRow(RepairRow? row)
    {
        if (row is not null && row.CanRemove) Rows.Remove(row);
    }

    [RelayCommand]
    private void RemoveFinished()
    {
        foreach (var r in Rows.Where(r => r.State is RepairRowState.Repaired or RepairRowState.Fine).ToList()) Rows.Remove(r);
    }

    [RelayCommand]
    private void Clear()
    {
        foreach (var r in Rows.Where(r => r.CanRemove).ToList()) Rows.Remove(r);
        Message = "";
    }

    [RelayCommand]
    private void OpenRow(RepairRow? row)
    {
        if (row is null) return;
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"/select,\"{row.Source}\"") { UseShellExecute = true }); }
        catch { }
    }

    private void UpdateSummary()
    {
        if (Rows.Count == 0) { Summary = ""; return; }
        int Count(RepairRowState s) => Rows.Count(r => r.State == s);
        var parts = new List<string> { RunTexts.Files(Rows.Count) };
        void Add(int n, string de, string en) { if (n > 0) parts.Add(L.T($"{n} {de}", $"{n} {en}")); }
        Add(Count(RepairRowState.Reading), "werden gelesen", "being read");
        Add(Count(RepairRowState.Ready) + Count(RepairRowState.Failed) + Count(RepairRowState.Cancelled) + Count(RepairRowState.Skipped), "zu reparieren", "to repair");
        Add(Count(RepairRowState.Running), "in Arbeit", "in progress");
        Add(Count(RepairRowState.Queued), "warten", "waiting");
        Add(Count(RepairRowState.Fine), "passen", "fine");
        Add(Count(RepairRowState.Repaired), "repariert", "repaired");
        Add(Count(RepairRowState.NotPossible), "nicht möglich", "not possible");
        Summary = string.Join(" · ", parts);
    }

    // ---------------- Lauf ----------------

    [RelayCommand]
    private async Task StartAsync()
    {
        if (IsRunning) return;
        Message = "";
        var tools = await _app.EnsureToolsAsync();
        if (tools is null) { Message = L.T("ffmpeg nicht gefunden.", "ffmpeg not found."); return; }
        foreach (var r in Rows.Where(r => r.Media is not null && r.State is RepairRowState.Failed or RepairRowState.Cancelled or RepairRowState.Skipped)) PlanRow(r);
        var rows = Rows.Where(r => r.State == RepairRowState.Ready).ToList();
        if (rows.Count == 0)
        {
            Message = Rows.Count == 0 ? L.T("Zuerst Dateien oder Ordner hinzufügen (oder hierher ziehen).", "Add files or folders first (or drag them here).")
                : L.T("Nichts zu tun: alle Dateien passen schon oder sind fertig.", "Nothing to do: all files are already fine or done.");
            return;
        }
        _active.Clear();
        foreach (var r in rows)
        {
            _active[r.Source] = r;
            r.State = RepairRowState.Queued;
            r.ResultText = L.T("wartet", "waiting");
        }
        _run = new RepairRun(tools, this);
        _cts = new CancellationTokenSource();
        IsRunning = true;
        IsPaused = false;
        TotalPercent = 0;
        _autoPause.Reset();
        _app.Run.UpdateKeepAwake();
        try
        {
            var run = _run;
            var token = _cts.Token;
            var opt = Options();
            var files = rows.Select(r => r.Source).ToList();
            _runTask = Task.Run(() => run.RunAsync(files, opt, token));
            await _runTask;
        }
        catch (Exception ex)
        {
            Message = ex.Message;
        }
        finally
        {
            await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.Background);
            foreach (var r in rows.Where(r => r.State is RepairRowState.Queued or RepairRowState.Running))
            {
                r.State = RepairRowState.Ready;
                r.ResultText = "";
                r.ProgressText = "";
                r.Percent = 0;
            }
            IsRunning = false;
            IsPaused = false;
            _autoPause.Reset();
            _app.Run.UpdateKeepAwake();
            _cts.Dispose();
            _cts = null;
            _run = null;
            Finish(rows);
        }
    }

    private void Finish(List<RepairRow> rows)
    {
        var repaired = rows.Where(r => r.State == RepairRowState.Repaired).ToList();
        var failed = rows.Count(r => r.State == RepairRowState.Failed);
        var skipped = rows.Count(r => r.State == RepairRowState.Skipped);
        var cancelled = rows.Any(r => r.State is RepairRowState.Cancelled or RepairRowState.Ready);
        var parts = new List<string> { L.T($"{repaired.Count} repariert", $"{repaired.Count} repaired") };
        if (skipped > 0) parts.Add(L.T($"{skipped} übersprungen", $"{skipped} skipped"));
        if (failed > 0) parts.Add(L.T($"{failed} mit Fehler (Original blieb jeweils unverändert)", $"{failed} failed (the original stayed unchanged each time)"));
        var title = cancelled ? L.T("Reparatur abgebrochen", "Repair cancelled") : L.T("Reparatur fertig", "Repair finished");
        var text = title + ": " + string.Join(", ", parts) + ".";
        if (repaired.Count > 0) text += L.T(" Im Medienserver die Bibliothek scannen, damit er die Dateien neu einliest.", " Scan the library in the media server so it reads the files again.");
        Message = text;
        TotalPercent = 0;
        UpdateSummary();
        if (!cancelled) Notify?.Invoke(title, string.Join(", ", parts), failed > 0);
    }

    [RelayCommand]
    private void TogglePause()
    {
        if (_run is null) return;
        _autoPause.UserToggled();
        if (IsPaused) { _run.Resume(); IsPaused = false; Message = ""; }
        else { _run.Pause(); IsPaused = true; }
    }

    private readonly AutoPauseState _autoPause = new();

    public void AutoPauseTick(bool enabled, string? reason, DateTime now)
    {
        var run = _run;
        switch (_autoPause.Decide(enabled, run is not null, IsPaused, reason is not null, now))
        {
            case AutoPauseAction.Pause:
                run!.Pause();
                _autoPause.MarkPaused();
                IsPaused = true;
                Message = L.T($"Automatisch pausiert: {reason}. Geht danach von selbst weiter.", $"Paused automatically: {reason}. Continues on its own afterwards.");
                break;
            case AutoPauseAction.Resume:
                run!.Resume();
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

    public void Started(string file) => Ui(() =>
    {
        if (!_active.TryGetValue(file, out var r)) return;
        r.State = RepairRowState.Running;
        r.ResultText = "";
        r.ProgressText = L.T("startet …", "starting …");
    });

    public void Progress(string file, ProgressInfo pi) => Ui(() =>
    {
        if (!_active.TryGetValue(file, out var r) || r.State != RepairRowState.Running) return;
        r.Percent = Math.Clamp(pi.Fraction * 100, 0, 100);
        var parts = new List<string> { pi.Fraction >= 0 ? (pi.Fraction * 100).ToString("N0", Fmt.De) + " %" : "…" };
        if (pi.EtaSeconds > 0) parts.Add(RunTexts.Eta(pi.EtaSeconds));
        if (pi.Speed > 0) parts.Add(RunTexts.Speed(pi.Speed));
        r.ProgressText = pi.Final ? L.T("prüfe das Ergebnis …", "checking the result …") : string.Join(" · ", parts);
        UpdateTotal();
    });

    public void Finished(string file, RepairResult res) => Ui(() =>
    {
        if (!_active.TryGetValue(file, out var r)) return;
        r.State = res.Outcome switch
        {
            RepairOutcome.Repaired => RepairRowState.Repaired,
            RepairOutcome.Fine => RepairRowState.Fine,
            RepairOutcome.Cancelled => RepairRowState.Cancelled,
            RepairOutcome.Failed => RepairRowState.Failed,
            _ => RepairRowState.Skipped,
        };
        r.Percent = r.State == RepairRowState.Repaired ? 100 : 0;
        r.ProgressText = "";
        r.ResultText = res.Outcome == RepairOutcome.Repaired
            ? L.T($"Repariert · {Fmt.Size(res.OldBytes)} » {Fmt.Size(res.NewBytes)}", $"Repaired · {Fmt.Size(res.OldBytes)} » {Fmt.Size(res.NewBytes)}")
            : res.Message;
        if (r.State == RepairRowState.Failed) _app.WriteLog($"Reparatur fehlgeschlagen: {r.Source}: {res.Message}");
        if (r.State == RepairRowState.Repaired) _app.WriteLog($"Repariert: {r.Source}: {r.PlanText}");
        UpdateTotal();
        UpdateSummary();
    });

    private void UpdateTotal()
    {
        if (_active.Count == 0) return;
        var sum = _active.Values.Sum(r => r.State switch
        {
            RepairRowState.Running => r.Percent,
            RepairRowState.Queued => 0,
            _ => 100,
        });
        TotalPercent = sum / _active.Count;
    }

    // ---------------- Screenshot-Demo ----------------

    public void FillDemo()
    {
        Rows.Clear();
        const double gb = 1024d * 1024 * 1024;
        RepairRow Row(string src, string info, string plan, string note, RepairRowState state, string result = "", double pct = 0, string progress = "")
        {
            var r = new RepairRow(src) { SourceText = info, PlanText = plan, NoteText = note, ResultText = result, Percent = pct, ProgressText = progress };
            r.State = state;
            return r;
        }
        var subs = L.T("4 UT", "4 subs");
        Rows.Add(Row(DemoText.T(@"\\NAS\Filme\Film A (2009)\Film A (2009).mkv"), $"HEVC 2160p · HDR10 · TrueHD 7.1 · {subs} · 02:41:50 · {Fmt.Size(21.4 * gb)}",
            L.T("Video-Level 6.2 → 5.1 (Bild bleibt bit-genau gleich) · Ton 1: TrueHD 7.1 → AC3 5.1 640k", "video level 6.2 → 5.1 (picture stays bit-identical) · audio 1: TrueHD 7.1 → AC3 5.1 640k"), "",
            RepairRowState.Repaired, L.T($"Repariert · {Fmt.Size(21.4 * gb)} » {Fmt.Size(17.9 * gb)}", $"Repaired · {Fmt.Size(21.4 * gb)} » {Fmt.Size(17.9 * gb)}"), 100));
        Rows.Add(Row(DemoText.T(@"\\NAS\Serien\Serie A\Staffel 2\Serie A - S02E09 - Der Anruf.mkv"), $"HEVC 1080p · DTS-HD MA 5.1 · AC3 2.0 · 00:47:02 · {Fmt.Size(3.1 * gb)}",
            L.T("Video-Level 6.2 → 4.1 (Bild bleibt bit-genau gleich) · Ton 1: DTS-HD MA 5.1 → AC3 5.1 640k", "video level 6.2 → 4.1 (picture stays bit-identical) · audio 1: DTS-HD MA 5.1 → AC3 5.1 640k"), "",
            RepairRowState.Running, "", 58, "58 % · " + RunTexts.Eta(41) + " · " + RunTexts.Speed(61)));
        Rows.Add(Row(DemoText.T(@"\\NAS\Serien\Serie A\Staffel 2\Serie A - S02E10 - Heimkehr.mkv"), $"HEVC 1080p · DTS 5.1 · 00:46:31 · {Fmt.Size(2.4 * gb)}",
            L.T("Ton 1: DTS 5.1 → AC3 5.1 640k", "audio 1: DTS 5.1 → AC3 5.1 640k"), "", RepairRowState.Queued, L.T("wartet", "waiting")));
        Rows.Add(Row(DemoText.T(@"\\NAS\Serien\Serie D\Staffel 1\Serie D - S01E04.mkv"), $"HEVC 720p · AAC 2.0 · 00:42:15 · {Fmt.Size(0.6 * gb)}",
            L.T("Nichts zu tun: Level und Ton passen.", "Nothing to do: level and audio are fine."), "", RepairRowState.Fine));
        Rows.Add(Row(DemoText.T(@"\\NAS\Filme\Film C (1982)\Film C (1982) - Final Cut.mp4"), $"HEVC 1080p · FLAC 2.0 · 01:57:12 · {Fmt.Size(6.8 * gb)}",
            L.T("Ton 1: FLAC 2.0 → AC3 2.0 256k", "audio 1: FLAC 2.0 → AC3 2.0 256k"), L.T("Datenspur entfällt (für die Wiedergabe ohne Bedeutung): bin_data", "data track is dropped (irrelevant for playback): bin_data"),
            RepairRowState.Queued, L.T("wartet", "waiting")));
        IsRunning = true;
        TotalPercent = 31;
        _active.Clear();
        UpdateSummary();
    }
}
