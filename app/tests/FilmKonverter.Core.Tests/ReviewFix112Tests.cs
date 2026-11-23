using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Rename;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

public sealed class ReviewFix112Tests
{
    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "fk-112-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Aufloesung_hinter_der_Folge_ist_keine_Doppelfolge()
    {
        var a = EpisodeParser.Parse("Show.S01E05-720p.German", null);
        Assert.Equal(5, a.Episode);
        Assert.Null(a.EpisodeEnd);
        var b = EpisodeParser.Parse("Show.S01E01E02E03.German", null);
        Assert.Equal((1, 3), (b.Episode, b.EpisodeEnd));
        var c = EpisodeParser.Parse("Show.S01E05-06.Titel", null);
        Assert.Equal((5, 6), (c.Episode, c.EpisodeEnd));
    }

    [Fact]
    public void Vollbreiten_Ziffern_bringen_die_Analyse_nicht_zum_Absturz()
    {
        var e = EpisodeParser.Parse("Show S01E０５", null);
        Assert.Null(e.Episode);
        Assert.NotNull(EpisodeParser.ParseMovie("Film ２００９"));
    }

    [Fact]
    public void Jahr_am_Ende_ohne_Release_Angaben_ist_unsicher()
    {
        Assert.True(EpisodeParser.ParseMovie("Die Heldin 1984").YearUncertain);
        Assert.False(EpisodeParser.ParseMovie("Horizont.2009.German.1080p.BluRay").YearUncertain);
        Assert.False(EpisodeParser.ParseMovie("Horizont (2009)").YearUncertain);
    }

    [Fact]
    public void Begleitdateien_einer_anderen_Fassung_bleiben_liegen()
    {
        var d = Tmp();
        foreach (var f in new[] { "Horizont.2009.mkv", "Horizont.2009.nfo", "Horizont.2009.Extended.mkv", "Horizont.2009.Extended.nfo", "Horizont.2009.Extended.de.srt", "Horizont.2009.de.srt" })
            File.WriteAllText(Path.Combine(d, f), "x");
        var c = RenamePlanner.Companions(Path.Combine(d, "Horizont.2009.mkv")).Select(Path.GetFileName).OrderBy(x => x).ToList();
        Assert.Equal(["Horizont.2009.de.srt", "Horizont.2009.nfo"], c);
    }

    [Fact]
    public void Rueckgaengig_wartet_waehrend_eine_Datei_bearbeitet_wird()
    {
        var d = Tmp();
        var neu = Path.Combine(d, "S01E01 - Titel.mkv");
        File.WriteAllText(neu, "x");
        var journal = Path.Combine(d, "lauf.jsonl");
        File.WriteAllLines(journal, ["{}", System.Text.Json.JsonSerializer.Serialize(new FileMove(Path.Combine(d, "alt.mkv"), neu))]);
        Directory.CreateDirectory(Path.Combine(d, "S01E01 - Titel.lock"));
        var r = RenamePlanner.Undo(journal);
        Assert.Equal(0, r.Renamed);
        Assert.True(File.Exists(neu));
        Assert.True(File.Exists(journal));
    }

    [Fact]
    public void Sprachcodes_ueber_die_alte_Tabelle_hinaus()
    {
        var list = Languages.ParseList("de, uk, ro, hr, he");
        Assert.True(Languages.Allowed("ukr", list));
        Assert.True(Languages.Allowed("ron", list));
        Assert.True(Languages.Allowed("rum", list));
        Assert.True(Languages.Allowed("hrv", list));
        Assert.True(Languages.Allowed("heb", list));
        Assert.False(Languages.Allowed("fre", list));
    }

