using FilmKonverter.Core.Rename;

namespace FilmKonverter.Core.Tests;

public sealed class MovieRenameTests
{
    [Theory]
    [InlineData("Horizont.Aufbruch.nach.Nirgendwo.2009.German.DL.1080p.BluRay.x264-iND", "Horizont Aufbruch nach Nirgendwo", "2009", "")]
    [InlineData("The.Night.Guard.2008", "The Night Guard", "2008", "")]
    [InlineData("The Night Guard (2008)", "The Night Guard", "2008", "")]
    [InlineData("2050.A.Long.Journey.1968.1080p.BluRay", "2050 A Long Journey", "1968", "")]
    [InlineData("Die lange Reise - Der Aufbruch (2001) Extended", "Die lange Reise - Der Aufbruch", "2001", "Extended")]
    [InlineData("Neue.Stadt.1982.Final.Cut.German.DTS.DL.1080p", "Neue Stadt", "1982", "Final Cut")]
    [InlineData("River.Deep.1979.Directors.Cut.German.DL.720p", "River Deep", "1979", "Director's Cut")]
    [InlineData("The Final Signal 1980 German", "The Final Signal", "1980", "")]
    [InlineData("Der Final Cut (2004)", "Der Final Cut", "2004", "")]
    [InlineData("Traumreise.German.DL.1080p", "Traumreise", "", "")]
    [InlineData("Dr. Muster oder wie ich lernte das Warten zu lieben (1964)", "Dr. Muster oder wie ich lernte das Warten zu lieben", "1964", "")]
    [InlineData("1931.2019.German.UHD", "1931", "2019", "")]
    [InlineData("Maschine.2.1991.Uncut.German.DL", "Maschine 2", "1991", "Uncut")]
    [InlineData("[GRP] Nordwind 1988 1080p", "Nordwind", "1988", "")]
    [InlineData("Agent.English.2003.German.DL.1080p", "Agent English", "2003", "")]
    [InlineData("The.English.Garden.1996", "The English Garden", "1996", "")]
    [InlineData("Lenas.Web.2006.German", "Lenas Web", "2006", "")]
    [InlineData("A.Quiet.Stranger.2024.German.DL.1080p.WEB.H264", "A Quiet Stranger", "2024", "")]
    [InlineData("The.Last.German.2006", "The Last German", "2006", "")]
    [InlineData("Neue.Stadt.2049.German.DL.1080p", "Neue Stadt 2049", "", "")]
    [InlineData("Neue.Stadt.2049.2017.German.DL.1080p", "Neue Stadt 2049", "2017", "")]
    [InlineData("Traumreise.German.DL.2010.1080p", "Traumreise", "2010", "")]
    public void Filmnamen_werden_erkannt(string name, string title, string year, string edition)
    {
        var i = EpisodeParser.ParseMovie(name);
        Assert.True(i.Movie);
        Assert.Equal((title, year, edition), (i.Title, i.Year, i.Edition));
    }

    [Fact]
    public void Mehrteiler_und_Schema()
    {
        var i = EpisodeParser.ParseMovie("Der.Bund.1972.German.cd2");
        Assert.Equal(2, i.Part);
        Assert.Equal("Der Bund", i.Title);
        Assert.Equal("Der Bund (1972) - CD2", RenamePlanner.BuildName(RenameOptions.DefaultMovieTemplate, i));
        Assert.Equal("Der Bund (1972) - Teil 2", RenamePlanner.BuildName("{Titel} ({Jahr}) - Teil {Teil}", i));

        var ext = EpisodeParser.ParseMovie("Fremde.1986.Special.Edition.German.DL");
        Assert.Equal("Fremde (1986) - Special Edition", RenamePlanner.BuildName(RenameOptions.DefaultMovieTemplate, ext));
        Assert.Equal("Fremde (1986) [Special Edition]", RenamePlanner.BuildName("{Titel} ({Jahr}) [{Edition}]", ext));
        var plain = EpisodeParser.ParseMovie("Traumreise.German.DL.1080p");
        Assert.Equal("Traumreise", RenamePlanner.BuildName(RenameOptions.DefaultMovieTemplate, plain));
        Assert.Equal("Traumreise", RenamePlanner.BuildName("{Jahr} - {Titel} - {Edition}", plain));
        Assert.Equal("Auftrag - Unmoeglich (1996)", RenamePlanner.BuildName("{Titel} ({Jahr})", plain with { Title = "Auftrag: Unmoeglich", Year = "1996" }));
    }

