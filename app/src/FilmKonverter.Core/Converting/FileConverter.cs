using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Converting;

public sealed class ConvertOptions
{
    public ConvertTarget Target { get; init; }
    public string? OutputDir { get; init; }
    public bool KeepStructure { get; init; } = true;
    public string Suffix { get; init; } = "";
    public int Parallel { get; init; } = 1;
    public System.Diagnostics.ProcessPriorityClass Priority { get; init; } = System.Diagnostics.ProcessPriorityClass.Normal;
}

public sealed record ConvertJob(int Id, string Source, string? BaseDir = null, MediaInfo? Media = null);

public sealed record ConvertResult(Outcome Outcome, string Message, string? OutputPath = null, long SourceBytes = 0, long OutputBytes = 0);

public interface IConvertObserver
{
    void Started(ConvertJob job, string outputPath);
    void Progress(ConvertJob job, ProgressInfo info);
    void Finished(ConvertJob job, ConvertResult result);
}

public sealed class FileConverter(FfmpegTools tools, ConvertCaps caps)
{
    private readonly List<FfmpegRun> _running = [];
    private readonly object _lock = new();
    private volatile bool _paused;

    public bool IsPaused => _paused;

    public void Pause()
    {
        lock (_lock)
        {
            _paused = true;
            foreach (var r in _running) r.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            _paused = false;
            foreach (var r in _running) r.Resume();
        }
    }

    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromMinutes(10);

