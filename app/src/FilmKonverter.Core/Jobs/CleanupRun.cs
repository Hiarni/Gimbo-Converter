using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed class CleanupOptions
{
    public bool DryRun { get; init; }

    public string? Folder { get; init; }

    public bool ApplyProfileRules { get; init; } = true;

    public int SkipNewerThanMinutes { get; init; } = 10;
}

public enum ChangeKind
{
    AudioConvert,
    AudioRemove,
    AudioDefault,
    AudioFlags,
    SubtitleRemove,
    SubtitleConvert,
    SubtitleDefault,
    SubtitleFlags,
    Attachments,
    Level,
    Lost,
}

public sealed record FileChange(ChangeKind Kind, string Text);

public sealed record TouchUpPlan(AudioPlan Audio, SubtitlePlan Subs, IReadOnlyList<int> Attachments, HevcLevel.Level? FixLevel,
    IReadOnlyList<FileChange> Changes, IReadOnlyList<FileChange> Losses);

public sealed class CleanupRun(Profile profile, FfmpegTools tools, IRunObserver observer)
{
    private FfmpegRun? _current;
    public RunCounters Counters { get; } = new();
    public bool StopAfterCurrent { get; set; }
    public System.Diagnostics.ProcessPriorityClass Priority { get; init; } = System.Diagnostics.ProcessPriorityClass.Normal;
    public bool Pause() => _current?.Pause() ?? false;
    public bool Resume() => _current?.Resume() ?? false;
    public ProbeCache? Cache { get; init; } = ProbeCache.Shared;
    public bool WriteStatsLog { get; init; } = true;

    private void Info(string label, string text, LineKind kind = LineKind.Info) => observer.Line(new InfoLine(label, text, kind));

    public static Profile PlayabilityRules(Profile p)
    {
        var r = p.Clone();
        r.AudioLanguages = "";
        r.DefaultAudioLanguage = "";
        r.RemoveCommentary = false;
        r.RemoveAudioDescription = false;
        r.SubtitleLanguages = "";
        r.RemoveImageSubtitles = false;
        r.ForcedSubtitles = ForcedSubtitleMode.Keep;
        r.KeepAttachments = true;
        r.ConvertLosslessAudio = true;
        r.ConvertDts = true;
        return r;
    }

