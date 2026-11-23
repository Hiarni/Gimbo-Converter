using System.Collections.Concurrent;
using System.Diagnostics;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed class LibraryRun
{
    private readonly Profile _p;
    private readonly FfmpegTools _tools;
    private readonly IRunObserver _obs;
    private readonly ConcurrentDictionary<int, FfmpegRun> _active = new();
    private volatile bool _paused;
    private volatile bool _halt;

    public RunCounters Counters { get; } = new();

    public bool StopAfterCurrent { get; set; }

    public bool IsPaused => _paused;

    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.Normal;

    public TimeWindow? Window { get; init; }

    public int Parallel { get; init; } = 1;

    public IReadOnlyList<string>? Sources { get; init; }

    public ProbeCache? Cache { get; init; } = ProbeCache.Shared;

    public LibraryRun(Profile profile, FfmpegTools tools, IRunObserver observer)
    {
        _p = profile;
        _tools = tools;
        _obs = observer;
    }

    public bool Pause()
    {
        lock (_pauseLock)
        {
            _paused = true;
            foreach (var r in _active.Values) r.Pause();
        }
        return true;
    }

    public bool Resume()
    {
        lock (_pauseLock)
        {
            _paused = false;
            foreach (var r in _active.Values) r.Resume();
        }
        return true;
    }

    private readonly object _pauseLock = new();

    private int Limit => _p.EarlyAbortPercent > 0 ? _p.EarlyAbortPercent : 100;

    private int Workers => Math.Clamp(Parallel, 1, 4);

    public async Task RunAsync(CancellationToken ct)
    {
        var cancelled = false;
        var total = 0;
        try
        {
            if (!Directory.Exists(_p.InputDir)) throw new DirectoryNotFoundException(L.T($"Quellordner nicht erreichbar: {_p.InputDir}", $"Source folder not reachable: {_p.InputDir}"));
            if (string.IsNullOrWhiteSpace(_p.OutputDir)) throw new InvalidOperationException(L.T("Kein Zielordner eingestellt.", "No target folder set."));
            if (Scanner.IsInside(_p.InputDir, _p.OutputDir))
                throw new InvalidOperationException(L.T("Der Quellordner liegt im Zielordner (oder ist derselbe). Bitte getrennte Ordner wählen.", "The source folder is inside the target folder (or the same). Please choose separate folders."));
            Directory.CreateDirectory(_p.OutputDir);
            DataStore.Link(_p.OutputDir, _p.InputDir);
            await Task.Run(() => DataStore.Sync(_p.OutputDir), ct).ConfigureAwait(false);

            _obs.ScanStarted(QueueOnly ? L.T("Warteschlange", "queue") : Sources is null ? _p.InputDir : string.Join(", ", Sources.Select(s => Path.GetFileName(s.TrimEnd('\\', '/')))));
            _queueFilter = ScanFilter.FromProfile(_p);
            var scan = QueueOnly ? new ScanResult([], 0, 0, 0) : await Task.Run(BuildList, ct).ConfigureAwait(false);
            var files = scan.Files;
            total = files.Count;
            Counters.Total = files.Count;
            Counters.Filtered = scan.Filtered;
            Counters.FilterText = scan.FilterText;
            Counters.TooNew = scan.TooNew;
            _obs.ScanDone(files.Count);
            if (scan.Filtered > 0) _obs.Status(L.T($"{files.Count} Dateien gefunden, ausgelassen: {scan.FilterText}.", $"{files.Count} files found, skipped: {scan.FilterText}."));

            var next = -1;
            var started = 0;
            var anyCancelled = false;
            var listExhausted = false;
            var inList = new HashSet<string>(files, PathComparer);
            async Task Worker(int slot)
            {
                while (true)
                {
                    if (ct.IsCancellationRequested || StopAfterCurrent || _halt) return;
                    if (!await WaitForWindowAsync(slot, ct).ConfigureAwait(false)) return;
                    if (!await WaitWhilePausedAsync(ct).ConfigureAwait(false)) return;
                    if (StopAfterCurrent || _halt || ct.IsCancellationRequested) return;
                    if (!EnoughSpace(out var spaceText))
                    {
                        if (!_halt)
                        {
                            _halt = true;
                            Counters.DiskFull = true;
                            _obs.Status(spaceText);
                        }
                        return;
                    }
                    string? file = null;
                    string? queueId = null;
                    if (UseQueue || QueueOnly)
                    {
                        var q = NextQueueFile(false) ?? (QueueOnly ? NextQueueFile(true) : null);
                        if (q is { } got)
                        {
                            (file, queueId) = got;
                            if (!inList.Contains(file)) lock (_qLock) Counters.Total++;
                        }
                        else if (QueueOnly) { listExhausted = true; return; }
                    }
                    if (file is null)
                    {
                        var i = Interlocked.Increment(ref next);
                        if (i >= files.Count)
                        {
                            if (UseQueue && NextQueueFile(true) is { } late)
                            {
                                (file, queueId) = late;
                                if (!inList.Contains(file)) lock (_qLock) Counters.Total++;
                            }
                            else { listExhausted = true; return; }
                        }
                        else
                        {
                            lock (_qLock)
                                if (!_claimed.Add(files[i])) continue;
                            file = files[i];
                        }
                    }

                    long srcSize = 0;
                    try { srcSize = new FileInfo(file).Length; } catch { }
                    var number = Interlocked.Increment(ref started);
                    var start = new FileStart(number, Math.Max(number, Counters.Total), Path.GetRelativePath(_p.InputDir, file), file, srcSize, _p.EarlyAbortPercent, slot);
                    _obs.FileStarted(start);
                    if (queueId is not null) Info(L.T("Vorrang", "Priority"), L.T("aus der Warteschlange", "from the queue"), LineKind.Dim, slot);
                    var facts = new FileFacts();
                    bool chosen;
                    lock (_qLock) chosen = _chosen.Contains(file);
                    var (end, reason) = await ProcessFileAsync(file, ct, slot, facts, chosen).ConfigureAwait(false);
                    Counters.Count(end, reason);
                    WriteStats(start, end, reason, facts);
                    if (queueId is not null && end.Outcome is not (Outcome.Cancelled or Outcome.OtherPc)) QueueFileDone(queueId);
                    _obs.FileFinished(start, end);
                    if (end.Outcome == Outcome.Cancelled) { anyCancelled = true; return; }
                }
            }

            var n = UseQueue || QueueOnly ? Workers : Math.Min(Workers, Math.Max(1, files.Count));
            await Task.WhenAll(Enumerable.Range(0, n).Select(slot => Task.Run(() => Worker(slot)))).ConfigureAwait(false);
            cancelled = anyCancelled || _halt || ct.IsCancellationRequested || !listExhausted;
        }
        finally
        {
            Cache?.Save();
            _obs.RunFinished(Counters, cancelled || ct.IsCancellationRequested);
        }
    }

    // ---------------- Warteschlange ----------------

    public bool UseQueue { get; init; }

    public bool QueueOnly { get; init; }

    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private readonly object _qLock = new();
    private readonly HashSet<string> _claimed = new(PathComparer);
    private List<QueueEntry> _qEntries = [];
    private DateTime _qStamp = DateTime.MinValue;
    private DateTime _qReadAt = DateTime.MinValue;
    private readonly Dictionary<string, List<string>> _qFiles = [];
    private readonly Dictionary<string, (int Claimed, int Done)> _qProgress = [];
    private readonly HashSet<string> _qExhausted = [];
    private ScanFilter? _queueFilter;

    private readonly HashSet<string> _qKeep = [];

    private readonly HashSet<string> _chosen = new(PathComparer);

    private (string File, string Id)? NextQueueFile(bool forceRead)
    {
        bool read;
        lock (_qLock)
        {
            read = forceRead || (DateTime.UtcNow - _qReadAt).TotalSeconds >= 5;
            if (read) _qReadAt = DateTime.UtcNow;
        }
        if (read)
        {
            var stamp = WorkQueue.Stamp(_p.OutputDir);
            List<QueueEntry>? fresh = null;
            lock (_qLock) if (stamp != _qStamp) _qStamp = stamp; else stamp = default;
            if (stamp != default && WorkQueue.TryRead(_p.OutputDir, out var list)) fresh = list;
            else if (stamp != default) lock (_qLock) _qStamp = default;
            if (fresh is not null)
                lock (_qLock)
                {
                    _qEntries = fresh;
                    _qExhausted.RemoveWhere(id => _qEntries.All(e => e.Id != id));
                }
        }

        while (true)
        {
            QueueEntry? toExpand = null;
            var toRemove = new List<string>();
            (string, string)? result = null;
            lock (_qLock)
            {
                foreach (var e in _qEntries)
                {
                    if (_qExhausted.Contains(e.Id)) continue;
                    if (!_qFiles.TryGetValue(e.Id, out var fs)) { toExpand = e; break; }
                    foreach (var f in fs)
                    {
                        if (!_claimed.Add(f)) continue;
                        var pr = _qProgress.GetValueOrDefault(e.Id);
                        _qProgress[e.Id] = (pr.Claimed + 1, pr.Done);
                        result = (f, e.Id);
                        break;
                    }
                    if (result is not null) break;
                    _qExhausted.Add(e.Id);
                    if (ShouldRemoveQueueEntry(e.Id)) toRemove.Add(e.Id);
                }
            }
            foreach (var id in toRemove) WorkQueue.Remove(_p.OutputDir, id);
            if (result is not null) return result;
            if (toExpand is null) return null;
            var (files, keep) = ExpandQueueEntry(toExpand);
            lock (_qLock)
            {
                _qFiles[toExpand.Id] = files;
                if (keep) _qKeep.Add(toExpand.Id);
            }
        }
    }

    private (List<string> Files, bool Keep) ExpandQueueEntry(QueueEntry e)
    {
        try
        {
            if (!Directory.Exists(_p.InputDir)) return ([], true);
            var filter = _queueFilter ?? ScanFilter.FromProfile(_p);
            var path = e.Relative.Length == 0 ? _p.InputDir : Path.Combine(_p.InputDir, e.Relative);
            if (filter.ExcludeDir is { } ex && Scanner.IsInside(path, ex)) return ([], false);
            if (Directory.Exists(path))
            {
                var r = Scanner.Find(path, filter);
                return (r.Files, r.TooNew > 0);
            }
            if (File.Exists(path))
            {
                var exts = new HashSet<string>(_p.Extensions.Select(x => x.StartsWith('.') ? x : "." + x), StringComparer.OrdinalIgnoreCase);
                if (!exts.Contains(Path.GetExtension(path)) || Scanner.IsTemp(path)) return ([], false);
                if (Scanner.IsFresh(path, filter.SkipNewerThan)) return ([], true);
                var single = Path.GetFullPath(path);
                lock (_qLock) _chosen.Add(single);
                return ([single], false);
            }
            return ([], DateTime.UtcNow - e.AddedUtc < TimeSpan.FromMinutes(10));
        }
        catch
        {
            return ([], true);
        }
    }

    private void QueueFileDone(string id)
    {
        bool remove;
        lock (_qLock)
        {
            var pr = _qProgress.GetValueOrDefault(id);
            _qProgress[id] = (pr.Claimed, pr.Done + 1);
            remove = ShouldRemoveQueueEntry(id);
        }
        if (remove) WorkQueue.Remove(_p.OutputDir, id);
    }

    private bool ShouldRemoveQueueEntry(string id)
    {
        if (!_qExhausted.Contains(id) || _qKeep.Contains(id)) return false;
        var pr = _qProgress.GetValueOrDefault(id);
        if (pr.Done < pr.Claimed) return false;
        _qProgress.Remove(id);
        _qKeep.Add(id);
        return true;
    }

    public bool WriteStatsLog { get; init; } = true;

    private void WriteStats(FileStart start, FileEnd end, string? reason, FileFacts f)
    {
        if (!WriteStatsLog || end.Outcome is not (Outcome.Encoded or Outcome.Replaced or Outcome.Skipped or Outcome.Error)) return;
        if (end.Outcome == Outcome.Skipped && f.EncodeSeconds <= 0) return;
        var parts = start.RelativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        StatsLog.Append(_p.OutputDir, new StatsRecord
        {
            TimeUtc = DateTime.UtcNow,
            Machine = FileLock.ComputerName,
            Profile = _p.Name,
            Encoder = f.AudioOnly ? "copy" : FfmpegTools.EncoderName(_p.Encoder),
            File = start.RelativePath,
            Group = parts.Length > 1 ? parts[0] : Path.GetFileNameWithoutExtension(start.RelativePath),
            Outcome = end.Outcome.ToString(),
            Reason = end.Outcome is Outcome.Encoded or Outcome.Replaced ? "" : reason ?? end.Message,
            SourceBytes = start.SourceBytes,
            OutputBytes = end.Outcome is Outcome.Encoded or Outcome.Replaced ? f.OutputBytes : 0,
            GainBytes = end.GainBytes,
            DurationSeconds = f.DurationSeconds,
            EncodeSeconds = f.EncodeSeconds,
            SourceCodec = f.SourceCodec,
            SourceHeight = f.SourceHeight,
            OutputHeight = f.OutputHeight,
            AudioOnly = f.AudioOnly,
            Ssim = f.Ssim is { } q ? Math.Round(q.Average, 4) : null,
            SsimLow = f.SsimLow,
        });
    }

    private ScanResult BuildList()
    {
        var cache = Cache;
        Func<string, long, DateTime, double?>? rate = cache is null ? null : (path, size, mtime) =>
            cache.Get(path, size, mtime) is { DurationSeconds: > 0 } m ? size / m.DurationSeconds : null;
        var filter = ScanFilter.FromProfile(_p, rate);
        if (Sources is null) return Scanner.Find(_p.InputDir, filter);

        var exts = new HashSet<string>(_p.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        var seen = new HashSet<string>(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
        static string TargetKey(string f) => Path.Combine(Path.GetDirectoryName(f) ?? "", Path.GetFileNameWithoutExtension(f));
        int excluded = 0, small = 0, fresh = 0, dupes = 0;
        var outside = new List<string>();
        foreach (var raw in Sources)
        {
            var src = raw.TrimEnd('\\', '/');
            if (!Scanner.IsInside(src, _p.InputDir) || (filter.ExcludeDir is { } ex && Scanner.IsInside(src, ex)))
            {
                outside.Add(src);
                continue;
            }
            if (Directory.Exists(src))
            {
                var r = Scanner.Find(src, filter);
                excluded += r.Excluded; small += r.TooSmall; fresh += r.TooNew; dupes += r.Duplicates;
                foreach (var f in r.Files) if (seen.Add(TargetKey(f))) list.Add(f);
            }
            else if (File.Exists(src))
            {
                if (!exts.Contains(Path.GetExtension(src)) || Scanner.IsTemp(src)) { excluded++; continue; }
                if (Scanner.IsFresh(src, filter.SkipNewerThan)) { fresh++; continue; }
                var full = Path.GetFullPath(src);
                lock (_qLock) _chosen.Add(full);
                if (seen.Add(TargetKey(full))) list.Add(full); else dupes++;
            }
            else
            {
                outside.Add(src);
            }
        }
        if (outside.Count > 0)
        {
            var names = string.Join(", ", outside.Take(3).Select(Path.GetFileName)) + (outside.Count > 3 ? " ..." : "");
            if (list.Count == 0)
                throw new InvalidOperationException(L.T($"Die Auswahl liegt nicht im Quellordner des Profils ({_p.InputDir}) oder fehlt: {names}",
                                                        $"The selection is not inside the profile's source folder ({_p.InputDir}) or is missing: {names}"));
            _obs.Status(L.T($"{outside.Count} ausgelassen (nicht im Quellordner): {names}", $"{outside.Count} left out (not in the source folder): {names}"));
        }
        return new ScanResult(list, excluded, small, fresh, dupes);
    }

    private async Task<bool> WaitWhilePausedAsync(CancellationToken ct)
    {
        while (_paused)
        {
            if (StopAfterCurrent) return false;
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        return true;
    }

    private async Task<bool> WaitForWindowAsync(int slot, CancellationToken ct)
    {
        if (Window is null || Window.IsOpen(DateTime.Now)) return true;
        if (slot == 0) _obs.Status(L.T($"Wartet auf das Zeitfenster {Window} (Beginn {Window.NextOpen(DateTime.Now):dd.MM. HH:mm}).", $"Waiting for the time window {Window} (starts {Window.NextOpen(DateTime.Now):dd.MM. HH:mm})."));
        while (!Window.IsOpen(DateTime.Now))
        {
            if (StopAfterCurrent) return false;
            try { await Task.Delay(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return false; }
        }
        if (slot == 0) _obs.Status(L.T("Zeitfenster offen, es geht weiter.", "Time window open, continuing."));
        return true;
    }

    private async Task<RunResult> RunFfmpegAsync(int slot, FfmpegRun run, string stage, bool report, CancellationToken ct)
    {
        lock (_pauseLock)
        {
            _active[slot] = run;
            if (_paused) run.Pause();
        }
        try
        {
            return await run.RunAsync(report ? new SyncProgress<ProgressInfo>(pi => _obs.Progress(slot, stage, pi)) : null, ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_pauseLock) _active.TryRemove(slot, out _);
        }
    }

    private bool EnoughSpace(out string text)
    {
        text = "";
        if (_p.MinFreeSpaceGb <= 0) return true;
        var free = Native.FreeBytes(_p.OutputDir);
        if (free < 0 || free >= _p.MinFreeSpaceGb * 1024L * 1024L * 1024L) return true;
        text = L.T($"Im Ziel sind nur noch {Fmt.Size(free)} frei (Grenze {_p.MinFreeSpaceGb} GB). Lauf angehalten.", $"Only {Fmt.Size(free)} free in the target (limit {_p.MinFreeSpaceGb} GB). Run stopped.");
        return false;
    }

    private static readonly AsyncLocal<int> CurrentSlot = new();

    private void Info(string label, string text, LineKind kind = LineKind.Info) => _obs.Line(CurrentSlot.Value, new InfoLine(label, text, kind));

    private void Info(string label, string text, LineKind kind, int slot) => _obs.Line(slot, new InfoLine(label, text, kind));

    private sealed class FileState
    {
        public FileLock? Lock;
        public long SourceBytes = -1;
        public bool NotTheFile;
    }

    public async Task<(FileEnd End, string? SkipReason)> ProcessFileAsync(string source, CancellationToken ct, int slot = 0, FileFacts? facts = null, bool chosen = false)
    {
        facts ??= new FileFacts();
        CurrentSlot.Value = slot;
        var paths = new LibraryPaths(_p.InputDir, _p.OutputDir, source);
        var state = new FileState();
        try
        {
            var (end, reason) = await ProcessCoreAsync(source, paths, ct, slot, facts, chosen, state).ConfigureAwait(false);
            return (Remember(source, paths, state, end), reason);
        }
        finally
        {
            state.Lock?.Dispose();
        }
    }

    private FileEnd Remember(string source, LibraryPaths paths, FileState state, FileEnd end)
    {
        if (end.Outcome is Outcome.Encoded or Outcome.Replaced)
        {
            ErrorMarks.Clear(paths);
            return end;
        }
        if (end.Outcome != Outcome.Error || state.SourceBytes < 0 || state.NotTheFile) return end;
        bool reachable;
        try { reachable = File.Exists(source) && Directory.Exists(paths.TargetDir); } catch { reachable = false; }
        if (!reachable) return end;
        if (ErrorMarks.Add(paths, state.SourceBytes, end.Message) <= 0) return end;
        var n = ErrorMarks.Get(paths, state.SourceBytes)?.OwnCount ?? 1;
        if (n < ErrorMarks.MaxFailures)
        {
            Info(L.T("Fehlversuch", "Failed attempt"), L.T($"{n} von {ErrorMarks.MaxFailures} vermerkt, der nächste Lauf versucht es noch einmal.", $"{n} of {ErrorMarks.MaxFailures} noted, the next run tries once more."), LineKind.Warn);
            return end;
        }
        return end with
        {
            Message = end.Message + L.T($" {FailedText(n)}: wird nicht mehr von selbst versucht, steht unter Bibliothek → Probleme.", $" {FailedText(n)}: no longer tried automatically, listed under Library → Problems."),
        };
    }

    private static string FailedText(int n) => n == 2 ? L.T("Zweimal fehlgeschlagen", "Failed twice") : L.T($"{n}-mal fehlgeschlagen", $"Failed {n} times");

    private async Task<(FileEnd End, string? SkipReason)> ProcessCoreAsync(string source, LibraryPaths paths, CancellationToken ct, int slot, FileFacts facts, bool chosen, FileState state)
    {
        var tag = _p.MarkTag;
        FileLock? fileLock = null;
        try
        {
            if (!File.Exists(source))
                return (new FileEnd(Outcome.Error, L.T("Datei nicht gefunden - während des Laufs umbenannt oder verschoben?", "File not found - renamed or moved during the run?")), null);
            var srcBytes = new FileInfo(source).Length;
            state.SourceBytes = srcBytes;

            try { Directory.CreateDirectory(paths.TargetDir); }
            catch (Exception ex)
            {
                state.NotTheFile = true;
                return (new FileEnd(Outcome.Error, L.T("Zielordner nicht erreichbar: ", "Target folder not reachable: ") + ex.Message), null);
            }

            if (chosen && ErrorMarks.Clear(paths) > 0)
                Info(L.T("Hinweis", "Note"), L.T("Gezielt gewählt: frühere Fehlversuche zählen nicht mehr.", "Chosen explicitly: earlier failed attempts no longer count."), LineKind.Dim);
            (FileEnd, string?)? FailedTooOften() => ErrorMarks.Get(paths, srcBytes) is { Blocks: true } mark
                ? (new FileEnd(Outcome.Skipped, L.T($"{FailedText(mark.OwnCount)}, steht unter Bibliothek → Probleme. Zuletzt: {mark.Reason}", $"{FailedText(mark.OwnCount)}, listed under Library → Problems. Last: {mark.Reason}")), "fehlgeschlagen")
                : null;

            var outputExists = File.Exists(paths.Output);
            if (outputExists)
            {
                if (!_p.CheckExisting)
                    return (new FileEnd(Outcome.AlreadyOk, L.T("Zieldatei existiert bereits.", "Target file already exists.")), null);
                var outBytesFast = new FileInfo(paths.Output).Length;
                if (MarkList.IsOk(paths, tag, srcBytes, outBytesFast))
                {
                    CopySidecars(source, paths);
                    return (new FileEnd(Outcome.AlreadyOk, L.T("Vorhandene Datei erfüllt die Vorgaben, bereits geprüft.", "Existing file meets the requirements, already checked.")), null);
                }
            }

            if (!outputExists && FailedTooOften() is { } blockedEarly) return blockedEarly;

            fileLock = state.Lock = FileLock.TryAcquire(paths.Lock, _p.LockStaleHours, out var owner);
            if (fileLock is null)
                return (new FileEnd(Outcome.OtherPc, L.T($"bei {owner} in Arbeit", $"in progress on {owner}")), null);

            outputExists = File.Exists(paths.Output);
            if (outputExists && !_p.CheckExisting)
                return (new FileEnd(Outcome.AlreadyOk, L.T("Zieldatei existiert bereits.", "Target file already exists.")), null);
            if (outputExists && MarkList.IsOk(paths, tag, srcBytes, new FileInfo(paths.Output).Length))
            {
                CopySidecars(source, paths);
                return (new FileEnd(Outcome.AlreadyOk, L.T("Vorhandene Datei erfüllt die Vorgaben, bereits geprüft.", "Existing file meets the requirements, already checked.")), null);
            }

            var marked = _p.UseMarkList && MarkList.IsMarked(paths, tag);
            var replace = false;
            var existingBroken = false;
            long oldBytes = 0;

            if (outputExists)
            {
                if (marked)
                    return (new FileEnd(Outcome.AlreadyOk, L.T("Vorhanden. Neukodierung schon versucht, laut Ausnahmeliste nicht kleiner.", "Exists. Re-encoding already tried, not smaller according to the skip list.")), null);

                oldBytes = new FileInfo(paths.Output).Length;
                var (ok, text, unreadable) = await CheckExistingAsync(source, paths.Output, oldBytes, srcBytes, ct).ConfigureAwait(false);
                existingBroken = unreadable;
                Info(L.T("Vorhanden", "Existing"), text, ok ? LineKind.Ok : LineKind.Warn);
                if (ok)
                {
                    MarkList.MarkOk(paths, tag, srcBytes, oldBytes);
                    ErrorMarks.Clear(paths);
                    CopySidecars(source, paths);
                    return (new FileEnd(Outcome.AlreadyOk, L.T("Vorhandene Datei erfüllt die Vorgaben.", "Existing file meets the requirements.")), null);
                }
                replace = true;
            }
            else if (marked)
            {
                return (new FileEnd(Outcome.Skipped, L.T("Ausnahmeliste: schon versucht, nicht klein genug.", "Skip list: already tried, not small enough.")), "Merkliste");
            }

            if (FailedTooOften() is { } blocked) return blocked;

            var media = await _tools.ProbeAsync(source, ct).ConfigureAwait(false);
            if (media is not null)
            {
                Cache?.Put(source, media);
                facts.DurationSeconds = media.DurationSeconds;
                facts.SourceCodec = media.Video?.CodecName ?? "";
                facts.SourceHeight = media.Video is { } sv0 ? Profile.ClassHeight(sv0.DisplayWidth, sv0.DisplayHeight) : 0;
            }
            if (media is null) return (new FileEnd(Outcome.Error, L.T("Quelle nicht lesbar (ffprobe).", "Source not readable (ffprobe).")), null);
            Info(L.T("Quelle", "Source"), FileCard.Source(media, srcBytes));
            if (FileCard.Rate(media) is { Length: > 0 } rate) Info(L.T("Datenrate", "Bitrate"), rate, LineKind.Dim);

            var decision = Planner.Decide(media, _p);
            if (!decision.Encode)
            {
                if (decision.Skip == SkipReason.NoVideo) return (new FileEnd(Outcome.Error, decision.Message), null);
                return (new FileEnd(Outcome.Skipped, decision.Message), SkipText(decision.Skip));
            }
            var plan = decision.Plan!;
            if (!plan.VideoCopy && plan.Video.HasDolbyVision && !_tools.CanStripDolbyVision)
                return (new FileEnd(Outcome.Skipped, L.T($"Dolby Vision: mit ffmpeg {_tools.Version} nicht sauber umwandelbar (die DV-Kennung bliebe ohne DV-Daten stehen). Das integrierte ffmpeg kann es.",
                    $"Dolby Vision: cannot be converted cleanly with ffmpeg {_tools.Version} (the DV flag would remain without DV data). The bundled ffmpeg can do it.")), "Dolby Vision");
            facts.AudioOnly = plan.VideoCopy;
            if (!plan.VideoCopy && media.Hdr is HdrKind.Pq or HdrKind.DolbyVision && _tools.Major is > 0 and < 8)
                Info("HDR", L.T($"ffmpeg {_tools.Version} übernimmt die HDR-Helligkeitsangaben (Mastering, MaxCLL) nicht zuverlässig ins neue Video: das Bild bleibt HDR, der Fernseher stellt die Helligkeit dann aber nach eigenen Annahmen ein. Das integrierte ffmpeg übernimmt sie.",
                    $"ffmpeg {_tools.Version} does not reliably carry the HDR brightness metadata (mastering, MaxCLL) into the new video: the picture stays HDR, but the TV then sets brightness by its own assumptions. The bundled ffmpeg carries it over."), LineKind.Warn, slot);
            var ep = _p.AdjustedFor(plan.Video.DisplayWidth, plan.Video.DisplayHeight);
            var resClass = LibraryAnalyzer.ResolutionClass(Profile.ClassHeight(plan.Video.DisplayWidth, plan.Video.DisplayHeight));
            if (plan.VideoCopy)
                Info(L.T("Ziel", "Target"), L.T($"nur Ton · {decision.Message} Video wird unverändert kopiert.", $"audio only · {decision.Message} Video is copied unchanged."));
            else
            {
                var (ow, oh) = plan.OutputSize(ep);
                Info(L.T("Ziel", "Target"), FileCard.Target(plan.CodecProfile, plan.BitDepth,
                    ep.QualityText + (ep != _p ? L.T($" (angepasst für {resClass})", $" (adjusted for {resClass})") : ""),
                    plan.Video.DisplayWidth, plan.Video.DisplayHeight, ow, oh));
            }
            Info(L.T("Ton", "Audio"), FileCard.Audio(plan.Audio), plan.Audio.Changed ? LineKind.Info : LineKind.Dim);
            if (FileCard.Subtitles(plan.Subs) is { Length: > 0 } subs) Info(L.T("Untertitel", "Subtitles"), subs, plan.Subs.Changed ? LineKind.Info : LineKind.Dim);
            if (plan.AttachmentIndexes.Count > 0) Info(L.T("Anhänge", "Attachments"), L.T($"{plan.AttachmentIndexes.Count} übernommen (Schriftarten, Bilder)", $"{plan.AttachmentIndexes.Count} kept (fonts, images)"), LineKind.Dim);

            if (Scanner.StillBeingWritten(source))
                return (new FileEnd(Outcome.Skipped, L.T("Zu neu: die Quelle wird gerade noch geschrieben oder kopiert. Beim nächsten Lauf.", "Too new: the source is still being written or copied. Next run.")), "zu neu");

            TryDelete(paths.Temp);
            var args = ArgsBuilder.BuildEncode(ep, plan, source, paths.Temp);
            var run = new FfmpegRun(_tools.FfmpegPath, args, new RunOptions
            {
                DurationSeconds = media.DurationSeconds,
                SourceFps = plan.OutputFps > 0 ? plan.OutputFps : media.Fps,
                SourceBytes = srcBytes,
                EarlyAbortPercent = plan.VideoCopy ? 0 : _p.EarlyAbortPercent,
                EarlyAbortFromPercent = _p.EarlyAbortFromPercent,
                Priority = Priority,
            });
            var res = await RunFfmpegAsync(slot, run, "Encoding", true, ct).ConfigureAwait(false);
            Counters.AddEncodeSeconds(run.ActiveSeconds);
            facts.EncodeSeconds = run.ActiveSeconds;

            if (res.Cancelled) { TryDelete(paths.Temp); return (new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")), null); }
            long nowBytes;
            try { nowBytes = new FileInfo(source).Length; } catch { nowBytes = srcBytes; }
            if (nowBytes != srcBytes)
            {
                TryDelete(paths.Temp);
                return (new FileEnd(Outcome.Skipped, L.T("Quelle hat sich während der Umwandlung geändert (wird wohl noch kopiert). Beim nächsten Lauf.", "Source changed during conversion (probably still being copied). Next run.")), "Quelle geändert");
            }
            if (res.Aborted)
            {
                TryDelete(paths.Temp);
                Info(L.T("Abbruch", "Aborted"), res.AbortMessage, LineKind.Warn);
                if (_p.UseMarkList) MarkList.Mark(paths, tag, "Fruehabbruch", ep.QualityText, Limit);
                return (new FileEnd(Outcome.Skipped, replace ? L.T("Früh abgebrochen. Vorhandene Datei bleibt erhalten.", "Aborted early. The existing file is kept.") : L.T("Früh abgebrochen. Wäre nicht klein genug geworden.", "Aborted early. Would not be small enough.")), "Frühabbruch");
            }
            if (res.ExitCode != 0)
            {
                TryDelete(paths.Temp);
                ShowErrors(res.ErrorLines, true);
                if (FfmpegErrors.IsDiskFull(res.ErrorLines) || TargetFull())
                {
                    state.NotTheFile = true;
                    return (new FileEnd(Outcome.Error, L.T("Kodieren abgebrochen: im Ziel ist kein Platz mehr.", "Encoding stopped: no space left in the target.")), null);
                }
                return (new FileEnd(Outcome.Error, L.T($"Kodieren fehlgeschlagen (Exit-Code {res.ExitCode}).", $"Encoding failed (exit code {res.ExitCode}).")), null);
            }
            if (FfmpegErrors.ReadErrors(res.ErrorLines).Count > 0)
            {
                TryDelete(paths.Temp);
                ShowErrors(res.ErrorLines, true);
                return (new FileEnd(Outcome.Error, L.T("Lesefehler beim Kodieren (Netzwerk oder Datenträger?): das Ergebnis wäre unvollständig. Verworfen.", "Read error while encoding (network or disk?): the result would be incomplete. Discarded.")), null);
            }
            ShowErrors(res.ErrorLines, false);
            if (!File.Exists(paths.Temp)) return (new FileEnd(Outcome.Error, L.T("Temp-Datei fehlt.", "Temp file missing.")), null);

            var outInfo = await _tools.ProbeAsync(paths.Temp, ct).ConfigureAwait(false);
            var check = CheckOutput(outInfo, plan, media);
            if (outInfo?.Video is { } ov)
            {
                Info(L.T("Ausgabe", "Output"), FileCard.Output(outInfo, 0));
                facts.OutputHeight = ov.DisplayHeight;
            }
            if (check is not null) { TryDelete(paths.Temp); return (new FileEnd(Outcome.Error, check), null); }

            var dec = new FfmpegRun(_tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(paths.Temp, _p.FullDecodeTest, FfmpegTools.NullTarget),
                new RunOptions { DurationSeconds = media.DurationSeconds, SourceFps = plan.OutputFps > 0 ? plan.OutputFps : media.Fps, Priority = Priority });
            if (!await WaitWhilePausedAsync(ct).ConfigureAwait(false) && ct.IsCancellationRequested) { TryDelete(paths.Temp); return (new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")), null); }
            var dres = await RunFfmpegAsync(slot, dec, "Decode", _p.FullDecodeTest, ct).ConfigureAwait(false);
            if (dres.Cancelled) { TryDelete(paths.Temp); return (new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")), null); }
            var decodeErrors = ArgsBuilder.DecodeErrors(dres.ErrorLines);
            if (dres.ExitCode != 0 || decodeErrors.Count > 0)
            {
                ShowErrors(dres.ExitCode != 0 && decodeErrors.Count == 0 ? dres.ErrorLines : decodeErrors, true);
                TryDelete(paths.Temp);
                return (new FileEnd(Outcome.Error, L.T("Prüfung: Datei ist beschädigt (Fehler beim Dekodieren).", "Check: file is damaged (decoding errors).")), null);
            }
            Info("Test", _p.FullDecodeTest ? L.T("vollständig dekodiert, keine Fehler", "fully decoded, no errors") : L.T("Anfang dekodiert, keine Fehler", "beginning decoded, no errors"), LineKind.Ok);

            if (_p.SsimCheck && !plan.VideoCopy && outInfo?.Video is { } sv)
            {
                SsimResult? q = null;
                try
                {
                    q = await QualityCheck.MeasureAsync(_tools, source, paths.Temp, plan, sv.Width, sv.Height, 0, outInfo.DurationSeconds > 0 ? outInfo.DurationSeconds : media.DurationSeconds, _p.SsimSamples, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Info(L.T("Qualität", "Quality"), ex.Message, LineKind.Dim);
                }
                if (q is null) Info(L.T("Qualität", "Quality"), L.T("SSIM nicht messbar", "SSIM not measurable"), LineKind.Dim);
                else
                {
                    facts.Ssim = q;
                    facts.SsimLow = q.Average < _p.SsimMin;
                    Info(L.T("Qualität", "Quality"), q.Text + (facts.SsimLow ? L.T($" · unter {_p.SsimMin.ToString("0.00", Fmt.De)}, bitte ansehen", $" · below {_p.SsimMin.ToString("0.00", Fmt.De)}, please check") : ""),
                        facts.SsimLow ? LineKind.Warn : LineKind.Ok);
                }
            }

            var newBytes = new FileInfo(paths.Temp).Length;
            var change = (newBytes - srcBytes) * 100d / srcBytes;
            var sizeText = FileCard.Size(srcBytes, newBytes, replace ? oldBytes : null);
            Info(L.T("Größe", "Size"), sizeText, change < 0 ? LineKind.Ok : LineKind.Warn);

            if (plan.VideoCopy && newBytes >= srcBytes)
                Info(L.T("Hinweis", "Note"), L.T("Nicht kleiner als die Quelle, wird trotzdem übernommen: hier geht es um Ton, den Fernseher direkt abspielen, nicht um Platz.",
                                                   "Not smaller than the source, kept anyway: this is about audio TVs play directly, not about space."), LineKind.Dim);
            if (srcBytes <= newBytes && !plan.VideoCopy)
            {
                TryDelete(paths.Temp);
                if (_p.UseMarkList) MarkList.Mark(paths, tag, "nicht kleiner als Quelle", ep.QualityText, Limit);
                return (new FileEnd(Outcome.Skipped, L.T("Neue Datei gleich groß oder größer als die Quelle.", "New file is as large as or larger than the source.")), "nicht kleiner");
            }
            if (Limit < 100 && newBytes * 100d / srcBytes > Limit && !existingBroken && !plan.VideoCopy)
            {
                TryDelete(paths.Temp);
                if (_p.UseMarkList) MarkList.Mark(paths, tag, "ueber der Grenze", ep.QualityText, Limit);
                return (new FileEnd(Outcome.Skipped, replace ? L.T("Über der Grenze. Vorhandene Datei bleibt erhalten.", "Above the limit. The existing file is kept.") : L.T($"Neue Datei über {Limit} % der Quelle, verworfen.", $"New file above {Limit} % of the source, discarded.")), "über Grenze");
            }
            if (replace && !existingBroken && newBytes >= oldBytes && !plan.VideoCopy)
            {
                TryDelete(paths.Temp);
                if (_p.UseMarkList) MarkList.Mark(paths, tag, "nicht kleiner als vorhandene Datei", ep.QualityText, Limit);
                return (new FileEnd(Outcome.Skipped, L.T("Neue Datei nicht kleiner als die vorhandene. Vorhandene bleibt erhalten.", "New file not smaller than the existing one. The existing file is kept.")), "nicht kleiner");
            }

            if (fileLock is not null && !fileLock.StillOwned())
            {
                return (new FileEnd(Outcome.OtherPc, L.T("Sperre wurde inzwischen von einem anderen PC übernommen. Ergebnis verworfen.", "The lock was taken over by another PC meanwhile. Result discarded.")), null);
            }
            try { File.Move(paths.Temp, paths.Output, overwrite: true); }
            catch (Exception ex)
            {
                TryDelete(paths.Temp);
                state.NotTheFile = FfmpegErrors.IsDiskFull([ex.Message]);
                return (new FileEnd(Outcome.Error, L.T("Verschieben fehlgeschlagen: ", "Moving failed: ") + ex.Message), null);
            }

            if (_p.PreserveDates)
            {
                try { File.SetLastWriteTimeUtc(paths.Output, File.GetLastWriteTimeUtc(source)); } catch { }
            }
            CopySidecars(source, paths);
            if (_p.FullDecodeTest) MarkList.MarkVerified(paths.OutputRoot, paths.Output, newBytes, "beim Kodieren");
            if (newBytes * 100d / srcBytes <= Limit || plan.VideoCopy) MarkList.MarkOk(paths, tag, srcBytes, newBytes);

            var gain = (replace ? oldBytes : srcBytes) - newBytes;
            if (plan.VideoCopy) gain = Math.Max(0, gain);
            facts.OutputBytes = newBytes;
            var msg = paths.RelativeOutput + (facts.SsimLow ? L.T(" · Qualität auffällig (SSIM)", " · quality looks low (SSIM)") : plan.VideoCopy ? L.T(" · nur Ton", " · audio only") : "");
            return replace
                ? (new FileEnd(Outcome.Replaced, msg, gain), null)
                : (new FileEnd(Outcome.Encoded, msg, gain), null);
        }
        catch (OperationCanceledException)
        {
            if (fileLock is not null) TryDelete(paths.Temp);
            return (new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")), null);
        }
        catch (Exception ex)
        {
            if (fileLock is not null) TryDelete(paths.Temp);
            if (FfmpegErrors.IsDiskFull([ex.Message])) state.NotTheFile = true;
            return (new FileEnd(Outcome.Error, ex.Message), null);
        }
    }

    private bool TargetFull()
    {
        var free = Native.FreeBytes(_p.OutputDir);
        return free >= 0 && free < 64L * 1024 * 1024;
    }

    private void CopySidecars(string source, LibraryPaths paths)
    {
        if (!_p.CopySidecars) return;
        int n;
        try { n = Sidecars.Copy(source, paths.TargetDir); }
        catch (Exception ex) { Info(L.T("Begleitdateien", "Companion files"), ex.Message, LineKind.Warn); return; }
        if (n > 0) Info(L.T("Begleitdateien", "Companion files"), L.T($"{n} kopiert (Untertitel, NFO, Bilder)", $"{n} copied (subtitles, NFO, images)"), LineKind.Ok);
    }

    private async Task<(bool Ok, string Text, bool Unreadable)> CheckExistingAsync(string source, string output, long outBytes, long srcBytes, CancellationToken ct)
    {
        var why = new List<string>();
        var pct = srcBytes > 0 ? outBytes * 100d / srcBytes : 0;
        var text = L.T($"{Fmt.Size(outBytes)} = {pct.ToString("N0", Fmt.De)} % der Quelle", $"{Fmt.Size(outBytes)} = {pct.ToString("N0", Fmt.De)} % of the source");
        if (pct > Limit || (Limit >= 100 && pct >= 100)) why.Add(L.T($"über {Limit} %", $"above {Limit} %"));
        var info = await _tools.ProbeAsync(output, ct).ConfigureAwait(false);
        if (info?.Video is null)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
            info = await _tools.ProbeAsync(output, ct).ConfigureAwait(false);
        }
        var v = info?.Video;
        if (v is null || v.Width <= 0 || v.Height <= 0)
        {
            why.Add(L.T("Video nicht lesbar", "video not readable"));
            return (false, text + L.T("   » neu kodieren: ", "   » re-encode: ") + string.Join(", ", why), true);
        }
        var unreadable = false;
        {
            text += $" · {v.Width}x{v.Height}";
            if (_p.LimitsResolution && (v.Width > _p.MaxWidth || v.Height > _p.MaxHeight)) why.Add(L.T($"größer als {_p.MaxWidth}x{_p.MaxHeight}", $"larger than {_p.MaxWidth}x{_p.MaxHeight}"));
            try
            {
                var srcInfo = Cache is not null ? await Cache.GetOrProbeAsync(_tools, source, ct).ConfigureAwait(false) : await _tools.ProbeAsync(source, ct).ConfigureAwait(false);
                if (Cache is not null && srcInfo is not null && DurationMismatch(srcInfo, info!) is not null
                    && await _tools.ProbeAsync(source, ct).ConfigureAwait(false) is { } fresh)
                {
                    Cache.Put(source, fresh);
                    srcInfo = fresh;
                }
                if (srcInfo is not null && DurationMismatch(srcInfo, info!) is not null)
                    why.Add(L.T($"Laufzeit {Fmt.Time(info!.DurationSeconds)} statt {Fmt.Time(srcInfo.DurationSeconds)}", $"duration {Fmt.Time(info.DurationSeconds)} instead of {Fmt.Time(srcInfo.DurationSeconds)}"));
            }
            catch (Exception ex) when (ex is not OperationCanceledException) { }
        }
        return why.Count == 0 ? (true, text + "   » ok", false) : (false, text + L.T("   » neu kodieren: ", "   » re-encode: ") + string.Join(", ", why), unreadable);
    }

    public static string? DurationMismatch(double source, double output)
    {
        if (source <= 0 || output <= 0) return null;
        var tol = Math.Max(3, source * 0.015);
        return Math.Abs(source - output) > tol ? MismatchText(source, output) : null;
    }

    public static string? DurationMismatch(MediaInfo source, MediaInfo output)
    {
        if (source.DurationSeconds <= 0 || output.DurationSeconds <= 0) return null;
        return DurationCheck.Matches(source, output.DurationSeconds, DurationCheck.EncodeTolerance(source)) ? null : MismatchText(source.DurationSeconds, output.DurationSeconds);
    }

    private static string MismatchText(double source, double output) =>
        L.T($"Laufzeit passt nicht: Quelle {Fmt.Time(source)}, Ausgabe {Fmt.Time(output)} (abgeschnitten?).", $"Duration does not match: source {Fmt.Time(source)}, output {Fmt.Time(output)} (truncated?).");

    private string? CheckOutput(MediaInfo? o, EncodePlan plan, MediaInfo src)
    {
        var v = o?.Video;
        if (o is null || v is null) return L.T("Ausgabe nicht lesbar.", "Output not readable.");
        if (plan.VideoCopy)
        {
            if (!v.CodecName.Equals(src.Video?.CodecName, StringComparison.OrdinalIgnoreCase)) return L.T($"Video wurde verändert ({v.CodecName}).", $"Video was changed ({v.CodecName}).");
            if (v.Width != src.Video!.Width || v.Height != src.Video.Height) return L.T("Videoauflösung hat sich geändert.", "Video resolution changed.");
        }
        else if (!v.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase)) return L.T($"Ausgabe ist nicht HEVC ({v.CodecName}).", $"Output is not HEVC ({v.CodecName}).");
        if (v.Width <= 0 || v.Height <= 0) return L.T("Ausgabeauflösung fehlt.", "Output resolution missing.");
        if (plan.VideoCopy) goto Tracks;
        if (_p.LimitsResolution && v.Width > _p.MaxWidth) return L.T($"Ausgabe breiter als {_p.MaxWidth}.", $"Output wider than {_p.MaxWidth}.");
        if (_p.LimitsResolution && v.Height > _p.MaxHeight) return L.T($"Ausgabe höher als {_p.MaxHeight}.", $"Output taller than {_p.MaxHeight}.");
        var pix = v.PixFmt.ToLowerInvariant();
        var okPix = plan.BitDepth == 10 ? pix is "yuv420p10le" or "p010le" : pix is "yuv420p" or "nv12";
        if (!okPix) return L.T($"Falsches Pixelformat in der Ausgabe: {v.PixFmt}.", $"Wrong pixel format in the output: {v.PixFmt}.");
        Tracks:
        if (DurationMismatch(src, o) is { } dm) return dm;
        if (o.Audio.Count() < plan.Audio.Tracks.Count) return L.T($"Tonspuren fehlen in der Ausgabe ({o.Audio.Count()} statt {plan.Audio.Tracks.Count}).", $"Audio tracks missing in the output ({o.Audio.Count()} instead of {plan.Audio.Tracks.Count}).");
        if (o.Subtitles.Count() < plan.Subs.Tracks.Count) return L.T($"Untertitel fehlen in der Ausgabe ({o.Subtitles.Count()} statt {plan.Subs.Tracks.Count}).", $"Subtitles missing in the output ({o.Subtitles.Count()} instead of {plan.Subs.Tracks.Count}).");
        return null;
    }

    private void ShowErrors(IReadOnlyList<string> lines, bool red)
    {
        if (lines.Count == 0) return;
        var kind = red ? LineKind.Error : LineKind.Warn;
        if (lines.Count > 15) Info("ffmpeg", L.T($"... {lines.Count - 15} weitere Zeilen ausgeblendet", $"... {lines.Count - 15} more lines hidden"), LineKind.Dim);
        foreach (var l in lines.TakeLast(15)) Info("ffmpeg", l, kind);
    }

    private static string SkipText(SkipReason r) => r switch
    {
        SkipReason.Av1 => "bereits AV1",
        SkipReason.Hevc => "bereits HEVC",
        SkipReason.Hdr => "HDR",
        SkipReason.PixelFormat => "Pixelformat",
        SkipReason.DolbyVision => "Dolby Vision 5",
        SkipReason.RateTooLow or SkipReason.RateUnknown => "Datenrate",
        _ => r.ToString(),
    };

    private static void TryDelete(string file)
    {
        try { if (File.Exists(file)) File.Delete(file); } catch { }
    }
}

public sealed class FileFacts
{
    public long OutputBytes { get; set; }
    public double DurationSeconds { get; set; }
    public double EncodeSeconds { get; set; }
    public string SourceCodec { get; set; } = "";
    public int SourceHeight { get; set; }
    public int OutputHeight { get; set; }
    public bool AudioOnly { get; set; }
    public SsimResult? Ssim { get; set; }
    public bool SsimLow { get; set; }
}
