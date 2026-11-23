using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class StatsAndQualityTests(TestMedia m)
{
    private (Profile P, string In, string Out) NewLibrary(params (string Rel, string Src)[] files)
    {
        var root = Path.Combine(m.Dir, "sq-" + Guid.NewGuid().ToString("N")[..6]);
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
        p.Quality = 28;
        p.MinSizeMb = 0; p.SkipNewerThanMinutes = 0; p.MinFreeSpaceGb = 0;
        return (p, input, output);
    }

    [Fact]
    public async Task Lauf_schreibt_Statistik_und_misst_SSIM()
    {
        var (p, _, output) = NewLibrary(("A/Staffel 1/A.S01E01.mkv", m.Long720), ("B/B.S01E01.mkv", m.Hevc8));
        p.SsimCheck = true;
        p.SsimMin = 0.999;
        p.SsimSamples = 2;
        p.SkipHevc = true;
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null }.RunAsync(CancellationToken.None);

        var stats = StatsLog.ReadAll([output]);
        var enc = stats.Single(s => s.File.Contains("A.S01E01"));
        Assert.Equal("Encoded", enc.Outcome);
        Assert.Equal("A", enc.Group);
        Assert.True(enc.SourceBytes > enc.OutputBytes && enc.OutputBytes > 0);
        Assert.True(enc.DurationSeconds > 0 && enc.EncodeSeconds > 0);
        Assert.Equal("h264", enc.SourceCodec);
        Assert.NotNull(enc.Ssim);
        Assert.InRange(enc.Ssim!.Value, 0.80, 0.9999);
        Assert.True(enc.SsimLow);
        Assert.Contains(rec.Lines, l => l.Text.StartsWith("SSIM"));

        Assert.DoesNotContain(stats, s => s.File.Contains("B.S01E01"));

        var problems = ProblemFinder.Find(p, stats);
        Assert.Contains(problems, x => x.Kind == ProblemKind.Quality && x.Relative.Contains("A.S01E01"));

        var sum = StatsSummary.Build(stats, DateTime.Now);
        Assert.Equal(1, sum.Files);
        Assert.Equal(0, sum.Skipped);
        Assert.Equal(enc.GainBytes, sum.GainBytes);
        Assert.Equal(enc.GainBytes, sum.Buckets[^1].GainBytes);
        Assert.Single(sum.Machines);
        Assert.True(sum.SourceBytesPerDay > 0);
        Assert.NotNull(sum.DaysLeft(10_000_000_000));
    }

    [Fact]
    public void Merkliste_Fehler_und_Defekt_in_der_Problemliste_zuruecksetzen()
    {
        var (p, input, output) = NewLibrary(("A/Staffel 1/A.S01E02.mkv", m.Sdr8), ("A/Staffel 1/A.S01E03.mkv", m.Sdr8));
        var paths = new LibraryPaths(input, output, Path.Combine(input, "A", "Staffel 1", "A.S01E02.mkv"));
        MarkList.Mark(paths, p.MarkTag, "Fruehabbruch", "CQ 27", 95);
        Directory.CreateDirectory(Path.Combine(output, LibraryPaths.BrokenFolder, "A", "Staffel 1"));
        File.WriteAllBytes(Path.Combine(output, LibraryPaths.BrokenFolder, "A", "Staffel 1", "A.S01E03.mkv"), [1]);
        var stats = new List<StatsRecord>
        {
            new() { TimeUtc = DateTime.UtcNow.AddHours(-2), Machine = "PC-2", File = Path.Combine("A", "Staffel 1", "A.S01E03.mkv"), Outcome = "Error", Reason = "kaputt" },
        };

        var problems = ProblemFinder.Find(p, stats);
        var skip = Assert.Single(problems, x => x.Kind == ProblemKind.SkipList);
        Assert.Contains("Früh abgebrochen", skip.Reason);
        Assert.EndsWith("A.S01E02.mkv", skip.SourcePath);
        Assert.Single(problems, x => x.Kind == ProblemKind.Error && x.Machine == "PC-2");
        Assert.Single(problems, x => x.Kind == ProblemKind.Broken);

        Assert.True(ProblemFinder.Reset(skip));
        Assert.False(MarkList.IsMarked(paths, p.MarkTag));
    }

    [Fact]
    public async Task Doppelte_und_fehlende_Folgen_werden_gefunden()
    {
        var (p, _, _) = NewLibrary(
            ("Insel/Staffel 1/Insel.S01E01.mkv", m.Sdr8),
            ("Insel/Staffel 1/Insel.S01E02.720p.mkv", m.Sdr8),
            ("Insel/Staffel 1/Insel.S01E02.1080p.mkv", m.Long720),
            ("Insel/Staffel 1/Insel.S01E05.mkv", m.Sdr8),
            ("Insel/Staffel 1/Insel.S01E06.mkv", m.Sdr8),
            ("Insel/Staffel 1/Insel.S01E06.avi", m.Sdr8),
            ("Filme/Glut.1995.German.mkv", m.Sdr8),
            ("Glut (1995)/glut.1080p.mkv", m.Long720),
            ("Der Bund (1972)/Der.Bund.cd1.mkv", m.Sdr8),
            ("Der Bund (1972)/Der.Bund.cd2.mkv", m.Sdr8),
            ("Wilde Natur (2006)/Teil 1.mkv", m.Sdr8),
            ("Wilde Natur (2006)/Teil 2.mkv", m.Sdr8));
        p.Extensions = [".mkv", ".avi"];
        var a = await LibraryAnalyzer.AnalyzeAsync(p, m.Tools, new ProbeCache(), null, CancellationToken.None);

        Assert.Equal(11, a.Files.Count);
        Assert.DoesNotContain(a.Duplicates, d => d.Label.Contains("Bund") || d.Files.Any(f => f.Path.Contains("Planet")));
        Assert.Contains(a.Duplicates, d => d.Label == "S01E02" && d.Files.Count == 2);
        Assert.Contains(a.Duplicates, d => d.Label == "S01E06" && d.Files.Any(f => f.Path.EndsWith(".avi")));
        var glut = Assert.Single(a.Duplicates, d => d.Label.StartsWith("Glut"));
        Assert.Equal(2, glut.Files.Count);
        Assert.True(glut.Reclaimable > 0);

        var miss = Assert.Single(a.Missing);
        Assert.Equal(("Insel", 1), (miss.Group, miss.Season));
        Assert.Equal([3, 4], miss.Missing);
        Assert.Equal("E03–E04", miss.MissingText);
    }

    [Fact]
    public async Task Testmodus_misst_SSIM_und_liefert_Dateien_fuer_den_Bildvergleich()
    {
        var (p, input, _) = NewLibrary(("A/A.S01E01.mkv", m.Long720));
        p.TestStartSeconds = 1;
        p.TestLengthSeconds = 6;
        p.TestValues = "24 34";
        var run = new TestRun(p, m.Tools, new Recorder());
        var rows = await run.RunAsync(Path.Combine(input, "A", "A.S01E01.mkv"), CancellationToken.None);
        var q24 = rows.Single(r => r.Clip.EndsWith("24"));
        var q34 = rows.Single(r => r.Clip.EndsWith("34"));
        Assert.NotNull(q24.Ssim);
        Assert.True(q24.Ssim > q34.Ssim, $"{q24.Ssim} > {q34.Ssim}");
        Assert.Null(rows.Single(r => r.IsReference).Ssim);
        Assert.True(File.Exists(q34.File));

        var png = Path.Combine(m.Dir, "frame-" + Guid.NewGuid().ToString("N")[..6] + ".png");
        Assert.True(await QualityCheck.ExtractFrameAsync(m.Tools, Path.Combine(input, "A", "A.S01E01.mkv"), run.ClipStart + 2, 320, 180, png, CancellationToken.None));
        Assert.True(new FileInfo(png).Length > 1000);
    }
}
