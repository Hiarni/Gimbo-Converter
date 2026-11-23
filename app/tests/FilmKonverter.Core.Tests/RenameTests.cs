using FilmKonverter.Core.Library;
using FilmKonverter.Core.Rename;

namespace FilmKonverter.Core.Tests;

public sealed class RenameTests
{
    [Theory]
    [InlineData("Gute.Chemie.S02E09.Der.Anruf.German.DL.AC3.5.1.1080p.Bluray.x264-iND", "Staffel 2", 2, 9, "Der Anruf")]
    [InlineData("Folge 10 (Prost!)", "Staffel 4", 4, 10, "Prost!")]
    [InlineData("Nachbar.S08E12.German.DL.1080p.BluRay.x264", "Staffel 8", 8, 12, "")]
    [InlineData("Nachbar.Wiedererwachen.S01E02.GERMAN.DL.HDR.2160p.WEB.h265", "Staffel 1", 1, 2, "")]
    [InlineData("Insel.S01E04.German.DL.720p", "Staffel 1", 1, 4, "")]
    [InlineData("Gute Chemie - S01E03 - Der Titel", "Staffel 1", 1, 3, "Der Titel")]
    [InlineData("show 1x05 Pilot", "Season 1", 1, 5, "Pilot")]
    [InlineData("Serie.S03E07.Die.Rückkehr.GERMAN.DL.WEB.x264-GRP", "Staffel 3", 3, 7, "Die Rückkehr")]
    public void Dateinamen_werden_erkannt(string name, string folder, int season, int episode, string title)
    {
        var i = EpisodeParser.Parse(name, folder);
        Assert.Equal((season, episode, title), (i.Season!.Value, i.Episode!.Value, i.Title));
    }

    [Fact]
    public void Doppelfolge_und_Specials()
    {
        var d = EpisodeParser.Parse("Show.S01E01E02.German.720p", "Staffel 1");
        Assert.Equal((1, 2), (d.Episode, d.EpisodeEnd));
        Assert.Equal("S01E01-E02", RenamePlanner.BuildName(RenameOptions.DefaultTemplate, d with { Series = "Show" }));
        Assert.Equal("Show - S01E01-E02", RenamePlanner.BuildName(RenameOptions.OldDefaultTemplate, d with { Series = "Show" }));
        Assert.Equal(0, EpisodeParser.SeasonFromFolderName("Specials"));
        Assert.Equal(4, EpisodeParser.SeasonFromFolderName("Staffel 4"));
        Assert.Null(EpisodeParser.SeasonFromFolderName("Extras Bonus"));
    }

    [Theory]
    [InlineData("Nachbar: Rueckkehr - S01E02 - Was?", "Nachbar - Rueckkehr - S01E02 - Was")]
    [InlineData("Serie - S01E01 - 10:30 Uhr", "Serie - S01E01 - 10.30 Uhr")]
    [InlineData("A/B - S01E01 - \"Zitat\"", "A-B - S01E01 - 'Zitat'")]
    [InlineData("Titel mit Punkt am Ende.", "Titel mit Punkt am Ende")]
    public void Namen_werden_fuer_Windows_bereinigt(string input, string expected) => Assert.Equal(expected, RenamePlanner.Sanitize(input));

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "ren-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Fact]
    public void Begleitdateien_ohne_Verwechslung()
    {
        var dir = Path.Combine(NewRoot(), "S");
        var v1 = Touch(Path.Combine(dir, "Folge 1.mkv"));
        Touch(Path.Combine(dir, "Folge 1.nfo"));
        Touch(Path.Combine(dir, "Folge 1.de.forced.srt"));
        Touch(Path.Combine(dir, "Folge 1-thumb.jpg"));
        Touch(Path.Combine(dir, "Folge 10.nfo"));
        Touch(Path.Combine(dir, "Folge 1-2.nfo"));
        var c = RenamePlanner.Companions(v1).Select(Path.GetFileName).OrderBy(x => x).ToArray();
        Assert.Equal(new[] { "Folge 1-thumb.jpg", "Folge 1.de.forced.srt", "Folge 1.nfo" }, c);
    }

