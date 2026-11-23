using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

public sealed class ArgsTests
{
    private static EncodePlan Plan(string? filter, int depth, string pix, string prof) =>
        new(new StreamInfo { Index = 0, CodecType = "video" }, filter, depth, pix, prof, new AudioPlan([]), "", "");

    private static string[] WithoutStats(List<string> a)
    {
        var r = new List<string>();
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].StartsWith("-metadata:s:", StringComparison.Ordinal)) { i++; continue; }
            r.Add(a[i]);
        }
        return r.ToArray();
    }

    [Fact]
    public void NVENC_wie_Batch_ohne_Skalierung_8Bit()
    {
        var p = Profile.Nvidia();
        var a = ArgsBuilder.BuildEncode(p, Plan(null, 8, "yuv420p", "main"), "IN", "OUT");
        string[] batch =
        [
            "-hide_banner", "-y", "-i", "IN", "-map", "0:0", "-map", "0:a?", "-map", "0:s?",
            "-map_metadata", "0", "-map_chapters", "0",
            "-c:v", "hevc_nvenc", "-preset", "p7", "-tune", "hq", "-rc", "vbr", "-cq", "27", "-b:v", "0",
            "-profile:v", "main", "-pix_fmt", "yuv420p", "-spatial-aq", "1", "-temporal-aq", "1",
            "-aq-strength", "8", "-rc-lookahead", "32", "-bf", "3", "-b_ref_mode", "middle",
            "-bsf:v", "hevc_metadata=level=123",
            "-c:a", "copy", "-c:s", "copy", "-max_muxing_queue_size", "9999", "OUT",
        ];
        Assert.Equal(batch, WithoutStats(a));
    }

    [Fact]
    public void NVENC_wie_Batch_mit_Skalierung_10Bit()
    {
        var p = Profile.Nvidia();
        var f = string.Format(Planner.ScaleFilterTemplate, 1920, 1080);
        var a = ArgsBuilder.BuildEncode(p, Plan(f, 10, "p010le", "main10"), "IN", "OUT");
        var i = a.IndexOf("-vf");
        Assert.Equal(f, a[i + 1]);
        Assert.Equal("-map_chapters", a[i - 2]);
        Assert.Equal("-c:v", a[i + 2]);
        Assert.Equal("main10", a[a.IndexOf("-profile:v") + 1]);
        Assert.Equal("p010le", a[a.IndexOf("-pix_fmt") + 1]);
    }

    [Fact]
    public void AMF_wie_Batch()
    {
        var p = Profile.Amd();
        var a = ArgsBuilder.BuildEncode(p, Plan("format=nv12", 8, "nv12", "main"), "IN", "OUT");
        string[] batch =
        [
            "-hide_banner", "-y", "-analyzeduration", "100M", "-probesize", "100M", "-i", "IN",
            "-map", "0:0", "-map", "0:a?", "-map", "0:s?", "-map_metadata", "0", "-map_chapters", "0",
            "-vf", "format=nv12", "-c:v", "hevc_amf", "-usage", "transcoding", "-quality", "quality",
            "-rc", "cqp", "-qp_i", "23", "-qp_p", "25", "-profile:v", "main", "-pix_fmt", "nv12", "-bf", "0",
            "-level:v", "4.1", "-bsf:v", "hevc_metadata=level=123",
            "-c:a", "copy", "-c:s", "copy", "-max_muxing_queue_size", "9999", "OUT",
        ];
        Assert.Equal(batch, WithoutStats(a));
    }

    [Fact]
    public void Testmodus_setzt_ss_und_t_vor_i()
    {
        var a = ArgsBuilder.BuildEncode(Profile.Nvidia(), Plan(null, 8, "yuv420p", "main"), "IN", "OUT", ["-ss", "600", "-t", "120"]);
        Assert.Equal(new[] {"-hide_banner", "-y", "-ss", "600", "-t", "120", "-i", "IN"}, a.Take(8).ToArray());
    }

    [Fact]
    public void Decode_Test_ganz_oder_Anfang()
    {
        Assert.Equal(new[] {"-hide_banner", "-v", "error", "-i", "F", "-map", "0:v:0", "-f", "null", "NUL"},
            ArgsBuilder.BuildDecodeTest("F", true, "NUL").ToArray());
        Assert.Contains("-frames:v", ArgsBuilder.BuildDecodeTest("F", false, "NUL"));
    }

    [Fact]
    public void Ohne_erzwungen_Modus_keine_Untertitel_Kennzeichen()
    {
        var a = ArgsBuilder.BuildEncode(Profile.Nvidia(), Plan(null, 8, "yuv420p", "main"), "IN", "OUT");
        Assert.DoesNotContain("-disposition:s", a);
        Assert.Equal(new[] { "-map", "0:0", "-map", "0:a?", "-map", "0:s?" }, a.Skip(4).Take(6).ToArray());
    }
}

public sealed class DecodeErrorTests
{
    [Fact]
    public void Null_Muxer_Meldungen_sind_kein_Schaden()
    {
        var lines = new[]
        {
            "[null @ 0x5] Application provided invalid, non monotonically increasing dts to muxer in stream 0: 29 >= 29",
            "[hevc @ 0x6] Could not find ref with POC 12",
        };
        Assert.Equal(new[] { lines[1] }, ArgsBuilder.DecodeErrors(lines).ToArray());
    }
}

