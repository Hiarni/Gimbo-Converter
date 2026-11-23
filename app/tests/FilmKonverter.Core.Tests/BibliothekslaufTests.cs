using FilmKonverter.Core.Converting;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class BibliothekslaufTests(TestMedia m)
{
    private (Profile P, string In, string Out) NewLibrary()
    {
        var root = Path.Combine(m.Dir, "bib-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        Directory.CreateDirectory(Path.Combine(input, "Serie"));
        var p = Profile.Cpu();
        p.Name = "Test";
        p.InputDir = input;
        p.OutputDir = Path.Combine(root, "Komprimiert");
        p.CpuPreset = "ultrafast";
        p.Quality = 30;
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        p.MinFreeSpaceGb = 0;
        p.EarlyAbortPercent = 0;
        return (p, input, p.OutputDir);
    }

    private async Task<Recorder> Run(Profile p, FfmpegTools? tools = null, IReadOnlyList<string>? sources = null)
    {
        var rec = new Recorder();
        await new LibraryRun(p, tools ?? m.Tools, rec) { Cache = null, Sources = sources }.RunAsync(CancellationToken.None);
        return rec;
    }

    private async Task<FfmpegTools> WrappedFfmpeg(string body)
    {
        var dir = Path.Combine(m.Dir, "wrapb-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        foreach (var (name, real, text) in new[] { ("ffmpeg", m.Tools.FfmpegPath, body), ("ffprobe", m.Tools.FfprobePath, "exec \"$REAL\" \"$@\"") })
        {
            var path = Path.Combine(dir, name);
            File.WriteAllText(path, "#!/bin/bash\nREAL='" + real + "'\n" + text + "\n");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return await FfmpegTools.TryLoadAsync(dir) ?? throw new InvalidOperationException("Skript-ffmpeg startet nicht");
    }

    // ---------------- Fehlversuche ----------------

    [Fact]
    public async Task Zwei_Fehlversuche_dann_Ruhe_bis_erneut_versuchen_und_eine_geaenderte_Quelle_zaehlt_neu()
    {
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Kaputt.mkv");
        File.WriteAllBytes(file, Enumerable.Range(0, 3000).Select(i => (byte)(i * 31)).ToArray());
        var paths = new LibraryPaths(input, output, file);
        List<ProblemItem> Problems() => ProblemFinder.Find(p, StatsLog.ReadAll([output]));

        var end = (await Run(p)).EndOf("Kaputt");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Equal(1, ErrorMarks.Get(paths, 3000)!.Count);
        var first = Assert.Single(Problems());
        Assert.Equal(ProblemKind.Error, first.Kind);
        Assert.Contains("versucht es noch einmal", first.Reason);
        Assert.Equal(file, first.SourcePath);
        Assert.Equal(Path.Combine("Serie", "Kaputt.mkv"), first.Relative);

        end = (await Run(p)).EndOf("Kaputt");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Contains("Zweimal fehlgeschlagen", end.Message);
        Assert.True(ErrorMarks.Get(paths, 3000)!.Blocks);

        var errorsLogged = StatsLog.ReadAll([output]).Count(r => r.Outcome == "Error");
        end = (await Run(p)).EndOf("Kaputt");
        Assert.Equal(Outcome.Skipped, end.Outcome);
        Assert.Contains("Zweimal fehlgeschlagen, steht unter Bibliothek → Probleme", end.Message);
        Assert.Equal(errorsLogged, StatsLog.ReadAll([output]).Count(r => r.Outcome == "Error"));

        var item = Assert.Single(Problems());
        Assert.Equal(ProblemKind.Error, item.Kind);
        Assert.Equal(ErrorMarks.FileFor(paths, 3000), item.MarkFile);
        Assert.Contains("2-mal fehlgeschlagen", item.Reason);
        Assert.Equal(FilmKonverter.Core.Library.FileLock.ComputerName, item.Machine);
        Assert.True(ProblemFinder.Reset(item));
        Assert.Null(ErrorMarks.Get(paths, 3000));
        Assert.Null(Assert.Single(Problems()).MarkFile);

        end = (await Run(p)).EndOf("Kaputt");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Equal(1, ErrorMarks.Get(paths, 3000)!.Count);
        await Run(p);
        Assert.Equal(Outcome.Skipped, (await Run(p)).EndOf("Kaputt").Outcome);

        File.WriteAllBytes(file, new byte[4000]);
        end = (await Run(p)).EndOf("Kaputt");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Equal(1, ErrorMarks.Get(paths, 4000)!.Count);
        Assert.False(File.Exists(ErrorMarks.FileFor(paths, 3000)));
        Assert.Single(Problems());
    }

    // ---------------- Nur Ton ----------------

    [Fact]
    public async Task Erfolg_loescht_den_Vermerk_und_eine_gezielt_gewaehlte_Datei_wird_trotz_Vermerk_versucht()
    {
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Gut.mkv");
        File.Copy(m.Sdr8, file);
        var size = new FileInfo(file).Length;
        var paths = new LibraryPaths(input, output, file);
        ErrorMarks.Add(paths, size, "Kodieren fehlgeschlagen (Exit-Code 1).");
        ErrorMarks.Add(paths, size, "Kodieren fehlgeschlagen (Exit-Code 1).");

        Assert.Equal(Outcome.Skipped, (await Run(p)).EndOf("Gut").Outcome);
        Assert.False(File.Exists(paths.Output));

        var rec = await Run(p, sources: [file]);
        Assert.Equal(Outcome.Encoded, rec.EndOf("Gut").Outcome);
        Assert.Null(ErrorMarks.Get(paths, size));
        Assert.Empty(ProblemFinder.Find(p, StatsLog.ReadAll([output])));

        File.Delete(paths.Output);
        MarkList.Clear(output, paths.Output);
        ErrorMarks.Add(paths, size, "Lesefehler beim Kodieren");
        Assert.Equal(Outcome.Encoded, (await Run(p)).EndOf("Gut").Outcome);
        Assert.Null(ErrorMarks.Get(paths, size));
    }

    // ---------------- Lesefehler und Laufzeit ----------------

    [Fact]
    public void Vermerk_haelt_Zahl_Zeit_PC_und_Grund()
    {
        var root = Path.Combine(m.Dir, "fm-" + Guid.NewGuid().ToString("N")[..6]);
        var src = Path.Combine(root, "in", "S", "Folge.S01E01 - Titel.mkv");
        var paths = new LibraryPaths(Path.Combine(root, "in"), Path.Combine(root, "out"), src);
        Assert.Null(ErrorMarks.Get(paths, 123));
        Assert.Equal(1, ErrorMarks.Add(paths, 123, "Kodieren fehlgeschlagen (Exit-Code 1)."));
        Assert.Equal(2, ErrorMarks.Add(paths, 123, "Prüfung: Datei - ist\nbeschädigt"));
        var mark = ErrorMarks.Get(paths, 123)!;
        Assert.Equal((2, true, "Prüfung: Datei - ist beschädigt"), (mark.Count, mark.Blocks, mark.Reason));
        Assert.Equal(FilmKonverter.Core.Library.FileLock.ComputerName, mark.Machine);
        Assert.Equal(Path.Combine(paths.MarkDir, "Folge.S01E01 - Titel.fehler_123.txt"), mark.File);
        Assert.False(MarkList.IsMarked(paths, "G95"));
        Assert.Equal([mark.File], MarkList.OwnMarks(paths.MarkDir, paths.Name));
        var other = new LibraryPaths(Path.Combine(root, "in"), Path.Combine(root, "out"), Path.Combine(root, "in", "S", "Folge.mkv"));
        Assert.Null(ErrorMarks.Get(other, 123));
        Assert.Equal(0, ErrorMarks.Clear(other));
        Assert.Equal(1, ErrorMarks.Clear(paths));
        Assert.Null(ErrorMarks.Get(paths, 123));
    }

    // ---------------- Noch zu neu, HDR-Hinweis ----------------

    [Fact]
    public async Task Nur_Ton_Kopie_darf_groesser_sein_als_die_Quelle_und_wird_nicht_vermerkt()
    {
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Folge.mkv");
        await m.Ff(["-i", m.Hevc8, "-f", "lavfi", "-i", "sine=440:sample_rate=44100", "-map", "0:v", "-map", "1:a", "-c:v", "copy", "-c:a", "wmav2", "-b:a", "32k", "-ac", "2", "-t", "2", file]);
        p.SkipHevc = true;
        var paths = new LibraryPaths(input, output, file);
        var srcBytes = new FileInfo(file).Length;

        var rec = await Run(p);
        var end = rec.EndOf("Folge");
        Assert.True(end.Outcome == Outcome.Encoded, end.Message + " | " + string.Join(" | ", rec.Lines.Select(l => l.Text)));
        var o = (await m.Tools.ProbeAsync(paths.Output))!;
        Assert.Equal(("hevc", "ac3"), (o.Video!.CodecName, o.Audio.Single().CodecName));
        var outBytes = new FileInfo(paths.Output).Length;
        Assert.True(outBytes > srcBytes, $"{outBytes} > {srcBytes}");
        Assert.Equal(0, end.GainBytes);
        Assert.Contains(rec.Lines, l => l.Text.Contains("Nicht kleiner als die Quelle, wird trotzdem übernommen"));
        Assert.False(MarkList.IsMarked(paths, p.MarkTag));
        Assert.True(MarkList.IsOk(paths, p.MarkTag, srcBytes, outBytes));

        Assert.Equal(Outcome.AlreadyOk, (await Run(p)).EndOf("Folge").Outcome);
    }

    [Fact]
    public async Task Lesefehler_beim_Kodieren_verwirft_das_Ergebnis()
    {
        if (OperatingSystem.IsWindows()) return;
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Folge.mkv");
        File.Copy(m.Sdr8, file);
        var tools = await WrappedFfmpeg("""
            "$REAL" "$@"; rc=$?
            for a in "$@"; do if [ "$a" = "libx265" ]; then
              echo "[in#0/matroska,webm @ 0x2] Error during demuxing: Input/output error" >&2
            fi; done
            exit $rc
            """);
        var rec = await Run(p, tools);
        var end = rec.EndOf("Folge");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Contains("Lesefehler", end.Message);
        Assert.Contains(rec.Lines, l => l.Kind == LineKind.Error && l.Text.Contains("Error during demuxing"));
        var paths = new LibraryPaths(input, output, file);
        Assert.False(File.Exists(paths.Output));
        Assert.False(File.Exists(paths.Temp));
        Assert.Equal(1, ErrorMarks.Get(paths, new FileInfo(file).Length)!.Count);
    }

    [Fact]
    public async Task Gekuerzte_Kodierung_faellt_an_der_Laufzeit_auf()
    {
        if (OperatingSystem.IsWindows()) return;
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Folge.mkv");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-t", "10", "-c:v", "libx264", "-preset", "ultrafast", file]);
        var tools = await WrappedFfmpeg("""
            args=("$@"); n=${#args[@]}; cut=
            for a in "$@"; do if [ "$a" = "libx265" ]; then cut=1; fi; done
            if [ -n "$cut" ]; then last="${args[$((n-1))]}"; unset 'args[$((n-1))]'; exec "$REAL" "${args[@]}" -t 8.5 "$last"; fi
            exec "$REAL" "$@"
            """);
        var end = (await Run(p, tools)).EndOf("Folge");
        Assert.Equal(Outcome.Error, end.Outcome);
        Assert.Contains("Laufzeit passt nicht", end.Message);
        Assert.False(File.Exists(new LibraryPaths(input, output, file).Output));
    }

    [Fact]
    public void Laufzeit_im_Bibliothekslauf_streng_im_Konverter_wie_bisher()
    {
        static MediaInfo M(double duration, double start = 0, string format = "matroska,webm") => new() { DurationSeconds = duration, StartSeconds = start, FormatName = format };
        Assert.Null(LibraryRun.DurationMismatch(M(2700), M(2696)));
        Assert.NotNull(LibraryRun.DurationMismatch(M(2700), M(2690)));
        Assert.NotNull(LibraryRun.DurationMismatch(M(300), M(298.54)));
        Assert.NotNull(LibraryRun.DurationMismatch(M(20), M(18.35)));
        Assert.Null(LibraryRun.DurationMismatch(M(20), M(19.2)));
        Assert.Null(LibraryRun.DurationMismatch(M(0), M(100)));
        Assert.Null(LibraryRun.DurationMismatch(M(100), M(0)));
        Assert.Null(LibraryRun.DurationMismatch(M(13, 2.995), M(10.02)));
        Assert.NotNull(LibraryRun.DurationMismatch(M(13, 2.995), M(11.5)));
        Assert.Null(LibraryRun.DurationMismatch(M(2700, format: "mpegts"), M(2690)));
        Assert.NotNull(LibraryRun.DurationMismatch(M(2700, format: "mpegts"), M(2600)));
        Assert.Null(LibraryRun.DurationMismatch(2700, 2690));
    }

    [Fact]
    public async Task Analyse_liest_die_Verpackung()
    {
        Assert.Equal("matroska,webm", (await m.Tools.ProbeAsync(m.Sdr8))!.FormatName);
        Assert.False((await m.Tools.ProbeAsync(m.Sdr8))!.DurationFromTimestamps);
        var ts = Path.Combine(m.Dir, "aufnahme-" + Guid.NewGuid().ToString("N")[..6] + ".ts");
        await m.Ff(["-i", m.Sdr8, "-c", "copy", ts]);
        Assert.True((await m.Tools.ProbeAsync(ts))!.DurationFromTimestamps);
    }

    [Fact]
    public async Task Quelle_die_gerade_geschrieben_wird_wird_unmittelbar_vor_dem_Kodieren_ausgelassen()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (p, input, output) = NewLibrary();
        var file = Path.Combine(input, "Serie", "Folge.mkv");
        File.Copy(m.Sdr8, file);
        var rec = new Recorder();
        var run = new LibraryRun(p, m.Tools, rec) { Cache = null };
        using (new FileStream(file, FileMode.Open, FileAccess.Write, FileShare.Read))
        {
            var (end, reason) = await run.ProcessFileAsync(file, CancellationToken.None);
            Assert.Equal(Outcome.Skipped, end.Outcome);
            Assert.Equal("zu neu", reason);
        }
        Assert.False(File.Exists(new LibraryPaths(input, output, file).Output));
        Assert.Equal(Outcome.Encoded, (await run.ProcessFileAsync(file, CancellationToken.None)).End.Outcome);
    }

    [Fact]
    public async Task Hinweis_auf_HDR_Helligkeitsangaben_bei_ffmpeg_unter_8()
    {
        var (p, input, _) = NewLibrary();
        File.Copy(m.Hdr10, Path.Combine(input, "Serie", "Hdr.mkv"));
        var rec = await Run(p);
        var hint = rec.Lines.Any(l => l.Label == "HDR" && l.Text.Contains("MaxCLL"));
        Assert.Equal(m.Tools.Major is > 0 and < 8, hint);
    }
}

