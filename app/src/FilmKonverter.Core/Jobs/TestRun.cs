using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed record TestRow(string Clip, long SizeBytes, double? RatioToSource, long? ProjectedFullBytes, bool IsReference)
{
    public string File { get; init; } = "";
    public double? Ssim { get; init; }
}

public sealed class TestRun(Profile profile, FfmpegTools tools, IRunObserver observer)
{
    private FfmpegRun? _current;

    public double ClipStart { get; private set; }
    public double ClipLength { get; private set; }

    public bool Pause() => _current?.Pause() ?? false;
    public bool Resume() => _current?.Resume() ?? false;

    public string TestFolder(string source) =>
        Path.Combine(DataStore.TestClips(profile.OutputDir), Path.GetFileNameWithoutExtension(source));

    public async Task<List<TestRow>> RunAsync(string source, CancellationToken ct)
    {
        var rows = new List<TestRow>();
        var start = new FileStart(1, 1, Path.GetFileName(source), source);
        observer.FileStarted(start);
        var cancelled = false;
        try
        {
            var media = await tools.ProbeAsync(source, ct) ?? throw new InvalidOperationException(L.T("Datei nicht lesbar.", "File not readable."));
            var decision = Planner.Decide(media, profile);
            if (decision.Plan is { VideoCopy: true })
            {
                observer.FileFinished(start, new FileEnd(Outcome.Skipped, L.T($"{decision.Message} Hier würde nur der Ton umgewandelt, das Video bleibt: nichts zu testen.", $"{decision.Message} Only the audio would be converted here, the video stays: nothing to test.")));
                return rows;
            }
            if (!decision.Encode)
            {
                observer.FileFinished(start, new FileEnd(Outcome.Skipped, decision.Message));
                return rows;
            }

            var dir = TestFolder(source);
            Directory.CreateDirectory(dir);
            DataStore.CleanTestClips(dir, TimeSpan.FromDays(3));
            double startSec = profile.TestStartSeconds;
            double len = profile.TestLengthSeconds;
            if (media.DurationSeconds > 0 && startSec + len > media.DurationSeconds) startSec = Math.Floor(media.DurationSeconds / 3);
            ClipStart = startSec;
            ClipLength = len;
            var ss = startSec.ToString(Fmt.Inv);
            var tt = len.ToString(Fmt.Inv);
            observer.Line(new InfoLine("Test", L.T($"Ausschnitt ab {Fmt.Time(startSec)} · {len} Sekunden · Werte {profile.TestValues}", $"Clip from {Fmt.Time(startSec)} · {len} seconds · values {profile.TestValues}")));
            observer.Line(new InfoLine(L.T("Ordner", "Folder"), dir));

            var reference = Path.Combine(dir, "00_Quelle.mkv");
            await ProcessRunner.RunAsync(tools.FfmpegPath,
                ["-hide_banner", "-v", "error", "-nostdin", "-y", "-ss", ss, "-t", tt, "-i", source, "-map", "0:v:0", "-map", "0:a?", "-map", "0:s?", "-c", "copy", reference],
                TimeSpan.FromMinutes(10), ct);

            var plans = new Dictionary<string, EncodePlan>(StringComparer.OrdinalIgnoreCase);
            foreach (var value in ParseValues(profile))
            {
                ct.ThrowIfCancellationRequested();
                var p = profile.Clone();
                string clipName;
                if (p.Encoder == EncoderKind.Amf) { p.QpI = value.I; p.QpP = value.P; clipName = $"QP{value.I}-{value.P}"; }
                else { p.Quality = value.I; clipName = (p.Encoder == EncoderKind.Cpu ? "CRF" : "CQ") + value.I; }

                var clipDecision = Planner.Decide(media, p);
                if (clipDecision.Plan is not { } plan)
                {
                    observer.Line(new InfoLine(L.T("Test", "Test"), L.T("Diese Datei würde mit dem Profil übersprungen: ", "This file would be skipped by the profile: ") + clipDecision.Message, LineKind.Warn));
                    break;
                }
                var output = Path.Combine(dir, clipName + ".mkv");
                plans[clipName] = plan;
                var enc = p.AdjustedFor(plan.Video.DisplayWidth, plan.Video.DisplayHeight);
                if (enc.QualityText != p.QualityText)
                    observer.Line(new InfoLine(clipName, L.T($"mit der Anpassung nach Auflösung: {enc.QualityText}", $"with the per-resolution adjustment: {enc.QualityText}"), LineKind.Dim));
                var args = ArgsBuilder.BuildEncode(enc, plan, source, output, ["-ss", ss, "-t", tt]);
                var run = new FfmpegRun(tools.FfmpegPath, args, new RunOptions { DurationSeconds = len, SourceFps = plan.OutputFps > 0 ? plan.OutputFps : media.Fps });
                _current = run;
                var res = await run.RunAsync(new SyncProgress<ProgressInfo>(pi => observer.Progress(clipName, pi)), ct);
                _current = null;
                if (res.Cancelled) { cancelled = true; break; }
                if (!res.Success) observer.Line(new InfoLine(clipName, L.T($"fehlgeschlagen (Exit-Code {res.ExitCode})", $"failed (exit code {res.ExitCode})"), LineKind.Error));
            }

            double? refRate = await VideoRateAsync(reference, ct);
            double? refTotal = await RateAsync(reference, ct);
            var videoShare = refRate is double vr && refTotal is double tr && tr > 0 ? Math.Clamp(vr / tr, 0, 1) : 1;
            var files = Directory.GetFiles(dir, "*.mkv")
                .Where(f => f.Equals(reference, StringComparison.OrdinalIgnoreCase) || plans.ContainsKey(Path.GetFileNameWithoutExtension(f)))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
            foreach (var f in files)
            {
                var size = new FileInfo(f).Length;
                var rate = await VideoRateAsync(f, ct);
                double? ratio = rate is double r && refRate is double rr && rr > 0 ? r / rr : null;
                long? full = ratio is double q ? (long)(media.SizeBytes * (videoShare * q + (1 - videoShare))) : null;
                var clip = Path.GetFileNameWithoutExtension(f);
                double? ssim = null;
                if (plans.TryGetValue(clip, out var cp) && (await tools.ProbeAsync(f, ct))?.Video is { } cv)
                {
                    observer.Status(L.T($"Messe Bildqualität von {clip} …", $"Measuring picture quality of {clip} …"));
                    ssim = (await QualityCheck.MeasureAsync(tools, source, f, cp, cv.Width, cv.Height, startSec, len, 3, ct))?.Average;
                }
                rows.Add(new TestRow(clip, size, ratio, full, f.Equals(reference, StringComparison.OrdinalIgnoreCase)) { File = f, Ssim = ssim });
            }
            observer.FileFinished(start, new FileEnd(cancelled ? Outcome.Cancelled : Outcome.Encoded, dir));
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
            observer.FileFinished(start, new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")));
        }
        catch (Exception ex)
        {
            observer.FileFinished(start, new FileEnd(Outcome.Error, ex.Message));
        }
        return rows;
    }

    private async Task<double?> VideoRateAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        try
        {
            var info = await tools.ProbeAsync(file, ct);
            if (info is null || info.DurationSeconds <= 0) return null;
            var r = await ProcessRunner.RunAsync(tools.FfprobePath,
                ["-v", "error", "-select_streams", "v:0", "-show_entries", "packet=size", "-of", "csv=p=0", file], TimeSpan.FromMinutes(2), ct);
            long sum = 0;
            foreach (var line in r.StdOut.Split('\n'))
                if (long.TryParse(line.Trim().TrimEnd(','), out var n)) sum += n;
            return sum > 0 ? sum / info.DurationSeconds : await RateAsync(file, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return await RateAsync(file, ct);
        }
    }

    private async Task<double?> RateAsync(string file, CancellationToken ct)
    {
        if (!File.Exists(file)) return null;
        var info = await tools.ProbeAsync(file, ct);
        if (info is null || info.DurationSeconds <= 0) return null;
        return new FileInfo(file).Length / info.DurationSeconds;
    }

    public static List<(int I, int P)> ParseValues(Profile p)
    {
        var list = new List<(int, int)>();
        foreach (var token in p.TestValues.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = token.Split('/');
            if (!int.TryParse(parts[0], out var a)) continue;
            var b = parts.Length > 1 && int.TryParse(parts[1], out var bb) ? bb : a + 2;
            list.Add((a, b));
        }
        return list;
    }
}