    public static string? TargetRelative(string root, string outputDir)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(outputDir)) return null;
        try
        {
            if (Scanner.IsInside(root, outputDir))
            {
                var rel = Path.GetRelativePath(outputDir, root);
                return rel == "." ? "" : rel;
            }
            if (!Directory.Exists(outputDir)) return null;
            var marker = ".gimbo-ort-" + Guid.NewGuid().ToString("N") + ".tmp";
            var markerFile = Path.Combine(outputDir, marker);
            try
            {
                File.WriteAllText(markerFile, "");
                var below = new List<string>();
                var dir = Path.GetFullPath(root);
                for (var depth = 0; depth < 64 && !string.IsNullOrEmpty(dir); depth++)
                {
                    if (File.Exists(Path.Combine(dir, marker)))
                    {
                        below.Reverse();
                        return below.Count == 0 ? "" : Path.Combine([.. below]);
                    }
                    var trimmed = dir.TrimEnd('\\', '/');
                    var parent = Path.GetDirectoryName(trimmed.Length == 0 ? dir : trimmed);
                    if (string.IsNullOrEmpty(parent) || parent == dir) break;
                    below.Add(Path.GetFileName(trimmed));
                    dir = parent;
                }
            }
            finally
            {
                try { File.Delete(markerFile); } catch { }
            }
        }
        catch
        {
        }
        return null;
    }

    private int CountOtherVideos(string root)
    {
        try
        {
            var exts = profile.Extensions.Concat(Profile.DefaultExtensions)
                .Select(e => e.StartsWith('.') ? e : "." + e)
                .Where(e => !e.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            return exts.Count == 0 ? 0 : Scanner.Find(root, exts).Count;
        }
        catch
        {
            return 0;
        }
    }

    private sealed record FileResult(FileEnd End, IReadOnlyList<FileChange> Changes, bool Counted = false, bool WouldChange = false);

    public async Task RunAsync(CleanupOptions opt, CancellationToken ct)
    {
        var cancelled = false;
        List<string>? files = null;
        var changedFiles = 0;
        var totals = new SortedDictionary<ChangeKind, int>();
        try
        {
            var root = string.IsNullOrWhiteSpace(opt.Folder) ? profile.OutputDir : opt.Folder.Trim();
            var rules = opt.ApplyProfileRules ? profile : PlayabilityRules(profile);
            if (!Directory.Exists(root)) throw new DirectoryNotFoundException(L.T($"Ordner nicht erreichbar: {root}", $"Folder not reachable: {root}"));
            if (!string.IsNullOrWhiteSpace(profile.OutputDir)) await Task.Run(() => DataStore.Sync(profile.OutputDir), ct);
            observer.ScanStarted(root);
            var window = TimeSpan.FromMinutes(Math.Max(0, opt.SkipNewerThanMinutes));
            var (scan, others, targetRel) = await Task.Run(() => (
                Scanner.Find(root, new ScanFilter { Extensions = [".mkv"], SkipNewerThan = window }),
                CountOtherVideos(root),
                TargetRelative(root, profile.OutputDir)), ct);
            files = scan.Files;
            Counters.Total = files.Count;
            var left = new List<string>();
            if (others > 0) left.Add(L.T($"{others} andere Videodateien (nur MKV wird nachgebessert)", $"{others} other video files (only MKV is touched up)"));
            if (scan.TooNew > 0) left.Add(L.T($"{scan.TooNew} in den letzten {opt.SkipNewerThanMinutes} Minuten geändert (werden vielleicht noch geschrieben)", $"{scan.TooNew} changed in the last {opt.SkipNewerThanMinutes} minutes (may still be written)"));
            Counters.Filtered = others + scan.TooNew;
            Counters.FilterText = string.Join(", ", left);
            observer.ScanDone(files.Count);
            if (left.Count > 0) observer.Status(L.T($"{files.Count} MKV-Dateien gefunden, ausgelassen: {Counters.FilterText}.", $"{files.Count} MKV files found, left out: {Counters.FilterText}."));
            if (!opt.ApplyProfileRules)
                observer.Status(L.T("Nur Abspielbarkeit: keine Spur entfällt. Ton, den Fernseher nicht abspielen, wird umgewandelt, auch wenn die Umwandlung im Profil ausgeschaltet ist.",
                                    "Playability only: no track is dropped. Audio that TVs cannot play is converted, even if conversion is switched off in the profile."));
            if (opt.DryRun) observer.Status(L.T("Probelauf: es wird nichts verändert.", "Dry run: nothing is changed."));

            for (var i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested || StopAfterCurrent) { cancelled = true; break; }
                var file = files[i];
                long size = 0;
                try { size = new FileInfo(file).Length; } catch { }
                var start = new FileStart(i + 1, files.Count, Path.GetRelativePath(root, file), file, size);
                observer.FileStarted(start);
                var res = await CleanFileAsync(root, targetRel, file, opt, rules, window, ct);
                if (res.Counted)
                {
                    changedFiles++;
                    foreach (var k in res.Changes.Select(c => c.Kind).Distinct()) totals[k] = totals.GetValueOrDefault(k) + 1;
                }
                if (res.WouldChange) { Counters.Processed++; Counters.WouldChange++; }
                else Counters.Count(res.End);
                observer.FileFinished(start, res.End);
                if (res.End.Outcome == Outcome.Cancelled) { cancelled = true; break; }
            }
        }
        finally
        {
            Cache?.Save();
            if (files is not null)
            {
                var text = Summary(opt.DryRun, changedFiles, Counters.Processed, totals);
                observer.Line(new InfoLine(L.T("Summe", "Total"), text, changedFiles > 0 ? LineKind.Ok : LineKind.Dim));
                observer.Status(text);
            }
            observer.RunFinished(Counters, cancelled || ct.IsCancellationRequested);
        }
    }

    public static string KindText(ChangeKind k) => k switch
    {
        ChangeKind.AudioConvert => L.T("Ton umwandeln", "convert audio"),
        ChangeKind.AudioRemove => L.T("Tonspuren entfernen", "remove audio tracks"),
        ChangeKind.AudioDefault => L.T("Standard-Tonspur", "default audio track"),
        ChangeKind.AudioFlags => L.T("Ton-Kennzeichen", "audio flags"),
        ChangeKind.SubtitleRemove => L.T("Untertitel entfernen", "remove subtitles"),
        ChangeKind.SubtitleConvert => L.T("Untertitel umwandeln", "convert subtitles"),
        ChangeKind.SubtitleDefault => L.T("Bild-Untertitel nicht mehr Standard", "image subtitles no longer default"),
        ChangeKind.SubtitleFlags => L.T("Untertitel-Kennzeichen", "subtitle flags"),
        ChangeKind.Attachments => L.T("Anhänge entfernen", "remove attachments"),
        ChangeKind.Level => L.T("Video-Level korrigieren", "correct video level"),
        _ => L.T("nicht übernehmbare Spuren", "tracks that cannot be carried over"),
    };

    public static string Summary(bool dryRun, int changedFiles, int checkedFiles, IReadOnlyDictionary<ChangeKind, int> totals)
    {
        var kinds = string.Join(" · ", totals.Select(t => $"{KindText(t.Key)}: {t.Value}"));
        if (dryRun)
            return changedFiles == 0
                ? L.T($"Probelauf: nichts zu ändern ({checkedFiles} Dateien geprüft).", $"Dry run: nothing to change ({checkedFiles} files checked).")
                : L.T($"Probelauf: zu ändern wären {changedFiles} von {checkedFiles} Dateien, nichts wurde angefasst. Dateien je Änderung: {kinds}",
                      $"Dry run: {changedFiles} of {checkedFiles} files would be changed, nothing was touched. Files per change: {kinds}");
        return changedFiles == 0
            ? L.T($"Nichts nachgebessert ({checkedFiles} Dateien geprüft).", $"Nothing touched up ({checkedFiles} files checked).")
            : L.T($"Nachgebessert: {changedFiles} von {checkedFiles} Dateien. Dateien je Änderung: {kinds}",
                  $"Touched up: {changedFiles} of {checkedFiles} files. Files per change: {kinds}");
    }

    public static TouchUpPlan Analyze(MediaInfo media, Profile profile, Profile rules, bool playabilityOnly)
    {
        var audio = AudioPlanner.Plan(media, rules);
        var subs = AudioPlanner.PlanSubtitles(media, rules);
        var attachments = AudioPlanner.PlanAttachments(media, rules);
        var changes = new List<FileChange>();
        var losses = new List<FileChange>();
        void Add(ChangeKind k, string de, string en) => changes.Add(new FileChange(k, L.T(de, en)));
        StreamInfo? Source(int index) => media.Streams.FirstOrDefault(x => x.Index == index);

        // ---------------- Ton ----------------
        foreach (var r in audio.Removed) Add(ChangeKind.AudioRemove, $"Ton entfernen: {r}", $"remove audio: {r}");
        foreach (var t in audio.Tracks.Where(t => t.Convert))
        {
            var text = t.Codec == "flac"
                ? L.T($"Ton verlustfrei umpacken: {t.Text}", $"repack audio losslessly: {t.Text}")
                : L.T($"Ton umwandeln, damit Fernseher ihn direkt abspielen: {t.Text}", $"convert audio so TVs play it directly: {t.Text}");
            if (t.NewTitle is not null)
                text += t.NewTitle.Length == 0
                    ? L.T($" · Titel „{t.SourceTitle}“ entfällt (nannte das alte Format)", $" · title “{t.SourceTitle}” is dropped (named the old format)")
                    : L.T($" · Titel „{t.SourceTitle}“ wird „{t.NewTitle}“", $" · title “{t.SourceTitle}” becomes “{t.NewTitle}”");
            if (playabilityOnly && t.Codec != "flac" && !(t.Lossless ? profile.ConvertLosslessAudio : profile.ConvertDts))
                text += L.T(" (gehört zur Abspielbarkeit, auch wenn die Umwandlung im Profil ausgeschaltet ist)", " (part of playability, even though conversion is switched off in the profile)");
            changes.Add(new FileChange(ChangeKind.AudioConvert, text));
        }
        if (audio.MovedFirst && audio.Tracks.Count > 0)
            Add(ChangeKind.AudioDefault, $"Standard-Tonspur an die erste Stelle: {audio.Tracks[0].SourceText} (viele Fernseher spielen nur die erste Tonspur direkt ab)",
                $"default audio track to the first position: {audio.Tracks[0].SourceText} (many TVs only play the first audio track directly)");
        foreach (var t in audio.Tracks.Where(t => t.Disposition is not null))
        {
            if (Source(t.SourceIndex) is not { } s || AudioPlanner.CurrentDisposition(s) == t.Disposition) continue;
            var isDefault = t.Disposition!.Contains("default");
            if (isDefault && audio.MovedFirst && t.OutputIndex == 0) continue;
            if (isDefault) Add(ChangeKind.AudioDefault, $"Standard-Tonspur: {t.SourceText}", $"default audio: {t.SourceText}");
            else Add(ChangeKind.AudioFlags, $"Kennzeichen ändern: {t.SourceText}", $"change flags: {t.SourceText}");
        }

        // ---------------- Untertitel ----------------
        var byRule = subs.Removed.ToList();
        foreach (var u in subs.Unsupported) byRule.Remove(u);
        foreach (var r in byRule) Add(ChangeKind.SubtitleRemove, $"Untertitel entfernen: {r}", $"remove subtitle: {r}");
        foreach (var t in subs.Tracks)
        {
            if (t.Codec is not null) Add(ChangeKind.SubtitleConvert, $"Untertitel umwandeln: {t.SourceText}", $"convert subtitle: {t.SourceText}");
            if (t.Disposition is null || Source(t.SourceIndex) is not { } s || AudioPlanner.CurrentDisposition(s) == t.Disposition) continue;
            if (t.DefaultRemoved)
                Add(ChangeKind.SubtitleDefault, $"Bild-Untertitel nicht mehr als Standard markieren: {t.SourceText} (sonst rechnet der Medienserver beim Abspielen das ganze Video um)",
                    $"no longer flag the image subtitle as default: {t.SourceText} (otherwise the media server converts the whole video on playback)");
            else Add(ChangeKind.SubtitleFlags, $"Untertitel-Kennzeichen: {t.SourceText}", $"subtitle flags: {t.SourceText}");
        }

        // ---------------- Anhaenge ----------------
        var attachCount = media.Attachments.Count();
        if (!rules.KeepAttachments && attachCount > 0)
            Add(ChangeKind.Attachments, $"Anhänge entfernen: {attachCount} (z.B. Schriftarten)", $"remove attachments: {attachCount} (e.g. fonts)");

        var fixLevel = HevcLevel.FixFor(media);
        if (fixLevel is not null)
            Add(ChangeKind.Level, $"Video-Level {HevcLevel.Text(media.Video!.Level)} → {fixLevel.Name} (damit Fernseher direkt abspielen)",
                $"video level {HevcLevel.Text(media.Video!.Level)} → {fixLevel.Name} (so TVs play it directly)");

        // ---------------- Was nicht mitkaeme ----------------
        void Lose(string de, string en) => losses.Add(new FileChange(ChangeKind.Lost, L.T(de, en)));
        foreach (var u in subs.Unsupported) Lose($"Untertitel entfällt (lässt sich nicht übernehmen): {u}", $"subtitle is lost (cannot be carried over): {u}");
        if (rules.KeepAttachments && attachCount > attachments.Count)
            Lose($"Anhänge ohne Typangabe entfallen: {attachCount - attachments.Count}", $"attachments without a type are lost: {attachCount - attachments.Count}");
        foreach (var s in media.Streams.Where(s => s.CodecType is not ("video" or "audio" or "subtitle" or "attachment")))
        {
            var what = s.CodecName.Length > 0 ? s.CodecName : s.CodecType.Length > 0 ? s.CodecType : "?";
            Lose($"Datenspur entfällt (lässt sich nicht übernehmen): {what}", $"data track is lost (cannot be carried over): {what}");
        }
        return new TouchUpPlan(audio, subs, attachments, fixLevel, changes, losses);
    }

    public static string TempFor(string file) => file + ".gimbo-tmp";

    public static DateTime TouchedTime(DateTime oldWriteUtc) => oldWriteUtc.AddSeconds(2);

    private static string Seconds(double s) => s.ToString("0.0", Fmt.De) + " s";

    private static FileEnd TooNew(int minutes) => new(Outcome.Skipped,
        L.T($"Zu neu: in den letzten {minutes} Minuten geändert oder wird noch geschrieben. Beim nächsten Mal.",
            $"Too new: changed in the last {minutes} minutes or still being written. Next time."));

    private async Task<FileResult> CleanFileAsync(string root, string? targetRel, string file, CleanupOptions opt, Profile rules, TimeSpan window, CancellationToken ct)
    {
        var dir = Path.GetDirectoryName(file)!;
        var name = Path.GetFileNameWithoutExtension(file);
        var temp = TempFor(file);
        var playabilityOnly = !opt.ApplyProfileRules;
        FileLock? lk = null;
        string? coverDir = null;
        static FileResult Plain(FileEnd end) => new(end, []);
        try
        {
            var seen = new FileInfo(file);
            seen.Refresh();
            if (Scanner.IsFresh(seen, window)) return Plain(TooNew(opt.SkipNewerThanMinutes));

            var media = Cache is not null ? await Cache.GetOrProbeAsync(tools, file, ct) : await tools.ProbeAsync(file, ct);
            if (media?.Video is { CodecName: "hevc", Level: 0 })
            {
                media = await tools.ProbeAsync(file, ct);
                if (media is not null) Cache?.Put(file, media);
            }
            if (media?.Video is null) return Plain(new FileEnd(Outcome.Error, L.T("Datei nicht lesbar.", "File not readable.")));
            var plan = Analyze(media, profile, rules, playabilityOnly);

            FileResult? Stop(TouchUpPlan p)
            {
                if (p.Changes.Count == 0) return Plain(new FileEnd(Outcome.AlreadyOk, L.T("Nichts zu tun.", "Nothing to do.")) { Label = L.T("PASST", "FINE") });
                if (!playabilityOnly || p.Losses.Count == 0) return null;
                foreach (var c in p.Losses) Info(L.T("Hinweis", "Note"), c.Text, LineKind.Warn);
                return Plain(new FileEnd(Outcome.Skipped,
                    L.T($"Nicht verändert: {p.Losses.Count} Spur(en) ließen sich nicht in die neue Datei übernehmen, und „nur Abspielbarkeit“ lässt keine Spur wegfallen. Offen bleibt: {string.Join("; ", p.Changes.Select(c => c.Text))}",
                        $"Not changed: {p.Losses.Count} track(s) could not be carried over into the new file, and “playability only” never drops a track. Still open: {string.Join("; ", p.Changes.Select(c => c.Text))}")));
            }
            if (Stop(plan) is { } early) return early;
            if (Scanner.StillBeingWritten(file)) return Plain(TooNew(opt.SkipNewerThanMinutes));

            List<FileChange> all = [.. plan.Changes, .. plan.Losses];
            void Show()
            {
                foreach (var c in all) Info(L.T("Änderung", "Change"), c.Text, c.Kind == ChangeKind.Lost ? LineKind.Warn : LineKind.Info);
            }
            if (opt.DryRun)
            {
                Show();
                return new FileResult(new FileEnd(Outcome.Skipped,
                    L.T($"Probelauf, würde ändern: {string.Join("; ", all.Select(c => c.Text))}", $"Dry run, would change: {string.Join("; ", all.Select(c => c.Text))}"))
                    { Label = L.T("WÜRDE ÄNDERN", "WOULD CHANGE") }, all, Counted: true, WouldChange: true);
            }

            lk = FileLock.TryAcquire(Path.Combine(dir, name + ".lock"), profile.LockStaleHours, out var owner, "Bereinigung");
            if (lk is null) return Plain(new FileEnd(Outcome.OtherPc, L.T($"bei {owner} in Arbeit", $"in progress on {owner}")));

            var fi = new FileInfo(file);
            if (fi.Length != seen.Length || fi.LastWriteTimeUtc != seen.LastWriteTimeUtc)
                return Plain(new FileEnd(Outcome.Skipped, L.T("Datei hat sich gerade geändert, beim nächsten Mal.", "File just changed, next time.")));
            media = await tools.ProbeAsync(file, ct);
            if (media?.Video is null) return Plain(new FileEnd(Outcome.Error, L.T("Datei nicht lesbar.", "File not readable.")));
            Cache?.Put(file, media);
            plan = Analyze(media, profile, rules, playabilityOnly);
            if (Stop(plan) is { } nothing) return nothing;
            all = [.. plan.Changes, .. plan.Losses];
            Show();

            var oldBytes = fi.Length;
            var created = fi.CreationTimeUtc;
            var written = fi.LastWriteTimeUtc;
            var v = media.Video;
            var audio = plan.Audio;
            var subs = plan.Subs;

            var extraVideo = media.Streams.Where(s => s.CodecType == "video" && !s.AttachedPic && s.Index != v.Index).Select(s => s.Index).ToList();
            var pictures = media.Streams.Where(s => s.CodecType == "video" && s.AttachedPic).ToList();
            var covers = new List<CoverFile>();
            if (pictures.Count > 0)
            {
                coverDir = Path.Combine(Path.GetTempPath(), "gimbo-cover-" + Guid.NewGuid().ToString("N")[..10]);
                Directory.CreateDirectory(coverDir);
                foreach (var pic in pictures)
                {
                    var cover = await ExtractCoverAsync(file, pic, Path.Combine(coverDir, covers.Count.ToString(Fmt.Inv) + ".bin"), ct);
                    if (cover is null)
                        return Plain(new FileEnd(Outcome.Skipped, L.T("Nicht verändert: das Titelbild ließ sich nicht in die neue Datei übernehmen.", "Not changed: the cover picture could not be carried over into the new file.")));
                    covers.Add(cover);
                }
            }

            var encodePlan = new EncodePlan(v, null, v.PixFmt.Contains("10", StringComparison.Ordinal) ? 10 : 8, v.PixFmt, "", audio,
                L.T("Video unverändert kopiert", "video copied unchanged"), "", subs, plan.Attachments, media.Fps, VideoCopy: true)
                { FixLevel = plan.FixLevel, ExtraVideo = extraVideo, Covers = covers };
            TryDelete(temp);
            var args = ArgsBuilder.BuildEncode(rules, encodePlan, file, temp);
            args.InsertRange(args.Count - 1, ["-f", "matroska"]);
            var run = new FfmpegRun(tools.FfmpegPath, args,
                new RunOptions { DurationSeconds = media.DurationSeconds, SourceFps = media.Fps, Priority = Priority });
            _current = run;
            var res = await run.RunAsync(new SyncProgress<ProgressInfo>(pi => observer.Progress(L.T("Bereinigen", "Cleaning"), pi)), ct);
            _current = null;
            if (res.Cancelled) { TryDelete(temp); return Plain(new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled."))); }
            if (res.ExitCode != 0 || !File.Exists(temp))
            {
                TryDelete(temp);
                foreach (var l in res.ErrorLines.TakeLast(8)) Info("ffmpeg", l, LineKind.Error);
                return Plain(new FileEnd(Outcome.Error, L.T($"Umschreiben fehlgeschlagen (Exit-Code {res.ExitCode}). Original bleibt.", $"Rewriting failed (exit code {res.ExitCode}). The original stays.")));
            }
            var readErrors = FfmpegErrors.ReadErrors(res.ErrorLines);
            if (readErrors.Count > 0)
            {
                TryDelete(temp);
                foreach (var l in readErrors.TakeLast(8)) Info("ffmpeg", l, LineKind.Error);
                return Plain(new FileEnd(Outcome.Error, L.T("Verworfen: Lesefehler beim Umschreiben (Netzwerk oder Datenträger?), das Ergebnis wäre unvollständig. Original bleibt.",
                                                            "Discarded: read error while rewriting (network or disk?), the result would be incomplete. The original stays.")));
            }
            var own = Stamp(temp);

            var o = await tools.ProbeAsync(temp, ct);
            int Count(MediaInfo m, Func<StreamInfo, bool> match) => m.Streams.Count(match);
            static bool RealVideo(StreamInfo s) => s.CodecType == "video" && !s.AttachedPic;
            static bool Picture(StreamInfo s) => s.CodecType == "video" && s.AttachedPic;
            string? problem = o?.Video is not { } ov ? L.T("Ergebnis nicht lesbar", "result not readable")
                : !ov.CodecName.Equals(v.CodecName, StringComparison.OrdinalIgnoreCase) || ov.Width != v.Width || ov.Height != v.Height ? L.T("Video verändert", "video changed")
                : Count(o, RealVideo) != 1 + extraVideo.Count ? L.T($"Videospuren fehlen ({Count(o, RealVideo)} statt {1 + extraVideo.Count})", $"video tracks missing ({Count(o, RealVideo)} instead of {1 + extraVideo.Count})")
                : Count(o, Picture) != covers.Count ? L.T("Titelbild fehlt", "cover picture missing")
                : o.Audio.Count() != audio.Tracks.Count ? L.T($"Tonspuren fehlen ({o.Audio.Count()} statt {audio.Tracks.Count})", $"audio tracks missing ({o.Audio.Count()} instead of {audio.Tracks.Count})")
                : o.Subtitles.Count() != subs.Tracks.Count ? L.T($"Untertitel fehlen ({o.Subtitles.Count()} statt {subs.Tracks.Count})", $"subtitles missing ({o.Subtitles.Count()} instead of {subs.Tracks.Count})")
                : o.Attachments.Count() != plan.Attachments.Count ? L.T($"Anhänge fehlen ({o.Attachments.Count()} statt {plan.Attachments.Count})", $"attachments missing ({o.Attachments.Count()} instead of {plan.Attachments.Count})")
                : media.DurationSeconds > 0 && !DurationCheck.Matches(media, o.DurationSeconds, DurationCheck.CopyTolerance)
                    ? L.T($"Laufzeit weicht ab ({Seconds(o.DurationSeconds)} statt {Seconds(media.DurationSeconds)})", $"duration differs ({Seconds(o.DurationSeconds)} instead of {Seconds(media.DurationSeconds)})")
                : plan.FixLevel is not null && ov.Level > plan.FixLevel.Idc ? L.T("Level nicht korrigiert", "level not corrected")
                : null;
            if (problem is null && !await DecodesCleanAsync(temp, ct))
            {
                if (await DecodesCleanAsync(file, ct)) problem = L.T("Dekodierprüfung fehlgeschlagen", "decoding check failed");
                else
                {
                    var src = await tools.CountVideoPacketsAsync(file, ct);
                    var outp = await tools.CountVideoPacketsAsync(temp, ct);
                    if (src > 0 && outp >= src - Math.Max(10, (long)(src * 0.005)))
                        Info(L.T("Hinweis", "Note"), L.T("Das Original war schon beschädigt, das Ergebnis ist nicht schlechter.", "The original was already damaged, the result is not worse."), LineKind.Warn);
                    else problem = L.T($"Original zu stark beschädigt (Ergebnis hätte {outp} statt {src} Videobilder)", $"original too damaged (result would have {outp} instead of {src} video frames)");
                }
            }
            if (problem is not null) { TryDelete(temp); return Plain(new FileEnd(Outcome.Error, L.T($"Verworfen: {problem}. Original bleibt.", $"Discarded: {problem}. The original stays."))); }

            var newBytes = new FileInfo(temp).Length;
            if (!lk.StillOwned())
            {
                if (Stamp(temp) == own) TryDelete(temp);
                return Plain(new FileEnd(Outcome.OtherPc, L.T("Sperre wurde inzwischen von einem anderen PC übernommen. Ergebnis verworfen, Original bleibt.", "The lock was taken over by another PC meanwhile. Result discarded, the original stays.")));
            }
            File.Move(temp, file, overwrite: true);
            if (OperatingSystem.IsWindows()) { try { File.SetCreationTimeUtc(file, created); } catch { } }
            try { File.SetLastWriteTimeUtc(file, TouchedTime(written)); } catch { }
            if (o is not null)
                Cache?.Put(file, new MediaInfo { Path = file, DurationSeconds = o.DurationSeconds, StartSeconds = o.StartSeconds, SizeBytes = newBytes, Streams = o.Streams, FormatTags = o.FormatTags });
            var relTarget = targetRel is not null ? Path.Combine(targetRel, Path.GetRelativePath(root, file))
                : Scanner.IsInside(file, profile.OutputDir) ? Path.GetRelativePath(profile.OutputDir, file)
                : null;
            if (relTarget is not null) UpdateMarks(relTarget, oldBytes, newBytes);
            var gain = Math.Max(0, oldBytes - newBytes);
            Info(L.T("Größe", "Size"), $"{Fmt.Size(oldBytes)} » {Fmt.Size(newBytes)}", newBytes <= oldBytes ? LineKind.Ok : LineKind.Warn);
            if (WriteStatsLog && relTarget is not null)
            {
                var parts = relTarget.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                StatsLog.Append(profile.OutputDir, new StatsRecord
                {
                    TimeUtc = DateTime.UtcNow, Machine = FileLock.ComputerName, Profile = profile.Name, Encoder = "copy", File = relTarget,
                    Group = parts.Length > 1 ? parts[0] : name, Outcome = "Cleaned", GainBytes = gain, AudioOnly = true,
                    DurationSeconds = media.DurationSeconds, EncodeSeconds = run.ActiveSeconds,
                });
            }
            return new FileResult(new FileEnd(Outcome.Replaced,
                L.T($"Nachgebessert: {string.Join("; ", all.Select(c => c.Text))}", $"Touched up: {string.Join("; ", all.Select(c => c.Text))}"), gain)
                { Label = L.T("NACHGEBESSERT", "TOUCHED UP") }, all, Counted: true);
        }
        catch (OperationCanceledException)
        {
            if (lk is not null) TryDelete(temp);
            return Plain(new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled.")));
        }
        catch (Exception ex)
        {
            if (lk is not null) TryDelete(temp);
            return Plain(new FileEnd(Outcome.Error, ex.Message));
        }
        finally
        {
            _current = null;
            lk?.Dispose();
            if (coverDir is not null) { try { Directory.Delete(coverDir, true); } catch { } }
        }
    }

    private static (long Bytes, DateTime WrittenUtc) Stamp(string f)
    {
        try { var fi = new FileInfo(f); return fi.Exists ? (fi.Length, fi.LastWriteTimeUtc) : (-1, default); }
        catch { return (-2, default); }
    }

    private async Task<CoverFile?> ExtractCoverAsync(string file, StreamInfo pic, string target, CancellationToken ct)
    {
        var r = await ProcessRunner.RunAsync(tools.FfmpegPath,
            ["-hide_banner", "-v", "error", "-nostdin", "-y", "-i", file, "-map", "0:" + pic.Index.ToString(Fmt.Inv), "-c", "copy", "-f", "data", target],
            TimeSpan.FromMinutes(10), ct);
        if (r.ExitCode != 0 || !File.Exists(target) || new FileInfo(target).Length == 0) return null;
        var (mime, ext) = pic.CodecName.ToLowerInvariant() switch
        {
            "mjpeg" => ("image/jpeg", ".jpg"),
            "png" => ("image/png", ".png"),
            "gif" => ("image/gif", ".gif"),
            "tiff" => ("image/tiff", ".tif"),
            "bmp" => ("image/bmp", ".bmp"),
            "webp" => ("image/webp", ".webp"),
            _ => ("", ""),
        };
        if (pic.MimeType.Length > 0) mime = pic.MimeType;
        if (mime.Length == 0) return null;
        var fileName = pic.Tags.TryGetValue("filename", out var fn) && fn.Trim().Length > 0 ? fn.Trim() : "cover" + ext;
        return new CoverFile(target, mime, fileName, pic.Title);
    }

    private async Task<bool> DecodesCleanAsync(string file, CancellationToken ct)
    {
        var dec = await ProcessRunner.RunAsync(tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(file, false, FfmpegTools.NullTarget), TimeSpan.FromMinutes(5), ct);
        return dec.ExitCode == 0 && ArgsBuilder.DecodeErrors(dec.StdErr.Split('\n').Where(l => l.Trim().Length > 0)).Count == 0;
    }

    public static HevcLevel.Level? LevelFix(MediaInfo media) => HevcLevel.FixFor(media);

    public static string LevelText(int idc) => HevcLevel.Text(idc);

    private void UpdateMarks(string relTarget, long oldBytes, long newBytes)
    {
        try
        {
            var relDir = Path.GetDirectoryName(relTarget) ?? "";
            var name = Path.GetFileNameWithoutExtension(relTarget);
            var srcDir = relDir.Length == 0 ? profile.InputDir : Path.Combine(profile.InputDir, relDir);
            var source = Directory.Exists(srcDir)
                ? Directory.EnumerateFiles(srcDir, name + ".*").FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Equals(name, StringComparison.OrdinalIgnoreCase)
                    && profile.Extensions.Any(e => Path.GetExtension(f).Equals(e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase)))
                : null;
            if (source is null) return;
            var paths = new LibraryPaths(profile.InputDir, profile.OutputDir, source);
            var srcBytes = new FileInfo(source).Length;
            MarkList.MarkOk(paths, profile.MarkTag, srcBytes, newBytes);
            if (oldBytes != newBytes) { try { File.Delete(MarkList.OkFile(paths, profile.MarkTag, srcBytes, oldBytes)); } catch { } }
        }
        catch
        {
        }
    }

    private static void TryDelete(string f)
    {
        try { if (File.Exists(f)) File.Delete(f); } catch { }
    }
}