public sealed class ConvertLevelTests
{
    private static MediaInfo Source(int w, int h, string fps, string codec = "h264", bool dolbyVision = false, int rotation = 0) => new()
    {
        Path = "x.mkv", DurationSeconds = 100,
        Streams =
        [
            new StreamInfo
            {
                Index = 0, CodecType = "video", CodecName = codec, Width = w, Height = h, PixFmt = dolbyVision ? "yuv420p10le" : "yuv420p", AvgFrameRate = fps, Rotation = rotation,
                ColorTransfer = dolbyVision ? "smpte2084" : "", SideDataTypes = dolbyVision ? ["DOVI configuration record"] : [], DvProfile = dolbyVision ? 8 : -1, DvCompatibility = dolbyVision ? 1 : -1,
            },
            new StreamInfo { Index = 1, CodecType = "audio", CodecName = "aac", Channels = 2 },
        ],
    };

    private static List<string> Plan(MediaInfo media, string hevc, ConvertTarget target = ConvertTarget.Apple, bool doviStrip = true) =>
        ConvertPlanner.Plan(media, target, new ConvertCaps { H264 = "libx264", Hevc = hevc, Hevc10 = true, Av1 = "libsvtav1", DoviStrip = doviStrip, DoviSafeH264 = true, Tonemap = true }, "x.mkv").Output;

