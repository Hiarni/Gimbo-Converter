using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;
using FilmKonverter.App.Services;

namespace FilmKonverter.App.ViewModels;

public sealed record LineItem(string Label, string Text, LineKind Kind);

public sealed record HistoryItem(DateTime Time, string Badge, Outcome Outcome, string Name, string Message)
{
    public string TimeText => Time.ToString("HH:mm:ss");
}

public sealed record PeerItem(string Machine, string Headline, string Detail, bool Active, bool Fresh);

public sealed record QueueItem(QueueEntry Entry, string Name, string Detail, string Target = "");

public sealed partial class RunViewModel : ObservableObject, IRunObserver
{
    private readonly AppState _app;
    private CancellationTokenSource? _cts;
    private LibraryRun? _library;
    private VerifyRun? _verify;
    private CleanupRun? _cleanup;
    private TestRun? _test;
    private Profile? _runProfile;
    private readonly DispatcherTimer _timer;
    private DateTime _lastStatusWrite;
    private DateTime _lastPeerRead;

    public RunViewModel(AppState app)
    {
        _app = app;
        Slots.Add(new SlotViewModel(0));
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _timer.Tick += (_, _) => OnTimer();
        if (Application.Current is not null) _timer.Start();
    }

    public ObservableCollection<SlotViewModel> Slots { get; } = [];

    public ObservableCollection<LineItem> Lines => Slots[0].Lines;
    public ObservableCollection<HistoryItem> History { get; } = [];

    public ObservableCollection<QueueItem> Queue { get; } = [];
    [ObservableProperty] private bool _hasQueue;

    [ObservableProperty] private string _spaceWarning = "";

    public ObservableCollection<PeerItem> Peers { get; } = [];
    [ObservableProperty] private bool _hasPeers;

