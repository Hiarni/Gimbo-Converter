using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;
using Xunit;

namespace FilmKonverter.Core.Tests;

internal sealed class FakeHandler(Dictionary<string, byte[]> files) : HttpMessageHandler
{
    public List<string> Requested { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var url = request.RequestUri!.ToString();
        Requested.Add(url);
        return Task.FromResult(files.TryGetValue(url, out var body)
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body) }
            : new HttpResponseMessage(HttpStatusCode.NotFound));
    }
}

public class FfmpegUpdateTests
{
    private static string Dir() { var d = Path.Combine(Path.GetTempPath(), "gimbo-upd-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    private static string Exe(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

    private static byte[] Package(string version)
    {
        var bin = Path.GetDirectoryName(new FfmpegTools_Locator().Ffmpeg)!;
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry($"ffmpeg-{version}-essentials_build/README.txt").Open().Dispose();
            zip.CreateEntryFromFile(Path.Combine(bin, Exe("ffmpeg")), $"ffmpeg-{version}-essentials_build/bin/{Exe("ffmpeg")}");
            zip.CreateEntryFromFile(Path.Combine(bin, Exe("ffprobe")), $"ffmpeg-{version}-essentials_build/bin/{Exe("ffprobe")}");
        }
        return ms.ToArray();
    }

    private static (HttpClient Http, FakeHandler Handler) Server(byte[] zip, string? sha = null, string version = "9.9.9")
    {
        var h = new FakeHandler(new Dictionary<string, byte[]>
        {
            [FfmpegUpdate.VersionUrl.ToString()] = System.Text.Encoding.ASCII.GetBytes(version + "\n"),
            [FfmpegUpdate.ZipUrl.ToString()] = zip,
            [FfmpegUpdate.ShaUrl.ToString()] = System.Text.Encoding.ASCII.GetBytes((sha ?? Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()) + "  ffmpeg-release-essentials.zip\n"),
        });
        return (new HttpClient(h), h);
    }

    [Theory]
    [InlineData("9.0.2", "9.0.2")]
    [InlineData("  8.1\r\n", "8.1")]
    [InlineData("<html>Fehler</html>", null)]
    [InlineData("", null)]
    public void Version_der_Quelle_wird_gelesen(string text, string? expected) =>
        Assert.Equal(expected, FfmpegUpdate.ParseRelease(text)?.Version);

    [Theory]
    [InlineData("9.1.0", "9.0.2-essentials_build", true)]
    [InlineData("9.0.2", "9.0.2-essentials_build", false)]
    [InlineData("8.1", "9.0.2-essentials_build", false)]
    [InlineData("9.1.0", null, true)]
    public void Nur_eine_neuere_Fassung_gilt_als_Update(string offered, string? current, bool newer) =>
        Assert.Equal(newer, FfmpegUpdate.IsNewer(FfmpegUpdate.ParseRelease(offered)!, current));

    [Fact]
    public async Task Update_wird_geladen_geprueft_und_entpackt()
    {
        var reference = (await FfmpegTools.TryLoadAsync(Path.GetDirectoryName(new FfmpegTools_Locator().Ffmpeg)!))!;
        var (http, _) = Server(Package("9.9.9"));
        var release = (await FfmpegUpdate.CheckAsync(http))!;
        Assert.Equal("9.9.9", release.Version);
        var root = Dir();
        var progress = new List<double>();
        var folder = await FfmpegUpdate.InstallAsync(http, release, root, reference, new SyncProgress<double>(progress.Add));
        Assert.Equal(Path.Combine(root, "9.9.9"), folder);
        Assert.NotNull(await FfmpegTools.TryLoadAsync(folder));
        Assert.Equal("9.9.9", File.ReadAllText(Path.Combine(folder, "version.txt")));
        Assert.Equal(1, progress[^1]);
        Assert.Equal([folder], Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Falsche_Pruefsumme_aendert_nichts()
    {
        var (http, _) = Server(Package("9.9.9"), sha: new string('0', 64));
        var root = Dir();
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => FfmpegUpdate.InstallAsync(http, FfmpegUpdate.ParseRelease("9.9.9")!, root, null));
        Assert.Contains("Prüfsumme", ex.Message);
        Assert.Empty(Directory.GetFileSystemEntries(root));
    }

    [Fact]
    public async Task Fehlt_der_neuen_Fassung_etwas_bleibt_die_bisherige()
    {
        var dir = Path.GetDirectoryName(new FfmpegTools_Locator().Ffmpeg)!;
        var reference = (await FfmpegTools.TryLoadAsync(dir))!;
        var candidate = (await FfmpegTools.TryLoadAsync(dir))!;
        Assert.Empty(await FfmpegUpdate.MissingAsync(candidate, reference));

        reference.Encoders.Add("libx264");
        reference.Bsfs.Add("hevc_metadata");
        candidate.Encoders.Remove("libx264");
        candidate.Bsfs.Remove("hevc_metadata");
        var missing = await FfmpegUpdate.MissingAsync(candidate, reference);
        Assert.Contains("libx264", missing);
        Assert.Contains("hevc_metadata", missing);

        reference.Encoders.Remove("libx264");
        reference.Bsfs.Remove("hevc_metadata");
        Assert.Empty(await FfmpegUpdate.MissingAsync(candidate, reference));
    }

    [Fact]
    public void Aufraeumen_behaelt_aktive_und_vorherige()
    {
        var root = Dir();
        foreach (var v in new[] { "8.0", "8.1", "9.0" }) Directory.CreateDirectory(Path.Combine(root, v));
        FfmpegUpdate.Prune(root, Path.Combine(root, "9.0"), Path.Combine(root, "8.1"), null);
        Assert.Equal(["8.1", "9.0"], Directory.GetDirectories(root).Select(Path.GetFileName).OrderBy(x => x).ToArray());
    }

    [Fact]
    public async Task Echte_Quelle_liefert_ein_brauchbares_Paket()
    {
        if (Environment.GetEnvironmentVariable("GIMBO_NET_TESTS") != "1" || !OperatingSystem.IsWindows()) return;
        using var http = FfmpegUpdate.CreateClient();
        var release = await FfmpegUpdate.CheckAsync(http);
        Assert.NotNull(release);
        Assert.Equal("GitHub", release!.Source);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var folder = await FfmpegUpdate.InstallAsync(http, release!, Dir(), null);
        Console.WriteLine($"ffmpeg {release.Version} von {release.Source} in {sw.Elapsed.TotalSeconds:0.0} s");
        var tools = (await FfmpegTools.TryLoadAsync(folder))!;
        Assert.Contains("hevc_nvenc", tools.Encoders);
        Assert.Contains("hevc_amf", tools.Encoders);
        Assert.Contains("dovi_rpu", tools.Bsfs);
    }
}

internal sealed class FfmpegTools_Locator
{
    public string Ffmpeg { get; } = Path.Combine(Environment.GetEnvironmentVariable("FFMPEG_DIR") is { Length: > 0 } d ? d : "/usr/bin", OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
}

public class AppUpdateTests
{
    private static string Dir() { var d = Path.Combine(Path.GetTempPath(), "gimbo-app-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    private static byte[] FakeExe(byte fill)
    {
        var b = new byte[1_200_000];
        Array.Fill(b, fill);
        b[0] = (byte)'M'; b[1] = (byte)'Z';
        return b;
    }

    private static byte[] Zip(byte[] exe)
    {
        using var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        using (var s = zip.CreateEntry(AppUpdate.ExeName).Open()) s.Write(exe);
        return ms.ToArray();
    }

    [Theory]
    [InlineData("v2.1.1", "2.1.1")]
    [InlineData("v2.2", "2.2.0")]
    [InlineData("Gimbo-Konverter-v2.10.3", "2.10.3")]
    [InlineData("2.1.1.0", "2.1.1")]
    [InlineData("keine", null)]
    public void Version_wird_erkannt(string text, string? expected) => Assert.Equal(expected, AppUpdate.ParseVersion(text)?.ToString(3));

    [Fact]
    public void GitHub_Antwort_wird_gelesen()
    {
        const string json = """
            {"tag_name":"v2.2.0","draft":false,"prerelease":false,
             "assets":[{"name":"Gimbo-Konverter-v2.2.0.zip","digest":"sha256:ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef0123456789",
                        "browser_download_url":"https://github.com/Hiarni/Gimbo-Konverter/releases/download/v2.2.0/Gimbo-Konverter-v2.2.0.zip"}]}
            """;
        var r = AppUpdate.ParseGitHub(json)!;
        Assert.Equal("2.2.0", r.VersionText);
        Assert.Equal("abcdef0123456789abcdef0123456789abcdef0123456789abcdef0123456789", r.Sha256);
        Assert.EndsWith("Gimbo-Konverter-v2.2.0.zip", r.ZipUrl!.ToString());
        Assert.Null(AppUpdate.ParseGitHub(json.Replace("\"prerelease\":false", "\"prerelease\":true")));
        Assert.Null(AppUpdate.ParseGitHub(json.Replace("https://", "http://")));
    }

    [Fact]
    public async Task Privates_oder_fehlendes_Release_gibt_eine_verstaendliche_Meldung()
    {
        using var http = new HttpClient(new FakeHandler([]));
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => AppUpdate.CheckGitHubAsync(http));
        Assert.Contains("GitHub", ex.Message);
    }

    [Fact]
    public void Netzwerkordner_neueste_ZIP_zaehlt()
    {
        var dir = Dir();
        File.WriteAllBytes(Path.Combine(dir, "Gimbo-Konverter-v2.1.1.zip"), Zip(FakeExe(1)));
        File.WriteAllBytes(Path.Combine(dir, "Gimbo-Konverter-v2.3.0.zip"), Zip(FakeExe(3)));
        File.WriteAllBytes(Path.Combine(dir, "Gimbo-Konverter-v2.2.0.zip"), Zip(FakeExe(2)));
        File.WriteAllText(Path.Combine(dir, "Notizen.zip"), "x");
        var r = AppUpdate.CheckFolder(dir)!;
        Assert.Equal("2.3.0", r.VersionText);
        Assert.Null(AppUpdate.CheckFolder(Path.Combine(dir, "gibt-es-nicht")));
        Assert.Equal("2.3.0", AppUpdate.NewerThan(new Version(2, 1, 1), r, null)!.VersionText);
        Assert.Null(AppUpdate.NewerThan(new Version(2, 3, 0), r));
    }

    [Fact]
    public async Task Update_aus_dem_Ordner_wird_geprueft_und_getauscht()
    {
        var app = Dir();
        var exe = Path.Combine(app, AppUpdate.ExeName);
        File.WriteAllBytes(exe, FakeExe(1));
        var share = Dir();
        File.WriteAllBytes(Path.Combine(share, "Gimbo-Konverter-v2.2.0.zip"), Zip(FakeExe(2)));

        var newExe = await AppUpdate.PrepareAsync(null, AppUpdate.CheckFolder(share)!, exe);
        Assert.Equal(1, File.ReadAllBytes(exe)[5]);
        AppUpdate.Swap(exe, newExe);
        Assert.Equal(2, File.ReadAllBytes(exe)[5]);
        Assert.True(File.Exists(exe + ".old"));
        AppUpdate.CleanupOld(exe);
        Assert.Equal([AppUpdate.ExeName], Directory.GetFiles(app).Select(Path.GetFileName).ToArray());
    }

    [Fact]
    public async Task Download_mit_falscher_Pruefsumme_oder_kaputter_Datei_aendert_nichts()
    {
        var app = Dir();
        var exe = Path.Combine(app, AppUpdate.ExeName);
        File.WriteAllBytes(exe, FakeExe(1));
        var zip = Zip(FakeExe(2));
        var url = new Uri("https://example.invalid/Gimbo-Konverter-v2.2.0.zip");
        using var http = new HttpClient(new FakeHandler(new() { [url.ToString()] = zip }));

        var wrong = new AppRelease(new Version(2, 2, 0), "GitHub", ZipUrl: url, Sha256: new string('0', 64));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppUpdate.PrepareAsync(http, wrong, exe));

        var broken = Path.Combine(Dir(), "Gimbo-Konverter-v2.2.0.zip");
        File.WriteAllBytes(broken, Zip(new byte[5000]));
        await Assert.ThrowsAsync<InvalidOperationException>(() => AppUpdate.PrepareAsync(null, new AppRelease(new Version(2, 2, 0), "x", LocalFile: broken), exe));

        Assert.Equal([AppUpdate.ExeName], Directory.GetFiles(app).Select(Path.GetFileName).ToArray());
        Assert.Equal(1, File.ReadAllBytes(exe)[5]);

        var right = wrong with { Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant() };
        var newExe = await AppUpdate.PrepareAsync(http, right, exe);
        Assert.Equal(2, File.ReadAllBytes(newExe)[5]);
    }

    [Fact]
    public void Scheitert_der_Tausch_bleibt_die_alte_EXE_am_Platz()
    {
        var app = Dir();
        var exe = Path.Combine(app, AppUpdate.ExeName);
        File.WriteAllBytes(exe, FakeExe(1));
        Assert.ThrowsAny<IOException>(() => AppUpdate.Swap(exe, Path.Combine(app, "fehlt.new")));
        Assert.Equal(1, File.ReadAllBytes(exe)[5]);
        Assert.False(File.Exists(exe + ".old"));
    }
}
