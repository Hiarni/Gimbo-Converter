using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed class RepairOptions
{
    public static readonly string[] DefaultAllowedAudio = ["aac", "ac3", "eac3", "mp3", "mp2"];

    public IReadOnlyCollection<string> AllowedAudio { get; init; } = DefaultAllowedAudio;
    public AudioTarget Target { get; init; } = AudioTarget.Ac3;
    public int Bitrate51 { get; init; } = 640;
    public bool DryRun { get; init; }
    public int SkipNewerThanMinutes { get; init; } = 10;
    public int LockStaleHours { get; init; } = 6;
    public System.Diagnostics.ProcessPriorityClass Priority { get; init; } = System.Diagnostics.ProcessPriorityClass.Normal;
}

public sealed record RepairTrack(int SourceIndex, int OutputIndex, string Codec, int OutChannels, int BitrateKbit, string Text, string? NewTitle);

public sealed record RepairPlan(HevcLevel.Level? FixLevel, int LevelFrom, IReadOnlyList<RepairTrack> Audio, IReadOnlyList<string> Changes, IReadOnlyList<string> Notes)
{
    public bool NeedsWork => FixLevel is not null || Audio.Count > 0;
}

public enum RepairOutcome
{
    Repaired,
    Fine,
    WouldChange,
    Skipped,
    Failed,
    Cancelled,
}

public sealed record RepairResult(RepairOutcome Outcome, string Message, long OldBytes = 0, long NewBytes = 0);

public interface IRepairObserver
{
    void Started(string file);
    void Progress(string file, ProgressInfo progress);
    void Finished(string file, RepairResult result);
}

public static class RepairPlanner
{
    public static readonly string[] Extensions = [".mkv", ".mp4", ".m4v"];

    public static bool IsMp4(string file) => !Path.GetExtension(file).Equals(".mkv", StringComparison.OrdinalIgnoreCase);

    public static RepairPlan Plan(MediaInfo media, RepairOptions opt)
    {
        var changes = new List<string>();
        var notes = new List<string>();
        var from = media.Video?.Level ?? 0;
        var fix = HevcLevel.FixFor(media);
        if (fix is not null)
            changes.Add(L.T($"Video-Level {HevcLevel.Text(from)} → {fix.Name} (Bild bleibt bit-genau gleich)", $"video level {HevcLevel.Text(from)} → {fix.Name} (picture stays bit-identical)"));

        var allowed = new HashSet<string>(opt.AllowedAudio.Select(a => a.Trim().ToLowerInvariant()).Where(a => a.Length > 0), StringComparer.Ordinal);
        var ac3 = opt.Target == AudioTarget.Ac3;
        var codec = ac3 ? "ac3" : "eac3";
        var tracks = new List<RepairTrack>();
        var i = 0;
        foreach (var a in media.Audio)
        {
            if (!allowed.Contains(a.CodecName.ToLowerInvariant()))
            {
                if (AudioPlanner.CanDecode(a))
                {
                    var ch = a.Channels <= 0 ? 2 : a.Channels;
                    var outCh = Math.Min(6, ch);
                    var br = outCh switch
                    {
                        >= 6 => opt.Bitrate51,
                        >= 3 => Math.Min(448, opt.Bitrate51),
                        2 => 256,
                        _ => 192,
                    };
                    if (ac3) br = Math.Min(br, 640);
                    var text = $"{AudioPlanner.Describe(a)} → {(ac3 ? "AC3" : "E-AC3")} {AudioPlanner.ChannelText(outCh)} {br}k";
                    tracks.Add(new RepairTrack(a.Index, i, codec, outCh, br, text, TrackTitles.CleanAudio(a.Title, a.CodecName, codec, ch > 6)));
                    changes.Add(L.T($"Ton {i + 1}: {text}", $"audio {i + 1}: {text}"));
                }
                else
                {
                    notes.Add(L.T($"Ton {i + 1} ({AudioPlanner.Describe(a)}) lässt sich nicht umwandeln und bleibt, wie er ist", $"audio {i + 1} ({AudioPlanner.Describe(a)}) cannot be converted and stays as it is"));
                }
            }
            i++;
        }
        if (changes.Count > 0)
            foreach (var s in DataStreams(media))
                notes.Add(L.T($"Datenspur entfällt (für die Wiedergabe ohne Bedeutung): {Name(s)}", $"data track is dropped (irrelevant for playback): {Name(s)}"));
        return new RepairPlan(fix, from, tracks, changes, notes);
    }

    internal static IEnumerable<StreamInfo> DataStreams(MediaInfo media) =>
        media.Streams.Where(s => s.CodecType is not ("video" or "audio" or "subtitle" or "attachment"));

