using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

internal sealed class RangeHandler(Dictionary<string, byte[]> files, bool ranges = true) : HttpMessageHandler
{
    public int Requests;
    public HashSet<long> BreakOnce { get; } = [];

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Interlocked.Increment(ref Requests);
        if (!files.TryGetValue(request.RequestUri!.ToString(), out var body))
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound) { RequestMessage = request });
        if (!ranges || request.Headers.Range?.Ranges.FirstOrDefault() is not { } r)
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(body), RequestMessage = request });
        var from = r.From ?? 0;
        var to = Math.Min(r.To ?? body.Length - 1, body.Length - 1);
        var len = (int)(to - from + 1);
        bool cut;
        lock (BreakOnce) cut = BreakOnce.Remove(from);
        var part = new byte[cut ? len / 2 : len];
        Array.Copy(body, from, part, 0, part.Length);
        var content = new ByteArrayContent(part);
        content.Headers.ContentRange = new ContentRangeHeaderValue(from, to, body.Length);
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent) { Content = content, RequestMessage = request });
    }
}

public class DownloadTests
{
    private static string Dir() { var d = Path.Combine(Path.GetTempPath(), "gimbo-dl-" + Guid.NewGuid().ToString("N")[..8]); Directory.CreateDirectory(d); return d; }

    private static byte[] Data(int size)
    {
        var b = new byte[size];
        new Random(7).NextBytes(b);
        return b;
    }

    [Fact]
    public async Task Grosse_Datei_wird_in_Teilen_geladen_und_ist_identisch()
    {
        var data = Data(30 * 1024 * 1024);
        var url = "https://example.invalid/paket.zip";
        var h = new RangeHandler(new() { [url] = data });
        h.BreakOnce.Add(0);
        using var http = new HttpClient(h);
        var file = Path.Combine(Dir(), "x.zip");
        var seen = new List<DownloadStatus>();
        await Download.DownloadAsync(http, new Uri(url), file, s => { lock (seen) seen.Add(s); });
        Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(file)));
        Assert.True(h.Requests >= 1 + 2 + 1, $"{h.Requests} Anfragen");
        Assert.Equal(data.Length, seen[^1].Done);
        Assert.Equal(1, seen[^1].Fraction);
    }

    [Fact]
    public async Task Ohne_Teilbereiche_oder_klein_eine_Verbindung()
    {
        var url = "https://example.invalid/a.zip";
        foreach (var (ranges, size) in new[] { (false, 20 * 1024 * 1024), (true, 100_000) })
        {
            var data = Data(size);
            var h = new RangeHandler(new() { [url] = data }, ranges);
            using var http = new HttpClient(h);
            var file = Path.Combine(Dir(), "a.zip");
            await Download.DownloadAsync(http, new Uri(url), file);
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(File.ReadAllBytes(file)));
            Assert.Equal(2, h.Requests);
        }
    }

    [Fact]
    public void Text_mit_Geschwindigkeit()
    {
        var s = new DownloadStatus(45L * 1024 * 1024, 98L * 1024 * 1024, 38.2 * 1024 * 1024);
        Assert.Equal("38,2 MB/s · 45 von 98 MB", s.Text);
    }

    private const string MirrorJson = """
        {"tag_name":"9.9.9","assets":[
          {"name":"ffmpeg-9.9.9-full_build.zip","browser_download_url":"https://github.com/GyanD/codexffmpeg/releases/download/9.9.9/ffmpeg-9.9.9-full_build.zip","digest":"sha256:1111111111111111111111111111111111111111111111111111111111111111"},
          {"name":"ffmpeg-9.9.9-essentials_build.zip","browser_download_url":"https://github.com/GyanD/codexffmpeg/releases/download/9.9.9/ffmpeg-9.9.9-essentials_build.zip","digest":"sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"}]}
        """;

    [Fact]
    public void Spiegel_wird_nur_mit_Pruefsumme_genommen()
    {
        var gyan = FfmpegUpdate.ParseRelease("9.9.9")!;
        var m = FfmpegUpdate.ParseMirror(MirrorJson, gyan)!;
        Assert.Equal("GitHub", m.Source);
        Assert.EndsWith("ffmpeg-9.9.9-essentials_build.zip", m.ZipUrl.ToString());
        Assert.Equal(new string('a', 64), m.Sha256);
        Assert.Null(FfmpegUpdate.ParseMirror(MirrorJson.Replace("\"digest\":\"sha256:AAAA", "\"x\":\"sha256:AAAA"), gyan));
        Assert.Null(FfmpegUpdate.ParseMirror("<html>", gyan));
    }

    [Fact]
    public async Task Liefert_der_Spiegel_nicht_kommt_es_von_gyan()
    {
        var zip = new byte[3000];
        new Random(3).NextBytes(zip);
        var h = new RangeHandler(new()
        {
            [FfmpegUpdate.VersionUrl.ToString()] = "9.9.9\n"u8.ToArray(),
            [$"https://api.github.com/repos/{FfmpegUpdate.MirrorRepo}/releases/tags/9.9.9"] = System.Text.Encoding.UTF8.GetBytes(MirrorJson),
            [FfmpegUpdate.ZipUrl.ToString()] = zip,
            [FfmpegUpdate.ShaUrl.ToString()] = System.Text.Encoding.ASCII.GetBytes(Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()),
        });
        using var http = new HttpClient(h);
        var release = (await FfmpegUpdate.CheckAsync(http))!;
        Assert.Equal("GitHub", release.Source);
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => FfmpegUpdate.InstallAsync(http, release, Dir(), null));
        Assert.DoesNotContain("Prüfsumme", ex.Message);
    }
}