    [Fact]
    public void Untertitel_die_MKV_nicht_kann_entfallen_mit_Hinweis()
    {
        var media = new MediaInfo
        {
            Path = "rec.ts", DurationSeconds = 100,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1280, Height = 720, PixFmt = "yuv420p" },
                new StreamInfo { Index = 1, CodecType = "audio", CodecName = "s302m", Channels = 2 },
                new StreamInfo { Index = 2, CodecType = "subtitle", CodecName = "dvb_teletext" },
                new StreamInfo { Index = 3, CodecType = "subtitle", CodecName = "subrip" },
                new StreamInfo { Index = 4, CodecType = "subtitle", CodecName = "mov_text" },
            ],
        };
        var p = Profile.Nvidia();
        p.ConvertLosslessAudio = false;
        var subs = AudioPlanner.PlanSubtitles(media, p);
        Assert.False(subs.AllMapped);
        Assert.Equal([3, 4], subs.Tracks.Select(t => t.SourceIndex));
        Assert.Null(subs.Tracks[0].Codec);
        Assert.Equal("srt", subs.Tracks[1].Codec);
        Assert.Single(subs.Removed);
        var audio = AudioPlanner.Plan(media, p);
        Assert.Equal("flac", audio.Tracks.Single().Codec);
    }

    [Fact]
    public void Dolby_Vision_wird_beim_Neukodieren_entfernt()
    {
        var v = new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 3840, Height = 2160, PixFmt = "yuv420p10le", SideDataTypes = ["DOVI configuration record"], DvProfile = 8, DvCompatibility = 1 };
        var plan = new EncodePlan(v, null, 10, "p010le", "main10", new AudioPlan([]), "", "");
        var args = ArgsBuilder.BuildEncode(Profile.Nvidia(), plan, "in.mkv", "out.mkv");
        Assert.Equal("dovi_rpu=strip=1,hevc_metadata=level=123", args[args.IndexOf("-bsf:v") + 1]);
        var copy = ArgsBuilder.BuildEncode(Profile.Nvidia(), plan with { VideoCopy = true }, "in.mkv", "out.mkv");
        Assert.DoesNotContain(copy, x => x.Contains("dovi_rpu"));
    }

    [Fact]
    public void Niedrige_Datenrate_mit_Tonumwandlung_kopiert_das_Video()
    {
        var media = new MediaInfo
        {
            Path = "x.mkv", DurationSeconds = 1000, SizeBytes = 250_000_000 + 100_000_000,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080, PixFmt = "yuv420p", BitRate = 2_000_000 },
                new StreamInfo { Index = 1, CodecType = "audio", CodecName = "truehd", Channels = 6, BitRate = 800_000 },
            ],
        };
        var p = Profile.Nvidia();
        p.MinRateMbit = 8;
        p.ConvertLosslessAudio = true;
        var d = Planner.Decide(media, p);
        Assert.True(d.Encode);
        Assert.True(d.Plan!.VideoCopy);
    }

    [Fact]
    public void Temp_Namen_und_Kopierzeit()
    {
        Assert.True(Scanner.IsTemp("Folge.part.mkv"));
        Assert.True(Scanner.IsTemp("Folge.part.mp4"));
        Assert.False(Scanner.IsTemp("Wueste.Part.Two.mkv"));
        var f = Path.Combine(Tmp(), "neu.mkv");
        File.WriteAllText(f, "x");
        File.SetLastWriteTime(f, new DateTime(2001, 1, 1));
        if (OperatingSystem.IsWindows()) Assert.True(Scanner.ChangedAt(f) > DateTime.Now.AddMinutes(-5));
    }

    [Fact]
    public void Statistik_Zeile_nach_Absturz_kostet_nicht_den_naechsten_Eintrag()
    {
        var root = Tmp();
        Directory.CreateDirectory(StatsLog.Folder(root));
        File.WriteAllText(Path.Combine(StatsLog.Folder(root), "PC1.jsonl"), "{\"TimeUtc\":\"2026-01-01T00:00:00Z\",\"Mach");
        Assert.True(StatsLog.Append(root, new StatsRecord { TimeUtc = DateTime.UtcNow, Machine = "PC1", File = "a.mkv", Outcome = "Encoded" }));
        Assert.Contains(StatsLog.ReadAll([root]), r => r.File == "a.mkv");
    }

    [Fact]
    public void Einstellungen_mit_null_starten_trotzdem()
    {
        var d = Tmp();
        var store = new SettingsStore(d);
        File.WriteAllText(store.SettingsFile, "{\"SchemaVersion\":5,\"Profiles\":[null,{\"Name\":\"X\",\"Extensions\":null,\"TestValues\":null}],\"SelectedProfile\":\"X\",\"RenameRecent\":null}");
        var s = store.LoadSettings();
        Assert.Equal("X", s.Profiles.Single().Name);
        Assert.NotEmpty(s.Profiles[0].Extensions);
        Assert.NotNull(s.Profiles[0].TestValues);
        Assert.NotNull(s.RenameRecent);
    }

    [Fact]
    public void Eigene_liegengebliebene_Warteschlangen_Sperre_ohne_Inhalt_blockiert_nicht_sofort_fremde()
    {
        var root = Tmp();
        var lockDir = Path.Combine(WorkQueue.Folder(root), "queue.lock");
        Directory.CreateDirectory(lockDir);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(WorkQueue.Modify(root, l => l.Clear()));
        Assert.True(sw.Elapsed.TotalSeconds >= 7);
        Assert.True(Directory.Exists(lockDir));
    }
}

