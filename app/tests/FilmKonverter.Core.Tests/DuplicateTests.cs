using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

public sealed class DuplicateMedia : IAsyncLifetime
{
    public FfmpegTools Tools { get; private set; } = null!;
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fk-dup-" + Guid.NewGuid().ToString("N")[..8]);

    public string Original => Path.Combine(Dir, "original.mkv");
    public string Reencoded => Path.Combine(Dir, "reencoded.mkv");
    public string OtherPicture => Path.Combine(Dir, "other.mkv");
    public string Sample => Path.Combine(Dir, "sample.mkv");
    public string HalfCopied => Path.Combine(Dir, "half.mkv");
    public string LowRes => Path.Combine(Dir, "lowres.mkv");
    public string English => Path.Combine(Dir, "english.mkv");
    public string OriginalNoLang => Path.Combine(Dir, "original-nolang.mkv");
    public string ReencodedNoLang => Path.Combine(Dir, "reencoded-nolang.mkv");
    public string Rich => Path.Combine(Dir, "rich.mkv");
    public string Derived => Path.Combine(Dir, "derived.mp4");
    public string Garbage => Path.Combine(Dir, "garbage.mkv");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Dir);
        var folder = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (string.IsNullOrEmpty(folder)) folder = FfmpegTools.FindCandidateFolders().FirstOrDefault();
        Tools = await FfmpegTools.TryLoadAsync(folder ?? "") ?? throw new InvalidOperationException("ffmpeg nicht gefunden (FFMPEG_DIR setzen).");

        string[] sine = ["-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000"];
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", .. sine, "-t", "30", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "12",
            "-c:a", "aac", "-b:a", "64k", "-ac", "2", "-metadata:s:a:0", "language=ger", Original]);
        await Ff(["-i", Original, "-map", "0", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-crf", "34", "-c:a", "copy", Reencoded]);
        await Ff(["-f", "lavfi", "-i", "mandelbrot=size=320x180:rate=24", .. sine, "-t", "30", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "20",
            "-c:a", "aac", "-b:a", "64k", "-ac", "2", "-metadata:s:a:0", "language=ger", OtherPicture]);
        await Ff(["-i", Original, "-map", "0", "-t", "4", "-c", "copy", Sample]);
        await Ff(["-i", Original, "-map", "0", "-vf", "scale=160:90", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "30", "-c:a", "copy", LowRes]);
        await Ff(["-i", Original, "-map", "0", "-c", "copy", "-metadata:s:a:0", "language=eng", English]);
        await Ff(["-i", Original, "-map", "0", "-c", "copy", "-metadata:s:a:0", "language=", OriginalNoLang]);
        await Ff(["-i", Reencoded, "-map", "0", "-c", "copy", "-metadata:s:a:0", "language=", ReencodedNoLang]);
        var srt = Path.Combine(Dir, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:01,000 --> 00:00:03,000\nHallo\n\n2\n00:00:20,000 --> 00:00:22,000\nEnde\n\n");
        await Ff(["-i", Original, .. sine, "-i", srt, "-i", srt, "-t", "30", "-map", "0:v", "-map", "1:a", "-map", "2", "-map", "3",
            "-c:v", "copy", "-c:a", "ac3", "-ac", "6", "-b:a", "448k", "-c:s", "srt", "-metadata:s:a:0", "language=ger", Rich]);
        await Ff(["-i", Rich, "-map", "0:v", "-map", "0:a", "-c:v", "copy", "-c:a", "aac", "-ac", "2", "-b:a", "96k", "-movflags", "+faststart", Derived]);
        var bytes = File.ReadAllBytes(Original);
        File.WriteAllBytes(HalfCopied, bytes[..(bytes.Length / 2)]);
        var noise = new byte[bytes.Length + 4096];
        new Random(5).NextBytes(noise);
        File.WriteAllBytes(Garbage, noise);
    }

    public async Task Ff(IEnumerable<string> args)
    {
        var r = await ProcessRunner.RunAsync(Tools.FfmpegPath, ["-hide_banner", "-v", "error", "-nostdin", "-y", .. args], TimeSpan.FromMinutes(5));
        if (r.ExitCode != 0) throw new InvalidOperationException("ffmpeg fehlgeschlagen: " + r.StdErr);
    }

    public Task DisposeAsync()
    {
        try { Directory.Delete(Dir, true); } catch { }
        return Task.CompletedTask;
    }
}

[Collection("media")]
public sealed class DuplicateTests(DuplicateMedia m)
{
    private sealed class Library(Profile p, string input, string output)
    {
        public Profile P { get; } = p;
        public string In { get; } = input;
        public string Out { get; } = output;
        public string Source(string rel) => Path.Combine(In, rel.Replace('/', Path.DirectorySeparatorChar));
        public string Target(string rel) => Path.Combine(Out, rel.Replace('/', Path.DirectorySeparatorChar));
    }

    private Library NewLibrary(params (string Rel, string Src)[] files)
    {
        var root = Path.Combine(m.Dir, "lib-" + Guid.NewGuid().ToString("N")[..8]);
        var lib = new Library(Profile.Nvidia(), Path.Combine(root, "Serien"), Path.Combine(root, "Komprimiert"));
        foreach (var (rel, src) in files)
        {
            var dst = lib.Source(rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
            WaitUntilFree(dst);
        }
        Directory.CreateDirectory(lib.Out);
        lib.P.Name = "Test";
        lib.P.InputDir = lib.In;
        lib.P.OutputDir = lib.Out;
        lib.P.MinSizeMb = 0;
        lib.P.SkipNewerThanMinutes = 0;
        lib.P.ExcludePatterns = "";
        return lib;
    }

    private static void WaitUntilFree(string path)
    {
        for (var i = 0; i < 100; i++)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private Task<LibraryAnalysis> Analyse(Library lib) => LibraryAnalyzer.AnalyzeAsync(lib.P, m.Tools, new ProbeCache(), null, CancellationToken.None);

    private async Task<List<DuplicateDecision>> Plan(Library lib, LibraryAnalysis a)
    {
        var plan = await DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, CancellationToken.None, lib.In, lib.Out);
        for (var attempt = 1; attempt < 3 && plan.Any(BrieflyUnreadable); attempt++)
        {
            await Task.Delay(1500 * attempt);
            plan = await DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, CancellationToken.None, lib.In, lib.Out);
        }
        return plan;
    }

    private bool BrieflyUnreadable(DuplicateDecision d)
    {
        if (d.Reason is null || !d.Reason.Contains(L.T("nicht lesbar", "not readable"), StringComparison.OrdinalIgnoreCase) || !File.Exists(d.File)) return false;
        var length = new FileInfo(d.File).Length;
        return length != new FileInfo(m.Garbage).Length && length != new FileInfo(m.HalfCopied).Length;
    }

    private static string Describe(IEnumerable<DuplicateDecision> plan) =>
        string.Join(Environment.NewLine, plan.Select(d => $"{Path.GetFileName(d.File)}: {(d.WillDelete ? "löschen" : d.IsKept ? "behalten" : "bleibt")} {d.Reason} {d.Why}"));

    private async Task<(LibraryAnalysis A, List<DuplicateDecision> Plan, DuplicateResult Result)> Clean(Library lib)
    {
        var a = await Analyse(lib);
        var plan = await Plan(lib, a);
        return (a, plan, DuplicateCleaner.Execute(plan, lib.In, lib.Out));
    }

    private static DuplicateDecision Of(List<DuplicateDecision> plan, string file) => plan.Single(d => d.File == file);

    private static void AssertAllThere(Library lib, params string[] rels)
    {
        foreach (var rel in rels) Assert.True(File.Exists(lib.Source(rel)), rel + " fehlt");
    }

    // ---------------- Namen: was ueberhaupt verglichen wird ----------------

    [Theory]
    [InlineData("Film.mkv", "Film")]
    [InlineData("Film (1).mkv", "Film")]
    [InlineData("Film - Kopie.mp4", "Film")]
    [InlineData("Film - Kopie (2).mkv", "Film")]
    [InlineData("Film - copy.mkv", "Film")]
    [InlineData("Show.S01E01_sample.mkv", "Show.S01E01")]
    [InlineData("Show.S01E01.reparatur.tmp.mkv", "Show.S01E01")]
    [InlineData("Show.S01E01.mkv.reparatur-tmp", "Show.S01E01")]
    [InlineData("Boxer (1976).mkv", "Boxer (1976)")]
    public void Bereinigter_Name_ohne_Kopier_Zusaetze(string file, string clean) => Assert.Equal(clean, DuplicateNames.CleanName(file));

    // ---------------- Entscheidung je Gruppe ----------------

    [Fact]
    public void Durchnummerierte_Aufnahmen_sind_keine_Kopien()
    {
        var series = DuplicateNames.Identities(["Urlaub (1).mp4", "Urlaub (2).mp4", "Urlaub (2).mkv"]);
        Assert.NotEqual(series["Urlaub (1).mp4"], series["Urlaub (2).mp4"]);
        Assert.Equal(series["Urlaub (2).mp4"], series["Urlaub (2).mkv"]);
        var copies = DuplicateNames.Identities(["Urlaub.mp4", "Urlaub (1).mp4", "Urlaub - Kopie.mp4"]);
        Assert.Single(copies.Values.Distinct());
    }

    [Theory]
    [InlineData("familie.muster.s05e02a.german", "a", "familiemuster", "")]
    [InlineData("Familie Muster - S05E02b - Titel", "b", "familiemuster", "titel")]
    [InlineData("Familie.Muster.S06E01a.Blaue.Ernte.Teil.1.German.DL.720p.BluRay.x264", "a", "familiemuster", "blaueernteteil1")]
    [InlineData("show.s04e04.1 Der Tag danach", "", "show", "1dertagdanach")]
    [InlineData("show.s04e04 Alles live", "", "show", "alleslive")]
    [InlineData("S01E02 - Über Größe", "", "", "uebergroesse")]
    [InlineData("Show.S01E05-720p.WEB-DL.H.264", "", "show", "")]
    [InlineData("Show.S01E01E02.German.1080p", "", "show", "")]
    [InlineData("Zeit Reise (2005) - 1x02a - Das Ende", "a", "zeitreise", "dasende")]
    [InlineData("[Gruppe] Show.German - Folge 10 (Prost!)", "", "show", "prost")]
    [InlineData("sample-show.s01e01", "", "show", "")]
    public void Folgenname_wird_in_Teil_Serie_und_Titel_zerlegt(string name, string part, string series, string title)
    {
        var e = DuplicateNames.Episode(name);
        Assert.True(e.Found);
        Assert.Equal((part, series, title), (e.Part, e.SeriesKey, e.TitleKey));
    }

    [Theory]
    [InlineData("Urlaub 2019 - 001", "2019", "001")]
    [InlineData("Urlaub 2019 - 002", "2019", "002")]
    [InlineData("Glut.1995.German.DL.1080p.BluRay.x264", "1995", "")]
    [InlineData("Glut (1995)", "1995", "")]
    [InlineData("Neue.Stadt.2049.2017.German.2160p", "2017", "")]
    [InlineData("Neue.Stadt.1982.Final.Cut.1080p", "1982", "finalcut")]
    [InlineData("glut.1080p", "1995", "glut")]
    [InlineData("Der Bund (1972) CD1", "1972", "")]
    public void Zusatz_hinter_dem_Jahr_unterscheidet_Filme(string name, string year, string tail) => Assert.Equal(tail, DuplicateNames.MovieTail(name, year));

    // ---------------- Sperren, Aenderungen, Abbruch ----------------

    [Fact]
    public async Task Verschiedene_Folgen_Teile_und_Aufnahmen_kommen_gar_nicht_in_eine_Gruppe()
    {
        string[] names =
        [
            "Familie Muster/Staffel 5/familie.muster.s05e02a.mkv", "Familie Muster/Staffel 5/familie.muster.s05e02b.mkv", "Familie Muster/Staffel 5/familie.muster.s05e02c.mkv",
            "Familie Muster/Staffel 6/Familie.Muster.S06E01a.Blaue.Ernte.Teil.1.mkv", "Familie Muster/Staffel 6/Familie.Muster.S06E01b.Blaue.Ernte.Teil.2.mkv",
            "Nord Stadt/Staffel 14/nord.stadt.s14e00.Das.erste.Special.mkv", "Nord Stadt/Staffel 14/nord.stadt.s14e00.Ein.anderes.Special.mkv", "Nord Stadt/Staffel 14/nord.stadt.s14e00.Noch.eins.mkv",
            "Nord Stadt/Staffel 13/nord.stadt.s13e09.teil1.mkv", "Nord Stadt/Staffel 13/nord.stadt.s13e09.teil2.mkv", "Nord Stadt/Staffel 13/nord.stadt.s13e09.ukfilmfassung.mkv",
            "Musterberg/Staffel 4/musterberg.s04e04.1 Der Tag danach.mkv", "Musterberg/Staffel 4/musterberg.s04e04 Alles live.mkv",
            "Kinder/ShowA S01E01.mkv", "Kinder/ShowB S01E01.mkv",
            "Privat/Urlaub 2019 - 001.mkv", "Privat/Urlaub 2019 - 002.mkv", "Privat/Urlaub 2019 - 003.mkv",
            "Privat/Garten (1).mkv", "Privat/Garten (2).mkv", "Privat/Garten (3).mkv",
        ];
        var lib = NewLibrary(names.Select(n => (n, m.Original)).ToArray());
        var (a, plan, result) = await Clean(lib);
        Assert.Empty(a.Duplicates);
        Assert.Empty(plan);
        Assert.Empty(result.Deleted);
        AssertAllThere(lib, names);
    }

    // ---------------- Nach dem Loeschen ----------------

    [Fact]
    public async Task Gleiche_Nummer_mit_und_ohne_Titel_wird_verglichen_aber_nur_nach_Inhalt_entschieden()
    {
        var lib = NewLibrary(
            ("Serie/Staffel 1/S01E01 - Der Anfang.mkv", m.Reencoded), ("Serie/Staffel 1/Serie.S01E01.German.1080p.WEB.x264.mkv", m.Original),
            ("Serie/Staffel 1/S01E02 - Weiter.mkv", m.OtherPicture), ("Serie/Staffel 1/Serie.S01E02.German.1080p.WEB.x264.mkv", m.Original));
        var (a, plan, result) = await Clean(lib);
        Assert.Equal(2, a.Duplicates.Count);
        var gone = Assert.Single(result.Deleted);
        Assert.Equal(lib.Source("Serie/Staffel 1/Serie.S01E01.German.1080p.WEB.x264.mkv"), gone.File);
        Assert.Contains(L.T("gleichwertig", "equivalent"), gone.Why);
        var other = plan.Where(d => d.File.Contains("S01E02")).ToList();
        Assert.Equal(2, other.Count);
        Assert.DoesNotContain(other, d => d.WillDelete);
        Assert.Contains(other, d => d.Reason!.Contains(L.T("anderes Bild", "different picture")));
        AssertAllThere(lib, "Serie/Staffel 1/S01E01 - Der Anfang.mkv", "Serie/Staffel 1/S01E02 - Weiter.mkv", "Serie/Staffel 1/Serie.S01E02.German.1080p.WEB.x264.mkv");
    }

    [Fact]
    public async Task Sample_neben_dem_Original_wird_geloescht_das_Original_bleibt()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E07.German.720p.mkv", m.Original), ("Show/Show.S01E07.German.720p.sample.mkv", m.Sample),
            ("Show/Show.S01E08.German.720p.mkv", m.Original), ("Show/sample-show.s01e08.german.720p.mkv", m.Sample),
            ("Show/Show.S01E09.German.720p.mkv", m.Original), ("Show/Show.S01E09.Vorschau.mkv", m.Sample));
        var (_, plan, result) = await Clean(lib);
        Assert.Equal(2, result.Deleted.Count);
        Assert.All(result.Deleted, d => Assert.Contains(L.T("Sample", "sample"), d.Why));
        Assert.False(File.Exists(lib.Source("Show/Show.S01E07.German.720p.sample.mkv")));
        Assert.False(File.Exists(lib.Source("Show/sample-show.s01e08.german.720p.mkv")));
        Assert.True(Of(plan, lib.Source("Show/Show.S01E07.German.720p.mkv")).IsKept);
        Assert.Contains(L.T("andere Laufzeit", "different duration"), Of(plan, lib.Source("Show/Show.S01E09.Vorschau.mkv")).Reason);
        AssertAllThere(lib, "Show/Show.S01E07.German.720p.mkv", "Show/Show.S01E08.German.720p.mkv", "Show/Show.S01E09.German.720p.mkv", "Show/Show.S01E09.Vorschau.mkv");
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Halb_kopierte_Datei_wird_geloescht_und_die_vollstaendige_bleibt()
    {
        var half = (await m.Tools.ProbeAsync(m.HalfCopied))!;
        var full = (await m.Tools.ProbeAsync(m.Original))!;
        Assert.Equal(full.DurationSeconds, half.DurationSeconds, 1);
        Assert.True(await ContentCheck.TailEndAsync(m.Tools, m.HalfCopied, half.DurationSeconds) < 20);
        Assert.True(ContentCheck.IsWhole(full.DurationSeconds, await ContentCheck.TailEndAsync(m.Tools, m.Original, full.DurationSeconds)));

        var lib = NewLibrary(
            ("Show/Show.S01E02.mkv", m.Original), ("Show/Show.S01E02 (1).mkv", m.HalfCopied),
            ("Filme/Glut (1995).mkv", m.Original), ("Filme/Glut (1995) - Kopie.mkv", m.HalfCopied),
            ("Privat/Hochzeit.mkv", m.Original), ("Privat/Hochzeit - Kopie.mkv", m.HalfCopied));
        var (a, plan, result) = await Clean(lib);
        Assert.Equal(3, a.Duplicates.Count);
        Assert.True(result.Deleted.Count == 3, Describe(plan));
        Assert.All(result.Deleted, d => Assert.Contains(L.T("unvollständige Kopie", "incomplete copy"), d.Why));
        Assert.False(File.Exists(lib.Source("Show/Show.S01E02 (1).mkv")));
        Assert.False(File.Exists(lib.Source("Filme/Glut (1995) - Kopie.mkv")));
        Assert.False(File.Exists(lib.Source("Privat/Hochzeit - Kopie.mkv")));
        Assert.All(new[] { "Show/Show.S01E02.mkv", "Filme/Glut (1995).mkv", "Privat/Hochzeit.mkv" }, rel =>
        {
            Assert.True(Of(plan, lib.Source(rel)).IsKept);
            Assert.Equal(new FileInfo(m.Original).Length, new FileInfo(lib.Source(rel)).Length);
        });
    }

    [Fact]
    public async Task Halb_kopierte_Datei_mit_anderem_Namen_oder_anderem_Bild_bleibt()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E02.German.1080p.mkv", m.Original), ("Show/Show.S01E02.German.720p.mkv", m.HalfCopied),
            ("Privat/Hochzeit.mkv", m.OtherPicture), ("Privat/Hochzeit (1).mkv", m.HalfCopied));
        var (_, plan, result) = await Clean(lib);
        Assert.Empty(result.Deleted);
        Assert.DoesNotContain(plan, d => d.WillDelete);
        Assert.Contains(L.T("andere Laufzeit", "different duration"), Of(plan, lib.Source("Show/Show.S01E02.German.720p.mkv")).Reason);
        Assert.Contains(L.T("anderes Bild", "different picture"), Of(plan, lib.Source("Privat/Hochzeit (1).mkv")).Reason);
        AssertAllThere(lib, "Show/Show.S01E02.German.1080p.mkv", "Show/Show.S01E02.German.720p.mkv", "Privat/Hochzeit.mkv", "Privat/Hochzeit (1).mkv");
    }

    [Fact]
    public async Task Kuerzere_eigene_Fassung_mit_gleichem_Namen_ist_keine_unvollstaendige_Kopie()
    {
        var lib = NewLibrary(("Privat/Film.mkv", m.LowRes), ("Privat/Film (1).mkv", m.Sample));
        var (a, plan, result) = await Clean(lib);
        Assert.Single(a.Duplicates);
        Assert.Empty(result.Deleted);
        Assert.True(Of(plan, lib.Source("Privat/Film.mkv")).IsKept);
        Assert.Contains(L.T("andere Auflösung", "different resolution"), Of(plan, lib.Source("Privat/Film (1).mkv")).Reason);
        AssertAllThere(lib, "Privat/Film.mkv", "Privat/Film (1).mkv");
    }

    [Fact]
    public async Task Sind_nur_unvollstaendige_Dateien_da_wird_nichts_geloescht()
    {
        var lib = NewLibrary(("Show/Show.S01E03.mkv", m.HalfCopied), ("Show/Show.S01E03 (1).mkv", m.HalfCopied), ("Show/Show.S01E03.720p.mkv", m.Garbage));
        var (_, plan, result) = await Clean(lib);
        Assert.Equal(3, plan.Count);
        Assert.DoesNotContain(plan, d => d.WillDelete || d.IsKept);
        Assert.Empty(result.Deleted);
        AssertAllThere(lib, "Show/Show.S01E03.mkv", "Show/Show.S01E03 (1).mkv", "Show/Show.S01E03.720p.mkv");
    }

    [Fact]
    public async Task Neu_kodierte_kleinere_Fassung_die_groessere_wird_geloescht()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E03.German.1080p.x264.mkv", m.Original), ("Show/Show.S01E03.German.1080p.x265.mkv", m.Reencoded),
            ("Filme/Glut.1995.German.DL.1080p.BluRay.x264.mkv", m.Original), ("Filme/Glut (1995).mkv", m.Reencoded),
            ("Privat/Hochzeit.mkv", m.Original), ("Privat/Hochzeit - Kopie.mkv", m.Original));
        Assert.True(new FileInfo(m.Reencoded).Length < new FileInfo(m.Original).Length);
        var (_, plan, result) = await Clean(lib);
        Assert.Equal(3, result.Deleted.Count);
        Assert.Equal(3 * new FileInfo(m.Original).Length, result.Bytes);
        Assert.False(File.Exists(lib.Source("Show/Show.S01E03.German.1080p.x264.mkv")));
        Assert.False(File.Exists(lib.Source("Filme/Glut.1995.German.DL.1080p.BluRay.x264.mkv")));
        Assert.False(File.Exists(lib.Source("Privat/Hochzeit - Kopie.mkv")));
        AssertAllThere(lib, "Show/Show.S01E03.German.1080p.x265.mkv", "Filme/Glut (1995).mkv", "Privat/Hochzeit.mkv");
        Assert.All(result.Deleted, d => Assert.Contains(L.T("gleichwertig", "equivalent"), d.Why));
        Assert.Equal(3, plan.Count(d => d.IsKept));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Zwei_Serien_in_einem_Ordner_gleiche_Laufzeit_anderes_Bild_bleiben()
    {
        var lib = NewLibrary(
            ("Kinder/ShowA S01E01.mkv", m.Original), ("Kinder/ShowB S01E01.mkv", m.OtherPicture),
            ("Kinder/ShowA S01E02.mkv", m.Original), ("Kinder/S01E02.mkv", m.OtherPicture));
        var a = await Analyse(lib);
        var set = Assert.Single(a.Duplicates);
        Assert.Equal("S01E02", set.Label);
        var same = (await m.Tools.ProbeAsync(m.Original))!;
        var other = (await m.Tools.ProbeAsync(m.OtherPicture))!;
        Assert.Equal(same.DurationSeconds, other.DurationSeconds, 1);
        Assert.Equal((same.Video!.Width, same.Video.Height), (other.Video!.Width, other.Video.Height));
        Assert.Equal(PictureMatch.Different, await ContentCheck.SamePictureAsync(m.Tools, m.Original, m.OtherPicture, 30));
        Assert.Equal(PictureMatch.Same, await ContentCheck.SamePictureAsync(m.Tools, m.Original, m.Reencoded, 30));

        var plan = await Plan(lib, a);
        var result = DuplicateCleaner.Execute(plan, lib.In, lib.Out);
        Assert.Empty(result.Deleted);
        Assert.Contains(plan, d => !d.IsKept && d.Reason!.Contains(L.T("anderes Bild", "different picture")));
        AssertAllThere(lib, "Kinder/ShowA S01E01.mkv", "Kinder/ShowB S01E01.mkv", "Kinder/ShowA S01E02.mkv", "Kinder/S01E02.mkv");
    }

    [Fact]
    public async Task Zwei_verschiedene_dunkle_Aufnahmen_gelten_nicht_als_gleich()
    {
        var dir = Path.Combine(m.Dir, "dunkel-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string one = Path.Combine(dir, "eins.mkv"), two = Path.Combine(dir, "zwei.mkv"), again = Path.Combine(dir, "eins-neu.mkv");
        async Task Dark(string file, int x, int y) => await m.Ff(["-f", "lavfi", "-i", $"color=c=black:s=320x180:r=24,drawbox=x={x}:y={y}:w=30:h=20:color=white:t=fill", "-t", "30", "-c:v", "libx264", "-preset", "ultrafast", file]);
        await Dark(one, 40, 40);
        await Dark(two, 200, 100);
        await m.Ff(["-i", one, "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-crf", "30", again]);
        var a = (await ContentCheck.ThumbAsync(m.Tools, one, 15))!;
        var b = (await ContentCheck.ThumbAsync(m.Tools, two, 15))!;
        Assert.InRange(ContentCheck.Difference(a, b), 1, ContentCheck.MaxDifference);
        Assert.True(a.Max() - a.Min() >= ContentCheck.MinContrast);
        Assert.Equal((true, false), ContentCheck.Compare(a, b));
        Assert.NotEqual(PictureMatch.Same, await ContentCheck.SamePictureAsync(m.Tools, one, two, 30));
        Assert.Equal(PictureMatch.Same, await ContentCheck.SamePictureAsync(m.Tools, one, again, 30));

        Assert.Equal((true, false), ContentCheck.Compare(new byte[1024], new byte[1024]));
        Assert.Equal((false, false), ContentCheck.Compare(new byte[1024], Enumerable.Repeat((byte)40, 1024).ToArray()));
    }

    [Fact]
    public async Task Sprachfassungen_bleiben()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E01.GERMAN.mkv", m.Original), ("Show/Show.S01E01.ENGLISH.mkv", m.English),
            ("Show/Show.S01E02.German.mkv", m.OriginalNoLang), ("Show/Show.S01E02.English.mkv", m.ReencodedNoLang),
            ("Show/Show.S01E03.German.mkv", m.OriginalNoLang), ("Show/S01E03.mkv", m.ReencodedNoLang));
        var (a, plan, result) = await Clean(lib);
        Assert.Equal(3, a.Duplicates.Count);
        Assert.Empty(result.Deleted);
        Assert.Equal(3, plan.Count(d => !d.IsKept && d.Reason!.Contains(L.T("andere Sprache", "different language"))));
        AssertAllThere(lib, "Show/Show.S01E01.GERMAN.mkv", "Show/Show.S01E01.ENGLISH.mkv", "Show/Show.S01E02.German.mkv", "Show/Show.S01E02.English.mkv", "Show/Show.S01E03.German.mkv", "Show/S01E03.mkv");
    }

    [Fact]
    public async Task Andere_Aufloesung_bleibt()
    {
        var lib = NewLibrary(("Show/Show.S01E04.720p.mkv", m.Original), ("Show/Show.S01E04.480p.mkv", m.LowRes));
        var (_, plan, result) = await Clean(lib);
        Assert.Empty(result.Deleted);
        Assert.Contains(L.T("andere Auflösung", "different resolution"), Of(plan, lib.Source("Show/Show.S01E04.720p.mkv")).Reason);
        AssertAllThere(lib, "Show/Show.S01E04.720p.mkv", "Show/Show.S01E04.480p.mkv");
    }

    [Fact]
    public async Task Umgewandelte_MP4_mit_weniger_Ton_und_ohne_Untertitel_loescht_das_Original_nicht()
    {
        var lib = NewLibrary(("Filme/Glut (1995).mkv", m.Rich), ("Filme/Glut (1995).mp4", m.Derived));
        Assert.True(new FileInfo(m.Derived).Length < new FileInfo(m.Rich).Length);
        var (a, plan, result) = await Clean(lib);
        Assert.Single(a.Duplicates);
        Assert.Empty(result.Deleted);
        Assert.True(Of(plan, lib.Source("Filme/Glut (1995).mp4")).IsKept);
        Assert.Contains(L.T("Tonkanäle", "audio channels"), Of(plan, lib.Source("Filme/Glut (1995).mkv")).Reason);
        AssertAllThere(lib, "Filme/Glut (1995).mkv", "Filme/Glut (1995).mp4");
    }

    [Fact]
    public async Task Nicht_lesbare_Datei_wird_nie_angefasst()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E05.German.x265.mkv", m.Reencoded), ("Show/Show.S01E05.German.1080p.mkv", m.Garbage),
            ("Show/Show.S01E06.German.x265.mkv", m.Garbage), ("Show/Show.S01E06.German.1080p.mkv", m.Original));
        var (_, plan, result) = await Clean(lib);
        Assert.Empty(result.Deleted);
        Assert.DoesNotContain(plan, d => d.WillDelete);
        Assert.Contains(L.T("nicht lesbar", "not readable"), Of(plan, lib.Source("Show/Show.S01E05.German.1080p.mkv")).Reason);
        Assert.Contains(L.T("nicht lesbar", "not readable"), Of(plan, lib.Source("Show/Show.S01E06.German.x265.mkv")).Reason);
        AssertAllThere(lib, "Show/Show.S01E05.German.x265.mkv", "Show/Show.S01E05.German.1080p.mkv", "Show/Show.S01E06.German.x265.mkv", "Show/Show.S01E06.German.1080p.mkv");
    }

    [Fact]
    public async Task Dateien_in_verschiedenen_Ordnern_werden_nie_geloescht_aber_begruendet()
    {
        var lib = NewLibrary(("Filme/Glut.1995.German.mkv", m.Original), ("Glut (1995)/glut.1080p.mkv", m.Reencoded));
        var (a, plan, result) = await Clean(lib);
        Assert.Equal(2, Assert.Single(a.Duplicates).Files.Count);
        Assert.Empty(result.Deleted);
        Assert.Equal(2, plan.Count);
        Assert.All(plan, d => Assert.Contains(L.T("anderen Ordner", "different folder"), d.Reason));
    }

    [Fact]
    public async Task Sperre_im_Zielordner_blockiert()
    {
        const string big = "Show/Staffel 1/Show.S01E06.German.1080p.mkv", small = "Show/Staffel 1/Show.S01E06.German.x265.mkv";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        var a = await Analyse(lib);
        var lockDir = Path.ChangeExtension(lib.Target(big), ".lock");
        Directory.CreateDirectory(lockDir);
        Assert.Equal(new LibraryPaths(lib.In, lib.Out, lib.Source(big)).Lock, lockDir);
        var blocked = await Plan(lib, a);
        Assert.Equal(2, blocked.Count);
        Assert.DoesNotContain(blocked, d => d.WillDelete);
        Assert.All(blocked, d => Assert.Contains(L.T("gerade", "right now"), d.Reason));

        Directory.Delete(lockDir);
        var plan = await Plan(lib, a);
        Assert.True(Of(plan, lib.Source(big)).WillDelete);
        Directory.CreateDirectory(Path.ChangeExtension(lib.Target(small), ".lock"));
        var result = DuplicateCleaner.Execute(plan, lib.In, lib.Out);
        Assert.Empty(result.Deleted);
        Assert.Single(result.Failed);
        AssertAllThere(lib, big, small);

        Directory.Delete(Path.ChangeExtension(lib.Target(small), ".lock"));
        Assert.Single(DuplicateCleaner.Execute(plan, lib.In, lib.Out).Deleted);
        Assert.False(File.Exists(lib.Source(big)));
        Assert.True(File.Exists(lib.Source(small)));
    }

    [Theory]
    [InlineData("{0}.lock", true)]
    [InlineData("{0}.reparatur.lock", true)]
    [InlineData("{0}.mkv.gimbo-tmp", false)]
    [InlineData("{0}.mp4.1a2b3c.gimbo-tmp", false)]
    [InlineData("{0}.part.mkv", false)]
    public async Task Sperre_oder_Temp_Datei_neben_der_Datei_blockiert(string pattern, bool folder)
    {
        const string big = "Show/Show.S01E06.German.1080p.mkv", small = "Show/Show.S01E06.German.x265.mkv";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        var marker = Path.Combine(lib.Source("Show"), string.Format(pattern, "Show.S01E06.German.1080p"));
        if (folder) Directory.CreateDirectory(marker); else File.WriteAllBytes(marker, [1]);
        var (_, plan, result) = await Clean(lib);
        Assert.DoesNotContain(plan, d => d.WillDelete);
        Assert.Empty(result.Deleted);
        AssertAllThere(lib, big, small);
    }

    [Fact]
    public async Task Aendert_sich_eine_Datei_nach_der_Pruefung_wird_nicht_geloescht()
    {
        const string big = "Show/Show.S01E06.German.1080p.mkv", small = "Show/Show.S01E06.German.x265.mkv";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        var a = await Analyse(lib);
        var plan = await Plan(lib, a);
        Assert.True(Of(plan, lib.Source(big)).WillDelete);

        File.Copy(m.HalfCopied, lib.Source(small), overwrite: true);
        var changedKeep = DuplicateCleaner.Execute(plan, lib.In, lib.Out);
        Assert.Empty(changedKeep.Deleted);
        Assert.Contains(L.T("geändert", "changed"), Assert.Single(changedKeep.Failed).Message);
        Assert.True(File.Exists(lib.Source(big)));

        File.Copy(m.Reencoded, lib.Source(small), overwrite: true);
        File.AppendAllText(lib.Source(big), "x");
        Assert.Empty(DuplicateCleaner.Execute(plan, lib.In, lib.Out).Deleted);
        Assert.True(File.Exists(lib.Source(big)));

        File.Delete(lib.Source(small));
        File.Copy(m.Original, lib.Source(big), overwrite: true);
        Assert.Empty(DuplicateCleaner.Execute(plan, lib.In, lib.Out).Deleted);
        Assert.True(File.Exists(lib.Source(big)));
    }

    [Fact]
    public async Task Gleich_grosse_aber_ersetzte_Datei_wird_an_der_Aenderungszeit_erkannt()
    {
        const string big = "Show/Show.S01E06.German.1080p.mkv", small = "Show/Show.S01E06.German.x265.mkv";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        var plan = await Plan(lib, await Analyse(lib));
        Assert.True(Of(plan, lib.Source(big)).WillDelete);
        File.SetLastWriteTimeUtc(lib.Source(small), DateTime.UtcNow.AddMinutes(5));
        var result = DuplicateCleaner.Execute(plan, lib.In, lib.Out);
        Assert.Empty(result.Deleted);
        Assert.Contains(L.T("geändert", "changed"), Assert.Single(result.Failed).Message);
        AssertAllThere(lib, big, small);
    }

    [Fact]
    public async Task Eben_erst_kopierte_Dateien_bleiben_samt_ihrer_Gruppe()
    {
        const string full = "Show/Show.S01E02.mkv", half = "Show/Show.S01E02 (1).mkv";
        var lib = NewLibrary((full, m.Original), (half, m.HalfCopied));
        var a = await Analyse(lib);
        File.SetLastWriteTime(lib.Source(full), DateTime.Now.AddDays(-3));
        WaitUntilFree(lib.Source(full));
        var fresh = await DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, CancellationToken.None, lib.In, lib.Out, null, TimeSpan.FromMinutes(30));
        Assert.Equal(2, fresh.Count);
        Assert.DoesNotContain(fresh, d => d.WillDelete || d.IsKept);
        Assert.All(fresh, d => Assert.Contains(L.T("eben erst", "just now"), d.Reason));

        File.SetLastWriteTime(lib.Source(half), DateTime.Now.AddDays(-3));
        WaitUntilFree(lib.Source(half));
        var old = await DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, CancellationToken.None, lib.In, lib.Out, null, TimeSpan.FromMinutes(30));
        Assert.True(Of(old, lib.Source(half)).WillDelete);
        Assert.True(Of(old, lib.Source(full)).IsKept);
    }

    [Fact]
    public async Task Abbrechen_waehrend_der_Pruefung_wird_beachtet()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E01.x264.mkv", m.Original), ("Show/Show.S01E01.x265.mkv", m.Reencoded),
            ("Show/Show.S01E02.x264.mkv", m.Original), ("Show/Show.S01E02.x265.mkv", m.Reencoded));
        var a = await Analyse(lib);
        using var cts = new CancellationTokenSource();
        var seen = new List<AnalyzeProgress>();
        var progress = new SyncProgress<AnalyzeProgress>(p => { seen.Add(p); if (p.Done == 1) cts.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, cts.Token, lib.In, lib.Out, progress));
        Assert.Equal([0, 1], seen.Select(p => p.Done));
        Assert.All(seen, p => Assert.Equal(2, p.Total));
        using var before = new CancellationTokenSource();
        before.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => DuplicateCleaner.PlanAsync(a.Duplicates, m.Tools, before.Token));
        AssertAllThere(lib, "Show/Show.S01E01.x264.mkv", "Show/Show.S01E01.x265.mkv", "Show/Show.S01E02.x264.mkv", "Show/Show.S01E02.x265.mkv");
    }

    [Fact]
    public async Task Was_einer_Loeschung_als_Original_dient_wird_nicht_selbst_geloescht()
    {
        var dir = Path.Combine(m.Dir, "kette-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        string big = Path.Combine(dir, "gross.mkv"), mid = Path.Combine(dir, "mittel.mkv"), small = Path.Combine(dir, "klein.mkv");
        File.Copy(m.Original, big);
        File.Copy(m.Reencoded, small);
        await m.Ff(["-i", m.Original, "-map", "0", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "24", "-c:a", "copy", mid]);
        Assert.True(new FileInfo(small).Length < new FileInfo(mid).Length && new FileInfo(mid).Length < new FileInfo(big).Length);
        AnalyzedFile F(string path) => new(path, Path.GetFileName(path), "x", new FileInfo(path).Length, 30, "H.264", 180, AnalysisState.Open, 0, 0, "");
        DuplicateSet upper = new("x", "oben", [F(big), F(mid)]), lower = new("x", "unten", [F(mid), F(small)]);

        var plan = await DuplicateCleaner.PlanAsync([upper, lower], m.Tools, CancellationToken.None);
        Assert.Equal(big, Assert.Single(plan, d => d.WillDelete).File);
        Assert.Contains(plan, d => d.File == mid && ReferenceEquals(d.Set, lower) && !d.WillDelete && !d.IsKept);

        var reverse = await DuplicateCleaner.PlanAsync([lower, upper], m.Tools, CancellationToken.None);
        Assert.Equal(mid, Assert.Single(reverse, d => d.WillDelete).File);
        Assert.DoesNotContain(reverse, d => d.File == mid && d.IsKept);
        Assert.Contains(reverse, d => d.File == big && !d.WillDelete);

        DuplicateDecision Del(string file, string keep) => new(file, keep, new FileInfo(file).Length, null) { KeepBytes = new FileInfo(keep).Length };
        var result = DuplicateCleaner.Execute([Del(big, mid), Del(mid, small)], null, null);
        Assert.Equal(big, Assert.Single(result.Deleted).File);
        Assert.Equal(mid, Assert.Single(result.Failed).Decision.File);
        Assert.True(File.Exists(mid) && File.Exists(small));
    }

    [Fact]
    public async Task Zweite_Pruefung_nach_dem_Loeschen_plant_nichts_mehr()
    {
        var lib = NewLibrary(
            ("Show/Show.S01E03.German.1080p.x264.mkv", m.Original), ("Show/Show.S01E03.German.1080p.x265.mkv", m.Reencoded),
            ("Show/Show.S01E04.720p.mkv", m.Original), ("Show/Show.S01E04.480p.mkv", m.LowRes));
        var (a, plan, result) = await Clean(lib);
        Assert.Single(result.Deleted);

        var again = await Plan(lib, a);
        Assert.DoesNotContain(again, d => d.WillDelete);
        Assert.True(Of(again, lib.Source("Show/Show.S01E03.German.1080p.x264.mkv")).Gone);
        Assert.Empty(DuplicateCleaner.Execute(again, lib.In, lib.Out).Deleted);

        var shown = DuplicateCleaner.WithoutDeleted(a, result.Deleted);
        var left = Assert.Single(shown.Duplicates);
        Assert.Equal("S01E04", left.Label);
        Assert.Equal(a.Files.Count - 1, shown.Files.Count);
        Assert.DoesNotContain(shown.Files, f => f.Path == result.Deleted[0].File);
        Assert.Equal(a.SourceBytes - result.Bytes, shown.SourceBytes);
        Assert.Equal(shown.Files.Count, shown.Groups.Sum(g => g.Files));
        Assert.DoesNotContain(await Plan(lib, shown), d => d.WillDelete);

        var fresh = await Analyse(lib);
        Assert.Equal("S01E04", Assert.Single(fresh.Duplicates).Label);
    }

    [Fact]
    public async Task Gleicher_Name_andere_Endung_die_behaltene_rueckt_in_der_Analyse_nach()
    {
        var lib = NewLibrary(("Filme/Glut (1995).mkv", m.Original), ("Filme/Glut (1995).avi", m.Reencoded));
        var (a, _, result) = await Clean(lib);
        Assert.Equal(lib.Source("Filme/Glut (1995).mkv"), Assert.Single(a.Files).Path);
        Assert.Equal(lib.Source("Filme/Glut (1995).mkv"), Assert.Single(result.Deleted).File);
        var shown = DuplicateCleaner.WithoutDeleted(a, result.Deleted);
        Assert.Empty(shown.Duplicates);
        var now = Assert.Single(shown.Files);
        Assert.Equal(lib.Source("Filme/Glut (1995).avi"), now.Path);
        Assert.Equal(a.Files[0].State, now.State);
        Assert.Equal(new FileInfo(m.Reencoded).Length, now.SourceBytes);
    }

    [Fact]
    public async Task Komprimierte_Fassung_und_Vermerke_der_geloeschten_Quelle_verschwinden_die_der_behaltenen_bleiben()
    {
        const string big = "Show/Staffel 1/Show.S01E03.German.1080p.x264.mkv", small = "Show/Staffel 1/S01E03 - Titel.mkv";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        Directory.CreateDirectory(Path.GetDirectoryName(lib.Target(big))!);
        File.Copy(m.LowRes, lib.Target(big));
        File.Copy(m.LowRes, lib.Target(small));
        var bigPaths = new LibraryPaths(lib.In, lib.Out, lib.Source(big));
        var smallPaths = new LibraryPaths(lib.In, lib.Out, lib.Source(small));
        MarkList.MarkOk(bigPaths, lib.P.MarkTag, 1, 2);
        MarkList.Mark(bigPaths, lib.P.MarkTag, "x", "y", 95);
        MarkList.MarkOk(smallPaths, lib.P.MarkTag, 1, 2);
        var outSize = new FileInfo(lib.Target(big)).Length;
        MarkList.MarkVerified(lib.Out, lib.Target(big), outSize, "voll");
        MarkList.MarkVerified(lib.Out, lib.Target(small), outSize, "voll");

        var (_, _, result) = await Clean(lib);
        Assert.Equal(lib.Source(big), Assert.Single(result.Deleted).File);
        Assert.Equal(lib.Target(big), Assert.Single(result.Outputs));
        Assert.False(File.Exists(lib.Target(big)));
        Assert.Empty(MarkList.OwnMarks(bigPaths.MarkDir, bigPaths.Name));
        Assert.False(MarkList.IsVerified(lib.Out, lib.Target(big), outSize));
        Assert.True(File.Exists(lib.Target(small)));
        Assert.True(MarkList.IsOk(smallPaths, lib.P.MarkTag, 1, 2));
        Assert.True(MarkList.IsVerified(lib.Out, lib.Target(small), outSize));
        Assert.Empty(result.Errors);
    }

    [Fact]
    public async Task Gleicher_Zielname_die_komprimierte_Fassung_gehoert_der_behaltenen_Quelle()
    {
        const string big = "Show/Show.S01E03.mkv", small = "Show/Show.S01E03.avi";
        var lib = NewLibrary((big, m.Original), (small, m.Reencoded));
        Directory.CreateDirectory(Path.GetDirectoryName(lib.Target(big))!);
        File.Copy(m.LowRes, lib.Target(big));
        var paths = new LibraryPaths(lib.In, lib.Out, lib.Source(big));
        MarkList.MarkOk(paths, lib.P.MarkTag, 1, 2);
        var (_, _, result) = await Clean(lib);
        Assert.Equal(lib.Source(big), Assert.Single(result.Deleted).File);
        Assert.Empty(result.Outputs);
        Assert.True(File.Exists(lib.Target(big)));
        Assert.True(MarkList.IsOk(paths, lib.P.MarkTag, 1, 2));
    }

    // ---------------- Angaben der Dateien ----------------

    private static MediaInfo Info(double dur, int w = 1920, int h = 1080, string[]? audio = null, int channels = 2, int subs = 0, string transfer = "", string path = "x.mkv") => new()
    {
        Path = path,
        DurationSeconds = dur,
        Streams =
        [
            new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = w, Height = h, ColorTransfer = transfer },
            .. (audio ?? ["ger"]).Select((l, i) => new StreamInfo { Index = 1 + i, CodecType = "audio", CodecName = "ac3", Channels = channels, Tags = new(StringComparer.OrdinalIgnoreCase) { ["language"] = l } }),
            .. Enumerable.Range(0, subs).Select(i => new StreamInfo { Index = 10 + i, CodecType = "subtitle", CodecName = "subrip" }),
        ],
    };

    [Fact]
    public void Gleichwertig_nur_wenn_die_behaltene_nichts_weniger_hat()
    {
        var keep = Info(2700);
        Assert.Null(DuplicateCleaner.WhyNot(keep, Info(2700.9), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(keep, Info(2702.5), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2704), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(Info(660), Info(662), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, transfer: "smpte2084"), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, channels: 6), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(Info(2700, channels: 6), Info(2700), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, subs: 1), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(Info(2700, subs: 2), Info(2700, subs: 1), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, audio: ["eng"]), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(keep, Info(2700, audio: ["ger", "eng"]), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(Info(2700, audio: ["deu", "en"]), Info(2700, audio: ["ger"]), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(keep, Info(2700, audio: ["und"]), 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(Info(2700, audio: ["und"]), keep, 1_000, 3_000));
        Assert.NotNull(DuplicateCleaner.WhyNot(Info(2700, audio: [""], path: "Show.S01E01.German.mkv"), Info(2700, audio: [""], path: "Show.S01E01.English.mkv"), 1_000, 3_000));
        Assert.Null(DuplicateCleaner.WhyNot(keep, Info(2700), 3_000, 3_000));
    }
}