    [Fact]
    public void Schema_Pruefung_fuer_Filme()
    {
        Assert.Null(RenamePlanner.TemplateProblem(RenameOptions.DefaultMovieTemplate, RenameKind.Movies));
        Assert.NotNull(RenamePlanner.TemplateProblem("{Jahr}", RenameKind.Movies));
        Assert.NotNull(RenamePlanner.TemplateProblem("{Titel} {S}", RenameKind.Movies));
        Assert.NotNull(RenamePlanner.TemplateProblem(RenameOptions.DefaultMovieTemplate));
    }

    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "fk-movies-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static void Touch(string path, string content = "x")
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void Filmordner_nfo_Begleitdateien_und_Rueckgaengig()
    {
        var root = NewDir();
        Touch(Path.Combine(root, "Horizont (2009)", "horizont.1080p-grp.mkv"));
        Touch(Path.Combine(root, "Horizont (2009)", "horizont.1080p-grp.nfo"), "<movie><title>Horizont</title><year>2009</year></movie>");
        Touch(Path.Combine(root, "Horizont (2009)", "horizont.1080p-grp.de.srt"));
        Touch(Path.Combine(root, "Sammlung", "billige.geschichten.mkv"));
        Touch(Path.Combine(root, "Sammlung", "billige.geschichten.nfo"), "<?xml version=\"1.0\"?>\n<movie>\n  <title>Billige Geschichten</title>\n  <year>1994</year>\n</movie>");
        Touch(Path.Combine(root, "Sammlung", "Glut.1995.German.DL.1080p.mkv"));
        Touch(Path.Combine(root, "Sammlung", "glut-sample.mkv"));

        var opt = new RenameOptions { Kind = RenameKind.Movies, Template = RenameOptions.DefaultMovieTemplate, LocalJournalDir = Path.Combine(root, "_log") };
        var items = RenamePlanner.Analyze([root], opt);
        var byOld = items.ToDictionary(i => i.OldName);

        Assert.Equal("Horizont (2009)", byOld["horizont.1080p-grp"].NewName);
        Assert.Equal("Billige Geschichten (1994)", byOld["billige.geschichten"].NewName);
        Assert.Equal("Glut (1995)", byOld["Glut.1995.German.DL.1080p"].NewName);
        Assert.Equal(RenameStatus.Unknown, byOld["glut-sample"].Status);
        Assert.All(items.Where(i => i.OldName != "glut-sample"), i => Assert.Equal(RenameStatus.Ready, i.Status));

        var res = RenamePlanner.Execute(items, opt);
        Assert.Equal(3, res.Renamed);
        Assert.True(File.Exists(Path.Combine(root, "Horizont (2009)", "Horizont (2009).mkv")));
        Assert.True(File.Exists(Path.Combine(root, "Horizont (2009)", "Horizont (2009).nfo")));
        Assert.True(File.Exists(Path.Combine(root, "Horizont (2009)", "Horizont (2009).de.srt")));
        Assert.True(Directory.Exists(Path.Combine(root, "Horizont (2009)")));
        Assert.True(File.Exists(Path.Combine(root, "Sammlung", "Billige Geschichten (1994).mkv")));

        RenamePlanner.Undo(res.Journal!);
        Assert.True(File.Exists(Path.Combine(root, "Sammlung", "billige.geschichten.mkv")));
        Assert.True(File.Exists(Path.Combine(root, "Horizont (2009)", "horizont.1080p-grp.de.srt")));
    }

    [Fact]
    public void Gleicher_Film_zweimal_ist_ein_Konflikt_und_Jahr_fehlt_wird_markiert()
    {
        var root = NewDir();
        Touch(Path.Combine(root, "Filme", "Glut.1995.German.mkv"));
        Touch(Path.Combine(root, "Filme", "Glut.1995.English.720p.mkv"));
        Touch(Path.Combine(root, "Filme", "Traumreise.German.mkv"));
        var items = RenamePlanner.Analyze([root], new RenameOptions { Kind = RenameKind.Movies, Template = RenameOptions.DefaultMovieTemplate });
        Assert.All(items.Where(i => i.OldName.StartsWith("Glut")), i => Assert.Equal(RenameStatus.Conflict, i.Status));
        var inc = items.Single(i => i.OldName.StartsWith("Traumreise"));
        Assert.Equal(RenameStatus.Check, inc.Status);
        Assert.Equal("Traumreise", inc.NewName);
    }
}