[Collection("media")]
public sealed class HevcLevelTests(TestMedia m)
{
    [Fact]
    public void Level_nach_Aufloesung_und_Bildrate()
    {
        Assert.Equal("4.1", HevcLevel.For(1920, 1080, 23.976).Name);
        Assert.Equal("4.1", HevcLevel.For(1280, 720, 50).Name);
        Assert.Equal("4.1", HevcLevel.For(720, 576, 25).Name);
        Assert.Equal("5.0", HevcLevel.For(1920, 1080, 100).Name);
        Assert.Equal("5.0", HevcLevel.For(3840, 2160, 23.976).Name);
        Assert.Equal("5.1", HevcLevel.For(3840, 2160, 59.94).Name);
        Assert.Equal("5.2", HevcLevel.For(3840, 2160, 100).Name);
        Assert.Equal(123, HevcLevel.For(1920, 800, 24).Idc);
    }

    [Fact]
    public void Encoder_bekommt_Level_des_fertigen_Bildes()
    {
        var v = new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 3840, Height = 2160, PixFmt = "yuv420p", AvgFrameRate = "24000/1001" };
        var plan = new EncodePlan(v, string.Format(Planner.ScaleFilterTemplate, 1920, 1080), 8, "yuv420p", "main", new AudioPlan([]), "", "");
        var p = Profile.Nvidia();
        var a = ArgsBuilder.BuildEncode(p, plan, "in", "out");
        Assert.DoesNotContain("-level:v", a);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
        p.MaxWidth = 0; p.MaxHeight = 0;
        a = ArgsBuilder.BuildEncode(p, plan, "in", "out");
        Assert.Equal("hevc_metadata=level=150", a[a.IndexOf("-bsf:v") + 1]);
        a = ArgsBuilder.BuildEncode(Profile.Amd(), plan, "in", "out");
        Assert.Equal("4.1", a[a.IndexOf("-level:v") + 1]);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public async Task Nachbessern_korrigiert_zu_hohen_Level_ohne_Neukodierung()
    {
        var root = Path.Combine(m.Dir, "lvl-" + Guid.NewGuid().ToString("N")[..6]);
        var output = Path.Combine(root, "Komprimiert");
        Directory.CreateDirectory(Path.Combine(output, "Serie"));
        var file = Path.Combine(output, "Serie", "Folge.mkv");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24", "-f", "lavfi", "-i", "sine=440:sample_rate=48000", "-t", "3",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:level-idc=62", "-c:a", "aac", file]);
        File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));
        var before = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(186, before.Video!.Level);
        Assert.NotNull(HevcLevel.FixFor(before));

        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(root, "Serien");
        p.OutputDir = output;
        var obs = new NullObserver();
        await new CleanupRun(p, m.Tools, obs) { Cache = null, WriteStatsLog = false }.RunAsync(new CleanupOptions(), CancellationToken.None);
        var after = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(123, after.Video!.Level);
        Assert.Equal(1, obs.Replaced);
        var obs2 = new NullObserver();
        await new CleanupRun(p, m.Tools, obs2) { Cache = null, WriteStatsLog = false }.RunAsync(new CleanupOptions(), CancellationToken.None);
        Assert.Equal(0, obs2.Replaced);
    }

    [Fact]
    public void Nur_Ton_Kopie_korrigiert_den_Level_gleich_mit()
    {
        var media = new MediaInfo
        {
            Path = "x.mkv", DurationSeconds = 100, SizeBytes = 1_000_000_000,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080, PixFmt = "yuv420p", AvgFrameRate = "24/1", Level = 186 },
                new StreamInfo { Index = 1, CodecType = "audio", CodecName = "truehd", Channels = 6 },
            ],
        };
        var p = Profile.Amd();
        p.ConvertLosslessAudio = true;
        var d = Planner.Decide(media, p);
        Assert.True(d.Plan!.VideoCopy);
        var a = ArgsBuilder.BuildEncode(p, d.Plan, "in", "out");
        Assert.Contains("hevc_metadata=level=123", a);
    }

    private sealed class NullObserver : IRunObserver
    {
        public int Replaced;
        public void ScanStarted(string folder) { }
        public void ScanDone(int total) { }
        public void FileStarted(FileStart start) { }
        public void Line(InfoLine line) { }
        public void Progress(string stage, ProgressInfo info) { }
        public void FileFinished(FileStart start, FileEnd end) { if (end.Outcome == Outcome.Replaced) Replaced++; }
        public void RunFinished(RunCounters counters, bool cancelled) { }
    }
}

