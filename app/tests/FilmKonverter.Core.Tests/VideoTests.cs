using System.Runtime.CompilerServices;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

internal static class TestSetup
{
    [ModuleInitializer]
    internal static void Init() => L.Set(UiLanguage.German);
}

[Collection("media")]
public sealed class VideoTests(TestMedia m)
{
    [Fact]
    public async Task Interlaced_wird_erkannt_und_Bildrate_gesenkt()
    {
        var info = (await m.Tools.ProbeAsync(m.Interlaced50, CancellationToken.None))!;
        Assert.True(info.Video!.IsInterlaced, "field_order: " + info.Video.FieldOrder);
        var p = Profile.Nvidia();
        p.MaxFps = 25;
        var plan = Planner.Decide(info, p).Plan!;
        Assert.Equal(Planner.DeinterlaceFilter(false) + ",fps=25", plan.VideoFilter);
        Assert.Equal(25, plan.OutputFps);

        p.Deinterlace = DeinterlaceMode.Off;
        p.MaxFps = 0;
        Assert.Null(Planner.Decide(info, p).Plan!.VideoFilter);
    }

    [Fact]
    public async Task Progressive_Quelle_wird_nicht_deinterlaced()
    {
        var info = (await m.Tools.ProbeAsync(m.Sdr8, CancellationToken.None))!;
        Assert.False(info.Video!.IsInterlaced);
        Assert.Null(Planner.Decide(info, Profile.Nvidia()).Plan!.VideoFilter);
        var p = Profile.Nvidia();
        p.Deinterlace = DeinterlaceMode.Always;
        Assert.Equal(Planner.DeinterlaceFilter(true), Planner.Decide(info, p).Plan!.VideoFilter);
    }

    [Fact]
    public async Task Originalaufloesung_und_10_Bit()
    {
        var uhd = (await m.Tools.ProbeAsync(m.Uhd, CancellationToken.None))!;
        var p = Profile.Nvidia();
        p.MaxWidth = 0;
        p.MaxHeight = 0;
        p.TenBit = true;
        var plan = Planner.Decide(uhd, p).Plan!;
        Assert.Null(plan.VideoFilter);
        Assert.Equal((10, "p010le", "main10"), (plan.BitDepth, plan.PixFmtOut, plan.CodecProfile));

        var amd = Profile.Amd();
        amd.TenBit = true;
        Assert.Equal(8, Planner.Decide(uhd, amd).Plan!.BitDepth);
    }

    [Fact]
    public void Hardware_Dekodierung_vor_der_Eingabe()
    {
        var p = Profile.Nvidia();
        p.HardwareDecode = true;
        var plan = new EncodePlan(new Media.StreamInfo { Index = 0, CodecType = "video" }, null, 8, "yuv420p", "main", new AudioPlan([]), "", "");
        var a = ArgsBuilder.BuildEncode(p, plan, "IN", "OUT");
        Assert.Equal(new[] { "-hide_banner", "-y", "-hwaccel", "auto", "-i", "IN" }, a.Take(6).ToArray());
    }

    [Fact]
    public async Task Kodieren_mit_Deinterlacing_Bildrate_und_10_Bit()
    {
        var root = Path.Combine(m.Dir, "vid-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "in"));
        File.Copy(m.Interlaced50, Path.Combine(root, "in", "TV.mkv"));
        var p = Profile.Cpu();
        p.InputDir = Path.Combine(root, "in");
        p.OutputDir = Path.Combine(root, "out");
        p.CpuPreset = "ultrafast";
        p.Quality = 30;
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        p.MinFreeSpaceGb = 0;
        p.EarlyAbortPercent = 0;
        p.MaxFps = 25;
        p.TenBit = true;
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec).RunAsync(CancellationToken.None);
        var end = rec.EndOf("TV");
        Assert.True(end.Outcome == Outcome.Encoded, end.Message + " | " + string.Join(" / ", rec.Lines.Select(l => l.Text)));
        var o = (await m.Tools.ProbeAsync(Path.Combine(p.OutputDir, "TV.mkv"), CancellationToken.None))!;
        Assert.Equal("yuv420p10le", o.Video!.PixFmt);
        Assert.InRange(o.Fps, 24.5, 25.5);
        Assert.False(o.Video.IsInterlaced);
        Assert.Equal(1.0, rec.Progresses.Where(x => x.Final).Last().Fraction, 2);
    }
}