    public static Dictionary<int, string> ReserveOutputs(IReadOnlyList<ConvertJob> jobs, ConvertOptions opt)
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new Dictionary<int, string>();
        foreach (var job in jobs)
        {
            var path = ConvertPlanner.OutputPath(job.Source, job.BaseDir, opt.OutputDir, opt.KeepStructure, opt.Target, opt.Suffix);
            var dir = Path.GetDirectoryName(path) ?? "";
            var name = Path.GetFileNameWithoutExtension(path);
            var ext = Path.GetExtension(path);
            for (var n = 2; !taken.Add(Path.GetFullPath(path)); n++) path = Path.Combine(dir, $"{name} ({n}){ext}");
            result[job.Id] = path;
        }
        return result;
    }

    public static bool IsTempName(string file) =>
        System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileName(file), @"\.[0-9a-f]{6}\.gimbo-tmp$|\.[0-9a-f]{6}\.part\.[A-Za-z0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    public static string Muxer(string extension) => extension.ToLowerInvariant() switch
    {
        ".mkv" => "matroska",
        ".m4a" => "ipod",
        ".mp3" => "mp3",
        ".flac" => "flac",
        _ => "mp4",
    };

    public async Task RunAsync(IReadOnlyList<ConvertJob> jobs, ConvertOptions opt, IConvertObserver observer, CancellationToken ct)
    {
        var outputs = ReserveOutputs(jobs, opt);
        var next = -1;
        var workers = Enumerable.Range(0, Math.Clamp(opt.Parallel, 1, 4)).Select(_ => Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                var i = Interlocked.Increment(ref next);
                if (i >= jobs.Count) break;
                var job = jobs[i];
                ConvertResult res;
                try { res = await ConvertAsync(job, opt, observer, ct, outputs[job.Id]).ConfigureAwait(false); }
                catch (OperationCanceledException) { res = new ConvertResult(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")); }
                catch (Exception ex) { res = new ConvertResult(Outcome.Error, ex.Message); }
                observer.Finished(job, res);
            }
        }, CancellationToken.None)).ToList();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    public async Task<ConvertResult> ConvertAsync(ConvertJob job, ConvertOptions opt, IConvertObserver? observer, CancellationToken ct, string? reservedOutput = null)
    {
        var src = job.Source;
        if (!File.Exists(src)) return new ConvertResult(Outcome.Error, L.T("Quelle nicht gefunden.", "Source not found."));
        var media = job.Media ?? await tools.ProbeAsync(src, ct).ConfigureAwait(false);
        if (media is null) return new ConvertResult(Outcome.Error, L.T("Datei nicht lesbar.", "File not readable."));
        var plan = ConvertPlanner.Plan(media, opt.Target, caps, src);
        if (!plan.Ok) return new ConvertResult(Outcome.Skipped, plan.Error!);

        var output = reservedOutput ?? ConvertPlanner.OutputPath(src, job.BaseDir, opt.OutputDir, opt.KeepStructure, opt.Target, opt.Suffix);
        if (File.Exists(output)) return new ConvertResult(Outcome.AlreadyOk, L.T("Gibt es schon, nichts überschrieben.", "Already exists, nothing overwritten."), output);
        var dir = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(dir);
        var temp = output + "." + Guid.NewGuid().ToString("N")[..6] + ".gimbo-tmp";
        var srcBytes = new FileInfo(src).Length;
        observer?.Started(job, output);
        try
        {
            TryDelete(temp);
            var args = new List<string> { "-hide_banner", "-y" };
            args.AddRange(plan.PreInput);
            args.AddRange(["-i", src]);
            args.AddRange(plan.Output);
            args.AddRange(["-f", Muxer(plan.Extension), temp]);
            var run = new FfmpegRun(tools.FfmpegPath, args, new RunOptions
            {
                DurationSeconds = media.DurationSeconds,
                SourceFps = plan.HasVideo ? media.Fps : 0,
                Priority = opt.Priority,
            });
            lock (_lock)
            {
                _running.Add(run);
                if (_paused) run.Pause();
            }
            RunResult res;
            using var jobCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var lastSign = DateTime.UtcNow;
            var stalled = false;
            var watch = Task.Run(async () =>
            {
                while (!jobCts.IsCancellationRequested)
                {
                    try { await Task.Delay(TimeSpan.FromSeconds(Math.Min(5, StallTimeout.TotalSeconds / 4)), jobCts.Token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { return; }
                    if (_paused || run.IsPaused) { lastSign = DateTime.UtcNow; continue; }
                    if (DateTime.UtcNow - lastSign > StallTimeout) { stalled = true; jobCts.Cancel(); return; }
                }
            }, CancellationToken.None);
            try
            {
                res = await run.RunAsync(new SyncProgress<ProgressInfo>(pi => { lastSign = DateTime.UtcNow; observer?.Progress(job, pi); }), jobCts.Token).ConfigureAwait(false);
            }
            finally
            {
                lock (_lock) _running.Remove(run);
                jobCts.Cancel();
                await watch.ConfigureAwait(false);
            }
            if (stalled && !ct.IsCancellationRequested)
            {
                TryDelete(temp);
                return new ConvertResult(Outcome.Error, L.T($"ffmpeg reagiert seit {StallTimeout.TotalMinutes:0} Minuten nicht mehr, abgebrochen.", $"ffmpeg has not responded for {StallTimeout.TotalMinutes:0} minutes, cancelled."));
            }
            if (res.Cancelled || ct.IsCancellationRequested) { TryDelete(temp); return new ConvertResult(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")); }
            if (!res.Success || !File.Exists(temp))
            {
                TryDelete(temp);
                var why = res.ErrorLines.LastOrDefault(l => !l.StartsWith("Conversion failed", StringComparison.OrdinalIgnoreCase)) ?? $"Exit-Code {res.ExitCode}";
                return new ConvertResult(Outcome.Error, L.T($"ffmpeg-Fehler: {why}", $"ffmpeg error: {why}"));
            }
            if (FfmpegErrors.ReadErrors(res.ErrorLines) is { Count: > 0 } read)
            {
                TryDelete(temp);
                return new ConvertResult(Outcome.Error, L.T($"Lesefehler in der Quelle: {read[0]}", $"Read error in the source: {read[0]}"));
            }

            var problem = await CheckAsync(temp, media, plan, ct).ConfigureAwait(false);
            if (problem is not null) { TryDelete(temp); return new ConvertResult(Outcome.Error, L.T($"Verworfen: {problem}", $"Discarded: {problem}")); }
            if (File.Exists(output)) { TryDelete(temp); return new ConvertResult(Outcome.AlreadyOk, L.T("Inzwischen vorhanden, nichts überschrieben.", "Appeared meanwhile, nothing overwritten."), output); }
            File.Move(temp, output);
            var outBytes = new FileInfo(output).Length;
            return new ConvertResult(Outcome.Encoded, $"{Fmt.Size(srcBytes)} » {Fmt.Size(outBytes)}", output, srcBytes, outBytes);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private async Task<string?> CheckAsync(string file, MediaInfo source, ConvertPlan plan, CancellationToken ct)
    {
        var o = await tools.ProbeAsync(file, ct).ConfigureAwait(false);
        if (o is null) return L.T("Ergebnis nicht lesbar", "result not readable");
        if (plan.HasVideo && o.Video is null) return L.T("Video fehlt", "video missing");
        if (o.Audio.Count() < plan.AudioCount) return L.T("Tonspuren fehlen", "audio tracks missing");
        if (LibraryRun.DurationMismatch(source.DurationSeconds, o.DurationSeconds) is not null)
            return L.T($"Laufzeit {Fmt.Time(o.DurationSeconds)} statt {Fmt.Time(source.DurationSeconds)}", $"duration {Fmt.Time(o.DurationSeconds)} instead of {Fmt.Time(source.DurationSeconds)}");
        if (plan.HasVideo)
        {
            var dec = await ProcessRunner.RunAsync(tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(file, false, FfmpegTools.NullTarget), TimeSpan.FromMinutes(5), ct).ConfigureAwait(false);
            if (dec.ExitCode != 0 || ArgsBuilder.DecodeErrors(dec.StdErr.Split('\n').Where(l => l.Trim().Length > 0)).Count > 0)
                return L.T("Dekodierprüfung fehlgeschlagen", "decoding check failed");
        }
        return null;
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