public sealed class ReviewFixTests
{
    private static MediaInfo Parse(string json) => MediaInfo.Parse("x.mkv", json, 1000);

    [Fact]
    public void Statistik_Tags_des_Videos_werden_geleert()
    {
        var plan = new EncodePlan(new StreamInfo { Index = 0, CodecType = "video" }, null, 8, "yuv420p", "main", new AudioPlan([]), "", "");
        var a = ArgsBuilder.BuildEncode(Profile.Nvidia(), plan, "IN", "OUT");
        var i = a.IndexOf("BPS=");
        Assert.True(i > 0);
        Assert.Equal("-metadata:s:v:0", a[i - 1]);
        Assert.True(i < a.IndexOf("-c:a"));
    }

    [Fact]
    public void Gedrehtes_Handyvideo_wird_nach_sichtbarer_Groesse_beurteilt()
    {
        var info = Parse("""{"format":{"duration":"10","size":"1000"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1920,"height":1080,"avg_frame_rate":"30/1","side_data_list":[{"side_data_type":"Display Matrix","rotation":-90}]}]}""");
        Assert.Equal((1080, 1920), (info.Video!.DisplayWidth, info.Video.DisplayHeight));
        var plan = Planner.Decide(info, Profile.Nvidia()).Plan!;
        Assert.NotNull(plan.VideoFilter);
    }

    [Fact]
    public void Dolby_Vision_Profil_5_wird_immer_uebersprungen()
    {
        var dv5 = Parse("""{"format":{"duration":"10","size":"1000"},"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"avg_frame_rate":"24/1","side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":5,"dv_bl_signal_compatibility_id":0}]}]}""");
        Assert.Equal(SkipReason.DolbyVision, Planner.Decide(dv5, Profile.Nvidia()).Skip);
        var dv8 = Parse("""{"format":{"duration":"10","size":"1000"},"streams":[{"index":0,"codec_type":"video","codec_name":"hevc","pix_fmt":"yuv420p10le","width":3840,"height":2160,"avg_frame_rate":"24/1","color_transfer":"smpte2084","side_data_list":[{"side_data_type":"DOVI configuration record","dv_profile":8,"dv_bl_signal_compatibility_id":1}]}]}""");
        Assert.True(Planner.Decide(dv8, Profile.Nvidia()).Encode);
    }

    [Fact]
    public void Bluray_LPCM_wird_immer_zu_FLAC()
    {
        var info = Parse("""{"format":{"duration":"10","size":"1000"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","pix_fmt":"yuv420p","width":1920,"height":1080,"avg_frame_rate":"24/1"},{"index":1,"codec_type":"audio","codec_name":"pcm_bluray","channels":6}]}""");
        var p = Profile.Nvidia();
        p.ConvertLosslessAudio = false;
        var plan = AudioPlanner.Plan(info, p);
        Assert.Equal("flac", plan.Tracks[0].Codec);
        Assert.Equal(0, plan.Converted);
        Assert.Equal(new[] { "-c:a:0", "flac" }, plan.Args().ToArray());
        Assert.Equal("ac3", AudioPlanner.Plan(info, Profile.Nvidia()).Tracks[0].Codec);
    }

    [Fact]
    public void Gleicher_Name_mit_anderer_Endung_nur_einmal()
    {
        var root = Path.Combine(Path.GetTempPath(), "dup-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        File.WriteAllBytes(Path.Combine(root, "Film.mkv"), new byte[100]);
        File.WriteAllBytes(Path.Combine(root, "Film.avi"), new byte[300]);
        File.WriteAllBytes(Path.Combine(root, "Anderer.mkv"), new byte[50]);
        var r = Scanner.Find(root, new ScanFilter { Extensions = [".mkv", ".avi"] });
        Assert.Equal(new[] { "Anderer.mkv", "Film.avi" }, r.Files.Select(Path.GetFileName).ToArray());
        Assert.Equal(1, r.Duplicates);
    }

    [Fact]
    public void Sperre_uebernehmen_und_fremde_nicht_freigeben()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lock-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);
        var lockDir = Path.Combine(dir, "Film.lock");

        Directory.CreateDirectory(lockDir);
        File.WriteAllText(Path.Combine(lockDir, "info.txt"), "ANDERERPC 2026-01-01 10:00");
        Assert.Null(FileLock.TryAcquire(lockDir, 24, out var owner));
        Assert.Equal("ANDERERPC", owner);

        File.SetLastWriteTime(Path.Combine(lockDir, "info.txt"), DateTime.Now.AddHours(-30));
        Directory.SetCreationTime(lockDir, DateTime.Now.AddHours(-30));
        var mine = FileLock.TryAcquire(lockDir, 24, out _);
        Assert.NotNull(mine);
        Assert.StartsWith(FileLock.ComputerName, File.ReadAllText(Path.Combine(lockDir, "info.txt")));

        File.WriteAllText(Path.Combine(lockDir, "info.txt"), "ANDERERPC 2026-01-01 11:00 #fremd");
        mine!.Dispose();
        Assert.True(Directory.Exists(lockDir));
    }

    [Fact]
    public void Sperre_meldet_echte_Fehler()
    {
        var missing = Path.Combine(Path.GetTempPath(), "fehlt-" + Guid.NewGuid().ToString("N")[..6], "x", "Film.lock");
        Assert.Throws<IOException>(() => FileLock.TryAcquire(missing, 24, out _));
    }
}