public sealed class TvAudioTests
{
    [Fact]
    public void Standard_wandelt_was_Fernseher_nicht_koennen()
    {
        var media = new MediaInfo
        {
            Path = "x.mkv", DurationSeconds = 100,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080, PixFmt = "yuv420p" },
                new StreamInfo { Index = 1, CodecType = "audio", CodecName = "dts", Profile = "DTS", Channels = 6 },
                new StreamInfo { Index = 2, CodecType = "audio", CodecName = "truehd", Channels = 8 },
                new StreamInfo { Index = 3, CodecType = "audio", CodecName = "opus", Channels = 2 },
                new StreamInfo { Index = 4, CodecType = "audio", CodecName = "ac3", Channels = 6 },
                new StreamInfo { Index = 5, CodecType = "audio", CodecName = "aac", Channels = 2 },
            ],
        };
        var plan = AudioPlanner.Plan(media, new Profile());
        Assert.Equal([true, true, true, false, false], plan.Tracks.Select(t => t.Convert));
        Assert.Equal(6, plan.Tracks[1].OutChannels);
    }

    [Fact]
    public void Migration_schaltet_Tonumwandlung_fuer_bestehende_Profile_ein()
    {
        var d = Path.Combine(Path.GetTempPath(), "fk-tv-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        var store = new SettingsStore(d);
        File.WriteAllText(store.SettingsFile, "{\"SchemaVersion\":6,\"Profiles\":[{\"Name\":\"NVIDIA\",\"ConvertLosslessAudio\":false,\"ConvertDts\":false}],\"SelectedProfile\":\"NVIDIA\"}");
        var s = store.LoadSettings();
        Assert.True(s.Profiles[0].ConvertLosslessAudio);
        Assert.True(s.Profiles[0].ConvertDts);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
    }
}

