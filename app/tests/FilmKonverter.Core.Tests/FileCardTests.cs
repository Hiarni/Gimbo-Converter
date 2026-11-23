using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;
using Xunit;

namespace FilmKonverter.Core.Tests;

public class FileCardTests
{

    [Theory]
    [InlineData("yuv420p", 8)]
    [InlineData("nv12", 8)]
    [InlineData("yuvj420p", 8)]
    [InlineData("yuv410p", 8)]
    [InlineData("yuv420p10le", 10)]
    [InlineData("p010le", 10)]
    [InlineData("yuv422p12le", 12)]
    [InlineData("p016le", 16)]
    [InlineData("", 8)]
    public void Bit_depth_from_pixel_format(string pix, int bits) => Assert.Equal(bits, FileCard.BitDepth(pix));

    [Fact]
    public void Readable_names_instead_of_ffmpeg_codes()
    {
        Assert.Equal("H.264", FileCard.Codec("h264"));
        Assert.Equal("MPEG-2", FileCard.Codec("mpeg2video"));
        Assert.Equal("PRORES", FileCard.Codec("prores"));
        Assert.Equal("1920×800 (1080p)", FileCard.Resolution(1920, 800));
        Assert.Equal("1920×1080 (1080i)", FileCard.Resolution(1920, 1080, interlaced: true));
        Assert.Equal("720×576 (SD)", FileCard.Resolution(720, 576, interlaced: true));
        Assert.Equal("10 Bit · HDR10 (PQ)", FileCard.Depth("yuv420p10le", HdrKind.Pq));
        Assert.Equal("8 Bit · 4:2:2", FileCard.Depth("yuv422p", HdrKind.None));
    }

    [Fact]
    public void Target_shows_scaling_as_arrow()
    {
        Assert.Equal("HEVC main10 · 10 Bit · NVENC CQ 27 · 3840×2160 → 1920×1080", FileCard.Target("main10", 10, "NVENC CQ 27", 3840, 2160, 1920, 1080));
        Assert.Equal("HEVC main · 8 Bit · AMF QP 22/24 · 1920×1080, Größe bleibt", FileCard.Target("main", 8, "AMF QP 22/24", 1920, 1080, 1920, 1080));
    }

    [Fact]
    public void Size_and_estimate_texts()
    {
        const long gb = 1024L * 1024 * 1024;
        var size = FileCard.Size(2 * gb, (long)(1.5 * gb));
        Assert.Contains("-25,0 %", size);
        Assert.Contains("512 MB gespart", size);
        Assert.DoesNotContain("gespart", FileCard.Size(gb, 2 * gb));
        Assert.Contains("vorher", FileCard.Size(2 * gb, gb, gb + 1));
        var est = FileCard.Estimate((long)(1.5 * gb), 2 * gb);
        Assert.StartsWith("~1,50 GB von 2,00 GB", est);
        Assert.EndsWith("-25 %", est);
        Assert.Equal("", FileCard.Estimate(0, gb));
    }

    [Fact]
    public void Source_line_from_probe()
    {
        var json = """
        {"streams":[{"index":0,"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1920,"height":1080,"avg_frame_rate":"24000/1001","r_frame_rate":"24000/1001"},
                    {"index":1,"codec_type":"audio","codec_name":"ac3","channels":6,"tags":{"language":"ger"}}],
         "format":{"duration":"2655.0","format_name":"matroska,webm"}}
        """;
        var m = MediaInfo.Parse("x.mkv", json, 1_836_000_000);
        var src = FileCard.Source(m, 1_836_000_000);
        Assert.StartsWith("H.264 · 1920×1080 (1080p) · 8 Bit · 23,976 fps · 00:44:15 · ", src);
        Assert.Contains("gesamt 5,5 Mbit/s", FileCard.Rate(m));
    }

    [Fact]
    public void Peer_disappears_an_hour_after_it_stopped()
    {
        var now = DateTime.UtcNow;
        var working = new PeerStatus { State = PeerState.Running, UpdatedUtc = now.AddSeconds(-20) };
        Assert.True(working.IsActive(now) && working.IsWorthShowing(now));
        var finished = new PeerStatus { State = PeerState.Idle, UpdatedUtc = now.AddMinutes(-30) };
        Assert.False(finished.IsActive(now));
        Assert.True(finished.IsWorthShowing(now));
        var old = new PeerStatus { State = PeerState.Idle, UpdatedUtc = now.AddHours(-20) };
        Assert.False(old.IsWorthShowing(now));
        var lost = new PeerStatus { State = PeerState.Running, UpdatedUtc = now.AddHours(-3) };
        Assert.False(lost.IsWorthShowing(now));
    }
}
