using FilmKonverter.Core.Library;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

public sealed class OptionsTests
{
    private static string NewDir()
    {
        var d = Path.Combine(Path.GetTempPath(), "gk-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static string Make(string root, string rel, int bytes, DateTime? written = null)
    {
        var f = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(f)!);
        File.WriteAllBytes(f, new byte[bytes]);
        File.SetLastWriteTime(f, written ?? DateTime.Now.AddDays(-2));
        try { File.SetCreationTime(f, written ?? DateTime.Now.AddDays(-2)); } catch { }
        return f;
    }

    [Theory]
    [InlineData("Film.Sample.mkv", "*sample*", true)]
    [InlineData("Film.mkv", "*sample*", false)]
    [InlineData("Trailer-1.mkv", "trailer*", true)]
    [InlineData("abc.mkv", "a?c.mkv", true)]
    [InlineData("abcd.mkv", "a?c.mkv", false)]
    public void Platzhalter(string name, string pattern, bool match) =>
        Assert.Equal(match, Scanner.WildcardMatch(name, pattern));

    [Fact]
    public void Muster_ohne_Stern_gelten_als_Teil_des_Namens() =>
        Assert.Equal(new[] { "*sample*", "*.extras.*" }, Scanner.ParsePatterns("sample; *.extras.*").ToArray());

    [Fact]
    public void Filter_und_Reihenfolge()
    {
        var root = NewDir();
        Make(root, "a/klein.mkv", 1000);
        Make(root, "a/gross.mkv", 50_000, DateTime.Now.AddDays(-30));
        Make(root, "b/mittel.mkv", 20_000, DateTime.Now.AddDays(-5));
        Make(root, "b/neu.mkv", 30_000, DateTime.Now);
        Make(root, "b/Film-sample.mkv", 30_000);
        Make(root, "out/fertig.mkv", 30_000);
        Make(root, "_Merkliste/x.mkv", 30_000);

        var f = new ScanFilter
        {
            Extensions = [".mkv"],
            ExcludePatterns = "*sample*",
            MinBytes = 10_000,
            SkipNewerThan = TimeSpan.FromMinutes(30),
            ExcludeDir = Path.Combine(root, "out"),
            Order = FileOrder.LargestFirst,
        };
        var r = Scanner.Find(root, f);
        Assert.Equal(new[] { "gross.mkv", "mittel.mkv" }, r.Files.Select(Path.GetFileName).ToArray());
        Assert.Equal((1, 1, 1), (r.Excluded, r.TooSmall, r.TooNew));
        Assert.Contains("zu klein", r.FilterText);

        var byDate = Scanner.Find(root, new ScanFilter { Extensions = [".mkv"], Order = FileOrder.NewestFirst, ExcludeDir = Path.Combine(root, "out") });
        Assert.Equal("neu.mkv", Path.GetFileName(byDate.Files[0]));
    }

    [Fact]
    public void Ordner_enthalten()
    {
        var root = NewDir();
        Assert.True(Scanner.IsInside(Path.Combine(root, "x", "y"), root));
        Assert.True(Scanner.IsInside(root, root));
        Assert.False(Scanner.IsInside(root + "2", root));
        Assert.False(Scanner.IsInside("", root));
    }

    [Fact]
    public void Zeitfenster_ueber_Mitternacht()
    {
        var w = TimeWindow.Parse("22:00", "7")!;
        var d = new DateTime(2026, 1, 1);
        Assert.True(w.IsOpen(d.AddHours(23)));
        Assert.True(w.IsOpen(d.AddHours(3)));
        Assert.False(w.IsOpen(d.AddHours(12)));
        Assert.Equal(d.AddHours(22), w.NextOpen(d.AddHours(12)));
        var day = TimeWindow.Parse("8.30", "17:00")!;
        Assert.True(day.IsOpen(d.AddHours(9)));
        Assert.False(day.IsOpen(d.AddHours(8)));
        Assert.Equal(d.AddDays(1).AddHours(8.5), day.NextOpen(d.AddHours(18)));
        Assert.Null(TimeWindow.Parse("25:00", "07:00"));
        Assert.True(TimeWindow.Parse("0", "0")!.IsOpen(d.AddHours(13)));
    }

    [Fact]
    public void Alte_Einstellungen_werden_umgestellt()
    {
        var dir = NewDir();
        var store = new SettingsStore(dir);
        var nv = Profile.Nvidia(); nv.Name = "Serien (NVIDIA)"; nv.Quality = 26;
        var amd = Profile.Amd(); amd.Name = "Serien (AMD)";
        var f2 = Profile.Amd(); f2.Name = "Filme (AMD)"; f2.InputDir = @"D:\Meine Filme";
        File.WriteAllText(store.SettingsFile, System.Text.Json.JsonSerializer.Serialize(
            new { FfmpegDir = "", Profiles = new[] { nv, amd, f2 }, SelectedProfile = "Serien (NVIDIA)", Theme = "Dark" }, JsonOptions.Default));

        var s = store.LoadSettings();
        Assert.Equal(new[] { "NVIDIA", "AMD", "Filme (AMD)", "Intel", "Prozessor" }, s.Profiles.Select(p => p.Name).ToArray());
        Assert.Equal("NVIDIA", s.SelectedProfile);
        Assert.Equal(26, s.FindProfile("NVIDIA")!.Quality);
        Assert.Equal(AppSettings.CurrentSchema, s.SchemaVersion);
        Assert.False(store.LoadSettings().Migrate());
    }

    [Fact]
    public void Alter_Datenordner_wird_uebernommen()
    {
        var root = NewDir();
        var old = Path.Combine(root, "FilmKonverter");
        Directory.CreateDirectory(Path.Combine(old, "logs"));
        File.WriteAllText(Path.Combine(old, "settings.json"), "{}");
        File.WriteAllText(Path.Combine(old, "logs", "2026-01-01.log"), "x");
        var neu = Path.Combine(root, "GimboKonverter");
        SettingsStore.MigrateFolder(old, neu);
        Assert.True(File.Exists(Path.Combine(neu, "settings.json")));
        Assert.True(File.Exists(Path.Combine(neu, "logs", "2026-01-01.log")));
        Assert.True(Directory.Exists(old));
    }

    [Fact]
    public void Profile_exportieren_und_importieren()
    {
        var file = Path.Combine(NewDir(), "profile.json");
        SettingsStore.ExportProfiles(file, [Profile.Nvidia(), Profile.Amd()]);
        var back = SettingsStore.ImportProfiles(file);
        Assert.Equal(new[] { "NVIDIA", "AMD" }, back.Select(p => p.Name).ToArray());
        Assert.Equal(EncoderKind.Amf, back[1].Encoder);
        File.WriteAllText(file, System.Text.Json.JsonSerializer.Serialize(Profile.Cpu(), JsonOptions.Default));
        Assert.Equal("Prozessor", Assert.Single(SettingsStore.ImportProfiles(file)).Name);
    }

    [Fact]
    public void Freier_Speicher_ist_bekannt() => Assert.True(Native.FreeBytes(Path.GetTempPath()) > 0);
}