    private static string Name(StreamInfo s) => s.CodecName.Length > 0 ? s.CodecName : s.CodecType.Length > 0 ? s.CodecType : "?";

    public static List<string> BuildArgs(MediaInfo media, RepairPlan plan, string input, string output, IReadOnlyList<CoverFile> covers, int coverAttachStart)
    {
        var mp4 = IsMp4(input);
        var v = media.Video!;
        var a = new List<string> { "-hide_banner", "-y", "-i", input, "-map", "0", "-map", "-0:d" };
        var pictures = media.Streams.Where(s => s.CodecType == "video" && s.AttachedPic).ToList();
        if (!mp4) foreach (var p in pictures) a.AddRange(["-map", "-0:" + I(p.Index)]);
        a.AddRange(["-c", "copy"]);
        if (plan.FixLevel is { } fix)
        {
            var pos = media.Streams.Count(s => s.CodecType == "video" && s.Index < v.Index && (mp4 || !s.AttachedPic));
            a.AddRange(["-bsf:v:" + I(pos), "hevc_metadata=level=" + I(fix.Idc)]);
        }
        foreach (var t in plan.Audio)
        {
            var o = I(t.OutputIndex);
            a.AddRange(["-c:a:" + o, t.Codec, "-b:a:" + o, I(t.BitrateKbit) + "k"]);
            var src = media.Streams.FirstOrDefault(s => s.Index == t.SourceIndex);
            if (src is not null && src.Channels > 0 && src.Channels != t.OutChannels) a.AddRange(["-ac:a:" + o, I(t.OutChannels)]);
            foreach (var tag in ArgsBuilder.StatsTags.Take(4)) a.AddRange(["-metadata:s:a:" + o, tag + "="]);
            if (t.NewTitle is not null) a.AddRange(["-metadata:s:a:" + o, "title=" + t.NewTitle]);
        }
        var n = coverAttachStart;
        foreach (var c in covers)
        {
            a.AddRange(["-attach", c.File, "-metadata:s:t:" + I(n), "mimetype=" + c.MimeType, "-metadata:s:t:" + I(n), "filename=" + c.FileName]);
            if (c.Title.Length > 0) a.AddRange(["-metadata:s:t:" + I(n), "title=" + c.Title]);
            n++;
        }
        a.AddRange(["-map_metadata", "0", "-map_chapters", "0", "-max_muxing_queue_size", "9999"]);
        if (mp4) a.AddRange(["-movflags", "+faststart"]);
        a.AddRange(["-f", mp4 ? "mp4" : "matroska", output]);
        return a;
    }

    private static string I(int n) => n.ToString(Fmt.Inv);
}

public sealed class RepairRun(FfmpegTools tools, IRepairObserver observer)
{
    private FfmpegRun? _current;

    public bool Pause() => _current?.Pause() ?? false;
    public bool Resume() => _current?.Resume() ?? false;

