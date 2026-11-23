using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class WorkflowTests(TestMedia m)
{
    private (Profile P, string In, string Out) NewLibrary(params (string Rel, string Src)[] files)
    {
        var root = Path.Combine(m.Dir, "wf-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        foreach (var (rel, src) in files)
        {
            var dst = Path.Combine(input, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }
        var p = Profile.Nvidia();
        p.Name = "Test";
        p.InputDir = input;
        p.OutputDir = output;
        p.Encoder = EncoderKind.Cpu;
        p.CpuPreset = "ultrafast";
        p.Quality = 30;
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        p.MinFreeSpaceGb = 0;
        return (p, input, output);
    }

    [Fact]
    public async Task Zwei_Dateien_gleichzeitig_alle_fertig_und_beide_Plaetze_genutzt()
    {
        var (p, _, output) = NewLibrary(
            ("A/Staffel 1/A.S01E01.mkv", m.Sdr8), ("A/Staffel 1/A.S01E02.mkv", m.Sdr8),
            ("B/Staffel 1/B.S01E01.mkv", m.Sdr8), ("B/Staffel 1/B.S01E02.mkv", m.Sdr8));
        var rec = new Recorder();
        var run = new LibraryRun(p, m.Tools, rec) { Parallel = 2, Cache = null };
        await run.RunAsync(CancellationToken.None);

        Assert.Equal(4, rec.Files.Count);
        Assert.All(rec.Files, f => Assert.Equal(Outcome.Encoded, f.End.Outcome));
        Assert.Equal(4, run.Counters.Encoded);
        Assert.Equal([0, 1], rec.Starts.Select(s => s.Slot).Distinct().Order());
        Assert.Equal([1, 2, 3, 4], rec.Starts.Select(s => s.Number).Order());
        Assert.True(File.Exists(Path.Combine(output, "B", "Staffel 1", "B.S01E02.mkv")));
        Assert.Empty(Directory.EnumerateFiles(output, "*.part.mkv", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Auswahl_bearbeitet_nur_gewaehlte_Ordner_und_Dateien()
    {
        var (p, input, _) = NewLibrary(
            ("A/Staffel 1/A.S01E01.mkv", m.Sdr8), ("A/Staffel 1/A.S01E02.mkv", m.Sdr8),
            ("B/Staffel 1/B.S01E01.mkv", m.Sdr8), ("C/C.S01E01.mkv", m.Sdr8));
        p.MinSizeMb = 500;
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec)
        {
            Cache = null,
            Sources = [Path.Combine(input, "A"), Path.Combine(input, "C", "C.S01E01.mkv")],
        }.RunAsync(CancellationToken.None);

        Assert.Single(rec.Files);
        Assert.Contains("C.S01E01", rec.Files[0].Start.RelativePath);
        Assert.Equal(Outcome.Encoded, rec.Files[0].End.Outcome);
    }

    [Fact]
    public async Task Auswahl_ausserhalb_des_Quellordners_wird_abgelehnt()
    {
        var (p, _, _) = NewLibrary(("A/A.S01E01.mkv", m.Sdr8));
        var rec = new Recorder();
        var run = new LibraryRun(p, m.Tools, rec) { Cache = null, Sources = [m.Sdr8] };
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => run.RunAsync(CancellationToken.None));
        Assert.Contains("Quellordner", ex.Message);
        Assert.True(rec.Finished);
    }

    [Fact]
    public async Task Pause_vor_dem_Start_haelt_neue_Dateien_zurueck_bis_Fortsetzen()
    {
        var (p, _, _) = NewLibrary(("A/A.S01E01.mkv", m.Sdr8));
        var rec = new Recorder();
        var run = new LibraryRun(p, m.Tools, rec) { Cache = null };
        run.Pause();
        var task = run.RunAsync(CancellationToken.None);
        await Task.Delay(1500);
        Assert.Empty(rec.Starts);
        Assert.False(task.IsCompleted);
        Assert.True(run.Resume());
        await task.WaitAsync(TimeSpan.FromMinutes(2));
        Assert.Equal(Outcome.Encoded, rec.Files.Single().End.Outcome);
    }

    [Fact]
    public async Task Analyse_schaetzt_offene_Dateien_erkennt_fertige_und_uebersprungene()
    {
        var (p, input, _) = NewLibrary(
            ("A/Staffel 1/A.S01E01.mkv", m.Sdr8), ("A/Staffel 1/A.S01E02.mkv", m.Sdr8),
            ("B/B.S01E01.mkv", m.Hevc8), ("Film.mkv", m.Sdr8));
        p.SkipHevc = true;
        var cache = new ProbeCache(Path.Combine(input, "..", "cache.json.gz"));

        await new LibraryRun(p, m.Tools, new Recorder()) { Cache = null, Sources = [Path.Combine(input, "A", "Staffel 1", "A.S01E01.mkv")] }
            .RunAsync(CancellationToken.None);

        var a = await LibraryAnalyzer.AnalyzeAsync(p, m.Tools, cache, null, CancellationToken.None);
        Assert.Equal(4, a.Files.Count);
        Assert.Equal(1, a.Count(AnalysisState.Done));
        Assert.Equal(2, a.Count(AnalysisState.Open));
        Assert.Equal(1, a.Count(AnalysisState.Skip));
        var open = a.Files.First(f => f.State == AnalysisState.Open);
        Assert.True(open.EstimatedBytes > 0 && open.EstimatedBytes <= open.SourceBytes);
        Assert.Equal("H.264", open.Codec);

        Assert.Equal(3, a.Groups.Count);
        var gA = a.Groups.Single(g => g.Name == "A");
        Assert.Equal(2, gA.Files);
        Assert.Equal(1, gA.Done);
        Assert.Equal(Path.Combine(input, "A"), gA.SourcePath);
        var film = a.Groups.Single(g => g.Name == "Film");
        Assert.EndsWith("Film.mkv", film.SourcePath);
        Assert.Equal(1, a.Groups.Single(g => g.Name == "B").Skip);

        var reloaded = ProbeCache.Load(Path.Combine(input, "..", "cache.json.gz"));
        Assert.Equal(4, reloaded.Count);
        var b = await LibraryAnalyzer.AnalyzeAsync(p, m.Tools, reloaded, null, CancellationToken.None);
        Assert.Equal(4, b.FromCache);
        Assert.Equal(a.EstimatedSaving, b.EstimatedSaving);
    }

    [Fact]
    public async Task Cache_behaelt_Entscheidungsdaten_und_erkennt_Aenderungen()
    {
        var file = Path.Combine(m.Dir, "cache-" + Guid.NewGuid().ToString("N")[..6] + ".gz");
        var c = new ProbeCache(file);
        var src = Path.Combine(m.Dir, "cachecopy-" + Guid.NewGuid().ToString("N")[..6] + ".mkv");
        File.Copy(m.AudioMix, src);
        var media = await c.GetOrProbeAsync(m.Tools, src, CancellationToken.None);
        Assert.NotNull(media);
        c.Save();

        var r = ProbeCache.Load(file);
        var back = r.Get(src);
        Assert.NotNull(back);
        Assert.Equal(media!.Streams.Count, back!.Streams.Count);
        Assert.Equal(media.Video!.CodecName, back.Video!.CodecName);
        Assert.Equal(media.DurationSeconds, back.DurationSeconds);
        Assert.Equal(media.Audio.Select(a => a.Language), back.Audio.Select(a => a.Language));

        File.SetLastWriteTimeUtc(src, DateTime.UtcNow.AddMinutes(-5));
        Assert.Null(r.Get(src));
    }

    [Fact]
    public void Reihenfolge_hoechste_Datenrate_zuerst_unbekannte_nach_Groesse()
    {
        var dir = Path.Combine(m.Dir, "rate-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        void Make(string n, int kb) => File.WriteAllBytes(Path.Combine(dir, n), new byte[kb * 1024]);
        Make("a.mkv", 10); Make("b.mkv", 30); Make("c.mkv", 20); Make("d.mkv", 50);
        var rates = new Dictionary<string, double> { ["a.mkv"] = 900, ["c.mkv"] = 100 };
        var r = Scanner.Find(dir, new ScanFilter
        {
            Order = FileOrder.HighestRateFirst,
            RateOf = (path, _, _) => rates.TryGetValue(Path.GetFileName(path), out var v) ? v : null,
        });
        Assert.Equal(["a.mkv", "c.mkv", "d.mkv", "b.mkv"], r.Files.Select(Path.GetFileName));
    }

    [Fact]
    public void Status_der_anderen_PCs_wird_gelesen_eigener_nicht()
    {
        var root = Path.Combine(m.Dir, "status-" + Guid.NewGuid().ToString("N")[..6]);
        Assert.True(StatusBoard.Write(root, new PeerStatus { Machine = "PC-1", UpdatedUtc = DateTime.UtcNow, State = PeerState.Running, Done = 3, Total = 10, Files = [new PeerFile { Name = "x.mkv", Percent = 42 }] }));
        Assert.True(StatusBoard.Write(root, new PeerStatus { Machine = "PC-2", UpdatedUtc = DateTime.UtcNow.AddMinutes(-10), State = PeerState.Idle }));
        var others = StatusBoard.ReadOthers(root, "pc-2");
        var s = Assert.Single(others);
        Assert.Equal("PC-1", s.Machine);
        Assert.Equal(PeerState.Running, s.State);
        Assert.Equal(42, s.Files.Single().Percent);
        Assert.True(s.IsFresh(DateTime.UtcNow));
        Assert.False(StatusBoard.ReadOthers(root, "PC-1").Single().IsFresh(DateTime.UtcNow));
        Assert.False(Directory.Exists(root));
    }
}

[Collection("media")]
public sealed class AudioOnlyTests(TestMedia m)
{
    [Fact]
    public async Task Hevc_mit_verlustfreiem_Ton_nur_Ton_wird_umgewandelt_Video_bleibt_bitgleich()
    {
        var root = Path.Combine(m.Dir, "ao-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien", "S");
        Directory.CreateDirectory(input);
        var src = Path.Combine(input, "S.S01E01.mkv");
        await m.Ff(["-i", m.Hevc8, "-f", "lavfi", "-i", "anoisesrc=sample_rate=48000:amplitude=0.5", "-map", "0:v", "-map", "1:a",
            "-c:v", "copy", "-c:a", "flac", "-ac", "6", "-t", "2", src]);
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(root, "Serien");
        p.OutputDir = Path.Combine(root, "Komprimiert");
        p.Encoder = EncoderKind.Cpu;
        p.MinSizeMb = 0; p.SkipNewerThanMinutes = 0; p.MinFreeSpaceGb = 0;
        p.SkipHevc = true;
        p.ConvertLosslessAudio = true;

        var media = (await m.Tools.ProbeAsync(src))!;
        var d = Encoding.Planner.Decide(media, p);
        Assert.True(d.Plan!.VideoCopy);
        Assert.DoesNotContain("-vf", Encoding.ArgsBuilder.BuildEncode(p, d.Plan, src, "x.mkv"));

        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null }.RunAsync(CancellationToken.None);
        var end = rec.Files.Single().End;
        Assert.True(end.Outcome == Outcome.Encoded, end.Message + " | " + string.Join(" | ", rec.Lines.Select(l => l.Text)));
        var outFile = Path.Combine(p.OutputDir, "S", "S.S01E01.mkv");
        var o = (await m.Tools.ProbeAsync(outFile))!;
        Assert.Equal("hevc", o.Video!.CodecName);
        Assert.Equal(media.Video!.Width, o.Video.Width);
        Assert.Equal("ac3", o.Audio.Single().CodecName);
        Assert.True(new FileInfo(outFile).Length < new FileInfo(src).Length);

        p.AudioOnlyForSkipped = false;
        Assert.Equal(Encoding.SkipReason.Hevc, Encoding.Planner.Decide(media, p).Skip);
    }
}

[Collection("media")]
public sealed class QueueTests(TestMedia m)
{
    private (Profile P, string In, string Out) Lib(params string[] rels)
    {
        var root = Path.Combine(m.Dir, "q-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        foreach (var r in rels)
        {
            var dst = Path.Combine(input, r);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(m.Sdr8, dst);
        }
        var p = Profile.Nvidia();
        p.InputDir = input;
        p.OutputDir = Path.Combine(root, "Komprimiert");
        p.Encoder = EncoderKind.Cpu; p.CpuPreset = "ultrafast";
        p.MinSizeMb = 0; p.SkipNewerThanMinutes = 0; p.MinFreeSpaceGb = 0;
        return (p, input, p.OutputDir);
    }

    [Fact]
    public async Task Warteschlange_hat_Vorrang_und_wird_geleert()
    {
        var (p, input, output) = Lib("A/A.S01E01.mkv", "A/A.S01E02.mkv", "Z/Z.S01E01.mkv");
        Assert.Equal(1, WorkQueue.Add(output, input, [Path.Combine(input, "Z")], "PC1"));
        Assert.Equal(0, WorkQueue.Add(output, input, [Path.Combine(input, "Z")], "PC1"));
        Assert.Equal(0, WorkQueue.Add(output, input, [m.Sdr8], "PC1"));

        var rec = new Recorder();
        var run = new LibraryRun(p, m.Tools, rec) { Cache = null, UseQueue = true };
        await run.RunAsync(CancellationToken.None);

        Assert.StartsWith("Z", rec.Starts[0].RelativePath);
        Assert.Equal(3, rec.Files.Count);
        Assert.All(rec.Files, f => Assert.Equal(Outcome.Encoded, f.End.Outcome));
        Assert.Empty(WorkQueue.Read(output));
        Assert.Contains(rec.Lines, l => l.Text.Contains("Warteschlange"));
    }

    [Fact]
    public async Task Nur_Warteschlange_bearbeitet_nur_die_Eintraege_in_ihrer_Reihenfolge()
    {
        var (p, input, output) = Lib("A/A.S01E01.mkv", "B/B.S01E01.mkv", "C/C.S01E01.mkv");
        WorkQueue.Add(output, input, [Path.Combine(input, "C"), Path.Combine(input, "B", "B.S01E01.mkv")], "PC1");
        var q = WorkQueue.Read(output);
        Assert.True(WorkQueue.Move(output, q[1].Id, -1));
        Assert.Equal("B", Path.GetDirectoryName(WorkQueue.Read(output)[0].Relative));

        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null, QueueOnly = true }.RunAsync(CancellationToken.None);
        Assert.Equal(["B", "C"], rec.Starts.Select(s => s.RelativePath[..1]));
        Assert.Empty(WorkQueue.Read(output));
        Assert.False(File.Exists(Path.Combine(output, "A", "A.S01E01.mkv")));
    }
}

public sealed class QualityRuleTests
{
    [Fact]
    public void Qualitaet_wird_nach_Aufloesung_angepasst()
    {
        var p = Profile.Nvidia();
        p.Quality = 27; p.QualityAdjustSd = -3; p.QualityAdjust4K = 2;
        Assert.Equal(24, p.AdjustedFor(576).Quality);
        Assert.Equal(27, p.AdjustedFor(720).Quality);
        Assert.Same(p, p.AdjustedFor(1080));
        Assert.Equal(29, p.AdjustedFor(2160).Quality);
        var amd = Profile.Amd();
        amd.QpI = 22; amd.QpP = 24; amd.QualityAdjustSd = -2;
        var a = amd.AdjustedFor(480);
        Assert.Equal((20, 22), (a.QpI, a.QpP));
        Assert.Contains("CQ 24", p.AdjustedFor(576).QualityText);
    }
}

[Collection("media")]
public sealed class CleanupTests(TestMedia m)
{
    [Fact]
    public async Task Bereinigung_entfernt_Spuren_im_Ziel_Probelauf_aendert_nichts_und_zweiter_Lauf_hat_nichts_zu_tun()
    {
        var root = Path.Combine(m.Dir, "cl-" + Guid.NewGuid().ToString("N")[..6]);
        var output = Path.Combine(root, "Komprimiert");
        var file = Path.Combine(output, "S", "S.S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(m.Multi, file);
        File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(root, "Serien");
        p.OutputDir = output;
        p.AudioLanguages = "de";
        p.RemoveCommentary = true;
        p.DefaultAudioLanguage = "de";
        var before = (await m.Tools.ProbeAsync(file))!;
        var size = new FileInfo(file).Length;

        var rec = new Recorder();
        await new CleanupRun(p, m.Tools, rec) { Cache = null }.RunAsync(new CleanupOptions { DryRun = true }, CancellationToken.None);
        Assert.Equal(Outcome.Skipped, rec.Files.Single().End.Outcome);
        Assert.Contains(rec.Lines, l => l.Text.Contains("entfernen"));
        Assert.Equal(size, new FileInfo(file).Length);

        rec = new Recorder();
        await new CleanupRun(p, m.Tools, rec) { Cache = null }.RunAsync(new CleanupOptions(), CancellationToken.None);
        var end = rec.Files.Single().End;
        Assert.True(end.Outcome == Outcome.Replaced, end.Message + " | " + string.Join(" | ", rec.Lines.Select(l => l.Text)));
        var after = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(before.Video!.CodecName, after.Video!.CodecName);
        Assert.True(after.Audio.Count() < before.Audio.Count());
        Assert.All(after.Audio, a => Assert.Equal("ger", a.Language));
        Assert.True(after.Audio.First().IsDefault);
        Assert.False(File.Exists(Path.Combine(output, "S", "S.S01E01.part.mkv")));
        Assert.Contains(StatsLog.ReadAll([output]), r => r.Outcome == "Cleaned");

        rec = new Recorder();
        await new CleanupRun(p, m.Tools, rec) { Cache = null }.RunAsync(new CleanupOptions(), CancellationToken.None);
        Assert.Equal(Outcome.AlreadyOk, rec.Files.Single().End.Outcome);
    }
}

public sealed class SpaceForecastTests
{
    [Fact]
    public void Warnung_wenn_der_Platz_nicht_fuer_den_Rest_reicht()
    {
        const long GB = 1L << 30;
        var snap = new Library.AnalysisSnapshot { CreatedUtc = DateTime.UtcNow.AddDays(-1), EstimatedOutputBytes = 500 * GB, OpenFiles = 400 };
        var written = new List<StatsRecord> { new() { TimeUtc = DateTime.UtcNow, Outcome = "Encoded", OutputBytes = 100 * GB } };
        var v = Library.SpaceForecast.Evaluate(snap, 250 * GB, 50 * GB, written);
        Assert.True(v.Warn);
        Assert.Equal(400 * GB, v.NeededBytes);
        Assert.Equal(0.5, v.Fraction, 3);
        Assert.False(Library.SpaceForecast.Evaluate(snap, 900 * GB, 50 * GB, written).Warn);

        var file = Path.Combine(Path.GetTempPath(), "fk-space-" + Guid.NewGuid().ToString("N")[..6] + ".json");
        Library.SpaceForecast.Save(file, new Dictionary<string, Library.AnalysisSnapshot> { ["NVIDIA"] = snap });
        Assert.Equal(400, Library.SpaceForecast.Load(file)["nvidia"].OpenFiles);
    }
}

[Collection("media")]
public sealed class ReviewFix110Tests(TestMedia m)
{
    [Fact]
    public async Task Bereinigung_nur_Kennzeichen_ist_beim_naechsten_Lauf_erledigt_auch_mit_Cache()
    {
        var output = Path.Combine(m.Dir, "clc-" + Guid.NewGuid().ToString("N")[..6]);
        var file = Path.Combine(output, "S", "S.S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.Copy(m.Multi, file);
        File.SetLastWriteTime(file, DateTime.Now.AddHours(-1));
        var p = Profile.Nvidia();
        p.InputDir = Path.Combine(output, "..", "keine-quelle");
        p.OutputDir = output;
        var cache = new ProbeCache();
        async Task<Outcome> Run(string lang)
        {
            p.DefaultAudioLanguage = lang;
            var rec = new Recorder();
            await new CleanupRun(p, m.Tools, rec) { Cache = cache, WriteStatsLog = false }.RunAsync(new CleanupOptions(), CancellationToken.None);
            return rec.Files.Single().End.Outcome;
        }
        Assert.Equal(Outcome.Replaced, await Run("de"));
        Assert.Equal(Outcome.Replaced, await Run("en"));
        Assert.Equal(Outcome.AlreadyOk, await Run("en"));
    }

    [Fact]
    public async Task Warteschlange_behaelt_Eintrag_wenn_Datei_noch_kopiert_wird()
    {
        var root = Path.Combine(m.Dir, "qk-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        var src = Path.Combine(input, "A", "A.S01E01.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(src)!);
        File.Copy(m.Sdr8, src);
        File.SetLastWriteTime(src, DateTime.Now);
        var p = Profile.Nvidia();
        p.InputDir = input; p.OutputDir = Path.Combine(root, "Komprimiert");
        p.Encoder = EncoderKind.Cpu; p.MinSizeMb = 0; p.MinFreeSpaceGb = 0;
        p.SkipNewerThanMinutes = 60;
        WorkQueue.Add(p.OutputDir, input, [src], "PC1");
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null, QueueOnly = true, WriteStatsLog = false }.RunAsync(CancellationToken.None);
        Assert.Empty(rec.Files);
        Assert.Single(WorkQueue.Read(p.OutputDir));
    }

    [Fact]
    public void Kaputte_Warteschlangen_Datei_wird_gesichert_und_neu_begonnen()
    {
        var root = Path.Combine(m.Dir, "qb-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(WorkQueue.Folder(root));
        File.WriteAllText(WorkQueue.FileOf(root), "{ kaputt");
        Assert.True(WorkQueue.Modify(root, l => l.Add(new QueueEntry { Relative = "Serie" })));
        Assert.Equal("{ kaputt", File.ReadAllText(WorkQueue.FileOf(root) + ".defekt"));
        Assert.Equal("Serie", WorkQueue.Read(root).Single().Relative);
    }

    [Fact]
    public void Breitbild_720p_zaehlt_nicht_als_SD()
    {
        var p = Profile.Nvidia();
        p.Quality = 27; p.QualityAdjustSd = -3;
        Assert.Equal(27, p.AdjustedFor(1280, 536).Quality);
        Assert.Equal(24, p.AdjustedFor(720, 576).Quality);
    }
}

public sealed class StatsPeriodTests
{
    private static StatsRecord R(DateTime local, long gain) => new() { TimeUtc = local.ToUniversalTime(), Outcome = "Encoded", File = Guid.NewGuid().ToString(), GainBytes = gain, SourceBytes = gain * 3, OutputBytes = gain * 2 };

    [Fact]
    public void Zeitraeume_filtern_und_waehlen_das_Raster()
    {
        var now = new DateTime(2026, 11, 13, 12, 0, 0);
        var recs = new List<StatsRecord> { R(now.AddDays(-2), 10), R(now.AddDays(-20), 20), R(now.AddDays(-80), 40), R(now.AddDays(-300), 80), R(now.AddDays(-900), 160) };
        var w = StatsSummary.Build(recs, now, StatsPeriod.Week);
        Assert.Equal((10L, StatsGrain.Day, 7), (w.GainBytes, w.Grain, w.Buckets.Count));
        Assert.Equal(30L, StatsSummary.Build(recs, now, StatsPeriod.Month).GainBytes);
        var q = StatsSummary.Build(recs, now, StatsPeriod.Quarter);
        Assert.Equal((70L, StatsGrain.Week), (q.GainBytes, q.Grain));
        Assert.Equal(StatsGrain.Month, StatsSummary.Build(recs, now, StatsPeriod.TwelveMonths).Grain);
        Assert.Equal(12, StatsSummary.Build(recs, now, StatsPeriod.TwelveMonths).Buckets.Count);
        Assert.Equal(11, StatsSummary.Build(recs, now, StatsPeriod.ThisYear).Buckets.Count);
        var all = StatsSummary.Build(recs, now, StatsPeriod.All);
        Assert.Equal(310L, all.GainBytes);
        Assert.Equal(StatsGrain.Month, all.Grain);
        Assert.Equal(all.GainBytes, all.Buckets.Sum(b => b.GainBytes));
        Assert.Equal(all.GainBytes, all.Cumulative[^1].GainBytes);
    }

    [Fact]
    public void Zuruecksetzen_archiviert_die_Protokolle()
    {
        var root = Path.Combine(Path.GetTempPath(), "fk-arch-" + Guid.NewGuid().ToString("N")[..6]);
        StatsLog.Append(root, new StatsRecord { TimeUtc = DateTime.UtcNow, Machine = "PC1", Outcome = "Encoded", GainBytes = 5 });
        StatsLog.Append(root, new StatsRecord { TimeUtc = DateTime.UtcNow, Machine = "PC2", Outcome = "Encoded", GainBytes = 7 });
        Assert.Equal(2, StatsLog.ReadAll([root]).Count);
        Assert.Equal(2, StatsLog.Archive([root, root]));
        Assert.Empty(StatsLog.ReadAll([root]));
        Assert.Equal(2, Directory.EnumerateFiles(Path.Combine(StatsLog.Folder(root), "Archiv"), "*.jsonl", SearchOption.AllDirectories).Count());
    }
}

[Collection("media")]
public sealed class DeepReviewTests(TestMedia m)
{
    private static string Tmp() { var d = Path.Combine(Path.GetTempPath(), "fk-deep-" + Guid.NewGuid().ToString("N")[..6]); Directory.CreateDirectory(d); return d; }

    [Fact]
    public void Abgeschnittene_Laufzeit_wird_erkannt()
    {
        Assert.NotNull(LibraryRun.DurationMismatch(20, 10));
        Assert.NotNull(LibraryRun.DurationMismatch(2700, 2600));
        Assert.Null(LibraryRun.DurationMismatch(2700, 2690));
        Assert.Null(LibraryRun.DurationMismatch(4, 2));
        Assert.Null(LibraryRun.DurationMismatch(0, 100));
    }

    [Fact]
    public void Sperre_eines_laufenden_Programms_auf_demselben_PC_wird_nicht_uebernommen()
    {
        var dir = Path.Combine(Tmp(), "x.lock");
        Directory.CreateDirectory(dir);
        using var other = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(m.Tools.FfmpegPath,
            "-hide_banner -v error -f lavfi -i nullsrc -t 60 -f null -") { RedirectStandardError = true, UseShellExecute = false })!;
        try
        {
            File.WriteAllText(Path.Combine(dir, "info.txt"), $"{FileLock.ComputerName} 2026-01-01 12:00 #abc pid={other.Id}:{other.StartTime.ToUniversalTime().Ticks}");
            Assert.Null(FileLock.TryAcquire(dir, 24, out _));
        }
        finally
        {
            try { other.Kill(); } catch { }
        }
        using (var me = System.Diagnostics.Process.GetCurrentProcess())
            File.WriteAllText(Path.Combine(dir, "info.txt"), $"{FileLock.ComputerName} 2026-01-01 12:00 #abc pid={me.Id}:{me.StartTime.ToUniversalTime().Ticks}");
        using (var own = FileLock.TryAcquire(dir, 24, out _)) Assert.NotNull(own);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "info.txt"), $"{FileLock.ComputerName} 2026-01-01 12:00 #abc pid=999999:1");
        using var l = FileLock.TryAcquire(dir, 24, out _);
        Assert.NotNull(l);
    }

    [Fact]
    public void Vermerke_nur_der_eigenen_Datei()
    {
        var d = Tmp();
        foreach (var n in new[] { "Folge.G95.txt", "Folge.CQ27_G95.txt", "Folge.ok_G95_1_2.txt", "Folge.Gold.S01E01.G95.txt", "Folge.Gold.S01E01.ok_G95_1_2.txt" })
            File.WriteAllText(Path.Combine(d, n), "x");
        Assert.Equal(3, MarkList.OwnMarks(d, "Folge").Count());
    }

    [Fact]
    public void Unbekannter_Enum_Wert_kostet_nicht_die_Profile()
    {
        var d = Tmp();
        var store = new SettingsStore(d);
        var s = AppSettings.CreateDefault();
        s.Profiles[0].Name = "MeinProfil";
        store.SaveSettings(s);
        var json = File.ReadAllText(store.SettingsFile).Replace("\"Theme\": \"Dark\"", "\"Theme\": \"Neon\"");
        File.WriteAllText(store.SettingsFile, json);
        var back = new SettingsStore(d).LoadSettings();
        Assert.Equal("MeinProfil", back.Profiles[0].Name);
        Assert.Equal(ThemeMode.System, back.Theme);
    }

    [Fact]
    public void Kaputte_Einstellungen_nehmen_die_Sicherung()
    {
        var d = Tmp();
        var store = new SettingsStore(d);
        var s = AppSettings.CreateDefault();
        s.Profiles[0].Name = "Gesichert";
        store.SaveSettings(s);
        store.SaveSettings(s);
        File.WriteAllText(store.SettingsFile, "{ kaputt");
        Assert.Equal("Gesichert", new SettingsStore(d).LoadSettings().Profiles[0].Name);
    }

    [Fact]
    public void Aeltere_NVIDIA_Karten_ohne_B_Frames()
    {
        var p = Profile.Nvidia();
        p.NvencLegacy = true;
        var plan = new Encoding.EncodePlan(new StreamInfo { Index = 0, CodecType = "video" }, null, 8, "yuv420p", "main", new Encoding.AudioPlan([]), "", "");
        var a = Encoding.ArgsBuilder.BuildEncode(p, plan, "in.mkv", "out.mkv");
        Assert.DoesNotContain("-b_ref_mode", a);
        Assert.DoesNotContain("-temporal-aq", a);
        Assert.Equal("0", a[a.IndexOf("-bf") + 1]);
    }
}
