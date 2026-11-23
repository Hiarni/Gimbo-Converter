using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class AnalysisTests(TestMedia m)
{
    [Fact]
    public async Task Probe_liest_Video_und_Ton()
    {
        var info = (await m.Tools.ProbeAsync(m.Sdr8))!;
        Assert.Equal("h264", info.Video!.CodecName);
        Assert.Equal(640, info.Video.Width);
        Assert.Equal(360, info.Video.Height);
        Assert.Equal("yuv420p", info.Video.PixFmt);
        Assert.Equal(24, info.Fps, 3);
        Assert.InRange(info.DurationSeconds, 3.9, 4.1);
        Assert.True(info.HasAudio);
        Assert.Equal(HdrKind.None, info.Hdr);
    }

    [Fact]
    public async Task HDR_wird_erkannt_und_Werte_haben_keine_Kommas()
    {
        var pq = (await m.Tools.ProbeAsync(m.Hdr10))!;
        Assert.Equal(HdrKind.Pq, pq.Hdr);
        Assert.Equal("hevc", pq.Video!.CodecName);
        Assert.Equal("yuv420p10le", pq.Video.PixFmt);
        Assert.Equal(640, pq.Video.Width);

        var hlg = (await m.Tools.ProbeAsync(m.Hlg264))!;
        Assert.Equal(HdrKind.Hlg, hlg.Hdr);
        Assert.Equal("h264", hlg.Video!.CodecName);
    }

    [Fact]
    public async Task Video_Datenrate_aus_BPS_Tag()
    {
        var info = (await m.Tools.ProbeAsync(m.Tagged))!;
        var (kbit, source) = info.VideoRate;
        Assert.Equal("Tag", source);
        Assert.Equal(3000, kbit!.Value, 0);
        Assert.True(info.TotalRateKbit > 3500);
    }

    [Fact]
    public async Task Verlustfreier_Ton_wird_nach_EAC3_geplant_5_1_bleibt()
    {
        var info = (await m.Tools.ProbeAsync(m.AudioMix))!;
        var p = TestProfiles.NvidiaFilm();
        var plan = AudioPlanner.Plan(info, p);
        Assert.Equal(4, plan.Tracks.Count);
        Assert.True(plan.Tracks[0].Convert);
        Assert.Equal(6, plan.Tracks[0].OutChannels);
        Assert.Equal(640, plan.Tracks[0].BitrateKbit);
        Assert.False(plan.Tracks[1].Convert);
        Assert.True(plan.Tracks[2].Convert);
        Assert.Equal(2, plan.Tracks[2].OutChannels);
        Assert.Equal(256, plan.Tracks[2].BitrateKbit);
        Assert.True(plan.Tracks[3].Convert);
        Assert.Equal(6, plan.Tracks[3].OutChannels);
        Assert.Equal(
            new[] {"-c:a:0", "eac3", "-b:a:0", "640k", "-ar:a:0", "48000", "-ac:a:0", "6",
             "-c:a:2", "eac3", "-b:a:2", "256k", "-ar:a:2", "48000",
             "-c:a:3", "eac3", "-b:a:3", "640k", "-ar:a:3", "48000"},
            plan.Args().ToArray());

        p.ConvertLosslessAudio = false;
        Assert.Equal(0, AudioPlanner.Plan(info, p).LosslessConverted);
    }

    [Fact]
    public async Task Ton_Umwandlung_ergibt_EAC3_mit_richtigen_Kanaelen()
    {
        var info = (await m.Tools.ProbeAsync(m.AudioMix))!;
        var plan = AudioPlanner.Plan(info, TestProfiles.NvidiaFilm());
        var outFile = Path.Combine(m.Dir, "audio-out.mkv");
        await m.Ff(["-i", m.AudioMix, "-map", "0:v:0", "-map", "0:a?", "-c:v", "copy", "-c:a", "copy", .. plan.Args(), outFile]);
        var o = (await m.Tools.ProbeAsync(outFile))!;
        var a = o.Audio.ToList();
        Assert.Equal(("eac3", 6), (a[0].CodecName, a[0].Channels));
        Assert.Equal(("ac3", 6), (a[1].CodecName, a[1].Channels));
        Assert.Equal(("eac3", 2), (a[2].CodecName, a[2].Channels));
        Assert.Equal(("eac3", 6), (a[3].CodecName, a[3].Channels));
    }

    [Fact]
    public async Task Planner_Ueberspringen_und_Skalierung()
    {
        var hevc = (await m.Tools.ProbeAsync(m.Hevc8))!;
        Assert.Equal(SkipReason.Hevc, Planner.Decide(hevc, Profile.Amd()).Skip);
        Assert.True(Planner.Decide(hevc, Profile.Nvidia()).Encode);

        var hdr = (await m.Tools.ProbeAsync(m.Hdr10))!;
        var amd = Profile.Amd();
        amd.SkipHevc = false;
        Assert.Equal(SkipReason.Hdr, Planner.Decide(hdr, amd).Skip);
        var nv = Planner.Decide(hdr, Profile.Nvidia());
        Assert.True(nv.Encode);
        Assert.Equal(10, nv.Plan!.BitDepth);
        Assert.Equal("p010le", nv.Plan.PixFmtOut);

        var hlg = (await m.Tools.ProbeAsync(m.Hlg264))!;
        Assert.Equal(SkipReason.Hdr, Planner.Decide(hlg, Profile.Amd()).Skip);

        var uhd = (await m.Tools.ProbeAsync(m.Uhd))!;
        var d = Planner.Decide(uhd, Profile.Nvidia());
        Assert.Equal("scale=1920:1080:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos", d.Plan!.VideoFilter);
        var da = Planner.Decide(uhd, Profile.Amd());
        Assert.EndsWith(",format=nv12", da.Plan!.VideoFilter);

        var sdr = (await m.Tools.ProbeAsync(m.Sdr8))!;
        Assert.Null(Planner.Decide(sdr, Profile.Nvidia()).Plan!.VideoFilter);
        Assert.Equal("format=nv12", Planner.Decide(sdr, Profile.Amd()).Plan!.VideoFilter);
    }

    [Fact]
    public async Task Planner_Mindest_Datenrate_Video_oder_verlustfreier_Ton()
    {
        var tagged = (await m.Tools.ProbeAsync(m.Tagged))!;
        var p = TestProfiles.NvidiaFilm();
        p.MinRateMbit = 8;
        Assert.True(Planner.Decide(tagged, p).Encode);
        p.ConvertLosslessAudio = false;
        Assert.Equal(SkipReason.RateTooLow, Planner.Decide(tagged, p).Skip);
        p.MinRateMbit = 2;
        Assert.True(Planner.Decide(tagged, p).Encode);
    }
}
