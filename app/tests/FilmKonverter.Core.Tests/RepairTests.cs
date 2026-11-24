using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

public sealed class RepairPlannerTests
{
    private static StreamInfo Audio(int index, string codec, int channels, string profile = "") =>
        new() { Index = index, CodecType = "audio", CodecName = codec, Channels = channels, Profile = profile };

    private static MediaInfo Media(int level, params StreamInfo[] audio) => new()
    {
        DurationSeconds = 60,
        Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080, Level = level, AvgFrameRate = "24/1" }, .. audio],
    };

    [Fact]
    public void Erlaubter_Ton_und_passender_Level_brauchen_nichts()
    {
        var plan = RepairPlanner.Plan(Media(123, Audio(1, "eac3", 6), Audio(2, "aac", 2)), new RepairOptions());
        Assert.False(plan.NeedsWork);
        Assert.Empty(plan.Changes);
    }

    [Fact]
    public void Zu_hoher_Level_wird_auf_den_passenden_gesetzt()
    {
        var plan = RepairPlanner.Plan(Media(186), new RepairOptions());
        Assert.Equal("4.1", plan.FixLevel!.Name);
        Assert.Single(plan.Changes);
    }

    [Fact]
    public void Level_bis_5_1_bleibt()
    {
        Assert.Null(RepairPlanner.Plan(Media(153), new RepairOptions()).FixLevel);
    }

    [Fact]
    public void Nicht_erlaubter_Ton_wird_mit_passender_Datenrate_umgewandelt()
    {
        var plan = RepairPlanner.Plan(Media(123, Audio(1, "truehd", 8), Audio(2, "dts", 2), Audio(3, "flac", 1), Audio(4, "aac", 2)), new RepairOptions());
        Assert.Equal(3, plan.Audio.Count);
        Assert.Equal((0, 6, 640), (plan.Audio[0].OutputIndex, plan.Audio[0].OutChannels, plan.Audio[0].BitrateKbit));
        Assert.Equal((1, 2, 256), (plan.Audio[1].OutputIndex, plan.Audio[1].OutChannels, plan.Audio[1].BitrateKbit));
        Assert.Equal((2, 1, 192), (plan.Audio[2].OutputIndex, plan.Audio[2].OutChannels, plan.Audio[2].BitrateKbit));
        Assert.All(plan.Audio, t => Assert.Equal("ac3", t.Codec));
    }

    [Fact]
    public void Ohne_EAC3_in_der_Liste_wird_EAC3_nach_AC3_umgewandelt()
    {
        var opt = new RepairOptions { AllowedAudio = ["aac", "ac3", "mp3", "mp2"] };
        var plan = RepairPlanner.Plan(Media(123, Audio(1, "eac3", 6)), opt);
        Assert.Equal("ac3", Assert.Single(plan.Audio).Codec);
    }

    [Fact]
    public void AC3_bleibt_hoechstens_640k_EAC3_darf_mehr()
    {
        var ac3 = RepairPlanner.Plan(Media(123, Audio(1, "dts", 6)), new RepairOptions { Bitrate51 = 1024 });
        Assert.Equal(640, ac3.Audio[0].BitrateKbit);
        var eac3 = RepairPlanner.Plan(Media(123, Audio(1, "dts", 6)), new RepairOptions { Bitrate51 = 1024, Target = AudioTarget.Eac3 });
        Assert.Equal(("eac3", 1024), (eac3.Audio[0].Codec, eac3.Audio[0].BitrateKbit));
    }

    [Fact]
    public void Nicht_dekodierbarer_Ton_bleibt_mit_Hinweis()
    {
        var plan = RepairPlanner.Plan(Media(123, Audio(1, "ac4", 6)), new RepairOptions());
        Assert.False(plan.NeedsWork);
        Assert.Single(plan.Notes);
    }
}

[Collection("media")]
public sealed class RepairRunTests(TestMedia m)
{
    private sealed class Sink : IRepairObserver
    {
        public void Started(string file) { }
        public void Progress(string file, ProgressInfo progress) { }
        public void Finished(string file, RepairResult result) { }
    }