    // ---------------- Zustand ----------------
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsIdle), nameof(IsBusy)), NotifyCanExecuteChangedFor(nameof(StartCommand), nameof(PauseCommand), nameof(StopAfterCurrentCommand), nameof(CancelCommand))]
    private bool _isRunning;

    public bool IsIdle => !IsRunning;

    [ObservableProperty] private bool _isPaused;
    [ObservableProperty] private bool _stopRequested;
    [ObservableProperty] private string _runTitle = L.T("Bereit", "Ready");
    [ObservableProperty] private string _statusText = L.T("Profil wählen und Start drücken.", "Choose a profile and press Start.");
    [ObservableProperty] private string _errorText = "";

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(CancelPendingCommand))] private string _bannerText = "";
    [ObservableProperty] private bool _bannerCancelable;

    public event Action<string, string, bool>? Notify;

    // ---------------- Aktuelle Datei(en), zusammengefasst fuer Tray und Taskleiste ----------------
    [ObservableProperty] private string _currentName = "";
    [ObservableProperty] private string _currentPosition = "";
    [ObservableProperty] private string _stageName = "";
    [ObservableProperty] private double _stagePercent;
    [ObservableProperty] private string _percentText = "";
    [ObservableProperty] private bool _hasStage;
    [ObservableProperty] private double _activeFraction;
    [ObservableProperty] private bool _autoPaused;

    // ---------------- Zaehler ----------------
    private readonly RunTally _tally = new();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(DoneText))] private int _total;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DoneText))] private int _done;

    public string DoneText => $"{Done.ToString("N0", Fmt.De)} / {Total.ToString("N0", Fmt.De)}";
    [ObservableProperty] private double _listPercent;
    [ObservableProperty] private int _countNew;
    [ObservableProperty] private int _countOk;
    [ObservableProperty] private int _countSkipped;
    [ObservableProperty] private int _countOther;
    [ObservableProperty] private int _countErrors;
    [ObservableProperty] private string _gainText = "0 MB";
    [ObservableProperty] private string _elapsedText = "";

    [ObservableProperty] private string _sinceStartText = "";

    private Profile? SelectedProfile => _app.Settings.FindProfile(_app.Settings.SelectedProfile) ?? _app.Settings.Profiles.FirstOrDefault();

    // ---------------- Befehle ----------------

    private bool CanStart() => !IsBusy;

    [ObservableProperty] private string _settingsWarning = "";

    [ObservableProperty] private string _updateNotice = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsBusy)), NotifyCanExecuteChangedFor(nameof(StartCommand))]
    private bool _starting;

    public bool IsBusy => IsRunning || Starting;

    public IReadOnlyList<string> ActiveFolders => IsBusy ? _activeFolders : [];
    private IReadOnlyList<string> _activeFolders = [];

    private RunKind _kind;

    private RunCounters? _lastCounters;

    private volatile TimeWindow? _passWindow;

    private bool _waitingIdle;

    private volatile int _queueLeft;

    private int _doneBase;

    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task StartAsync() => RunGuardedAsync(RunKind.Library, L.T("Komprimieren", "Compressing"), async (p, tools, ct) =>
    {
        var pass = 1;
        var failures = 0;
        var everRan = false;
        while (true)
        {
            _passWindow = _app.Window;
            _library = new LibraryRun(p, tools, this) { Priority = _app.FfmpegPriority, Window = _passWindow, Parallel = Parallel, UseQueue = true };
            if (IsPaused) _library.Pause();
            string? failed = null;
            try
            {
                await _library.RunAsync(ct);
                everRan = true;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && _app.Settings.ContinuousMode && !ct.IsCancellationRequested && !StopRequested
                                       && (everRan || ex is IOException or UnauthorizedAccessException))
            {
                failed = ex.Message;
            }
            var counters = _library.Counters;
            if (!_app.Settings.ContinuousMode || ct.IsCancellationRequested || StopRequested || counters.DiskFull) break;

            failures = failed is null ? 0 : failures + 1;
            var rescan = Math.Max(1, _app.Settings.RescanMinutes);
            var next = DateTime.Now.AddMinutes(failed is null ? rescan : RunPolicy.RetryMinutes(failures, rescan));
            var (donePass, firstFailure) = (pass, failures == 1);
            Ui(() => PassFinished(donePass, counters, failed, next, firstFailure));
            var lastQueueCheck = DateTime.Now;
            var outDir = p.OutputDir;
            var known = (await Task.Run(() => WorkQueue.Read(outDir))).Select(e => e.Id).ToHashSet();
            while (DateTime.Now < next && !StopRequested && _app.Settings.ContinuousMode)
            {
                try { await Task.Delay(1000, ct); }
                catch (OperationCanceledException) { break; }
                if ((DateTime.Now - lastQueueCheck).TotalSeconds >= 10)
                {
                    lastQueueCheck = DateTime.Now;
                    var now = await Task.Run(() => WorkQueue.Read(outDir));
                    if (now.Any(e => !known.Contains(e.Id))) break;
                }
            }
            if (ct.IsCancellationRequested || StopRequested || !_app.Settings.ContinuousMode) break;
            pass++;
            var name = p.Name;
            var changed = _app.Settings.FindProfile(name)?.Clone();
            string? notice = null;
            if (changed is not null && changed.Encoder != p.Encoder && !(tools.HasEncoder(changed.Encoder) && (await TestEncoderAsync(changed, tools)).Ok))
            {
                _app.WriteLog($"Dauerbetrieb: Encoder {changed.Encoder} laeuft hier nicht, bleibe bei {p.Encoder}");
                notice = L.T($"Das Profil steht jetzt auf {FfmpegTools.EncoderName(changed.Encoder)}. Dieser Encoder läuft auf diesem PC nicht: der Lauf arbeitet mit {FfmpegTools.EncoderName(p.Encoder)} und den bisherigen Einstellungen weiter.",
                             $"The profile now uses {FfmpegTools.EncoderName(changed.Encoder)}. This encoder does not work on this PC: the run continues with {FfmpegTools.EncoderName(p.Encoder)} and the previous settings.");
                changed = null;
            }
            p = changed ?? p;
            var (current, newPass) = (p, pass);
            Ui(() => BeginPass(current, newPass, notice));
        }
    }, afterRun: true, slots: Parallel);

    private void PassFinished(int pass, RunCounters counters, string? failed, DateTime next, bool firstFailure)
    {
        HasStage = false;
        BannerCancelable = false;
        _waitingIdle = failed is not null || counters.Encoded + counters.Replaced == 0;
        var maySleep = _waitingIdle && _app.Settings.PreventSleep
            ? L.T(" Bis dahin darf der PC schlafen, nach dem Aufwachen geht es von selbst weiter.", " Until then the PC may sleep, the run continues on its own after waking up.")
            : "";
        if (failed is null)
        {
            BannerText = L.T($"Dauerbetrieb: Durchlauf {pass} beendet. Nächste Suche um {next:HH:mm} Uhr.", $"Continuous mode: pass {pass} finished. Next scan at {next:HH:mm}.") + maySleep;
            StatusText = L.T("Wartet auf die nächste Suche.", "Waiting for the next scan.") + RunTexts.FinishHint(RunKind.Library, false, _tally, counters);
            _app.WriteLog($"=== Durchlauf {pass} beendet: {RunTexts.SummaryLine(RunKind.Library, _tally, counters)}, gewonnen {RunTexts.Gain(_tally.GainBytes)}");
            if (_app.Settings.Notifications && counters.Encoded + counters.Replaced + counters.Errors > 0)
                Notify?.Invoke(L.T("Gimbo Konverter – Durchlauf beendet", "Gimbo Converter – pass finished"), RunTexts.Summary(RunKind.Library, _tally, counters), _tally.Errors > 0);
        }
        else
        {
            var why = failed.TrimEnd('.', ' ');
            BannerText = L.T($"Dauerbetrieb: Durchlauf {pass} war nicht möglich ({why}). Nächster Versuch um {next:HH:mm} Uhr.",
                             $"Continuous mode: pass {pass} was not possible ({why}). Next attempt at {next:HH:mm}.") + maySleep;
            StatusText = L.T($"{why}. Der Lauf bleibt aktiv und versucht es von selbst wieder.", $"{why}. The run stays active and tries again on its own.");
            _app.WriteLog($"=== Durchlauf {pass} nicht moeglich: {why}. Naechster Versuch {next:HH:mm}");
            if (firstFailure) Notify?.Invoke(L.T("Gimbo Konverter – wartet", "Gimbo Converter – waiting"), StatusText, true);
        }
        UpdateKeepAwake();
        WriteOwnStatus();
    }

    private void BeginPass(Profile profile, int pass, string? notice)
    {
        _waitingIdle = false;
        _runProfile = profile;
        _activeFolders = [profile.InputDir, profile.OutputDir];
        _runName = L.T($"Komprimieren · {profile.Name} · Durchlauf {pass}", $"Compressing · {profile.Name} · pass {pass}");
        RunTitle = _runName;
        _tally.NextPass();
        ShowTally();
        foreach (var sl in Slots) sl.Reset();
        BannerText = notice ?? "";
        BannerCancelable = false;
        UpdateKeepAwake();
    }

    private int Parallel => Math.Clamp(_app.Settings.ParallelFiles, 1, 3);

    public Profile? ProfileFor(IReadOnlyList<string> sources)
    {
        bool Contains(Profile p) => !string.IsNullOrWhiteSpace(p.InputDir) && sources.All(s => Scanner.IsInside(s, p.InputDir));
        var sel = SelectedProfile;
        if (sel is not null && Contains(sel)) return sel;
        return _app.Settings.Profiles.FirstOrDefault(Contains);
    }

    public async Task<bool> StartSelectionAsync(IReadOnlyList<string> sources, bool fullRun = false)
    {
        if (sources.Count == 0) return false;
        ErrorText = "";
        var profile = ProfileFor(sources);
        if (profile is null)
        {
            ErrorText = L.T("Die Auswahl liegt in keinem Quellordner eines Profils. Gezielt kodiert werden kann nur, was zur Bibliothek gehört (Ziel und Sperren müssen passen).",
                            "The selection is not inside any profile's source folder. Only items belonging to the library can be encoded (target and locks must match).");
            return false;
        }
        var added = await Task.Run(() => WorkQueue.Add(profile.OutputDir, profile.InputDir, sources, FileLock.ComputerName));
        if (added < 0)
        {
            ErrorText = L.T("Die Warteschlange im Zielordner ist gerade nicht erreichbar. Bitte gleich noch einmal versuchen.", "The queue in the target folder is not reachable right now. Please try again shortly.");
            return false;
        }
        ReadQueue();
        if (IsBusy)
        {
            var sameTarget = _library is not null && string.Equals(_runProfile?.OutputDir, profile.OutputDir, StringComparison.OrdinalIgnoreCase);
            StatusText = sameTarget
                ? L.T("In die Warteschlange aufgenommen: kommt als Nächstes dran.", "Added to the queue: comes next.")
                : L.T("In die Warteschlange aufgenommen: wird beim nächsten Lauf zuerst bearbeitet.", "Added to the queue: processed first in the next run.");
            return true;
        }
        if (fullRun && profile == SelectedProfile && StartCommand.CanExecute(null)) StartCommand.Execute(null);
        else _ = StartQueueRunAsync(profile);
        return true;
    }

    private Task StartQueueRunAsync(Profile profile) => RunGuardedAsync(RunKind.Queue, L.T("Warteschlange", "Queue"), async (p, tools, ct) =>
    {
        var seen = new HashSet<string>();
        while (true)
        {
            foreach (var e in WorkQueue.Read(p.OutputDir)) seen.Add(e.Id);
            _library = new LibraryRun(p, tools, this) { Priority = _app.FfmpegPriority, Parallel = Parallel, QueueOnly = true };
            if (IsPaused) _library.Pause();
            await _library.RunAsync(ct);
            var left = WorkQueue.Read(p.OutputDir);
            _queueLeft = left.Count;
            if (ct.IsCancellationRequested || StopRequested || _library.Counters.DiskFull) break;
            if (!left.Any(e => !seen.Contains(e.Id))) break;
            Ui(() =>
            {
                _doneBase = _tally.Done;
                foreach (var sl in Slots) sl.Reset();
            });
        }
    }, profileOverride: profile, slots: Parallel);

    public Task StartCleanupAsync(CleanupOptions opt) => RunGuardedAsync(opt.DryRun ? RunKind.TouchUpDryRun : RunKind.TouchUp,
        opt.DryRun ? L.T("Nachbessern prüfen", "Check touch-up") : L.T("Fertige Dateien nachbessern", "Touch up finished files"), async (p, tools, ct) =>
    {
        _cleanup = new CleanupRun(p, tools, this) { Priority = _app.FfmpegPriority };
        await _cleanup.RunAsync(opt, ct);
    }, needsInput: false, needsEncoder: false, folders: p => [string.IsNullOrWhiteSpace(opt.Folder) ? p.OutputDir : opt.Folder.Trim()]);

    public Task StartVerifyAsync(VerifyOptions opt) => RunGuardedAsync(RunKind.Verify, L.T("Prüflauf", "Verification"), async (p, tools, ct) =>
    {
        _verify = new VerifyRun(p, tools, this);
        await _verify.RunAsync(opt, ct);
    }, needsInput: false, needsEncoder: false, folders: p => [p.OutputDir]);

    public (double Start, double Length) LastTestClip { get; private set; }

    public string LastTestFolder { get; private set; } = "";

    public Task<List<TestRow>> StartTestAsync(string file)
    {
        var result = new List<TestRow>();
        var task = RunGuardedAsync(RunKind.Test, L.T("Testmodus", "Test mode"), async (p, tools, ct) =>
        {
            _test = new TestRun(p, tools, this);
            LastTestFolder = _test.TestFolder(file);
            result.AddRange(await _test.RunAsync(file, ct));
            LastTestClip = (_test.ClipStart, _test.ClipLength);
        }, needsInput: false, folders: _ => [file]);
        return task.ContinueWith(_ => result, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task RunGuardedAsync(RunKind kind, string title, Func<Profile, FfmpegTools, CancellationToken, Task> work, bool needsInput = true, bool afterRun = false,
        bool needsEncoder = true, Profile? profileOverride = null, int slots = 1, Func<Profile, IReadOnlyList<string>>? folders = null)
    {
        if (IsBusy) return;
        Starting = true;
        try
        {
            await RunGuardedCoreAsync(kind, title, work, needsInput, afterRun, needsEncoder, profileOverride, slots, folders);
        }
        finally
        {
            Starting = false;
            _activeFolders = [];
        }
    }

    private async Task RunGuardedCoreAsync(RunKind kind, string title, Func<Profile, FfmpegTools, CancellationToken, Task> work, bool needsInput, bool afterRun,
        bool needsEncoder, Profile? profileOverride, int slots, Func<Profile, IReadOnlyList<string>>? folders)
    {
        if (_app.Rename.IsExecuting)
        {
            ErrorText = L.T("Gerade wird umbenannt. Der Lauf startet danach von Hand oder beim nächsten automatischen Start.", "Renaming is in progress. Start the run afterwards manually or at the next automatic start.");
            return;
        }
        CancelPending();
        ErrorText = "";
        _lastCancelled = false;
        _lastDiskFull = false;
        _lastCounters = null;
        _queueLeft = 0;
        _passWindow = null;
        _waitingIdle = false;
        _autoPause.Reset();
        AutoPaused = false;
        var profile = (profileOverride ?? SelectedProfile)?.Clone();
        if (profile is null) { ErrorText = L.T("Kein Profil vorhanden.", "No profile available."); return; }
        _activeFolders = folders?.Invoke(profile) ?? [profile.InputDir, profile.OutputDir];
        var tools = await _app.EnsureToolsAsync(encoder: needsEncoder ? profile.Encoder : null);
        if (tools is null) { ErrorText = L.T("ffmpeg nicht gefunden. Auf der Seite \"ffmpeg\" den Ordner wählen.", "ffmpeg not found. Choose the folder on the \"ffmpeg\" page."); return; }
        if (needsEncoder && !tools.HasEncoder(profile.Encoder))
        {
            ErrorText = L.T($"Dieses ffmpeg kennt den Encoder {FfmpegTools.EncoderName(profile.Encoder)} nicht. Profil oder ffmpeg prüfen.", $"This ffmpeg does not know the encoder {FfmpegTools.EncoderName(profile.Encoder)}. Check the profile or ffmpeg.");
            return;
        }
        if (needsInput && !await Io.DirExistsAsync(profile.InputDir, 8000)) { ErrorText = L.T($"Quellordner nicht erreichbar: {profile.InputDir}", $"Source folder not reachable: {profile.InputDir}"); return; }
        if (needsEncoder)
        {
            StatusText = L.T("Prüfe den Encoder …", "Checking the encoder …");
            var (works, msg) = await TestEncoderAsync(profile, tools);
            if (!works)
            {
                ErrorText = L.T($"Der Encoder lässt sich mit diesem Profil nicht starten: {msg}", $"The encoder cannot be started with this profile: {msg}");
                StatusText = L.T("Bereit", "Ready");
                return;
            }
        }

        await SyncDataAsync(profile);
        ResetCounters();
        _kind = kind;
        _runProfile = profile;
        while (Slots.Count > slots) Slots.RemoveAt(Slots.Count - 1);
        while (Slots.Count < slots) Slots.Add(new SlotViewModel(Slots.Count));
        _runName = $"{title} · {profile.Name}";
        RunTitle = _runName;
        StatusText = L.T("Starte ...", "Starting ...");
        IsRunning = true;
        IsPaused = false;
        StopRequested = false;
        var cts = new CancellationTokenSource();
        _cts = cts;
        var started = DateTime.Now;
        UpdateKeepAwake();
        RefreshTargetViews();
        _app.WriteLog($"=== {RunTitle} gestartet ({tools.FfmpegPath}, {profile.QualityText})");
        string? error = null;
        try
        {
            _work = Task.Run(() => work(profile, tools, cts.Token));
            await _work;
        }
        catch (OperationCanceledException)
        {
            _lastCancelled = true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            ErrorText = ex.Message;
            _app.WriteLog("FEHLER " + ex.Message);
        }
        finally
        {
            IsRunning = false;
            IsPaused = false;
            HasStage = false;
            ElapsedText = Fmt.Time((DateTime.Now - started).TotalSeconds);
            _library = null;
            _verify = null;
            _cleanup = null;
            _test = null;
            cts.Dispose();
            if (_cts == cts) _cts = null;
            BannerText = "";
            AutoPaused = false;
            _autoPause.Reset();
            ActiveFraction = 0;
            _passWindow = null;
            _waitingIdle = false;
            try { FinishRun(error); }
            catch (Exception ex) { _app.WriteLog("FEHLER beim Abschluss des Laufs: " + ex.Message); }
            WriteOwnStatus();
            _runProfile = null;
            UpdateKeepAwake();
            RefreshTargetViews();
        }
        if (afterRun && error is null && !_lastCancelled && !StopRequested) StartAfterRunAction();
    }

    private void FinishRun(string? error)
    {
        var stopped = _lastCancelled || StopRequested;
        var c = _lastCounters;
        if (error is not null)
        {
            StatusText = L.T("Mit einem Fehler beendet, siehe Meldung.", "Ended with an error, see the message.");
            RunTitle = L.T("Fehler", "Error") + " · " + _runName;
        }
        else
        {
            if (!_lastDiskFull) StatusText = RunTexts.FinishStatus(_kind, stopped, _tally, c, _queueLeft);
            RunTitle = (stopped ? L.T("Angehalten", "Stopped") : L.T("Fertig", "Done")) + " · " + _runName;
        }
        var since = RunTexts.SinceStart(_tally);
        _app.WriteLog($"=== Ende ({StatusText}) {RunTexts.SummaryLine(_kind, _tally, c)}, gewonnen {RunTexts.Gain(_tally.GainBytes)}" + (since.Length > 0 ? " | " + since : ""));
        if (error is not null) Notify?.Invoke(L.T("Gimbo Konverter – Fehler", "Gimbo Converter – error"), error, true);
        else if (_lastDiskFull) Notify?.Invoke(L.T("Gimbo Konverter – Speicher voll", "Gimbo Converter – disk full"), StatusText, true);
        else if (_app.Settings.Notifications && !stopped)
        {
            var hint = _tally.Done == 0 ? RunTexts.FinishHint(_kind, false, _tally, c, _queueLeft).Trim() : "";
            var text = hint.Length > 0 ? hint : RunTexts.Summary(_kind, _tally, c) + (since.Length > 0 ? "\n" + since : "");
            Notify?.Invoke(L.T("Gimbo Konverter – fertig", "Gimbo Converter – done"), text, _tally.Errors > 0);
        }
    }

    private async Task<(bool Ok, string Message)> TestEncoderAsync(Profile profile, FfmpegTools tools)
    {
        if (App.ScreenshotFolder is not null) return (true, "");
        var (ok, msg) = await tools.TestProfileAsync(profile);
        if (!ok && profile.Encoder == EncoderKind.Nvenc && !profile.NvencLegacy)
        {
            profile.NvencLegacy = true;
            var (ok2, _) = await tools.TestProfileAsync(profile);
            if (ok2)
            {
                await RememberNvencLegacyAsync(profile.Name, true);
                _app.WriteLog("NVENC ohne B-Frames (aeltere Grafikkarte): " + msg);
                return (true, "");
            }
            profile.NvencLegacy = false;
        }
        else if (ok && profile.Encoder == EncoderKind.Nvenc && profile.NvencLegacy)
        {
            var full = profile.Clone();
            full.NvencLegacy = false;
            if ((await tools.TestProfileAsync(full)).Ok)
            {
                profile.NvencLegacy = false;
                await RememberNvencLegacyAsync(profile.Name, false);
            }
        }
        return (ok, msg);
    }

    private Task RememberNvencLegacyAsync(string profileName, bool legacy) => OnUiAsync(() =>
    {
        if (_app.Settings.FindProfile(profileName) is not { } stored || stored.NvencLegacy == legacy) return;
        stored.NvencLegacy = legacy;
        _app.Save();
    });

    private bool _lastCancelled;
    private string _runName = "";
    private bool _lastDiskFull;

    // ---------------- Aktion nach dem Lauf (Standby, Herunterfahren ...) ----------------

    private CancellationTokenSource? _pendingCts;

    private string? OtherWork() =>
        _app.Convert.IsRunning ? L.T("das Konvertieren", "the conversion")
        : _app.Repair.IsRunning ? L.T("das Reparieren", "the repair")
        : _app.Rename.IsExecuting ? L.T("das Umbenennen", "the renaming")
        : _app.Library.IsBusy ? L.T("die Arbeit auf der Seite „Bibliothek“", "the work on the “Library” page")
        : null;

    private async void StartAfterRunAction()
    {
        var action = _app.Settings.AfterRun;
        if (action == AfterRunAction.Nothing || App.ScreenshotFolder is not null) return;
        var what = action switch
        {
            AfterRunAction.Sleep => L.T("Energie sparen (Standby)", "Sleep"),
            AfterRunAction.Hibernate => L.T("Ruhezustand", "Hibernate"),
            AfterRunAction.Shutdown => L.T("Herunterfahren", "Shut down"),
            _ => L.T("Programm beenden", "Exit program"),
        };
        _pendingCts = new CancellationTokenSource();
        var ct = _pendingCts.Token;
        BannerCancelable = true;
        Notify?.Invoke(App.Title, L.T($"Fertig. {what} in 60 Sekunden, im Programm abbrechbar.", $"Done. {what} in 60 seconds, can be cancelled in the program."), false);
        try
        {
            do
            {
                while (OtherWork() is { } other)
                {
                    BannerText = L.T($"Fertig. {what} folgt, sobald {other} abgeschlossen ist.", $"Done. {what} follows once {other} has finished.");
                    await Task.Delay(5000, ct);
                }
                for (var s = 60; s > 0; s--)
                {
                    BannerText = L.T($"Fertig. {what} in {s} Sekunden.", $"Done. {what} in {s} seconds.");
                    await Task.Delay(1000, ct);
                }
            }
            while (OtherWork() is not null);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        finally
        {
            BannerText = "";
            BannerCancelable = false;
        }
        _app.WriteLog("Nach dem Lauf: " + what);
        switch (action)
        {
            case AfterRunAction.Sleep: Power.Sleep(); break;
            case AfterRunAction.Hibernate: Power.Hibernate(); break;
            case AfterRunAction.Shutdown: Power.Shutdown(); break;
            case AfterRunAction.CloseApp: (Application.Current as App)?.ExitApp(); break;
        }
    }

    private bool CanCancelPending() => BannerText.Length > 0 && BannerCancelable;

    [RelayCommand(CanExecute = nameof(CanCancelPending))]
    private void CancelPending()
    {
        if (_pendingCts is null) return;
        _pendingCts.Cancel();
        _pendingCts = null;
        StatusText = L.T("Aktion nach dem Lauf abgebrochen.", "Action after the run cancelled.");
    }

    private bool CanControl() => IsRunning;

    private bool PauseWork() => (_library?.Pause() ?? false) | (_verify?.Pause() ?? false) | (_test?.Pause() ?? false) | (_cleanup?.Pause() ?? false);

    private bool ResumeWork() => (_library?.Resume() ?? false) | (_verify?.Resume() ?? false) | (_test?.Resume() ?? false) | (_cleanup?.Resume() ?? false);

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void Pause()
    {
        if (IsPaused)
        {
            if (ResumeWork() | (_library is not null))
            {
                _autoPause.UserToggled();
                AutoPaused = false;
                IsPaused = false;
                StatusText = L.T("Fortgesetzt.", "Resumed.");
            }
        }
        else if (PauseWork())
        {
            _autoPause.UserToggled();
            IsPaused = true;
            StatusText = L.T("Pausiert - ffmpeg ist angehalten, keine neue Datei beginnt.", "Paused - ffmpeg is suspended, no new file starts.");
        }
        else
        {
            StatusText = L.T("Pause ist nur während des Kodierens oder Prüfens möglich.", "Pause is only possible while encoding or verifying.");
        }
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void StopAfterCurrent()
    {
        StopRequested = true;
        if (_library is not null) _library.StopAfterCurrent = true;
        if (_verify is not null) _verify.StopAfterCurrent = true;
        if (_cleanup is not null) _cleanup.StopAfterCurrent = true;
        StatusText = Slots.Count > 1 ? L.T("Hält nach den aktuellen Dateien an.", "Stops after the current files.") : L.T("Hält nach der aktuellen Datei an.", "Stops after the current file.");
    }

    [RelayCommand(CanExecute = nameof(CanControl))]
    private void Cancel()
    {
        if (IsPaused) Pause();
        _cts?.Cancel();
        _lastCancelled = true;
        StatusText = L.T("Wird abgebrochen ...", "Cancelling ...");
    }

    private Task? _work;

    public void CancelAndWait(TimeSpan wait)
    {
        if (IsPaused) Pause();
        try { _cts?.Cancel(); } catch { }
        try { _work?.Wait(wait); } catch { }
        if (!_app.Settings.ShareData || !_app.Settings.ShareStatus || App.ScreenshotFolder is not null || StatusTarget is not { } target) return;
        var idle = BuildStatus(running: false, L.T("Programm beendet.", "Program closed."));
        try
        {
            Task.Run(() =>
            {
                lock (_statusWriteLock)
                    if (StatusBoard.Write(target, idle)) _lastWrittenUtc = idle.UpdatedUtc;
            }).Wait(TimeSpan.FromSeconds(2));
        }
        catch
        {
        }
    }

    // ---------------- Automatische Pause, Standby, Status fuer die anderen PCs ----------------

    private readonly AutoPauseState _autoPause = new();

    private void OnTimer()
    {
        if (App.ScreenshotFolder is not null) return;
        CheckAutoPause();
        UpdateKeepAwake();
        if (IsRunning && (DateTime.Now - _lastStatusWrite).TotalSeconds >= 5) WriteOwnStatus();
        if (!SameTarget(StatusTarget, _shownTarget)) { RefreshTargetViews(); return; }
        if ((DateTime.Now - _lastPeerRead).TotalSeconds >= (IsRunning ? 10 : 30)) ReadPeers();
        if ((DateTime.Now - _lastQueueRead).TotalSeconds >= 10) ReadQueue();
        if ((DateTime.Now - _lastSpaceCheck).TotalMinutes >= 10) CheckSpace();
    }

    private void CheckAutoPause()
    {
        var s = _app.Settings;
        var conv = _app.Convert;
        var enabled = s.PauseWhenFullscreen || s.PauseWhenUserActive;
        var reason = enabled && (IsRunning || conv.IsRunning || _app.Repair.IsRunning)
            ? UserPresence.BusyReason(s.PauseWhenFullscreen, s.PauseWhenUserActive, s.UserIdleMinutes, s.FullscreenIgnore)
            : null;
        var now = DateTime.Now;
        switch (_autoPause.Decide(enabled, IsRunning, IsPaused, reason is not null, now))
        {
            case AutoPauseAction.Pause:
                if (PauseWork())
                {
                    _autoPause.MarkPaused();
                    IsPaused = true;
                    AutoPaused = true;
                    StatusText = L.T($"Automatisch pausiert: {reason}. Geht danach von selbst weiter.", $"Paused automatically: {reason}. Continues on its own afterwards.");
                    _app.WriteLog(StatusText);
                }
                break;
            case AutoPauseAction.Resume:
                ResumeWork();
                _autoPause.MarkResumed();
                IsPaused = false;
                AutoPaused = false;
                StatusText = L.T("Automatische Pause beendet, es geht weiter.", "Automatic pause ended, continuing.");
                _app.WriteLog(StatusText);
                break;
        }
        conv.AutoPauseTick(enabled, reason, now);
        _app.Repair.AutoPauseTick(enabled, reason, now);
    }

    private bool _awake;

    public void UpdateKeepAwake()
    {
        if (App.ScreenshotFolder is not null) return;
        var windowClosed = _passWindow is { } w && !w.IsOpen(DateTime.Now);
        var want = _app.Settings.PreventSleep
                   && (RunPolicy.NeedsAwake(IsRunning, Slots.Any(x => x.Busy), windowClosed, _waitingIdle) || _app.Convert.IsRunning || _app.Repair.IsRunning);
        if (want == _awake) return;
        _awake = want;
        Power.KeepAwake(want);
    }

    private string? StatusTarget => (_runProfile ?? SelectedProfile)?.OutputDir is { Length: > 0 } o ? o : null;

    private static bool SameTarget(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private PeerStatus BuildStatus(bool running, string idleNote) => new()
    {
        Machine = FileLock.ComputerName,
        UpdatedUtc = DateTime.UtcNow,
        State = !running ? PeerState.Idle : IsPaused ? PeerState.Paused : Slots.Any(x => x.Busy) ? PeerState.Running : PeerState.Waiting,
        Profile = _runProfile?.Name ?? "",
        Note = !running ? idleNote : AutoPaused ? StatusText : BannerText,
        Done = Done,
        Total = Total,
        Encoded = _kind is RunKind.Library or RunKind.Queue ? _tally.TotalNew : 0,
        Errors = CountErrors,
        GainBytes = _kind is RunKind.Library or RunKind.Queue ? _tally.TotalGainBytes : 0,
        Version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "",
        Files = running ? Slots.Where(x => x.Busy).Select(x => x.ToPeer()).ToList() : [],
    };

    private void WriteOwnStatus()
    {
        if (!_app.Settings.ShareData || !_app.Settings.ShareStatus || App.ScreenshotFolder is not null) return;
        var target = StatusTarget;
        if (target is null) return;
        _lastStatusWrite = DateTime.Now;
        var status = BuildStatus(IsRunning, StatusText);
        Interlocked.Exchange(ref _pendingStatus, new PendingStatus(target, status));
        if (!_statusBusy.TryEnter()) return;
        _ = Task.Run(() =>
        {
            while (true)
            {
                while (Interlocked.Exchange(ref _pendingStatus, null) is { } next)
                {
                    lock (_statusWriteLock)
                    {
                        if (next.Status.UpdatedUtc < _lastWrittenUtc) continue;
                        if (StatusBoard.Write(next.Target, next.Status)) _lastWrittenUtc = next.Status.UpdatedUtc;
                    }
                }
                _statusBusy.Exit();
                if (Volatile.Read(ref _pendingStatus) is null || !_statusBusy.TryEnter()) return;
            }
        });
    }

    private sealed record PendingStatus(string Target, PeerStatus Status);
    private PendingStatus? _pendingStatus;
    private readonly BusyFlag _statusBusy = new(), _queueBusy = new(), _spaceBusy = new(), _peersBusy = new();

    private static readonly object _statusWriteLock = new();
    private static DateTime _lastWrittenUtc;

    private DateTime _lastQueueRead;
    private DateTime _lastSpaceCheck;

    private string? _shownTarget;

    public void RefreshTargetViews()
    {
        if (App.ScreenshotFolder is not null) return;
        var target = StatusTarget;
        if (!SameTarget(target, _shownTarget))
        {
            _shownTarget = target;
            ShowQueue(null, []);
            Peers.Clear();
            HasPeers = false;
            SpaceWarning = "";
        }
        ReadQueue();
        ReadPeers();
        CheckSpace();
    }

    private string? _queueTarget;

    public void ReadQueue()
    {
        _lastQueueRead = DateTime.Now;
        if (App.ScreenshotFolder is not null) return;
        var target = StatusTarget;
        if (target is null) { ShowQueue(null, []); return; }
        _queueBusy.Run(() =>
        {
            var ok = WorkQueue.TryRead(target, out var list);
            Ui(() =>
            {
                if (!SameTarget(target, StatusTarget)) return;
                if (ok || !SameTarget(target, _queueTarget)) ShowQueue(target, list);
            });
        });
    }

    private void ShowQueue(string? target, List<QueueEntry> list)
    {
        _queueTarget = target;
        Queue.Clear();
        foreach (var e in list) Queue.Add(new QueueItem(e, e.Name, QueueDetail(e.AddedBy, e.AddedUtc.ToLocalTime()), target ?? ""));
        HasQueue = Queue.Count > 0;
    }

    private static string QueueDetail(string addedBy, DateTime added) =>
        L.T($"von {addedBy} · {added:dd.MM. HH:mm}", $"by {addedBy} · {added:g}");

    private async Task ChangeQueueAsync(string? target, Func<string, bool> change)
    {
        if (string.IsNullOrEmpty(target)) return;
        if (!await Task.Run(() => change(target)))
            ErrorText = L.T("Die Warteschlange im Zielordner ist gerade nicht erreichbar.", "The queue in the target folder is not reachable right now.");
        ReadQueue();
    }

    [RelayCommand]
    private Task QueueUp(QueueItem? item) => item is null ? Task.CompletedTask : ChangeQueueAsync(item.Target, t => WorkQueue.Move(t, item.Entry.Id, -1));

    [RelayCommand]
    private Task QueueDown(QueueItem? item) => item is null ? Task.CompletedTask : ChangeQueueAsync(item.Target, t => WorkQueue.Move(t, item.Entry.Id, 1));

    [RelayCommand]
    private Task QueueRemove(QueueItem? item) => item is null ? Task.CompletedTask : ChangeQueueAsync(item.Target, t => WorkQueue.Remove(t, item.Entry.Id));

    [RelayCommand]
    private Task QueueClear()
    {
        var target = _queueTarget;
        if (string.IsNullOrEmpty(target)) return Task.CompletedTask;
        var ok = System.Windows.MessageBox.Show(
            L.T($"Die Warteschlange im Zielordner {target} gilt für alle PCs, die dorthin arbeiten. Wirklich alle Einträge entfernen?",
                $"The queue in the target folder {target} applies to all PCs working into it. Really remove all entries?"),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        return ok == System.Windows.MessageBoxResult.Yes ? ChangeQueueAsync(target, WorkQueue.Clear) : Task.CompletedTask;
    }

    public void CheckSpace()
    {
        _lastSpaceCheck = DateTime.Now;
        var p = _runProfile ?? SelectedProfile;
        if (p is null || App.ScreenshotFolder is not null) return;
        if (!_app.Snapshots.TryGetValue(p.Name, out var snap) || !string.Equals(snap.OutputDir, p.OutputDir, StringComparison.OrdinalIgnoreCase))
        {
            SpaceWarning = "";
            return;
        }
        var reserve = Math.Max(0, p.MinFreeSpaceGb) * 1024L * 1024L * 1024L;
        var target = p.OutputDir;
        _spaceBusy.Run(() =>
        {
            var free = Native.FreeBytes(target);
            var v = SpaceForecast.Evaluate(snap, free, reserve, StatsLog.ReadAll([target]));
            Ui(() =>
            {
                if (SameTarget(target, StatusTarget)) SpaceWarning = v.Warn ? v.Text : "";
            });
        });
    }

    private async Task SyncDataAsync(Profile profile)
    {
        if (App.ScreenshotFolder is not null || string.IsNullOrWhiteSpace(profile.OutputDir)) return;
        Core.Library.DataStore.Link(profile.OutputDir, profile.InputDir);
        var (r, state) = await Task.Run(() => (Core.Library.DataStore.Sync(profile.OutputDir), Core.Library.DataStore.State(profile.OutputDir)));
        if (r.Imported > 0) _app.WriteLog($"Listen frueherer Versionen aus dem Zielordner uebernommen: {r.Imported} Dateien");
        if (r.Restored) _app.WriteLog($"Gemeinsamer Ordner war leer: {r.MarksRestored} Vermerke und {r.LogsRestored} Protokolle aus der Sicherung zurueckgespielt");
        if (state == Core.Library.SharingState.NotWritable)
            _app.WriteLog("Quellordner nicht beschreibbar oder nicht erreichbar: Listen bleiben vorerst auf diesem PC, keine Abstimmung mit anderen PCs");
    }

    public void ReadPeers()
    {
        _lastPeerRead = DateTime.Now;
        var target = StatusTarget;
        if (target is null || App.ScreenshotFolder is not null) return;
        _peersBusy.Run(() =>
        {
            var list = StatusBoard.ReadOthers(target, FileLock.ComputerName);
            Ui(() =>
            {
                if (SameTarget(target, StatusTarget)) ShowPeers(list);
            });
        });
    }

    private void ShowPeers(List<PeerStatus> list)
    {
        Peers.Clear();
        var now = DateTime.UtcNow;
        foreach (var p in list.Where(p => p.IsWorthShowing(now)))
        {
            var fresh = p.IsFresh(now);
            var active = p.IsActive(now);
            var at = p.UpdatedUtc.ToLocalTime();
            var head = p.State switch
            {
                PeerState.Idle => L.T($"beendet um {at:HH:mm}", $"finished at {at:HH:mm}"),
                _ when !fresh => L.T($"keine Meldung seit {at:HH:mm}", $"no update since {at:HH:mm}"),
                PeerState.Running => L.T($"arbeitet · {p.Profile}", $"working · {p.Profile}"),
                PeerState.Paused => L.T("pausiert", "paused"),
                _ => L.T("wartet", "waiting"),
            };
            var parts = new List<string>();
            if (p.Total > 0) parts.Add($"{p.Done.ToString("N0", Fmt.De)} / {RunTexts.Files(p.Total)}");
            if (p.Encoded > 0) parts.Add(L.T($"{p.Encoded.ToString("N0", Fmt.De)} komprimiert, {Fmt.Size(p.GainBytes)} gewonnen", $"{p.Encoded.ToString("N0", Fmt.De)} compressed, {Fmt.Size(p.GainBytes)} saved"));
            foreach (var f in active ? p.Files : [])
                parts.Add($"{Path.GetFileName(f.Name)} {f.Percent.ToString("0", Fmt.De)} %" + (f.EtaSeconds > 0 ? " · " + RunTexts.Eta(f.EtaSeconds) : ""));
            if (parts.Count == 0 && p.Note.Length > 0) parts.Add(p.Note);
            Peers.Add(new PeerItem(p.Machine, head, string.Join("\n", parts), active, fresh));
        }
        HasPeers = Peers.Count > 0;
    }

    public void FillDemo()
    {
        const long gb = 1024L * 1024 * 1024;
        var day = new DateTime(2026, 11, 13);
        RunTitle = L.T("Komprimieren · NVIDIA", "Compressing · NVIDIA");
        IsRunning = true;
        StatusText = L.T("2 Dateien gleichzeitig in Arbeit.", "2 files in progress at once.");
        Total = 618; Done = 182; ListPercent = Done * 100d / Total;
        CountNew = 57; CountOk = 91; CountSkipped = 22; CountOther = 11; CountErrors = 1;
        GainText = Fmt.Size(83.71 * gb);
        while (Slots.Count < 2) Slots.Add(new SlotViewModel(Slots.Count));
        var a = Slots[0];
        a.Start(new FileStart(183, 618, DemoText.T(@"Serie C\Staffel 1\Serie.C.S01E02.GERMAN.DL.HDR.2160p.WEB.h265.mkv"), "", 2_600_000_000, 60));
        a.Lines.Add(new LineItem(L.T("Quelle", "Source"), L.T("HEVC · 3840×2160 (2160p) · 10 Bit · HDR10 (PQ) · 23,976 fps · 00:48:17 · 2,42 GB", "HEVC · 3840×2160 (2160p) · 10 Bit · HDR10 (PQ) · 23.976 fps · 00:48:17 · 2.42 GB"), LineKind.Info));
        a.Lines.Add(new LineItem(L.T("Datenrate", "Bitrate"), L.T("Video 6,4 Mbit/s · gesamt 7,2 Mbit/s", "video 6.4 Mbit/s · total 7.2 Mbit/s"), LineKind.Dim));
        a.Lines.Add(new LineItem(L.T("Ziel", "Target"), L.T("HEVC main10 · 10 Bit · NVENC CQ 27 · 3840×2160 → 1920×1080", "HEVC main10 · 10 Bit · NVENC CQ 27 · 3840×2160 → 1920×1080"), LineKind.Info));
        a.Lines.Add(new LineItem(L.T("Ton", "Audio"), L.T("Deutsch E-AC3 5.1 | Englisch E-AC3 5.1 · unverändert", "German E-AC3 5.1 | English E-AC3 5.1 · unchanged"), LineKind.Dim));
        a.Lines.Add(new LineItem(L.T("Untertitel", "Subtitles"), L.T("Deutsch SRT (erzwungen) | Deutsch SRT | Englisch SRT · unverändert", "German SRT (forced) | German SRT | English SRT · unchanged"), LineKind.Dim));
        a.Progress("Encoding", new ProgressInfo(0.384, 1113, 2897, 93, 3.89, 393_000_000, 2820, 286, 457, 1_022_000_000, false));
        var b = Slots[1];
        b.Start(new FileStart(184, 618, DemoText.T(@"Serie A\Staffel 3\Serie.A.S03E01.German.DL.1080p.BluRay.x264.mkv"), "", 4_100_000_000, 60));
        b.Lines.Add(new LineItem(L.T("Quelle", "Source"), L.T("H.264 · 1920×1080 (1080p) · 8 Bit · 23,976 fps · 00:47:02 · 3,82 GB", "H.264 · 1920×1080 (1080p) · 8 Bit · 23.976 fps · 00:47:02 · 3.82 GB"), LineKind.Info));
        b.Lines.Add(new LineItem(L.T("Datenrate", "Bitrate"), L.T("Video 9,7 Mbit/s · gesamt 11,6 Mbit/s", "video 9.7 Mbit/s · total 11.6 Mbit/s"), LineKind.Dim));
        b.Lines.Add(new LineItem(L.T("Ziel", "Target"), L.T("HEVC main · 8 Bit · NVENC CQ 27 · 1920×1080, Größe bleibt", "HEVC main · 8 Bit · NVENC CQ 27 · 1920×1080, size kept"), LineKind.Info));
        b.Lines.Add(new LineItem(L.T("Ton", "Audio"), L.T("Deutsch DTS-HD MA 5.1 → E-AC3 5.1 640k (Standard) | Englisch AC3 5.1 kopiert", "German DTS-HD MA 5.1 → E-AC3 5.1 640k (default) | English AC3 5.1 copied"), LineKind.Info));
        b.Lines.Add(new LineItem(L.T("Untertitel", "Subtitles"), L.T("12 Spuren: Deutsch, Englisch, Französisch, Spanisch (1 erzwungen) · unverändert", "12 tracks: German, English, French, Spanish (1 forced) · unchanged"), LineKind.Dim));
        b.Progress("Encoding", new ProgressInfo(0.712, 2009, 2822, 141, 5.88, 610_000_000, 2430, 341, 138, 857_000_000, false));
        Queue.Clear();
        Queue.Add(new QueueItem(new QueueEntry { Relative = DemoText.T(@"Serie F") }, DemoText.T("Serie F"), QueueDetail("PC-1", day.AddHours(8).AddMinutes(12))));
        Queue.Add(new QueueItem(new QueueEntry { Relative = DemoText.T(@"Serie G\Staffel 2") }, DemoText.T(@"Serie G\Staffel 2"), QueueDetail("PC-2", day.AddHours(8).AddMinutes(40))));
        HasQueue = true;
        SpaceWarning = SpaceForecast.Evaluate(
            new AnalysisSnapshot { Profile = "NVIDIA", CreatedUtc = day.AddDays(-1).AddHours(12).ToUniversalTime(), OpenFiles = 448, EstimatedOutputBytes = (long)(300.21 * gb) },
            252 * gb, 50 * gb, []).Text;
        ShowPeers(
        [
            new PeerStatus
            {
                Machine = "PC-2", UpdatedUtc = DateTime.UtcNow, State = PeerState.Running, Profile = "AMD", Done = 311, Total = 618,
                Encoded = 91, GainBytes = (long)(133.31 * gb),
                Files = [new PeerFile { Name = DemoText.T(@"Serie D\Staffel 2\Serie.D.S02E07.German.DL.720p.mkv"), Percent = 54, EtaSeconds = 680 }],
            },
        ]);
        History.Clear();
        var t = day.AddHours(14).AddMinutes(32);
        void Add(DateTime time, Outcome outcome, string name, string message) => History.Add(new HistoryItem(time, new FileEnd(outcome, message).Badge, outcome, name, message));
        Add(t, Outcome.Encoded, DemoText.T(@"Serie C\Staffel 1\Serie.C.S01E01.GERMAN.DL.HDR.2160p.WEB.h265.mkv"), DemoText.T(@"Serie C\Staffel 1\Serie.C.S01E01.GERMAN.DL.HDR.2160p.WEB.h265.mkv"));
        Add(t.AddMinutes(-14), Outcome.AlreadyOk, DemoText.T(@"Serie C\Staffel 8\Serie C.S08E12.German.DL.1080p.BluRay.x264.mkv"), L.T("Vorhandene Datei erfüllt die Vorgaben, bereits geprüft.", "Existing file meets the requirements, already checked."));
        Add(t.AddMinutes(-14), Outcome.OtherPc, DemoText.T(@"Serie C\Staffel 8\Serie C.S08E11.German.DL.1080p.BluRay.x264.mkv"), L.T("bei PC-2 in Arbeit", "in progress on PC-2"));
        Add(t.AddMinutes(-15), Outcome.Skipped, DemoText.T(@"Serie C\Staffel 8\Serie C.S08E10.German.DL.1080p.AMZN.WEB.H265.mkv"), L.T("Früh abgebrochen. Wäre nicht klein genug geworden.", "Aborted early. Would not be small enough."));
        Add(t.AddMinutes(-21), Outcome.Replaced, DemoText.T(@"Serie A\Staffel 2\Serie.A.S02E09.Der.Anruf.German.DL.AC3.5.1.1080p.Bluray.x264-iND.mkv"), DemoText.T(@"Serie A\Staffel 2\Serie.A.S02E09.Der.Anruf.German.DL.AC3.5.1.1080p.Bluray.x264-iND.mkv"));
        Add(t.AddMinutes(-35), Outcome.Error, DemoText.T(@"Serie D\Staffel 1\Serie.D.S01E04.German.DL.720p.mkv"), L.T("Prüfung: Datei ist beschädigt (Fehler beim Dekodieren).", "Check: file is damaged (decoding errors)."));
    }

    private void ResetCounters()
    {
        foreach (var sl in Slots) sl.Reset();
        _tally.Reset();
        _doneBase = 0;
        Total = 0;
        ShowTally();
        ElapsedText = "";
        CurrentName = "";
        CurrentPosition = "";
        HasStage = false;
    }

    private void ShowTally()
    {
        Done = _tally.Done;
        CountNew = _tally.New;
        CountOk = _tally.Ok;
        CountSkipped = _tally.Skipped;
        CountOther = _tally.Other;
        CountErrors = _tally.Errors;
        GainText = RunTexts.Gain(_tally.GainBytes);
        ListPercent = Total > 0 ? Math.Min(100, Done * 100d / Total) : 0;
        SinceStartText = RunTexts.PassNote(_tally);
    }

    // ---------------- IRunObserver (Hintergrund-Thread -> Oberflaeche) ----------------

    private static void Ui(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess()) a();
        else d.BeginInvoke(a);
    }

    private static Task OnUiAsync(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d is null || d.CheckAccess())
        {
            a();
            return Task.CompletedTask;
        }
        return d.InvokeAsync(a).Task;
    }

    public void Status(string text) => Ui(() =>
    {
        StatusText = text;
        _app.WriteLog(text);
    });

    public void ScanStarted(string folder) => Ui(() => StatusText = _kind == RunKind.Queue
        ? L.T("Lese die Warteschlange ...", "Reading the queue ...")
        : L.T($"Suche Videodateien in {folder} ...", $"Searching for video files in {folder} ..."));

    public void ScanDone(int total) => Ui(() =>
    {
        Total = _doneBase + total;
        if (_kind == RunKind.Queue) StatusText = L.T("Arbeite die Warteschlange ab ...", "Working through the queue ...");
        else StatusText = L.T($"{RunTexts.Files(total)} gefunden.", $"{RunTexts.Files(total)} found.");
    });

    private SlotViewModel SlotFor(int slot)
    {
        while (Slots.Count <= slot) Slots.Add(new SlotViewModel(Slots.Count));
        return Slots[slot];
    }

    public void FileStarted(FileStart start) => Ui(() =>
    {
        if (_doneBase + start.Total > Total) Total = _doneBase + start.Total;
        SlotFor(start.Slot).Start(_doneBase > 0 ? start with { Number = _doneBase + start.Number, Total = Total } : start);
        StatusText = Slots.Count(x => x.Busy) > 1
            ? L.T($"{Slots.Count(x => x.Busy)} Dateien gleichzeitig in Arbeit.", $"{Slots.Count(x => x.Busy)} files in progress at once.")
            : start.FullPath;
        if (AutoPaused) StatusText = L.T("Automatisch pausiert.", "Paused automatically.");
        UpdateSummary();
        UpdateKeepAwake();
    });

    public void Line(InfoLine line) => Line(0, line);

    public void Line(int slot, InfoLine line) => Ui(() => SlotFor(slot).Lines.Add(new LineItem(line.Label, line.Text, line.Kind)));

    public void Progress(string stage, ProgressInfo pi) => Progress(0, stage, pi);

    public void Progress(int slot, string stage, ProgressInfo pi) => Ui(() =>
    {
        if (!IsRunning) return;
        SlotFor(slot).Progress(stage, pi);
        UpdateSummary();
    });

    private void UpdateSummary()
    {
        var busy = Slots.Where(x => x.Busy).ToList();
        if (busy.Count <= 1)
        {
            var one = busy.FirstOrDefault() ?? Slots[0];
            CurrentName = one.CurrentName;
            CurrentPosition = one.CurrentPosition;
            StageName = one.StageName;
            PercentText = one.PercentText;
            HasStage = one.HasStage;
            StagePercent = one.StagePercent;
        }
        else
        {
            CurrentName = string.Join(" | ", busy.Select(x => Path.GetFileName(x.CurrentName)));
            CurrentPosition = L.T($"{DoneText} erledigt, {busy.Count} gleichzeitig", $"{DoneText} done, {busy.Count} at once");
            StageName = busy[0].StageName;
            PercentText = string.Join(" · ", busy.Where(x => x.HasStage).Select(x => x.PercentText));
            HasStage = busy.Any(x => x.HasStage);
            StagePercent = busy.Average(x => x.StagePercent);
        }
        ActiveFraction = busy.Sum(x => x.Fraction);
    }

    public void FileFinished(FileStart start, FileEnd end) => Ui(() =>
    {
        _tally.Count(end);
        ShowTally();
        SlotFor(start.Slot).Finish();
        if (end.GainBytes > 0)
        {
            _app.Stats.BytesSaved += end.GainBytes;
            if (end.Outcome == Outcome.Replaced) _app.Stats.FilesReplaced++; else _app.Stats.FilesEncoded++;
            _app.Stats.LastRun = DateTime.Now;
            _app.Stats.FirstRun ??= DateTime.Now;
            _app.SaveStats();
        }
        UpdateSummary();
        if (_app.Settings.NotifyEachFile && _kind is not (RunKind.Verify or RunKind.Test) && end.Outcome is Outcome.Encoded or Outcome.Replaced)
            Notify?.Invoke(_kind == RunKind.TouchUp ? L.T("Nachgebessert", "Touched up") : end.Outcome == Outcome.Replaced ? L.T("Ersetzt", "Replaced") : L.T("Fertig", "Done"),
                L.T($"{Path.GetFileName(start.RelativePath)}\n{RunTexts.Gain(end.GainBytes)} gespart", $"{Path.GetFileName(start.RelativePath)}\n{RunTexts.Gain(end.GainBytes)} saved"), false);
        if (!_tally.ShowInHistory(start.RelativePath, end)) return;
        History.Insert(0, new HistoryItem(DateTime.Now, end.Badge, end.Outcome, start.RelativePath, end.Message));
        while (History.Count > 1000) History.RemoveAt(History.Count - 1);
        _app.WriteLog($"[{end.Badge}] {start.RelativePath} - {end.Message}");
    });

    public void RunFinished(RunCounters counters, bool cancelled) => Ui(() =>
    {
        _app.Stats.EncodeSeconds += counters.EncodeSeconds;
        _app.SaveStats();
        _lastCounters = counters;
        _lastCancelled |= cancelled;
        _lastDiskFull = counters.DiskFull;
    });
}
