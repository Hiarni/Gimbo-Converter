using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

public sealed class TestMedia : IAsyncLifetime
{
    public FfmpegTools Tools { get; private set; } = null!;
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "fk-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public string Sdr8 => Path.Combine(Dir, "sdr8.mkv");
    public string Hdr10 => Path.Combine(Dir, "hdr10.mkv");
    public string Hlg264 => Path.Combine(Dir, "hlg264.mkv");
    public string Hevc8 => Path.Combine(Dir, "hevc8.mkv");
    public string AudioMix => Path.Combine(Dir, "audiomix.mkv");
    public string Tagged => Path.Combine(Dir, "tagged.mkv");
    public string Long720 => Path.Combine(Dir, "long720.mkv");
    public string Uhd => Path.Combine(Dir, "uhd.mkv");
    public string Multi => Path.Combine(Dir, "multi.mkv");
    public string Interlaced50 => Path.Combine(Dir, "interlaced50.mkv");
    public string Mp4Text => Path.Combine(Dir, "text.mp4");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Dir);
        var folder = Environment.GetEnvironmentVariable("FFMPEG_DIR");
        if (string.IsNullOrEmpty(folder)) folder = FfmpegTools.FindCandidateFolders().FirstOrDefault();
        Tools = await FfmpegTools.TryLoadAsync(folder ?? "") ?? throw new InvalidOperationException("ffmpeg nicht gefunden (FFMPEG_DIR setzen).");

        var src = new[] { "-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000" };
        await Ff([.. src, "-t", "4", "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "aac", "-ac", "2", Sdr8]);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-t", "2", "-vf", "setparams=color_primaries=bt2020:color_trc=smpte2084:colorspace=bt2020nc",
            "-pix_fmt", "yuv420p10le", "-c:v", "libx265", "-preset", "ultrafast",
            "-x265-params", "log-level=error:hdr10=1:master-display=G(13250,34500)B(7500,3000)R(34000,16000)WP(15635,16450)L(10000000,1):max-cll=1000,400",
            "-color_primaries", "bt2020", "-color_trc", "smpte2084", "-colorspace", "bt2020nc", Hdr10]);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-t", "1", "-vf", "setparams=color_primaries=bt2020:color_trc=arib-std-b67:colorspace=bt2020nc",
            "-pix_fmt", "yuv420p10le", "-c:v", "libx264", "-preset", "ultrafast",
            "-color_primaries", "bt2020", "-color_trc", "arib-std-b67", "-colorspace", "bt2020nc", Hlg264]);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-t", "2", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", Hevc8]);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24",
            "-f", "lavfi", "-i", "sine=440:sample_rate=96000", "-f", "lavfi", "-i", "sine=550:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=660:sample_rate=48000", "-f", "lavfi", "-i", "sine=770:sample_rate=48000", "-t", "3",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a:0", "flac", "-ac:a:0", "8", "-c:a:1", "ac3", "-ac:a:1", "6", "-b:a:1", "448k", "-c:a:2", "pcm_s16le", "-ac:a:2", "2",
            "-c:a:3", "truehd", "-strict", "-2", "-ac:a:3", "6", AudioMix]);
        await Ff([.. src, "-t", "4", "-c:v", "libx264", "-b:v", "3M", "-c:a", "pcm_s16le", "-ac", "2",
            "-metadata:s:v:0", "BPS=3000000", "-metadata:s:a:0", "BPS=1536000", Tagged]);
        await Ff([.. src, "-t", "24", "-c:v", "libx264", "-preset", "ultrafast", "-crf", "16", "-c:a", "aac", "-ac", "2", Long720.Replace("long720", "long720tmp")]);
        File.Move(Long720.Replace("long720", "long720tmp"), Long720);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=2560x1440:rate=24", "-t", "1", "-c:v", "libx264", "-preset", "ultrafast", Uhd]);

        var srt = Path.Combine(Dir, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nHallo\n\n");
        var font = Path.Combine(Dir, "font.ttf");
        File.WriteAllBytes(font, new byte[2048]);
        string[] sine = ["-f", "lavfi", "-i", "sine=440:sample_rate=48000"];
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", .. sine, .. sine, .. sine, .. sine, "-i", srt, "-i", srt, "-i", srt,
            "-t", "3", "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a", "-map", "5", "-map", "6", "-map", "7",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "ac3", "-ac", "2", "-c:a:1", "dca", "-strict", "-2", "-c:s", "srt",
            "-metadata:s:a:0", "language=ger", "-metadata:s:a:1", "language=eng",
            "-metadata:s:a:2", "language=ger", "-metadata:s:a:2", "title=Audiokommentar",
            "-metadata:s:a:3", "language=ger", "-metadata:s:a:3", "title=Hörfilm",
            "-disposition:a:0", "0", "-disposition:a:1", "default", "-disposition:a:2", "comment", "-disposition:a:3", "visual_impaired",
            "-metadata:s:s:0", "language=ger", "-metadata:s:s:0", "title=Forced", "-metadata:s:s:1", "language=ger", "-metadata:s:s:2", "language=eng",
            "-disposition:s:0", "0", "-disposition:s:1", "0", "-disposition:s:2", "0",
            "-attach", font, "-metadata:s:t", "mimetype=application/x-truetype-font", Multi]);
        await Ff(["-f", "lavfi", "-i", "testsrc2=size=720x576:rate=50", "-t", "2", "-c:v", "libx264", "-preset", "ultrafast",
            "-vf", "setfield=tff", "-flags", "+ilme+ildct", "-field_order", "tt", Interlaced50]);
        await Ff([.. src, "-i", srt, "-t", "3", "-map", "0:v", "-map", "1:a", "-map", "2", "-c:v", "libx264", "-preset", "ultrafast",
            "-c:a", "aac", "-c:s", "mov_text", "-metadata:s:s:0", "language=ger", Mp4Text]);
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

[CollectionDefinition("media")]
public sealed class MediaCollection : ICollectionFixture<TestMedia>;
