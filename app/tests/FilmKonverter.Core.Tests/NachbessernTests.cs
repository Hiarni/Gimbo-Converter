using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class NachbessernTests(TestMedia m)
{
    private string NewDir(string prefix)
    {
        var d = Path.Combine(m.Dir, prefix + "-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Age(string file) => File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));

    private async Task<string> Level62(string dir, string name = "Folge.mkv", int seconds = 3, params string[] more)
    {
        var file = Path.Combine(dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=440:sample_rate=48000", "-t", seconds.ToString(),
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:level-idc=62", "-c:a", "ac3", .. more, file]);
        Age(file);
        return file;
    }

    private Profile Foreign()
    {
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(m.Dir, "nirgends-quelle");
        p.OutputDir = Path.Combine(m.Dir, "nirgends-ziel");
        return p;
    }

    private async Task<(Recorder Rec, CleanupRun Run)> Clean(Profile p, CleanupOptions opt, FfmpegTools? tools = null)
    {
        var rec = new Recorder();
        var run = new CleanupRun(p, tools ?? m.Tools, rec) { Cache = null };
        await run.RunAsync(opt, CancellationToken.None);
        return (rec, run);
    }

    private async Task<FfmpegTools> Wrapped(string? ffmpeg = null, string? ffprobe = null)
    {
        var dir = NewDir("wrap");
        void Script(string name, string real, string? body)
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "#!/bin/bash\nREAL='" + real + "'\n" + (body ?? "exec \"$REAL\" \"$@\"") + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UserWriteExecute);
        }
        Script("ffmpeg", m.Tools.FfmpegPath, ffmpeg);
        Script("ffprobe", m.Tools.FfprobePath, ffprobe);
        return await FfmpegTools.TryLoadAsync(dir) ?? throw new InvalidOperationException("Skript-ffmpeg startet nicht");
    }

    private const UnixFileMode UserWriteExecute = UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private async Task<byte[]> StreamBytes(string file, int index)
    {
        var tmp = Path.Combine(m.Dir, "bytes-" + Guid.NewGuid().ToString("N")[..6] + ".bin");
        await m.Ff(["-i", file, "-map", "0:" + index, "-c", "copy", "-f", "data", tmp]);
        return File.ReadAllBytes(tmp);
    }

    // ---------------- Spuren gehen nicht verloren ----------------

    [Fact]
    public async Task Titelbilder_zweite_Videospur_und_Schriftart_bleiben()
    {
        var lib = NewDir("cover");
        var jpg = Path.Combine(lib, "bild.jpg");
        var png = Path.Combine(lib, "bild.png");
        var font = Path.Combine(lib, "font.ttf");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=64x48", "-frames:v", "1", "-update", "1", jpg]);
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=32x32", "-frames:v", "1", "-update", "1", png]);
        File.WriteAllBytes(font, new byte[2048]);
        var file = Path.Combine(lib, "Film.mkv");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=440:sample_rate=48000",
            "-t", "3", "-map", "0:v", "-map", "1:v", "-map", "2:a", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:level-idc=62", "-c:a", "ac3",
            "-attach", jpg, "-metadata:s:t:0", "mimetype=image/jpeg", "-metadata:s:t:0", "filename=cover.jpg",
            "-attach", font, "-metadata:s:t:1", "mimetype=application/x-truetype-font", "-metadata:s:t:1", "filename=font.ttf",
            "-attach", png, "-metadata:s:t:2", "mimetype=image/png", "-metadata:s:t:2", "filename=back.png", "-metadata:s:t:2", "title=Rückseite", file]);
        File.Delete(jpg); File.Delete(png);
        Age(file);
        var before = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(2, before.Streams.Count(s => s.CodecType == "video" && s.AttachedPic));
        var jpgBytes = await StreamBytes(file, before.Streams.First(s => s.AttachedPic && s.CodecName == "mjpeg").Index);

        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        var end = rec.Files.Single().End;
        Assert.True(end.Outcome == Outcome.Replaced, end.Message + " | " + string.Join(" | ", rec.Lines.Select(l => l.Text)));

        var o = (await m.Tools.ProbeAsync(file))!;
        var videos = o.Streams.Where(s => s.CodecType == "video" && !s.AttachedPic).ToList();
        Assert.Equal(2, videos.Count);
        Assert.Equal((640, 123), (videos[0].Width, videos[0].Level));
        Assert.Equal((320, 186), (videos[1].Width, videos[1].Level));
        var pics = o.Streams.Where(s => s.CodecType == "video" && s.AttachedPic).ToList();
        Assert.Equal(["cover.jpg", "back.png"], pics.Select(s => s.Tags["filename"]));
        Assert.Equal(["image/jpeg", "image/png"], pics.Select(s => s.MimeType));
        Assert.Equal("Rückseite", pics[1].Title);
        Assert.Equal(jpgBytes, await StreamBytes(file, pics[0].Index));
        Assert.Equal("font.ttf", Assert.Single(o.Attachments).Tags["filename"]);
        Assert.Single(o.Audio);
        Assert.Empty(Directory.GetFiles(lib, "*.gimbo-tmp"));
    }

    // ---------------- Strenge Pruefungen ----------------

    [Fact]
    public void Was_sich_nicht_uebernehmen_laesst_ist_ein_Verlust_und_loest_kein_Umschreiben_aus()
    {
        MediaInfo Media(params StreamInfo[] more) => new()
        {
            Path = "x.mkv", DurationSeconds = 100,
            Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080, PixFmt = "yuv420p", Level = 123 }, .. more],
        };
        var text = new StreamInfo { Index = 2, CodecType = "subtitle", CodecName = "hdmv_text_subtitle" };
        var data = new StreamInfo { Index = 3, CodecType = "data", CodecName = "bin_data" };
        var noType = new StreamInfo { Index = 4, CodecType = "attachment", CodecName = "none" };
        var p = Profile.Nvidia();
        var playability = CleanupRun.PlayabilityRules(p);

        var plan = CleanupRun.Analyze(Media(new StreamInfo { Index = 1, CodecType = "audio", CodecName = "ac3", Channels = 6 }, text, data, noType), p, playability, true);
        Assert.Empty(plan.Changes);
        Assert.Equal(3, plan.Losses.Count);
        Assert.All(plan.Losses, l => Assert.Equal(ChangeKind.Lost, l.Kind));

        plan = CleanupRun.Analyze(Media(new StreamInfo { Index = 1, CodecType = "audio", CodecName = "dts", Profile = "DTS", Channels = 6 }, text), p, p, false);
        Assert.Single(plan.Changes);
        Assert.Contains("hdmv_text_subtitle", Assert.Single(plan.Losses).Text);
        Assert.DoesNotContain(plan.Changes, c => c.Kind == ChangeKind.SubtitleRemove);
    }

    // ---------------- Probelauf, Beschriftungen, Zeiten ----------------

    [Fact]
    public void Aenderungszeilen_sagen_was_passiert()
    {
        var media = new MediaInfo
        {
            Path = "x.mkv", DurationSeconds = 100,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080, PixFmt = "yuv420p", Level = 186, AvgFrameRate = "24/1" },
                new StreamInfo { Index = 1, CodecType = "audio", CodecName = "ac3", Channels = 6, IsDefault = true, Tags = new(StringComparer.OrdinalIgnoreCase) { ["language"] = "eng" } },
                new StreamInfo { Index = 2, CodecType = "audio", CodecName = "dts", Profile = "DTS", Channels = 6, Tags = new(StringComparer.OrdinalIgnoreCase) { ["language"] = "ger", ["title"] = "DTS 5.1" } },
                new StreamInfo { Index = 3, CodecType = "subtitle", CodecName = "hdmv_pgs_subtitle", IsDefault = true, Tags = new(StringComparer.OrdinalIgnoreCase) { ["language"] = "ger" } },
            ],
        };
        var p = Profile.Nvidia();
        p.ConvertDts = false;
        p.DefaultAudioLanguage = "de";
        var plan = CleanupRun.Analyze(media, p, CleanupRun.PlayabilityRules(p), true);
        Assert.Equal([ChangeKind.AudioConvert, ChangeKind.SubtitleDefault, ChangeKind.Level], plan.Changes.Select(c => c.Kind));
        var convert = plan.Changes[0].Text;
        Assert.Contains("auch wenn die Umwandlung im Profil ausgeschaltet ist", convert);
        Assert.Contains("Titel „DTS 5.1“ entfällt", convert);
        Assert.Contains("Medienserver", plan.Changes[1].Text);
        Assert.False(plan.Audio.MovedFirst);

        plan = CleanupRun.Analyze(media, p, p, false);
        Assert.DoesNotContain(plan.Changes, c => c.Kind == ChangeKind.AudioConvert);
        Assert.Single(plan.Changes, c => c.Kind == ChangeKind.AudioDefault && c.Text.Contains("erste Stelle"));
        Assert.Single(plan.Changes, c => c.Kind == ChangeKind.AudioFlags);
    }

    // ---------------- Vermerke und Statistik im Zielordner ----------------

    [Fact]
    public async Task Bild_Untertitel_verliert_nur_das_Standard_Kennzeichen()
    {
        var lib = NewDir("pgsfix");
        var sup = Path.Combine(lib, "bild.sup");
        File.WriteAllBytes(sup, PgsSample.Build());
        var srt = Path.Combine(lib, "text.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nHallo\n\n");
        var file = Path.Combine(lib, "Film.mkv");
        await m.Ff(["-i", m.Hevc8, "-i", sup, "-i", srt, "-map", "0:v", "-map", "1", "-map", "2", "-c", "copy",
            "-disposition:s:0", "default", "-disposition:s:1", "default", file]);
        Age(file);
        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        var end = rec.Files.Single().End;
        Assert.True(end.Outcome == Outcome.Replaced, end.Message);
        Assert.Contains("Bild-Untertitel nicht mehr als Standard", end.Message);
        var subs = (await m.Tools.ProbeAsync(file))!.Subtitles.ToList();
        Assert.Equal(["hdmv_pgs_subtitle", "subrip"], subs.Select(s => s.CodecName));
        Assert.Equal([false, true], subs.Select(s => s.IsDefault));
    }

    [Fact]
    public void Lesefehler_Zeilen_werden_erkannt()
    {
        string[] real =
        [
            "[matroska,webm @ 0x55855daeefc0] Read error",
            "[in#0/matroska,webm @ 0x55855daeeec0] Error during demuxing: Input/output error",
            "[in#0/matroska,webm @ 0x55855daeeec0] Error retrieving a packet from demuxer: Input/output error",
            "[in#0/mpegts @ 0x55d180079c40] Error during demuxing: Input/output error",
            "[matroska,webm @ 0x1] Read error at pos. 1204593 (0x12616f)",
            "[avi @ 0x1] I/O error occurred",
            "[mov,mp4,m4a,3gp,3g2,mj2 @ 0x1] error reading header",
        ];
        Assert.Equal(real, FfmpegErrors.ReadErrors(real));
        string[] other =
        [
            "[matroska,webm @ 0x559290dacfc0] File ended prematurely",
            "[hevc @ 0x6] Could not find ref with POC 12",
            "[null @ 0x5] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 29 >= 29",
            "[h264 @ 0x55dfb39cdbc0] error while decoding MB 5 0",
        ];
        Assert.Empty(FfmpegErrors.ReadErrors(other));
        Assert.True(FfmpegErrors.IsDiskFull(["av_interleaved_write_frame(): No space left on device"]));
        Assert.False(FfmpegErrors.IsDiskFull(real));
    }

    [Fact]
    public async Task Lesefehler_beim_Umschreiben_laesst_das_Original_stehen()
    {
        if (OperatingSystem.IsWindows()) return;
        var lib = NewDir("eio");
        var file = await Level62(lib);
        var size = new FileInfo(file).Length;
        var written = File.GetLastWriteTimeUtc(file);
        var tools = await Wrapped(ffmpeg: """
            "$REAL" "$@"; rc=$?
            for a in "$@"; do if [ "$a" = "matroska" ]; then
              echo "[matroska,webm @ 0x1] Read error" >&2
              echo "[in#0/matroska,webm @ 0x2] Error during demuxing: Input/output error" >&2
            fi; done
            exit $rc
            """);
        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false }, tools);
        var end = rec.Files.Single().End;
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Contains("Lesefehler", end.Message);
        Assert.Contains(rec.Lines, l => l.Kind == LineKind.Error && l.Text.Contains("Error during demuxing"));
        Assert.Equal(size, new FileInfo(file).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(file));
        Assert.Equal(186, (await m.Tools.ProbeAsync(file))!.Video!.Level);
        Assert.Empty(Directory.GetFiles(lib, "*.gimbo-tmp"));
    }

    [Fact]
    public async Task Gekuerztes_Ergebnis_wird_verworfen_auch_wenn_nur_anderthalb_Sekunden_fehlen()
    {
        if (OperatingSystem.IsWindows()) return;
        var lib = NewDir("kurz");
        var file = await Level62(lib, seconds: 10);
        var size = new FileInfo(file).Length;
        var tools = await Wrapped(ffmpeg: """
            args=("$@"); n=${#args[@]}; cut=
            for a in "$@"; do if [ "$a" = "matroska" ]; then cut=1; fi; done
            if [ -n "$cut" ]; then last="${args[$((n-1))]}"; unset 'args[$((n-1))]'; exec "$REAL" "${args[@]}" -t 8.5 "$last"; fi
            exec "$REAL" "$@"
            """);
        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false }, tools);
        var end = rec.Files.Single().End;
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Contains("Laufzeit weicht ab", end.Message);
        Assert.Equal(size, new FileInfo(file).Length);
        Assert.Empty(Directory.GetFiles(lib, "*.gimbo-tmp"));
    }

    [Fact]
    public async Task Datei_mit_spaeterem_Beginn_der_Zeitstempel_wird_nicht_faelschlich_verworfen()
    {
        var lib = NewDir("start");
        var file = await Level62(lib, seconds: 4, more: ["-output_ts_offset", "3"]);
        var before = (await m.Tools.ProbeAsync(file))!;
        Assert.True(before.StartSeconds > 2.5 && before.DurationSeconds > 6.5, $"{before.StartSeconds} / {before.DurationSeconds}");
        var packets = await m.Tools.CountVideoPacketsAsync(file);
        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        var end = rec.Files.Single().End;
        Assert.True(end.Outcome == Outcome.Replaced, end.Message);
        Assert.Equal(packets, await m.Tools.CountVideoPacketsAsync(file));
        Assert.Equal(123, (await m.Tools.ProbeAsync(file))!.Video!.Level);

        var src = new MediaInfo { DurationSeconds = 7, StartSeconds = 3 };
        Assert.True(DurationCheck.Matches(src, 4.02, DurationCheck.CopyTolerance));
        Assert.True(DurationCheck.Matches(src, 7.3, DurationCheck.CopyTolerance));
        Assert.False(DurationCheck.Matches(src, 5.5, DurationCheck.CopyTolerance));
        Assert.False(DurationCheck.Matches(new MediaInfo { DurationSeconds = 300 }, 298.54, DurationCheck.CopyTolerance));
    }

    [Fact]
    public async Task Verlorene_Sperre_laesst_keine_Temp_Datei_zurueck()
    {
        if (OperatingSystem.IsWindows()) return;
        var lib = NewDir("sperre");
        var file = await Level62(lib);
        var size = new FileInfo(file).Length;
        var tools = await Wrapped(ffprobe: """
            for a in "$@"; do case "$a" in *.mkv.gimbo-tmp) echo "ANDERERPC 2026-01-01 10:00 #fremd" > "${a%.mkv.gimbo-tmp}.lock/info.txt";; esac; done
            exec "$REAL" "$@"
            """);
        var (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false }, tools);
        Assert.Equal(Outcome.OtherPc, rec.Files.Single().End.Outcome);
        Assert.Empty(Directory.GetFiles(lib, "*.gimbo-tmp"));
        Assert.Equal(size, new FileInfo(file).Length);
        Assert.Equal(186, (await m.Tools.ProbeAsync(file))!.Video!.Level);
    }

    [Fact]
    public async Task Probelauf_ist_nachvollziehbar_Liste_in_der_Meldung_eigener_Zaehler_Summen_am_Ende()
    {
        var lib = NewDir("probe");
        var a = await Level62(lib, "Serie/A.mkv");
        await Level62(lib, "Serie/B.mkv");
        var ok = Path.Combine(lib, "Serie", "C.mkv");
        File.Copy(m.Hevc8, ok);
        Age(ok);
        var size = new FileInfo(a).Length;
        var written = File.GetLastWriteTimeUtc(a);

        var (rec, run) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false, DryRun = true });
        var endA = rec.EndOf("A.mkv");
        Assert.Equal(Outcome.Skipped, endA.Outcome);
        Assert.Equal("WÜRDE ÄNDERN", endA.Badge);
        Assert.Contains("Video-Level 6.2 → 4.1", endA.Message);
        Assert.Equal("PASST", rec.EndOf("C.mkv").Badge);
        Assert.Equal(2, run.Counters.WouldChange);
        Assert.Equal(0, run.Counters.Skipped);
        Assert.Equal(1, run.Counters.AlreadyOk);
        Assert.Equal(3, run.Counters.Processed);
        var summary = rec.Statuses.Last();
        Assert.Contains("2 von 3", summary);
        Assert.Contains("Video-Level korrigieren: 2", summary);
        Assert.Contains(rec.Lines, l => l.Text == summary);
        Assert.Equal(size, new FileInfo(a).Length);
        Assert.Equal(written, File.GetLastWriteTimeUtc(a));
        Assert.Equal(186, (await m.Tools.ProbeAsync(a))!.Video!.Level);
    }

    [Fact]
    public async Task Ehrliche_Beschriftung_und_Aenderungszeit_plus_zwei_Sekunden()
    {
        var lib = NewDir("zeit");
        var file = await Level62(lib);
        var written = File.GetLastWriteTimeUtc(file);

        var (rec, run) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        var end = rec.Files.Single().End;
        Assert.Equal(Outcome.Replaced, end.Outcome);
        Assert.Equal("NACHGEBESSERT", end.Badge);
        Assert.StartsWith("Nachgebessert: Video-Level 6.2 → 4.1", end.Message);
        Assert.Contains("1 von 1", rec.Statuses.Last());
        Assert.Equal(0, run.Counters.WouldChange);
        Assert.Equal(written.AddSeconds(2), File.GetLastWriteTimeUtc(file));
        Assert.Equal(new DateTime(2020, 5, 1, 12, 0, 2, DateTimeKind.Utc), CleanupRun.TouchedTime(new DateTime(2020, 5, 1, 12, 0, 0, DateTimeKind.Utc)));

        (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        Assert.Equal(Outcome.AlreadyOk, rec.Files.Single().End.Outcome);
        Assert.Equal("PASST", rec.Files.Single().End.Badge);
        Assert.Equal(written.AddSeconds(2), File.GetLastWriteTimeUtc(file));
    }

    [Fact]
    public async Task Prueflauf_beschriftet_gepruefte_Dateien_als_geprueft()
    {
        var output = NewDir("pruef");
        File.Copy(m.Hevc8, Path.Combine(output, "Folge.mkv"));
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(m.Dir, "nirgends-quelle");
        p.OutputDir = output;
        async Task<FileEnd> Verify()
        {
            var rec = new Recorder();
            await new VerifyRun(p, m.Tools, rec).RunAsync(new VerifyOptions(), CancellationToken.None);
            return rec.Files.Single().End;
        }
        var first = await Verify();
        Assert.Equal((Outcome.Encoded, "GEPRÜFT"), (first.Outcome, first.Badge));
        var second = await Verify();
        Assert.Equal((Outcome.AlreadyOk, "GEPRÜFT"), (second.Outcome, second.Badge));
    }

    [Fact]
    public async Task Frisch_geaenderte_Dateien_bleiben_liegen_andere_Videodateien_werden_genannt()
    {
        var lib = NewDir("frisch");
        var file = await Level62(lib);
        File.SetLastWriteTime(file, DateTime.Now.AddMinutes(-2));
        File.WriteAllBytes(Path.Combine(lib, "Urlaub.mp4"), new byte[10]);
        File.WriteAllBytes(Path.Combine(lib, "Alt.avi"), new byte[10]);
        var size = new FileInfo(file).Length;

        var (rec, run) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        Assert.Empty(rec.Files);
        Assert.Equal(3, run.Counters.Filtered);
        Assert.Contains("2 andere Videodateien (nur MKV wird nachgebessert)", run.Counters.FilterText);
        Assert.Contains("10 Minuten", run.Counters.FilterText);
        Assert.Contains(rec.Statuses, s => s.Contains("2 andere Videodateien (nur MKV wird nachgebessert)"));
        Assert.Equal(size, new FileInfo(file).Length);

        File.SetLastWriteTime(file, DateTime.Now.AddMinutes(-11));
        (rec, _) = await Clean(Foreign(), new CleanupOptions { Folder = lib, ApplyProfileRules = false });
        Assert.Equal(Outcome.Replaced, rec.Files.Single().End.Outcome);
    }

    [Fact]
    public async Task Unterordner_des_Ziels_zaehlt_als_Ziel_auch_ueber_einen_anderen_Weg()
    {
        var root = NewDir("ziel");
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        Directory.CreateDirectory(Path.Combine(input, "Serie", "Staffel 1"));
        var source = Path.Combine(input, "Serie", "Staffel 1", "Folge.mkv");
        File.WriteAllBytes(source, new byte[5000]);
        var file = await Level62(output, Path.Combine("Serie", "Staffel 1", "Folge.mkv"));
        var oldBytes = new FileInfo(file).Length;
        var p = Profile.Nvidia();
        p.InputDir = input;
        p.OutputDir = output;
        var paths = new LibraryPaths(input, output, source);
        MarkList.MarkOk(paths, p.MarkTag, 5000, oldBytes);

        Assert.Equal("", CleanupRun.TargetRelative(output, output));
        Assert.Equal(Path.Combine("Serie", "Staffel 1"), CleanupRun.TargetRelative(Path.Combine(output, "Serie", "Staffel 1"), output));
        Assert.Null(CleanupRun.TargetRelative(input, output));
        Assert.Empty(Directory.GetFiles(output, ".gimbo-ort-*"));

        var (rec, _) = await Clean(p, new CleanupOptions { Folder = Path.Combine(output, "Serie") });
        Assert.Equal(Outcome.Replaced, rec.Files.Single().End.Outcome);
        var newBytes = new FileInfo(file).Length;
        Assert.True(MarkList.IsOk(paths, p.MarkTag, 5000, newBytes));
        if (newBytes != oldBytes) Assert.False(MarkList.IsOk(paths, p.MarkTag, 5000, oldBytes));
        var stat = Assert.Single(StatsLog.ReadAll([output]), r => r.Outcome == "Cleaned");
        Assert.Equal(Path.Combine("Serie", "Staffel 1", "Folge.mkv"), stat.File);
        Assert.Equal("Serie", stat.Group);
        Assert.False(Directory.Exists(Path.Combine(output, "Serie", LibraryPaths.StatsFolder)));

        var alias = Path.Combine(root, "Laufwerk");
        try { Directory.CreateSymbolicLink(alias, output); }
        catch { return; }
        Assert.Equal(Path.Combine("Serie", "Staffel 1"), CleanupRun.TargetRelative(Path.Combine(alias, "Serie", "Staffel 1"), output));
        Assert.Equal("", CleanupRun.TargetRelative(alias, output));
    }
}