public sealed class FreshFileTests
{
    [Fact]
    public void Fertig_kopierte_Datei_mit_altem_Datum_laeuft_sofort()
    {
        var f = Path.Combine(Path.GetTempPath(), "fk-fresh-" + Guid.NewGuid().ToString("N")[..6] + ".mkv");
        File.WriteAllText(f, "x");
        File.SetLastWriteTime(f, DateTime.Now.AddDays(-30));
        Assert.False(Scanner.IsFresh(f, TimeSpan.FromMinutes(30)));
        File.SetLastWriteTime(f, DateTime.Now);
        Assert.True(Scanner.IsFresh(f, TimeSpan.FromMinutes(30)));
        Assert.False(Scanner.IsFresh(f, TimeSpan.Zero));
    }

    [Fact]
    public void Datei_die_noch_kopiert_wird_wartet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var f = Path.Combine(Path.GetTempPath(), "fk-copy-" + Guid.NewGuid().ToString("N")[..6] + ".mkv");
        using var writer = new FileStream(f, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        writer.Write(new byte[10]);
        writer.Flush();
        File.SetLastWriteTime(f, DateTime.Now.AddDays(-30));
        Assert.True(Scanner.StillBeingWritten(f));
        Assert.True(Scanner.IsFresh(f, TimeSpan.FromMinutes(30)));
    }
}

[Collection("media")]
public sealed class V2Tests(TestMedia m)
{
    private sealed class Obs : IRunObserver
    {
        public List<FileEnd> Ends { get; } = [];
        public List<string> Lines { get; } = [];
        public void ScanStarted(string folder) { }
        public void ScanDone(int total) { }
        public void FileStarted(FileStart start) { }
        public void Line(InfoLine line) => Lines.Add(line.Text);
        public void Progress(string stage, ProgressInfo info) { }
        public void FileFinished(FileStart start, FileEnd end) => Ends.Add(end);
        public void RunFinished(RunCounters counters, bool cancelled) { }
    }

