using FilmKonverter.Core.Converting;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Tests;

[Collection("media")]
public sealed class ConvertTests(TestMedia m)
{
    private static ConvertCaps? _caps;
    private async Task<ConvertCaps> Caps() => _caps ??= await ConvertCaps.DetectAsync(m.Tools);

    private string NewDir()
    {
        var d = Path.Combine(m.Dir, "cv-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(d);
        return d;
    }

    private async Task<(ConvertResult R, MediaInfo? O)> Convert(string src, ConvertTarget t, string? outDir = null, string suffix = "")
    {
        var conv = new FileConverter(m.Tools, await Caps());
        var r = await conv.ConvertAsync(new ConvertJob(1, src), new ConvertOptions { Target = t, OutputDir = outDir ?? NewDir(), Suffix = suffix }, null, CancellationToken.None);
        var o = r.OutputPath is not null && File.Exists(r.OutputPath) ? await m.Tools.ProbeAsync(r.OutputPath) : null;
        return (r, o);
    }

    [Fact]
    public async Task Caps_FindWorkingEncoders()
    {
        var c = await Caps();
        Assert.NotNull(c.H264);
        Assert.NotNull(c.Hevc);
        Assert.True(c.Available(ConvertTarget.RemuxMkv));
        Assert.True(c.Available(ConvertTarget.AudioFlac));
    }

    [Fact]
    public async Task RemuxMp4_ConvertsDtsKeepsAc3_TextSubsToMovText_DropsFonts()
    {
        var info = (await m.Tools.ProbeAsync(m.Multi))!;
        var plan = ConvertPlanner.Plan(info, ConvertTarget.RemuxMp4, await Caps(), m.Multi);
        Assert.True(plan.Ok);
        Assert.Contains(plan.Warnings, w => w.Contains("Anhänge") || w.Contains("attachments"));
        Assert.Contains(plan.Changes, c => c.Contains("DTS → AAC"));

        var (r, o) = await Convert(m.Multi, ConvertTarget.RemuxMp4);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.EndsWith(".mp4", r.OutputPath);
        Assert.Equal("h264", o!.Video!.CodecName);
        var audio = o.Audio.ToList();
        Assert.Equal(4, audio.Count);
        Assert.Equal("ac3", audio[0].CodecName);
        Assert.Equal("aac", audio[1].CodecName);
        Assert.Equal(3, o.Subtitles.Count());
        Assert.All(o.Subtitles, s => Assert.Equal("mov_text", s.CodecName));
        Assert.Empty(o.Attachments);
        Assert.Equal("ger", audio[0].Language);
    }

    [Fact]
    public async Task RemuxMkv_FromMp4_MovTextBecomesSrt()
    {
        var (r, o) = await Convert(m.Mp4Text, ConvertTarget.RemuxMkv);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("subrip", o!.Subtitles.Single().CodecName);
        Assert.Equal("aac", o.Audio.Single().CodecName);
    }

    [Fact]
    public async Task RemuxMkv_FromAvi_Works()
    {
        var avi = Path.Combine(NewDir(), "alt.avi");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=25", "-f", "lavfi", "-i", "sine=440:sample_rate=44100", "-t", "3",
            "-c:v", "mpeg4", "-q:v", "5", "-bf", "2", "-c:a", "libmp3lame", "-b:a", "128k", avi]);
        var (r, o) = await Convert(avi, ConvertTarget.RemuxMkv);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("mpeg4", o!.Video!.CodecName);
        var (r2, o2) = await Convert(avi, ConvertTarget.RemuxMp4);
        Assert.Equal(Outcome.Encoded, r2.Outcome);
        Assert.Equal("mp3", o2!.Audio.Single().CodecName);
    }

    [Fact]
    public async Task RemuxMp4_RefusesUnsupportedVideo()
    {
        var info = new MediaInfo
        {
            Path = "x.wmv", DurationSeconds = 10,
            Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "wmv3", Width = 720, Height = 480 }],
        };
        var plan = ConvertPlanner.Plan(info, ConvertTarget.RemuxMp4, await Caps(), "x.wmv");
        Assert.False(plan.Ok);
        Assert.True(ConvertPlanner.Plan(info, ConvertTarget.RemuxMkv, await Caps(), "x.wmv").Ok);
        Assert.True(ConvertPlanner.Plan(info, ConvertTarget.Compatible, await Caps(), "x.wmv").Ok);
    }

    [Fact]
    public async Task Compatible_Hdr10_BecomesSdrH264()
    {
        var caps = await Caps();
        var (r, o) = await Convert(m.Hdr10, ConvertTarget.Compatible);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("h264", o!.Video!.CodecName);
        Assert.Equal("yuv420p", o.Video.PixFmt);
        if (caps.Tonemap) Assert.Equal(HdrKind.None, o.Hdr);
    }

    [Fact]
    public async Task Compatible_H264Source_IsOnlyRemuxed()
    {
        var caps = await Caps();
        var info = (await m.Tools.ProbeAsync(m.Sdr8))!;
        Assert.True(ConvertPlanner.IsCompatibleH264(info));
        var plan = ConvertPlanner.Plan(info, ConvertTarget.Compatible, caps, m.Sdr8);
        Assert.DoesNotContain(caps.H264!, plan.Output);
        Assert.False(ConvertPlanner.IsCompatibleH264((await m.Tools.ProbeAsync(m.Uhd))!));
        Assert.False(ConvertPlanner.IsCompatibleH264((await m.Tools.ProbeAsync(m.Interlaced50))!));
        Assert.False(ConvertPlanner.IsCompatibleH264((await m.Tools.ProbeAsync(m.Hlg264))!));
        var (r, o) = await Convert(m.Sdr8, ConvertTarget.Compatible);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("h264", o!.Video!.CodecName);
    }

    [Fact]
    public async Task Compatible_ScalesDownLargeVideo()
    {
        var (r, o) = await Convert(m.Uhd, ConvertTarget.Compatible);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal(1920, o!.Video!.Width);
        Assert.Equal(1080, o.Video.Height);
    }

    [Fact]
    public async Task Mobile_OneStereoAacTrack()
    {
        var (r, o) = await Convert(m.AudioMix, ConvertTarget.Mobile);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        var a = o!.Audio.Single();
        Assert.Equal("aac", a.CodecName);
        Assert.Equal(2, a.Channels);
        Assert.Equal(360, o.Video!.Height);
    }

    [Fact]
    public async Task Compatible_ConvertsAllAudioToAac_MaxSixChannels()
    {
        var (r, o) = await Convert(m.AudioMix, ConvertTarget.Compatible);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        var audio = o!.Audio.ToList();
        Assert.Equal(4, audio.Count);
        Assert.All(audio, a => Assert.Equal("aac", a.CodecName));
        Assert.Equal(6, audio[0].Channels);
    }

    [Fact]
    public async Task Apple_HevcIsOnlyRemuxed_H264IsEncoded()
    {
        var caps = await Caps();
        var info = (await m.Tools.ProbeAsync(m.Hevc8))!;
        var plan = ConvertPlanner.Plan(info, ConvertTarget.Apple, caps, m.Hevc8);
        Assert.DoesNotContain(caps.Hevc!, plan.Output);
        Assert.Contains("hvc1", plan.Output);
        var (r, o) = await Convert(m.Hevc8, ConvertTarget.Apple);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("hevc", o!.Video!.CodecName);

        var (r2, o2) = await Convert(m.Sdr8, ConvertTarget.Apple);
        Assert.Equal(Outcome.Encoded, r2.Outcome);
        Assert.Equal("hevc", o2!.Video!.CodecName);
        Assert.Equal("aac", o2.Audio.Single().CodecName);
    }

    [Fact]
    public async Task Av1_KeepsAllTracksInMkv()
    {
        var caps = await Caps();
        if (caps.Av1 is null) return;
        var (r, o) = await Convert(m.Multi, ConvertTarget.Av1);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("av1", o!.Video!.CodecName);
        Assert.Equal(4, o.Audio.Count());
        Assert.Equal(3, o.Subtitles.Count());
        Assert.Single(o.Attachments);
    }

    [Fact]
    public async Task Interlaced_IsDeinterlaced()
    {
        var info = (await m.Tools.ProbeAsync(m.Interlaced50))!;
        var plan = ConvertPlanner.Plan(info, ConvertTarget.Compatible, await Caps(), m.Interlaced50);
        Assert.Contains(plan.Output, a => a.Contains("bwdif"));
        var remux = ConvertPlanner.Plan(info, ConvertTarget.RemuxMkv, await Caps(), m.Interlaced50);
        Assert.DoesNotContain(remux.Output, a => a.Contains("bwdif"));
    }

    [Fact]
    public async Task AudioOnly_Mp3_M4a_Flac()
    {
        var caps = await Caps();
        if (caps.Mp3)
        {
            var (r, o) = await Convert(m.Multi, ConvertTarget.AudioMp3);
            Assert.Equal(Outcome.Encoded, r.Outcome);
            Assert.EndsWith(".mp3", r.OutputPath);
            Assert.Null(o!.Video);
            Assert.Equal("mp3", o.Audio.Single().CodecName);
        }
        var (r2, o2) = await Convert(m.Sdr8, ConvertTarget.AudioM4a);
        Assert.Equal(Outcome.Encoded, r2.Outcome);
        Assert.Equal("aac", o2!.Audio.Single().CodecName);
        Assert.Null(o2.Video);

        var (r3, o3) = await Convert(m.AudioMix, ConvertTarget.AudioFlac);
        Assert.Equal(Outcome.Encoded, r3.Outcome);
        Assert.Equal("flac", o3!.Audio.Single().CodecName);
    }

    [Fact]
    public async Task AudioOnly_PicksDefaultTrack()
    {
        var info = (await m.Tools.ProbeAsync(m.Multi))!;
        var plan = ConvertPlanner.Plan(info, ConvertTarget.AudioFlac, await Caps(), m.Multi);
        var eng = info.Audio.First(a => a.IsDefault);
        Assert.Equal("0:" + eng.Index, plan.Output[1]);
    }

    [Fact]
    public async Task ExistingOutput_IsNeverOverwritten()
    {
        var dir = NewDir();
        var target = Path.Combine(dir, "sdr8.mp4");
        File.WriteAllText(target, "nicht anfassen");
        var (r, _) = await Convert(m.Sdr8, ConvertTarget.RemuxMp4, dir);
        Assert.Equal(Outcome.AlreadyOk, r.Outcome);
        Assert.Equal("nicht anfassen", File.ReadAllText(target));
        Assert.Empty(Directory.GetFiles(dir, "*.part.*"));
        Assert.Empty(Directory.GetFiles(dir, "*.gimbo-tmp"));
    }

    [Fact]
    public void OutputPath_NextToSource_FolderStructure_Suffix()
    {
        var sep = Path.DirectorySeparatorChar;
        var src = Path.Combine(m.Dir, "Serie", "Staffel 1", "Folge.mkv");
        Assert.Equal(Path.Combine(m.Dir, "Serie", "Staffel 1", "Folge (remux).mkv"), ConvertPlanner.OutputPath(src, null, null, true, ConvertTarget.RemuxMkv, ""));
        Assert.Equal(Path.Combine(m.Dir, "Serie", "Staffel 1", "Folge.mp4"), ConvertPlanner.OutputPath(src, null, null, true, ConvertTarget.Compatible, ""));
        var outDir = Path.Combine(m.Dir, "Ausgabe");
        Assert.Equal(Path.Combine(outDir, "Serie", "Staffel 1", "Folge.mp4"), ConvertPlanner.OutputPath(src, m.Dir, outDir, true, ConvertTarget.Compatible, ""));
        Assert.Equal(Path.Combine(outDir, "Folge.mp4"), ConvertPlanner.OutputPath(src, m.Dir, outDir, false, ConvertTarget.Compatible, ""));
        Assert.Equal(Path.Combine(outDir, "Folge (Handy).mp4"), ConvertPlanner.OutputPath(src, null, outDir, true, ConvertTarget.Mobile, "(Handy)"));
        Assert.DoesNotContain(".." + sep, ConvertPlanner.OutputPath(src, Path.Combine(m.Dir, "anders"), outDir, true, ConvertTarget.Mobile, ""));
    }

    [Fact]
    public async Task DolbyVisionWithoutBase_IsRefusedForEncoding()
    {
        var info = new MediaInfo
        {
            Path = "dv.mkv", DurationSeconds = 10,
            Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 3840, Height = 2160, SideDataTypes = ["DOVI configuration record"], DvProfile = 5 }],
        };
        var caps = await Caps();
        Assert.False(ConvertPlanner.Plan(info, ConvertTarget.Compatible, caps, "dv.mkv").Ok);
        Assert.True(ConvertPlanner.Plan(info, ConvertTarget.RemuxMp4, caps, "dv.mkv").Ok);
        Assert.True(ConvertPlanner.Plan(info, ConvertTarget.Apple, caps, "dv.mkv").Ok);
    }

    [Fact]
    public async Task RunAsync_Parallel_ReportsEveryFile()
    {
        var dir = NewDir();
        var conv = new FileConverter(m.Tools, await Caps());
        var obs = new Obs();
        var jobs = new[] { new ConvertJob(1, m.Sdr8), new ConvertJob(2, m.Mp4Text), new ConvertJob(3, Path.Combine(dir, "fehlt.mkv")) };
        await conv.RunAsync(jobs, new ConvertOptions { Target = ConvertTarget.RemuxMkv, OutputDir = dir, Parallel = 2 }, obs, CancellationToken.None);
        Assert.Equal(3, obs.Results.Count);
        Assert.Equal(Outcome.Encoded, obs.Results[1].Outcome);
        Assert.Equal(Outcome.Encoded, obs.Results[2].Outcome);
        Assert.Equal(Outcome.Error, obs.Results[3].Outcome);
    }

    [Fact]
    public async Task Cancel_DeletesTempFile()
    {
        var dir = NewDir();
        var conv = new FileConverter(m.Tools, await Caps());
        using var cts = new CancellationTokenSource();
        var obs = new Obs { OnProgress = () => cts.Cancel() };
        await conv.RunAsync([new ConvertJob(1, m.Long720)], new ConvertOptions { Target = ConvertTarget.Compatible, OutputDir = dir }, obs, cts.Token);
        Assert.Equal(Outcome.Cancelled, obs.Results[1].Outcome);
        Assert.Empty(Directory.GetFiles(dir));
    }

    [Fact]
    public async Task SameNameFromDifferentFolders_GetNumberedOutputs()
    {
        var root = NewDir();
        var a = Path.Combine(root, "a", "Folge.mkv");
        var b = Path.Combine(root, "b", "Folge.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(a)!);
        Directory.CreateDirectory(Path.GetDirectoryName(b)!);
        File.Copy(m.Long720, a);
        File.Copy(m.Sdr8, b);
        var outDir = Path.Combine(root, "out");
        var conv = new FileConverter(m.Tools, await Caps());
        var obs = new Obs();
        await conv.RunAsync([new ConvertJob(1, a), new ConvertJob(2, b)], new ConvertOptions { Target = ConvertTarget.RemuxMp4, OutputDir = outDir, KeepStructure = false, Parallel = 2 }, obs, CancellationToken.None);
        Assert.Equal(Outcome.Encoded, obs.Results[1].Outcome);
        Assert.Equal(Outcome.Encoded, obs.Results[2].Outcome);
        Assert.Equal(Path.Combine(outDir, "Folge.mp4"), obs.Results[1].OutputPath);
        Assert.Equal(Path.Combine(outDir, "Folge (2).mp4"), obs.Results[2].OutputPath);
        Assert.Equal(24, (await m.Tools.ProbeAsync(obs.Results[1].OutputPath!))!.DurationSeconds, 0);
        Assert.Equal(4, (await m.Tools.ProbeAsync(obs.Results[2].OutputPath!))!.DurationSeconds, 0);
    }

    [Fact]
    public void TempNames_OnlyOwnPattern()
    {
        Assert.True(FileConverter.IsTempName(@"Film.1a2b3c.part.mp4"));
        Assert.True(FileConverter.IsTempName(@"Film.mp4.1a2b3c.gimbo-tmp"));
        Assert.False(FileConverter.IsTempName(@"Wueste.Part.Two.2024.mkv"));
        Assert.False(FileConverter.IsTempName(@"Harry.Potter.Deathly.Hallows.Part.1.mkv"));
    }

    [Fact]
    public async Task Mkv_BlurayPcmBecomesFlac()
    {
        var src = Path.Combine(NewDir(), "bd.m2ts");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=640x360:rate=24", "-f", "lavfi", "-i", "sine=440:sample_rate=48000", "-t", "2",
            "-c:v", "libx264", "-preset", "ultrafast", "-c:a", "pcm_bluray", "-ac", "2", "-f", "mpegts", src]);
        var info = (await m.Tools.ProbeAsync(src))!;
        Assert.Equal("pcm_bluray", info.Audio.Single().CodecName);
        var (r, o) = await Convert(src, ConvertTarget.RemuxMkv);
        Assert.Equal(Outcome.Encoded, r.Outcome);
        Assert.Equal("flac", o!.Audio.Single().CodecName);
    }

    [Fact]
    public async Task Subtitles_UnsupportedFormatsAreDroppedWithWarning()
    {
        var info = new MediaInfo
        {
            Path = "rec.ts", DurationSeconds = 10,
            Streams =
            [
                new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1280, Height = 720, PixFmt = "yuv420p" },
                new StreamInfo { Index = 1, CodecType = "subtitle", CodecName = "dvb_teletext" },
                new StreamInfo { Index = 2, CodecType = "subtitle", CodecName = "hdmv_pgs_subtitle" },
                new StreamInfo { Index = 3, CodecType = "subtitle", CodecName = "ttml" },
            ],
        };
        var caps = await Caps();
        var mkv = ConvertPlanner.Plan(info, ConvertTarget.RemuxMkv, caps, "rec.ts");
        Assert.Contains("0:2", mkv.Output);
        Assert.DoesNotContain("0:1", mkv.Output);
        Assert.DoesNotContain("0:3", mkv.Output);
        Assert.Contains(mkv.Warnings, w => w.StartsWith("2 "));
        var mp4 = ConvertPlanner.Plan(info, ConvertTarget.RemuxMp4, caps, "rec.ts");
        Assert.Equal(2, mp4.Warnings.Count);
        Assert.Contains(mp4.Warnings, w => w.StartsWith("1 "));
    }

    [Fact]
    public async Task Portrait_KeepsFullHeight()
    {
        var caps = await Caps();
        MediaInfo Info(int w, int h, int rot = 0) => new()
        {
            Path = "p.mp4", DurationSeconds = 10,
            Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = w, Height = h, PixFmt = "yuv420p", Rotation = rot }],
        };
        Assert.Contains(ConvertPlanner.Plan(Info(2160, 3840), ConvertTarget.Compatible, caps, "p.mp4").Output, a => a.Contains("scale=1080:1920"));
        Assert.Contains(ConvertPlanner.Plan(Info(3840, 2160, -90), ConvertTarget.Compatible, caps, "p.mp4").Output, a => a.Contains("scale=1080:1920"));
        Assert.DoesNotContain(ConvertPlanner.Plan(Info(1080, 1920), ConvertTarget.Compatible, caps, "p.mp4").Output, a => a.Contains("scale="));
        Assert.Contains(ConvertPlanner.Plan(Info(1080, 1920), ConvertTarget.Mobile, caps, "p.mp4").Output, a => a.Contains("scale=720:1280"));
    }

    [Fact]
    public async Task HdrToSdr_IsTaggedBt709()
    {
        var info = (await m.Tools.ProbeAsync(m.Hdr10))!;
        var plan = ConvertPlanner.Plan(info, ConvertTarget.Compatible, await Caps(), m.Hdr10);
        Assert.Contains("-color_trc", plan.Output);
        Assert.Equal("bt709", plan.Output[plan.Output.IndexOf("-color_trc") + 1]);
    }

    [Fact]
    public void Suffix_InvalidCharactersRemoved()
    {
        var src = Path.Combine(m.Dir, "Film.mkv");
        var outDir = Path.Combine(m.Dir, "o");
        Assert.Equal(Path.Combine(outDir, "Film HandyTablet.mp4"), ConvertPlanner.OutputPath(src, null, outDir, true, ConvertTarget.Mobile, "Handy/Tablet"));
        Assert.Equal(Path.Combine(outDir, "Film a b.mp4"), ConvertPlanner.OutputPath(src, null, outDir, true, ConvertTarget.Mobile, "a: b?"));
    }

    [Fact]
    public async Task Stalled_FfmpegIsCancelled()
    {
        var dir = NewDir();
        var fifo = Path.Combine(dir, "stumm.mkv");
        if (OperatingSystem.IsWindows()) return;
        var mk = await ProcessRunner.RunAsync("mkfifo", [fifo], TimeSpan.FromSeconds(10));
        if (mk.ExitCode != 0) return;
        var info = (await m.Tools.ProbeAsync(m.Sdr8))!;
        var conv = new FileConverter(m.Tools, await Caps()) { StallTimeout = TimeSpan.FromSeconds(4) };
        var r = await conv.ConvertAsync(new ConvertJob(1, fifo, null, info), new ConvertOptions { Target = ConvertTarget.RemuxMkv, OutputDir = Path.Combine(dir, "o") }, null, CancellationToken.None);
        Assert.Equal(Outcome.Error, r.Outcome);
        Assert.True(r.Message.Contains("reagiert") || r.Message.Contains("responded"), r.Message);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "o")));
    }

    private sealed class Obs : IConvertObserver
    {
        public Dictionary<int, ConvertResult> Results { get; } = [];
        public Action? OnProgress { get; init; }
        public void Started(ConvertJob job, string outputPath) { }
        public void Progress(ConvertJob job, ProgressInfo info) => OnProgress?.Invoke();
        public void Finished(ConvertJob job, ConvertResult result) { lock (Results) Results[job.Id] = result; }
    }
}
