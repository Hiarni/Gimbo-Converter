using System.Diagnostics;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public sealed record ProgressInfo(
    double Fraction,
    double PositionSeconds,
    double DurationSeconds,
    double Fps,
    double Speed,
    long SizeBytes,
    double BitrateKbit,
    double ElapsedSeconds,
    double EtaSeconds,
    double EstimatedFinalBytes,
    bool Final);

public sealed class RunOptions
{
    public double DurationSeconds { get; init; }
    public double SourceFps { get; init; }

    public long SourceBytes { get; init; }
    public int EarlyAbortPercent { get; init; }
    public int EarlyAbortFromPercent { get; init; } = 10;

    public ProcessPriorityClass Priority { get; init; } = ProcessPriorityClass.Normal;
}

public sealed record RunResult(int ExitCode, bool Aborted, string AbortMessage, bool Cancelled, IReadOnlyList<string> ErrorLines)
{
    public bool Success => !Aborted && !Cancelled && ExitCode == 0;

    public bool Started { get; init; } = true;
}

public sealed class FfmpegRun
{
    private readonly string _ffmpeg;
    private readonly IReadOnlyList<string> _args;
    private readonly RunOptions _opt;
    private Process? _proc;
    private volatile bool _paused;
    private readonly Stopwatch _sw = new();

    public double ActiveSeconds => _sw.Elapsed.TotalSeconds;

    public FfmpegRun(string ffmpegPath, IReadOnlyList<string> args, RunOptions options)
    {
        _ffmpeg = ffmpegPath;
        _args = args;
        _opt = options;
    }

    public bool IsPaused => _paused;

    private readonly object _pauseLock = new();
    private bool _started;
    private bool _pendingPause;

    public bool Pause()
    {
        lock (_pauseLock)
        {
            if (_paused || _pendingPause) return false;
            if (!_started) { _pendingPause = true; return true; }
            if (_proc is null) return false;
            _paused = Native.Suspend(_proc);
            if (_paused) _sw.Stop();
            return _paused;
        }
    }

    public bool Resume()
    {
        lock (_pauseLock)
        {
            if (_pendingPause) { _pendingPause = false; return true; }
            if (_proc is null || !_paused) return false;
            if (Native.Resume(_proc)) { _paused = false; _sw.Start(); }
            return !_paused;
        }
    }

    public async Task<RunResult> RunAsync(IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var args = new List<string> { "-nostdin", "-v", "error", "-nostats", "-progress", "pipe:1" };
        args.AddRange(_args);
        var psi = ProcessRunner.CreateStartInfo(_ffmpeg, args);

        using var proc = new Process { StartInfo = psi };
        _proc = proc;
        try
        {
            proc.Start();
            Native.KillWithApp(proc);
            Native.SetPriority(proc, _opt.Priority);
        }
        catch (Exception ex)
        {
            return new RunResult(-1, false, "", false, [L.T("ffmpeg konnte nicht gestartet werden: ", "ffmpeg could not be started: ") + ex.Message]) { Started = false };
        }

        var errTask = proc.StandardError.ReadToEndAsync(CancellationToken.None);
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        _sw.Restart();
        lock (_pauseLock)
        {
            _started = true;
            if (_pendingPause)
            {
                _pendingPause = false;
                _paused = Native.Suspend(proc);
                if (_paused) _sw.Stop();
            }
        }
        var sw = _sw;
        var aborted = false;
        var abortMsg = "";
        var cancelled = false;

        using var reg = ct.Register(() =>
        {
            cancelled = true;
            if (_paused) Native.Resume(proc);
            ProcessRunner.TryKill(proc);
        });

        try
        {
            while (true)
            {
                var line = await proc.StandardOutput.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
                if (line is null) break;
                var k = line.IndexOf('=');
                if (k < 1) continue;
                var key = line[..k].Trim();
                values[key] = line[(k + 1)..].Trim();
                if (key != "progress") continue;

                var final = values[key] == "end";
                var info = Compute(values, sw.Elapsed.TotalSeconds, final);
                progress?.Report(info);

                if (!final && _opt.EarlyAbortPercent > 0 && _opt.SourceBytes > 0
                    && info.Fraction >= _opt.EarlyAbortFromPercent / 100d && info.EstimatedFinalBytes > 0
                    && info.EstimatedFinalBytes > _opt.SourceBytes * (_opt.EarlyAbortPercent / 100d))
                {
                    aborted = true;
                    var pctText = (info.EstimatedFinalBytes * 100d / _opt.SourceBytes).ToString("N0", Fmt.De);
                    var atText = (info.Fraction * 100).ToString("N0", Fmt.De);
                    abortMsg = L.T($"Prognose {Fmt.Size(info.EstimatedFinalBytes)} = {pctText} % der Quelle bei {atText} % Fortschritt, Grenze {_opt.EarlyAbortPercent} %",
                                   $"Forecast {Fmt.Size(info.EstimatedFinalBytes)} = {pctText} % of the source at {atText} % progress, limit {_opt.EarlyAbortPercent} %");
                    ProcessRunner.TryKill(proc);
                    break;
                }
            }
            await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            lock (_pauseLock)
            {
                _proc = null;
                _paused = false;
                _pendingPause = false;
            }
            _sw.Stop();
        }

        var errText = await errTask.ConfigureAwait(false);
        var errors = errText.Split('\n').Select(l => l.TrimEnd('\r')).Where(l => l.Trim().Length > 0).ToList();
        return new RunResult(proc.ExitCode, aborted, abortMsg, cancelled, errors);
    }

    private ProgressInfo Compute(Dictionary<string, string> v, double elapsed, bool final)
    {
        var t = Fmt.ParseDouble(v.GetValueOrDefault("out_time_us"), 0) / 1_000_000d;
        if (t < 0) t = 0;
        var frames = Fmt.ParseDouble(v.GetValueOrDefault("frame"), 0);
        if (_opt.SourceFps > 0 && frames > 0) t = Math.Max(t, frames / _opt.SourceFps);

        var dur = _opt.DurationSeconds;
        var frac = dur > 0 ? Math.Min(1.0, t / dur) : -1;
        if (final && frac > 0.99) frac = 1.0;

        var size = (long)Fmt.ParseDouble(v.GetValueOrDefault("total_size"), 0);
        var eta = frac > 0.005 && elapsed > 3 ? elapsed * (1 - frac) / frac : -1;
        if (final) eta = 0;
        var est = frac > 0.02 && size > 0 && !final ? size / frac : 0;
        return new ProgressInfo(
            Fraction: frac,
            PositionSeconds: t,
            DurationSeconds: dur,
            Fps: Fmt.ParseDouble(v.GetValueOrDefault("fps"), 0),
            Speed: elapsed > 1 && t > 0 ? t / elapsed : 0,
            SizeBytes: size,
            BitrateKbit: size > 0 && t > 1 ? size * 8d / t / 1000d : 0,
            ElapsedSeconds: elapsed,
            EtaSeconds: eta,
            EstimatedFinalBytes: est,
            Final: final);
    }
}