    [Fact]
    public void NVENC_wird_beschriftet_aber_nicht_eingeschraenkt()
    {
        var a = Plan(Source(1920, 1080, "24000/1001"), "hevc_nvenc");
        Assert.DoesNotContain("-level:v", a);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
        a = Plan(Source(3840, 2160, "60/1"), "hevc_nvenc");
        Assert.Equal("hevc_metadata=level=153", a[a.IndexOf("-bsf:v") + 1]);
        a = Plan(Source(1920, 1080, "30/1", rotation: -90), "hevc_nvenc");
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public void AMF_bekommt_Vorgabe_und_Beschriftung()
    {
        var a = Plan(Source(1920, 1080, "25/1"), "hevc_amf");
        Assert.Equal("4.1", a[a.IndexOf("-level:v") + 1]);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public void Dolby_Vision_und_Level_in_einer_Kette()
    {
        var a = Plan(Source(3840, 2160, "24/1", "hevc", dolbyVision: true), "hevc_nvenc", ConvertTarget.Av1);
        Assert.Equal("dovi_rpu=strip=1", a[a.IndexOf("-bsf:v") + 1]);
        a = Plan(Source(3840, 2160, "24/1", "av1", dolbyVision: true), "hevc_nvenc");
        Assert.Single(a, x => x == "-bsf:v");
        Assert.Equal("dovi_rpu=strip=1,hevc_metadata=level=150", a[a.IndexOf("-bsf:v") + 1]);
        Assert.Equal("dovi_rpu=strip=1,hevc_metadata=level=150", ConvertPlanner.VideoBitstreamFilters("hevc_nvenc", true, HevcLevel.For(3840, 2160, 24)));
        Assert.Equal("dovi_rpu=strip=1", ConvertPlanner.VideoBitstreamFilters("libx265", true, HevcLevel.For(3840, 2160, 24)));
        Assert.Null(ConvertPlanner.VideoBitstreamFilters("libx265", false, null));
    }

    [Fact]
    public void Andere_Encoder_und_H264_bleiben_wie_sie_sind()
    {
        Assert.DoesNotContain("-bsf:v", Plan(Source(1920, 1080, "24/1"), "libx265"));
        Assert.DoesNotContain("-bsf:v", Plan(Source(1920, 1080, "24/1"), "hevc_qsv"));
        var h264 = Plan(Source(1920, 1080, "24/1", "mpeg2video"), "hevc_nvenc", ConvertTarget.Compatible);
        Assert.DoesNotContain("-bsf:v", h264);
        Assert.DoesNotContain("-level:v", h264);
    }
}