    [Fact]
    public async Task Nachbessern_im_fremden_Ordner_nur_Abspielbarkeit_entfernt_nichts()
    {
        var lib = Path.Combine(m.Dir, "lib-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(lib, "Serie"));
        var file = Path.Combine(lib, "Serie", "Folge.mkv");
        File.Copy(m.Multi, file);
        File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(m.Dir, "anderswo-quelle");
        p.OutputDir = Path.Combine(m.Dir, "anderswo-ziel");
        p.RemoveCommentary = true;
        p.RemoveAudioDescription = true;
        p.AudioLanguages = "de";
        var obs = new Obs();
        await new CleanupRun(p, m.Tools, obs) { Cache = null }.RunAsync(new CleanupOptions { Folder = lib, ApplyProfileRules = false }, CancellationToken.None);
        Assert.Equal(Outcome.Replaced, obs.Ends.Single().Outcome);
        var o = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(4, o.Audio.Count());
        Assert.DoesNotContain(o.Audio, a => a.CodecName == "dts");
        Assert.Equal(3, o.Subtitles.Count());
        Assert.False(Directory.Exists(Path.Combine(lib, "_Statistik")));
        Assert.Empty(Directory.GetFiles(lib, "*.gimbo-tmp", SearchOption.AllDirectories));
    }

    [Fact]
    public void Doppelte_nur_bei_gleichem_Inhalt_loeschen()
    {
        MediaInfo Info(double dur, int w, int h, int audio) => new()
        {
            Path = "x", DurationSeconds = dur,
            Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = w, Height = h }, .. Enumerable.Range(1, audio).Select(i => new StreamInfo { Index = i, CodecType = "audio", CodecName = "ac3" })],
        };
        var keep = Info(2700, 1920, 1080, 2);
        Assert.Null(DuplicateCleaner.WhyNot(keep, Info(2701, 1920, 1080, 2), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(Info(900, 1920, 1080, 2), Info(2700, 1920, 1080, 2), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, 1280, 720, 2), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, 1920, 1080, 3), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, 1920, 1080, 2), 3_000, 1_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(null, Info(2700, 1920, 1080, 2), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, null, 1_000, 3_000));
    }

    [Fact]
    public void Doppelte_loeschen_entfernt_auch_die_komprimierte_Fassung()
    {
        var root = Path.Combine(m.Dir, "dup-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        Directory.CreateDirectory(Path.Combine(input, "Serie"));
        Directory.CreateDirectory(Path.Combine(output, "Serie"));
        var keep = Path.Combine(input, "Serie", "S01E01 - Titel.mkv");
        var big = Path.Combine(input, "Serie", "Serie.S01E01.1080p.mkv");
        File.WriteAllBytes(keep, new byte[100]);
        File.WriteAllBytes(big, new byte[300]);
        var keepOut = Path.Combine(output, "Serie", "S01E01 - Titel.mkv");
        var bigOut = Path.Combine(output, "Serie", "Serie.S01E01.1080p.mkv");
        File.WriteAllBytes(keepOut, new byte[10]);
        File.WriteAllBytes(bigOut, new byte[10]);
        var (n, bytes, errors) = DuplicateCleaner.Execute([new DuplicateDecision(big, keep, 300, null), new DuplicateDecision(keep, big, 100, "nicht gleichwertig")], input, output);
        Assert.Equal(1, n);
        Assert.Equal(300, bytes);
        Assert.Empty(errors);
        Assert.False(File.Exists(big));
        Assert.False(File.Exists(bigOut));
        Assert.True(File.Exists(keep));
        Assert.True(File.Exists(keepOut));
    }

    [Fact]
    public async Task Beschaedigtes_Original_wird_trotzdem_nachgebessert_wenn_nichts_verloren_geht()
    {
        var lib = Path.Combine(m.Dir, "dmg-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(lib);
        var file = Path.Combine(lib, "Folge.mkv");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=24", "-f", "lavfi", "-i", "sine=440:sample_rate=48000", "-t", "10",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error:level-idc=62", "-c:a", "aac", file]);
        using (var fs = new FileStream(file, FileMode.Open, FileAccess.ReadWrite))
        {
            fs.Seek(20_000, SeekOrigin.Begin);
            fs.Write(Enumerable.Repeat((byte)0xFF, 300).ToArray());
        }
        File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));
        var dec = await ProcessRunner.RunAsync(m.Tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(file, false, FfmpegTools.NullTarget), TimeSpan.FromMinutes(2));
        if (ArgsBuilder.DecodeErrors(dec.StdErr.Split('\n').Where(l => l.Trim().Length > 0)).Count == 0 && dec.ExitCode == 0) return;
        var p = Profile.Nvidia();
        p.OutputDir = Path.Combine(m.Dir, "nirgends");
        var obs = new Obs();
        await new CleanupRun(p, m.Tools, obs) { Cache = null }.RunAsync(new CleanupOptions { Folder = lib, ApplyProfileRules = false }, CancellationToken.None);
        Assert.Equal(Outcome.Replaced, obs.Ends.Single().Outcome);
        Assert.Contains(obs.Lines, l => l.Contains("beschädigt") || l.Contains("damaged"));
        Assert.Equal(123, (await m.Tools.ProbeAsync(file))!.Video!.Level);
    }

    [Fact]
    public void Reste_der_Reparatur_sind_nie_eine_Quelle()
    {
        Assert.True(Scanner.IsTemp("Folge.reparatur.tmp.mkv"));
        Assert.False(Scanner.IsTemp("Folge.mkv"));
        Assert.Equal("matroska", FilmKonverter.Core.Converting.FileConverter.Muxer(".mkv"));
        Assert.Equal("ipod", FilmKonverter.Core.Converting.FileConverter.Muxer(".m4a"));
    }
}