    [Fact]
    public void Umbenennen_mit_Ziel_und_Vermerken_und_rueckgaengig()
    {
        var root = NewRoot();
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        DataStore.Link(output, input);
        var show = Path.Combine(input, "Gute Chemie");
        Touch(Path.Combine(show, "tvshow.nfo"), "<tvshow><title>Gute Chemie</title><year>2008</year></tvshow>");
        var src = Touch(Path.Combine(show, "Staffel 4", "Folge 10 (Prost!).mkv"), "video");
        Touch(Path.Combine(show, "Staffel 4", "Folge 10 (Prost!).nfo"),
            "<?xml version=\"1.0\"?><episodedetails><title>Zum Wohl</title><season>4</season><episode>10</episode></episodedetails>");
        Touch(Path.Combine(show, "Staffel 4", "Folge 10 (Prost!)-thumb.jpg"));
        var other = Touch(Path.Combine(show, "Staffel 4", "Gute.Chemie.S04E01.Werkzeug.German.DL.1080p.x264.mkv"), "video");
        var outDir = Path.Combine(output, "Gute Chemie", "Staffel 4");
        Touch(Path.Combine(outDir, "Folge 10 (Prost!).mkv"), "small");
        Touch(Path.Combine(outDir, "Folge 10 (Prost!).nfo"), "<episodedetails/>");
        Touch(Path.Combine(DataStore.Marks(output), "Gute Chemie", "Staffel 4", "Folge 10 (Prost!).ok_G95_5_5.txt"));
        Touch(Path.Combine(DataStore.VerifyMarks(output), "Gute Chemie", "Staffel 4", "Folge 10 (Prost!).mkv.5.ok"));

        var opt = new RenameOptions();
        var items = RenamePlanner.Analyze(input, opt, output);
        var it = items.Single(i => i.Source == src);
        Assert.Equal("S04E10 - Zum Wohl", it.NewName);
        Assert.Equal(RenameStatus.Ready, it.Status);
        var it2 = items.Single(i => i.Source == other);
        Assert.Equal("S04E01 - Werkzeug", it2.NewName);
        Assert.Equal(RenameStatus.Ready, it2.Status);

        it.Selected = true;
        var r = RenamePlanner.Execute(items, opt);
        Assert.Equal(2, r.Renamed);
        Assert.Equal(0, r.Failed);
        string N(string s) => Path.Combine(show, "Staffel 4", s);
        Assert.True(File.Exists(N("S04E10 - Zum Wohl.mkv")));
        Assert.True(File.Exists(N("S04E10 - Zum Wohl.nfo")));
        Assert.True(File.Exists(N("S04E10 - Zum Wohl-thumb.jpg")));
        Assert.True(File.Exists(Path.Combine(outDir, "S04E10 - Zum Wohl.mkv")));
        Assert.True(File.Exists(Path.Combine(outDir, "S04E10 - Zum Wohl.nfo")));
        Assert.True(File.Exists(Path.Combine(DataStore.Marks(output), "Gute Chemie", "Staffel 4", "S04E10 - Zum Wohl.ok_G95_5_5.txt")));
        Assert.True(File.Exists(Path.Combine(DataStore.VerifyMarks(output), "Gute Chemie", "Staffel 4", "S04E10 - Zum Wohl.mkv.5.ok")));
        Assert.True(Directory.Exists(Path.Combine(show, "Staffel 4")));
        Assert.False(Directory.Exists(Path.Combine(outDir, "Folge 10 (Prost!).lock")));

        Assert.All(RenamePlanner.Analyze(input, opt, output), i => Assert.Equal(RenameStatus.Unchanged, i.Status));

        var journal = Assert.Single(RenamePlanner.Journals(output, input));
        var u = RenamePlanner.Undo(journal.File);
        Assert.Equal(0, u.Failed);
        Assert.True(File.Exists(src));
        Assert.True(File.Exists(N("Folge 10 (Prost!)-thumb.jpg")));
        Assert.True(File.Exists(other));
        Assert.True(File.Exists(Path.Combine(outDir, "Folge 10 (Prost!).mkv")));
        Assert.True(File.Exists(Path.Combine(DataStore.VerifyMarks(output), "Gute Chemie", "Staffel 4", "Folge 10 (Prost!).mkv.5.ok")));
        Assert.True(Assert.Single(RenamePlanner.Journals(output, input)).Undone);
    }