    public async Task RunAsync(IReadOnlyList<string> files, RepairOptions opt, CancellationToken ct)
    {
        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;
            observer.Started(file);
            var res = await RepairFileAsync(file, opt, ct);
            observer.Finished(file, res);
            if (res.Outcome == RepairOutcome.Cancelled) break;
        }
    }

    private static string Seconds(double s) => s.ToString("0.0", Fmt.De) + " s";

    public async Task<RepairResult> RepairFileAsync(string file, RepairOptions opt, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(file)!;
        var name = Path.GetFileNameWithoutExtension(file);
        var temp = CleanupRun.TempFor(file);
        FileLock? lk = null;
        string? coverDir = null;
        try
        {
            var seen = new FileInfo(file);
            if (!seen.Exists) return new RepairResult(RepairOutcome.Skipped, L.T("Datei ist nicht mehr vorhanden.", "File no longer exists."));
            var window = TimeSpan.FromMinutes(Math.Max(0, opt.SkipNewerThanMinutes));
            if (Scanner.IsFresh(seen, window) || File.Exists(Path.Combine(dir, name + ".part.mkv")))
                return new RepairResult(RepairOutcome.Skipped, L.T("Gerade erst geschrieben oder in Arbeit, beim nächsten Mal.", "Just written or in progress, next time."));

            var media = await tools.ProbeAsync(file, ct);
            if (media?.Video is null) return new RepairResult(RepairOutcome.Failed, L.T("Datei nicht lesbar oder ohne Video.", "File not readable or without video."));
            var plan = RepairPlanner.Plan(media, opt);
            if (!plan.NeedsWork) return new RepairResult(RepairOutcome.Fine, L.T("Nichts zu tun.", "Nothing to do."));
            var all = string.Join("; ", plan.Changes.Concat(plan.Notes));
            if (opt.DryRun) return new RepairResult(RepairOutcome.WouldChange, L.T($"Probelauf, würde ändern: {all}", $"Dry run, would change: {all}"));
            if (Scanner.StillBeingWritten(file)) return new RepairResult(RepairOutcome.Skipped, L.T("Wird gerade geschrieben, beim nächsten Mal.", "Still being written, next time."));

            lk = FileLock.TryAcquire(Path.Combine(dir, name + ".lock"), opt.LockStaleHours, out var owner, "Reparatur");
            if (lk is null) return new RepairResult(RepairOutcome.Skipped, L.T($"Wird gerade bearbeitet ({owner}).", $"Currently being processed ({owner})."));

            var fi = new FileInfo(file);
            var oldBytes = fi.Length;
            var created = fi.CreationTimeUtc;
            var written = fi.LastWriteTimeUtc;
            if (oldBytes != seen.Length || written != seen.LastWriteTimeUtc)
                return new RepairResult(RepairOutcome.Skipped, L.T("Datei hat sich gerade geändert, beim nächsten Mal.", "File just changed, next time."));
            var v = media.Video;
            var mp4 = RepairPlanner.IsMp4(file);

            var pictures = media.Streams.Where(s => s.CodecType == "video" && s.AttachedPic).ToList();
            var covers = new List<CoverFile>();
            if (!mp4 && pictures.Count > 0)
            {
                coverDir = Path.Combine(Path.GetTempPath(), "gimbo-reparatur-" + Guid.NewGuid().ToString("N")[..10]);
                Directory.CreateDirectory(coverDir);
                foreach (var pic in pictures)
                {
                    var cover = await CleanupRun.ExtractCoverAsync(tools, file, pic, Path.Combine(coverDir, covers.Count.ToString(Fmt.Inv) + ".bin"), ct);
                    if (cover is null) return new RepairResult(RepairOutcome.Skipped, L.T("Nicht verändert: das Titelbild ließ sich nicht übernehmen.", "Not changed: the cover picture could not be carried over."));
                    covers.Add(cover);
                }
            }

            TryDelete(temp);
            var args = RepairPlanner.BuildArgs(media, plan, file, temp, covers, media.Attachments.Count());
            var run = new FfmpegRun(tools.FfmpegPath, args, new RunOptions { DurationSeconds = media.DurationSeconds, SourceFps = media.Fps, Priority = opt.Priority });
            _current = run;
            var res = await run.RunAsync(new SyncProgress<ProgressInfo>(pi => observer.Progress(file, pi)), ct);
            _current = null;
            if (res.Cancelled) { TryDelete(temp); return new RepairResult(RepairOutcome.Cancelled, L.T("Abgebrochen, Original bleibt.", "Cancelled, the original stays.")); }
            if (res.ExitCode != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                var last = res.ErrorLines.LastOrDefault() is { } l ? $" ({l})" : "";
                return new RepairResult(RepairOutcome.Failed, L.T($"ffmpeg Exit-Code {res.ExitCode}{last}. Original bleibt.", $"ffmpeg exit code {res.ExitCode}{last}. The original stays."));
            }
            if (FfmpegErrors.ReadErrors(res.ErrorLines) is { Count: > 0 } readErrors)
            {
                TryDelete(temp);
                return new RepairResult(RepairOutcome.Failed, L.T($"Verworfen: Lesefehler, die Datei wurde nicht vollständig gelesen ({readErrors[0]}). Original bleibt.",
                                                                   $"Discarded: read error, the file was not read completely ({readErrors[0]}). The original stays."));
            }

            var problem = await CheckAsync(media, plan, file, temp, pictures.Count, ct);
            if (problem is null)
            {
                fi.Refresh();
                if (!fi.Exists || fi.Length != oldBytes || fi.LastWriteTimeUtc != written) problem = L.T("Original wurde während der Reparatur verändert", "original was changed during the repair");
            }
            if (problem is null && !lk.StillOwned()) problem = L.T("Sperre wurde von einem anderen PC übernommen", "lock was taken over by another PC");
            if (problem is not null)
            {
                TryDelete(temp);
                return new RepairResult(RepairOutcome.Failed, L.T($"Verworfen: {problem}. Original bleibt.", $"Discarded: {problem}. The original stays."));
            }

            var newBytes = new FileInfo(temp).Length;
            File.Move(temp, file, overwrite: true);
            if (OperatingSystem.IsWindows()) { try { File.SetCreationTimeUtc(file, created); } catch { } }
            try { File.SetLastWriteTimeUtc(file, CleanupRun.TouchedTime(written)); } catch { }
            return new RepairResult(RepairOutcome.Repaired, L.T($"Repariert: {all}", $"Repaired: {all}"), oldBytes, newBytes);
        }
        catch (OperationCanceledException)
        {
            if (lk is not null) TryDelete(temp);
            return new RepairResult(RepairOutcome.Cancelled, L.T("Abgebrochen, Original bleibt.", "Cancelled, the original stays."));
        }
        catch (Exception ex)
        {
            if (lk is not null && File.Exists(file)) TryDelete(temp);
            return new RepairResult(RepairOutcome.Failed, ex.Message);
        }
        finally
        {
            _current = null;
            lk?.Dispose();
            if (coverDir is not null) { try { Directory.Delete(coverDir, true); } catch { } }
        }
    }

    private async Task<string?> CheckAsync(MediaInfo media, RepairPlan plan, string file, string temp, int pictures, CancellationToken ct)
    {
        var o = await tools.ProbeAsync(temp, ct);
        if (o?.Video is not { } ov) return L.T("Ergebnis nicht lesbar", "result not readable");
        var v = media.Video!;
        static bool Real(StreamInfo s) => s.CodecType == "video" && !s.AttachedPic;
        static bool Pic(StreamInfo s) => s.CodecType == "video" && s.AttachedPic;
        if (!ov.CodecName.Equals(v.CodecName, StringComparison.OrdinalIgnoreCase) || ov.Width != v.Width || ov.Height != v.Height) return L.T("Video verändert", "video changed");
        if (o.Streams.Count(Real) != media.Streams.Count(Real)) return L.T("Videospuren weichen ab", "video tracks differ");
        if (o.Streams.Count(Pic) != pictures) return L.T("Titelbild fehlt", "cover picture missing");
        if (o.Audio.Count() != media.Audio.Count()) return L.T($"Tonspuren fehlen ({o.Audio.Count()} statt {media.Audio.Count()})", $"audio tracks missing ({o.Audio.Count()} instead of {media.Audio.Count()})");
        if (o.Subtitles.Count() != media.Subtitles.Count()) return L.T($"Untertitel fehlen ({o.Subtitles.Count()} statt {media.Subtitles.Count()})", $"subtitles missing ({o.Subtitles.Count()} instead of {media.Subtitles.Count()})");
        if (o.Attachments.Count() != media.Attachments.Count()) return L.T("Anhänge fehlen", "attachments missing");
        var tolerance = plan.Audio.Count > 0 ? 1.0 : DurationCheck.CopyTolerance;
        if (media.DurationSeconds > 0 && !DurationCheck.Matches(media, o.DurationSeconds, tolerance))
            return L.T($"Laufzeit weicht ab ({Seconds(o.DurationSeconds)} statt {Seconds(media.DurationSeconds)})", $"duration differs ({Seconds(o.DurationSeconds)} instead of {Seconds(media.DurationSeconds)})");
        if (plan.FixLevel is not null && ov.Level > plan.FixLevel.Idc) return L.T("Level nicht korrigiert", "level not corrected");
        foreach (var t in plan.Audio)
        {
            var oa = o.Audio.ElementAtOrDefault(t.OutputIndex);
            if (oa is null || !oa.CodecName.Equals(t.Codec, StringComparison.OrdinalIgnoreCase)) return L.T("Ton nicht umgewandelt", "audio not converted");
        }

        var cleanOut = await DecodesCleanAsync(temp, ct);
        var outPackets = await tools.CountVideoPacketsAsync(temp, ct);
        if (outPackets <= 0) return L.T("Ergebnis lässt sich nicht bis zum Ende lesen", "result cannot be read to the end");
        var srcPackets = await tools.CountVideoPacketsAsync(file, ct);
        if (srcPackets <= 0) return L.T("Videobilder des Originals lassen sich nicht zählen (Lesefehler?)", "video frames of the original cannot be counted (read error?)");
        if (cleanOut && outPackets >= srcPackets) return null;
        if (await DecodesCleanAsync(file, ct))
            return cleanOut ? L.T($"Ergebnis hat weniger Videobilder als das Original ({outPackets} statt {srcPackets})", $"result has fewer video frames than the original ({outPackets} instead of {srcPackets})")
                : L.T("Dekodierprüfung fehlgeschlagen", "decoding check failed");
        return outPackets >= srcPackets - Math.Max(10, (long)(srcPackets * 0.005)) ? null
            : L.T($"Original zu stark beschädigt (Ergebnis hätte {outPackets} statt {srcPackets} Videobilder)", $"original too damaged (result would have {outPackets} instead of {srcPackets} video frames)");
    }

    private async Task<bool> DecodesCleanAsync(string file, CancellationToken ct)
    {
        var dec = await ProcessRunner.RunAsync(tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(file, false, FfmpegTools.NullTarget), TimeSpan.FromMinutes(5), ct);
        return dec.ExitCode == 0 && ArgsBuilder.DecodeErrors(dec.StdErr.Split('\n').Where(l => l.Trim().Length > 0)).Count == 0;
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