    private string NewDir()
    {
        var d = Path.Combine(m.Dir, "rep-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(d);
        return d;
    }

    private static RepairOptions Options(bool dry = false) => new() { DryRun = dry, SkipNewerThanMinutes = 0 };

    private async Task<string> BrokenMkvAsync(string dir)
    {
        var srt = Path.Combine(dir, "sub.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nHallo\n\n");
        var cover = Path.Combine(dir, "cover.jpg");
        await m.Ff(["-f", "lavfi", "-i", "color=c=red:size=64x64", "-frames:v", "1", cover]);
        var raw = Path.Combine(dir, "raw.mkv");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-f", "lavfi", "-i", "anullsrc=channel_layout=5.1:sample_rate=48000",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-i", srt, "-t", "3",
            "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error",
            "-c:a:0", "flac", "-c:a:1", "aac", "-ac:a:1", "2", "-c:s", "srt", "-metadata:s:a:0", "title=FLAC 5.1", raw]);
        var file = Path.Combine(dir, "Film (2001).mkv");
        await m.Ff(["-i", raw, "-map", "0", "-c", "copy", "-bsf:v", "hevc_metadata=level=186",
            "-attach", cover, "-metadata:s:t", "mimetype=image/jpeg", "-metadata:s:t", "filename=cover.jpg", file]);
        File.Delete(raw);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        return file;
    }

    [Fact]
    public async Task Level_und_Ton_werden_korrigiert_alles_andere_bleibt()
    {
        var file = await BrokenMkvAsync(NewDir());
        var before = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(186, before.Video!.Level);
        Assert.Equal(1, before.Streams.Count(s => s.AttachedPic));
        var written = File.GetLastWriteTimeUtc(file);

        var res = await new RepairRun(m.Tools, new Sink()).RepairFileAsync(file, Options(), CancellationToken.None);
        Assert.True(res.Outcome == RepairOutcome.Repaired, res.Message);

        var after = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(123, after.Video!.Level);
        var audio = after.Audio.ToList();
        Assert.Equal(("ac3", 6), (audio[0].CodecName, audio[0].Channels));
        Assert.Equal("aac", audio[1].CodecName);
        Assert.Single(after.Subtitles);
        Assert.Equal(1, after.Streams.Count(s => s.AttachedPic));
        Assert.Equal(written.AddSeconds(2), File.GetLastWriteTimeUtc(file));
        Assert.False(File.Exists(CleanupRun.TempFor(file)));
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(file)!, "Film (2001).lock")));
    }

    [Fact]
    public async Task Probelauf_fasst_nichts_an()
    {
        var file = await BrokenMkvAsync(NewDir());
        var bytes = File.ReadAllBytes(file);
        var res = await new RepairRun(m.Tools, new Sink()).RepairFileAsync(file, Options(dry: true), CancellationToken.None);
        Assert.Equal(RepairOutcome.WouldChange, res.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }

    [Fact]
    public async Task MP4_bleibt_MP4_und_bekommt_den_richtigen_Level()
    {
        var dir = NewDir();
        var raw = Path.Combine(dir, "raw.mp4");
        await m.Ff(["-f", "lavfi", "-i", "testsrc2=size=320x180:rate=24", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000", "-t", "3",
            "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", "-tag:v", "hvc1", "-c:a", "aac", raw]);
        var file = Path.Combine(dir, "Clip.mp4");
        await m.Ff(["-i", raw, "-map", "0", "-c", "copy", "-bsf:v", "hevc_metadata=level=186", file]);
        File.Delete(raw);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));

        var res = await new RepairRun(m.Tools, new Sink()).RepairFileAsync(file, Options(), CancellationToken.None);
        Assert.True(res.Outcome == RepairOutcome.Repaired, res.Message);
        var after = (await m.Tools.ProbeAsync(file))!;
        Assert.Equal(123, after.Video!.Level);
        Assert.Contains("mp4", after.FormatName);
        Assert.Equal("aac", Assert.Single(after.Audio).CodecName);
    }

    [Fact]
    public async Task Passende_Datei_bleibt_unberuehrt()
    {
        var dir = NewDir();
        var file = Path.Combine(dir, "Gut.mkv");
        File.Copy(m.Sdr8, file);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
        var bytes = File.ReadAllBytes(file);
        var res = await new RepairRun(m.Tools, new Sink()).RepairFileAsync(file, Options(), CancellationToken.None);
        Assert.Equal(RepairOutcome.Fine, res.Outcome);
        Assert.Equal(bytes, File.ReadAllBytes(file));
    }
}