    [Fact]
    public void Datei_in_Arbeit_wird_nicht_angefasst()
    {
        var root = NewRoot();
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        var src = Touch(Path.Combine(input, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"));
        var lockDir = Path.Combine(output, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.lock");
        Directory.CreateDirectory(lockDir);
        File.WriteAllText(Path.Combine(lockDir, "info.txt"), "ANDERERPC 2026-01-01 10:00");
        var items = RenamePlanner.Analyze(input, new RenameOptions(), output);
        Assert.Equal(RenameStatus.Conflict, Assert.Single(items).Status);
        items[0].Selected = true;
        Assert.Equal(0, RenamePlanner.Execute(items, new RenameOptions()).Renamed);
        items[0].Status = RenameStatus.Ready;
        var r = RenamePlanner.Execute(items, new RenameOptions());
        Assert.Equal(1, r.Failed);
        Assert.Contains("ANDERERPC", r.Errors[0]);
        Assert.True(File.Exists(src));
    }

    [Fact]
    public void Doppelte_Zielnamen_sind_Konflikte()
    {
        var root = NewRoot();
        var a = Touch(Path.Combine(root, "Show", "Staffel 1", "Show.S01E01.German.720p.mkv"));
        var b = Touch(Path.Combine(root, "Show", "Staffel 1", "Show.S01E01.German.1080p.mkv"));
        var items = RenamePlanner.Analyze(root, new RenameOptions());
        Assert.All(items, i => Assert.Equal(RenameStatus.Conflict, i.Status));
        Assert.All(items, i => Assert.False(i.Selected));
    }

    [Fact]
    public void Freie_Quellen_Ordner_und_Dateien()
    {
        var root = NewRoot();
        var a = Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"));
        var b = Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E05.German.DL.720p.mkv"));
        var c = Touch(Path.Combine(root, "Nachbar", "Nachbar.S08E12.German.DL.1080p.BluRay.x264.mkv"));
        Touch(Path.Combine(root, "Insel", "Staffel 1", "notiz.txt"));
        var opt = new RenameOptions();
        var items = RenamePlanner.Analyze([Path.Combine(root, "Insel", "Staffel 1"), c, c], opt);
        Assert.Equal(3, items.Count);
        Assert.Equal(new[] { "S01E04", "S01E05", "S08E12" }, items.Select(i => i.NewName).OrderBy(x => x).ToArray());
        Assert.Equal("Nachbar", items.Single(i => i.Source == c).Info.Series);
        Assert.Equal(Path.Combine("Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"), items.Single(i => i.Source == a).RelativeSource);
        Assert.All(items, i => Assert.Null(i.Library));
    }

    [Fact]
    public void Ohne_Bibliothek_lokales_Protokoll_und_rueckgaengig()
    {
        var root = NewRoot();
        var a = Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"));
        Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.nfo"), "<episodedetails><title>Der Weg</title></episodedetails>");
        var local = Path.Combine(root, "_lokal");
        var opt = new RenameOptions { LocalJournalDir = local };
        var items = RenamePlanner.Analyze([root], opt);
        Assert.Equal("S01E04 - Der Weg", Assert.Single(items).NewName);
        var r = RenamePlanner.Execute(items, opt);
        Assert.Equal(1, r.Renamed);
        Assert.StartsWith(local, r.Journal);
        Assert.True(File.Exists(Path.Combine(root, "Insel", "Staffel 1", "S01E04 - Der Weg.nfo")));
        RenamePlanner.Undo(Assert.Single(RenamePlanner.Journals([local])).File);
        Assert.True(File.Exists(a));
    }

    [Fact]
    public void Platzhalter_Jahr_und_leere_Werte()
    {
        var i = new EpisodeInfo { Series = "Insel", Season = 1, Episode = 4, Title = "Der Weg", Year = "2004" };
        Assert.Equal("Insel (2004) - S01E04 - Der Weg", RenamePlanner.BuildName("{Serie} ({Jahr}) - S{S}E{E} - {Titel}", i));
        Assert.Equal("Insel - S01E04", RenamePlanner.BuildName("{Serie} ({Jahr}) - S{S}E{E} - {Titel}", i with { Year = "", Title = "" }));
        Assert.Equal("S01E04 - Der Weg", RenamePlanner.BuildName("S{S}E{E} - {Title}", i));
    }

    [Fact]
    public void Schema_aendern_ohne_neue_Analyse_behaelt_Handarbeit()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"));
        Touch(Path.Combine(root, "Insel", "Staffel 1", "Insel.S01E05.German.DL.720p.mkv"));
        var items = RenamePlanner.Analyze([root], new RenameOptions());
        var edited = items.Single(i => i.Info.Episode == 5);
        edited.NewName = "S01E05 - Weißer Hase";
        edited.Edited = true;
        RenamePlanner.Render(items, "{Serie} - S{S}E{E}");
        RenamePlanner.Validate(items);
        Assert.Equal("Insel - S01E04", items.Single(i => i.Info.Episode == 4).NewName);
        Assert.Equal("S01E05 - Weißer Hase", edited.NewName);
        Assert.Equal(RenameStatus.Ready, edited.Status);
    }

    [Fact]
    public void Serienordner_mit_Jahr_im_Namen()
    {
        var root = NewRoot();
        Touch(Path.Combine(root, "Insel (2004)", "Staffel 1", "Insel.S01E04.German.DL.720p.mkv"));
        var it = Assert.Single(RenamePlanner.Analyze([root], new RenameOptions { Template = "{Serie} - S{S}E{E}" }));
        Assert.Equal("2004", it.Info.Year);
    }

    [Fact]
    public void Kurzform_und_Schema_Pruefung()
    {
        var i = new EpisodeInfo { Series = "Insel", Season = 2, Episode = 9, Title = "Test" };
        Assert.Equal("2x09 - Test", RenamePlanner.BuildName("{Staffel}x{E} - {Titel}", i));
        Assert.Equal("Staffel 2 Folge 9 - Test", RenamePlanner.BuildName("Staffel {Staffel} Folge {Folge} - {Titel}", i));
        Assert.Null(RenamePlanner.TemplateProblem(RenameOptions.DefaultTemplate));
        Assert.All(RenameOptions.BuiltInPresets, p => Assert.Null(RenamePlanner.TemplateProblem(p.Template)));
        Assert.NotNull(RenamePlanner.TemplateProblem("{Serie} - {Titel}"));
        Assert.NotNull(RenamePlanner.TemplateProblem("S{S}E{E} - {Name}"));
    }
}
