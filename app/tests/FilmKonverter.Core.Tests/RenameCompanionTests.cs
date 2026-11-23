using System.Text;
using FilmKonverter.Core.Rename;

namespace FilmKonverter.Core.Tests;

public sealed class RenameCompanionTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "renc-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("3 Ein Ärgerliches Leben", 3, "Ein Ärgerliches Leben")]
    [InlineData("03 - Bühne frei für Marge", 3, "Bühne frei für Marge")]
    [InlineData("12. Marge muss Jobben", 12, "Marge muss Jobben")]
    [InlineData("7", 7, "")]
    public void Nummer_vorne_mit_Staffel_aus_dem_Ordner(string name, int episode, string title)
    {
        var i = EpisodeParser.Parse(name, "Staffel 15");
        Assert.Equal((15, episode, title), (i.Season!.Value, i.Episode!.Value, i.Title));
        Assert.True(i.SeasonFromFolder);
    }

    [Fact]
    public void Nummer_vorne_nur_im_Staffelordner()
    {
        Assert.Null(EpisodeParser.Parse("1931", "Filme").Episode);
        Assert.Null(EpisodeParser.Parse("300 Rise of an Empire", null).Episode);
        Assert.Null(EpisodeParser.Parse("720p Testclip", "Staffel 1").Episode);
    }

    [Fact]
    public void Kapitel_xml_und_Untertitel_wandern_mit()
    {
        var dir = Path.Combine(NewRoot(), "Die Mustermanns", "Staffel 15");
        var v = Touch(Path.Combine(dir, "3 Ein Ärgerliches Leben.mkv"));
        Touch(Path.Combine(dir, "3 Ein Ärgerliches Leben.nfo"), "<episodedetails><title>Ein Ärgerliches Leben</title><season>15</season><episode>3</episode></episodedetails>");
        Touch(Path.Combine(dir, "3 Ein Ärgerliches Leben_chapters.xml"));
        Touch(Path.Combine(dir, "3 Ein Ärgerliches Leben.deu.srt"));
        Touch(Path.Combine(dir, "33 Andere Folge.mkv"));
        Touch(Path.Combine(dir, "33 Andere Folge_chapters.xml"));

        var companions = RenamePlanner.Companions(v).Select(Path.GetFileName).ToHashSet();
        Assert.True(companions.SetEquals(["3 Ein Ärgerliches Leben.deu.srt", "3 Ein Ärgerliches Leben.nfo", "3 Ein Ärgerliches Leben_chapters.xml"]));

        var opt = new RenameOptions { LocalJournalDir = Path.Combine(dir, "..", "_lokal") };
        var items = RenamePlanner.Analyze([dir], opt);
        var item = items.Single(i => i.Source == v);
        Assert.Equal("S15E03 - Ein Ärgerliches Leben", item.NewName);
        RenamePlanner.Execute([item], opt);
        foreach (var f in new[] { ".mkv", ".nfo", "_chapters.xml", ".deu.srt" })
            Assert.True(File.Exists(Path.Combine(dir, "S15E03 - Ein Ärgerliches Leben" + f)), f);
        Assert.True(File.Exists(Path.Combine(dir, "33 Andere Folge_chapters.xml")));
    }

    [Fact]
    public void Umlaute_in_anderer_Schreibweise_werden_erkannt()
    {
        var dir = Path.Combine(NewRoot(), "Die Mustermanns", "Staffel 15");
        var composed = "4 Anna die Schönheitskönigin";
        var decomposed = composed.Normalize(NormalizationForm.FormD);
        var v = Touch(Path.Combine(dir, composed + ".mkv"));
        Touch(Path.Combine(dir, decomposed + ".nfo"), "<episodedetails><title>Anna die Schönheitskönigin</title></episodedetails>");
        Touch(Path.Combine(dir, decomposed + "_chapters.xml"));
        Assert.Equal(2, RenamePlanner.Companions(v).Count);
        Assert.NotNull(RenamePlanner.Sibling(v, ".nfo"));
        var item = RenamePlanner.Moves(new RenameItem { Source = v, RelativeSource = "x", Info = new EpisodeInfo { Season = 15, Episode = 4 }, NewName = "S15E04 - Anna die Schönheitskönigin" });
        Assert.Contains(item, m => m.To.EndsWith("S15E04 - Anna die Schönheitskönigin_chapters.xml", StringComparison.Ordinal));
        Assert.Contains(item, m => m.To.EndsWith("S15E04 - Anna die Schönheitskönigin.nfo", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("Die.Mustermanns.S07E02.Fernsehstar.wider.Willen.GERMAN.DL.1080p.BluRay.x264-TSCC.xml", ".xml")]
    [InlineData("Die.Mustermanns.S07E02.Fernsehstar.wider.Willen.GERMAN.DL.1080p.BluRay.x264-TSCC_chapters.xml", "_chapters.xml")]
    [InlineData("Die.Mustermanns.S07E02.Fernsehstar.GERMAN.DL.x264.de.forced.srt", ".de.forced.srt")]
    [InlineData("Die.Mustermanns.S07E02.Fernsehstar.GERMAN.DL.x264.deu.srt", ".deu.srt")]
    [InlineData("Die.Mustermanns.S07E02.Fernsehstar.GERMAN.DL.x264-thumb.jpg", "-thumb.jpg")]
    public void Kennungen_alter_Dateien_bleiben(string file, string suffix) => Assert.Equal(suffix, RenamePlanner.OrphanSuffix(file));

    [Fact]
    public void Liegengebliebene_Dateien_mit_altem_Namen_werden_nachgezogen()
    {
        var root = NewRoot();
        var dir = Path.Combine(root, "Die Mustermanns", "Staffel 7");
        var v1 = Touch(Path.Combine(dir, "S07E02 - Fernsehstar wider Willen.mkv"));
        Touch(Path.Combine(dir, "S07E02 - Fernsehstar wider Willen.nfo"), "<episodedetails><title>Fernsehstar wider Willen</title></episodedetails>");
        var o1 = Touch(Path.Combine(dir, "Die.Mustermanns.S07E02.Fernsehstar.wider.Willen.GERMAN.DL.1080p.BluRay.x264-TSCC.xml"));
        var o2 = Touch(Path.Combine(dir, "Die.Mustermanns.S07E02.Fernsehstar.wider.Willen.GERMAN.DL.1080p.BluRay.x264-TSCC.deu.srt"));
        Touch(Path.Combine(dir, "S07E03 - Bei Mustermanns stimmt was nicht.mkv"));
        Touch(Path.Combine(dir, "S07E03 - Bei Mustermanns stimmt was nicht.nfo"), "<episodedetails><title>Bei Mustermanns stimmt was nicht</title></episodedetails>");
        var o3 = Touch(Path.Combine(dir, "Die.Mustermanns.S07E03.Bei.Mustermanns.stimmt.was.nicht.GERMAN.xml"));
        Touch(Path.Combine(dir, "S07E03 - Bei Mustermanns stimmt was nicht.xml"), "neu");
        var o4 = Touch(Path.Combine(dir, "Die.Mustermanns.S07E25.Ein.Sommer.xml"));

        var opt = new RenameOptions { LocalJournalDir = Path.Combine(root, "_lokal") };
        var items = RenamePlanner.Analyze([dir], opt);
        var i1 = items.Single(i => i.Source == v1);
        Assert.False(i1.NameChanges);
        Assert.Equal(2, i1.Orphans.Count);
        Assert.True(i1.Selected);

        var r = RenamePlanner.Execute(items, opt);
        Assert.Equal(0, r.Failed);
        Assert.True(File.Exists(Path.Combine(dir, "S07E02 - Fernsehstar wider Willen.xml")));
        Assert.True(File.Exists(Path.Combine(dir, "S07E02 - Fernsehstar wider Willen.deu.srt")));
        Assert.False(File.Exists(o1) || File.Exists(o2));
        Assert.True(File.Exists(o3));
        Assert.Equal("neu", File.ReadAllText(Path.Combine(dir, "S07E03 - Bei Mustermanns stimmt was nicht.xml")));
        Assert.True(File.Exists(o4));
        Assert.True(File.Exists(v1));

        RenamePlanner.Undo(Assert.Single(RenamePlanner.Journals([opt.LocalJournalDir])).File);
        Assert.True(File.Exists(o1) && File.Exists(o2));
    }
}

public sealed class RenamePartTests
{
    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "renp-" + Guid.NewGuid().ToString("N")[..8]);

    private static string Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    [Theory]
    [InlineData("tvarchiv.meeres.held.s08e01a", 8, 1, "a", "")]
    [InlineData("tvarchiv.meeres.held.s08e01b", 8, 1, "b", "")]
    [InlineData("Familie.Muster.S06E01a.Blaue.Ernte.Teil.1.GERMAN.AC3.720p.WEB.x265-w00t", 6, 1, "a", "Blaue Ernte Teil 1")]
    [InlineData("wdc-zeitreise.2005.s13e09.teil1-720p", 13, 9, "a", "")]
    [InlineData("wdc-zeitreise.2005.s13e09.teil2-720p", 13, 9, "b", "")]
    [InlineData("wdc-zeitreise.2005.s13e09.ukfilmfassung-720p", 13, 9, "", "ukfilmfassung")]
    [InlineData("s04e04.1 Der Tag danach", 4, 4, "", "Der Tag danach")]
    [InlineData("Gute.Chemie.S02E09.Der.Anruf.German.DL.AC3.5.1.1080p.Bluray.x264-iND", 2, 9, "", "Der Anruf")]
    [InlineData("Show.S01E05-720p", 1, 5, "", "")]
    public void Teile_werden_erkannt(string name, int season, int episode, string part, string title)
    {
        var i = EpisodeParser.Parse(name, $"Staffel {season}");
        Assert.Equal((season, episode, part, title), (i.Season!.Value, i.Episode!.Value, i.PartLetter, i.Title));
    }

    [Fact]
    public void Teil_steht_im_neuen_Namen()
    {
        var i = EpisodeParser.Parse("tvarchiv.meeres.held.s08e01b", "Staffel 8") with { Title = "Titel" };
        Assert.Equal("S08E01b - Titel", RenamePlanner.BuildName(RenameOptions.DefaultTemplate, i));
        Assert.Equal("8x01b - Titel", RenamePlanner.BuildName("{Staffel}x{E} - {Titel}", i));
    }

    [Fact]
    public void Teile_und_Fassungen_ergeben_verschiedene_Namen()
    {
        var root = NewRoot();
        var sb = Path.Combine(root, "SpongeBob", "Staffel 8");
        Touch(Path.Combine(sb, "tvarchiv.meeres.held.s08e01a.mkv"));
        Touch(Path.Combine(sb, "tvarchiv.meeres.held.s08e01a.nfo"), "<episodedetails><title>Teil eins</title></episodedetails>");
        Touch(Path.Combine(sb, "tvarchiv.meeres.held.s08e01b.mkv"));
        Touch(Path.Combine(sb, "tvarchiv.meeres.held.s08e01b.nfo"), "<episodedetails><title>Teil zwei</title></episodedetails>");

        var dw = Path.Combine(root, "Zeit Reise (2005)", "Staffel 14");
        foreach (var t in new[] { "das.lachen", "in.weiter.ferne", "vom.himmel.hoch" })
        {
            Touch(Path.Combine(dw, $"zeit.reise.2005.s14e00.{t}.german.dl.1080p.web.h264-wayne.mkv"));
            Touch(Path.Combine(dw, $"zeit.reise.2005.s14e00.{t}.german.dl.1080p.web.h264-wayne.nfo"), "<episodedetails><title>zeit.reise</title></episodedetails>");
        }

        var hl = Path.Combine(root, "Horvathslos", "Staffel 4");
        Touch(Path.Combine(hl, "s04e04 Alles live.mkv"));
        Touch(Path.Combine(hl, "s04e04 Alles live.nfo"), "<episodedetails><title>Reingelegt</title></episodedetails>");
        Touch(Path.Combine(hl, "s04e04.1 Der Tag danach.mkv"));
        Touch(Path.Combine(hl, "s04e04.1 Der Tag danach.nfo"), "<episodedetails><title>Reingelegt</title></episodedetails>");

        var items = RenamePlanner.Analyze([root], new RenameOptions());
        string Name(string file) => items.Single(i => Path.GetFileName(i.Source) == file).NewName;
        Assert.Equal("S08E01a - Teil eins", Name("tvarchiv.meeres.held.s08e01a.mkv"));
        Assert.Equal("S08E01b - Teil zwei", Name("tvarchiv.meeres.held.s08e01b.mkv"));
        Assert.Equal("S14E00 - das lachen", Name("zeit.reise.2005.s14e00.das.lachen.german.dl.1080p.web.h264-wayne.mkv"));
        Assert.Equal("S14E00 - in weiter ferne", Name("zeit.reise.2005.s14e00.in.weiter.ferne.german.dl.1080p.web.h264-wayne.mkv"));
        Assert.Equal("S04E04 - Alles live", Name("s04e04 Alles live.mkv"));
        Assert.Equal("S04E04 - Der Tag danach", Name("s04e04.1 Der Tag danach.mkv"));
        Assert.DoesNotContain(items, i => i.Status == RenameStatus.Conflict);
    }
}

public sealed class RenameJournalDeleteTests
{
    [Fact]
    public void Protokolle_loeschen_laesst_Dateien_und_Fremdes_in_Ruhe()
    {
        var root = Path.Combine(Path.GetTempPath(), "renj-" + Guid.NewGuid().ToString("N")[..8]);
        var dir = Path.Combine(root, "Insel", "Staffel 1");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "Insel.S01E04.German.DL.720p.mkv"), "x");
        var local = Path.Combine(root, "_lokal");
        var opt = new RenameOptions { LocalJournalDir = local };
        var items = RenamePlanner.Analyze([dir], opt);
        foreach (var i in items) i.Selected = true;
        var r = RenamePlanner.Execute(items, opt);
        var foreign = Path.Combine(local, "fremd.jsonl");
        File.WriteAllText(foreign, "kein Protokoll");

        Assert.Equal(1, RenamePlanner.DeleteJournals([r.Journal!, foreign]));
        Assert.False(File.Exists(r.Journal));
        Assert.True(File.Exists(foreign));
        Assert.Empty(RenamePlanner.Journals([local]));
        Assert.True(File.Exists(Path.Combine(dir, "S01E04.mkv")));
    }
}
