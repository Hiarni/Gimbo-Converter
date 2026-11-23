using System.IO.Compression;
using FilmKonverter.Core.Media;

namespace FilmKonverter.Core.Tests;

public sealed class FfmpegLocatorTests
{
    [Theory]
    [InlineData("9.0.2-essentials_build-www.gyan.dev", 9, 0, 2)]
    [InlineData("7.0.2-Jellyfin", 7, 0, 2)]
    [InlineData("n7.1-27-gabcdef", 7, 1, 0)]
    [InlineData("6.1.1-3ubuntu5", 6, 1, 1)]
    public void Versionen_werden_gelesen(string text, int a, int b, int c) =>
        Assert.Equal(new Version(a, b, c), FfmpegLocator.ParseVersion(text));

    [Fact]
    public void Git_Builds_haben_keine_Version() => Assert.Null(FfmpegLocator.ParseVersion("N-117123-g1234abcd"));

    [Fact]
    public void Entpacken_einmalig_und_alte_Versionen_weg()
    {
        var root = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N")[..6]);
        byte[] MakeZip(string version, string content)
        {
            using var ms = new MemoryStream();
            using (var z = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
            {
                foreach (var (name, text) in new[] { ("version.txt", version), ("ffmpeg.exe", content), ("ffprobe.exe", content) })
                {
                    using var w = new StreamWriter(z.CreateEntry(name).Open());
                    w.Write(text);
                }
            }
            return ms.ToArray();
        }
        var v1 = MakeZip("8.0.1-test", "eins");
        var dir1 = BundledFfmpeg.EnsureExtracted(() => new MemoryStream(v1), root)!;
        Assert.Equal("eins", File.ReadAllText(Path.Combine(dir1, "ffmpeg.exe")));
        var stamp = File.GetLastWriteTimeUtc(Path.Combine(dir1, "ffmpeg.exe"));
        Thread.Sleep(20);
        Assert.Equal(dir1, BundledFfmpeg.EnsureExtracted(() => new MemoryStream(v1), root));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(Path.Combine(dir1, "ffmpeg.exe")));

        var v2 = MakeZip("9.0.2-test", "zwei");
        var dir2 = BundledFfmpeg.EnsureExtracted(() => new MemoryStream(v2), root)!;
        Assert.NotEqual(dir1, dir2);
        Assert.False(Directory.Exists(dir1));
        Assert.Null(BundledFfmpeg.EnsureExtracted(() => null, root));
    }

    [Fact]
    public async Task Neueste_Version_gewinnt_bei_Gleichstand_die_integrierte()
    {
        var folder = Path.GetDirectoryName(Environment.GetEnvironmentVariable("FFMPEG_DIR") is { Length: > 0 } d
            ? Path.Combine(d, "x") : FfmpegTools.FindCandidateFolders().FirstOrDefault() is { } f ? Path.Combine(f, "x") : null);
        if (folder is null) return;
        var t = (await FfmpegTools.TryLoadAsync(folder))!;
        var installed = new FfmpegCandidate(t, false);
        var bundled = new FfmpegCandidate(t, true);
        Assert.True(FfmpegLocator.PickNewest([installed, bundled])!.Bundled);
        Assert.Same(installed, FfmpegLocator.PickNewest([installed]));
    }
}
