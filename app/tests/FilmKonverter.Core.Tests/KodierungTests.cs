using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

internal static class PgsSample
{
    public static byte[] Build(int width = 640, int height = 360)
    {
        using var ms = new MemoryStream();
        void U16(Stream s, int v) { s.WriteByte((byte)(v >> 8)); s.WriteByte((byte)v); }
        void Segment(uint pts, byte type, byte[] payload)
        {
            ms.Write("PG"u8);
            foreach (var shift in new[] { 24, 16, 8, 0 }) ms.WriteByte((byte)(pts >> shift));
            ms.Write(new byte[4]);
            ms.WriteByte(type);
            U16(ms, payload.Length);
            ms.Write(payload);
        }
        void Display(uint pts, bool show)
        {
            using var pcs = new MemoryStream();
            U16(pcs, width); U16(pcs, height);
            pcs.WriteByte(0x10);
            U16(pcs, show ? 1 : 2);
            pcs.Write([(byte)(show ? 0x80 : 0x00), 0, 0, (byte)(show ? 1 : 0)]);
            if (show) { U16(pcs, 0); pcs.Write([0, 0]); U16(pcs, 100); U16(pcs, 100); }
            Segment(pts, 0x16, pcs.ToArray());
            using var wds = new MemoryStream();
            wds.Write([1, 0]); U16(wds, 100); U16(wds, 100); U16(wds, 16); U16(wds, 4);
            Segment(pts, 0x17, wds.ToArray());
            if (show)
            {
                Segment(pts, 0x14, [0, 0, 1, 235, 128, 128, 255]);
                var rle = Enumerable.Repeat(new byte[] { 0x00, 0x80 | 16, 1, 0x00, 0x00 }, 4).SelectMany(x => x).ToArray();
                using var ods = new MemoryStream();
                U16(ods, 0); ods.Write([0, 0xC0]);
                var len = rle.Length + 4;
                ods.Write([(byte)(len >> 16), (byte)(len >> 8), (byte)len]);
                U16(ods, 16); U16(ods, 4);
                ods.Write(rle);
                Segment(pts, 0x15, ods.ToArray());
            }
            Segment(pts, 0x80, []);
        }
        Display(45000, true);
        Display(135000, false);
        return ms.ToArray();
    }
}

[Collection("media")]
public sealed class LevelLabelTests(TestMedia m)
{
    private static EncodePlan Plan(StreamInfo v, string pix = "yuv420p", string prof = "main") =>
        new(v, null, pix.Contains("10") ? 10 : 8, pix, prof, new AudioPlan([]), "", "");

    private static StreamInfo Video(int w, int h, string avg, string r = "", string codec = "h264") =>
        new() { Index = 0, CodecType = "video", CodecName = codec, Width = w, Height = h, PixFmt = "yuv420p", AvgFrameRate = avg, RFrameRate = r };

    [Fact]
    public void NVENC_bekommt_keinen_Level_vorgegeben_der_Datenstrom_wird_beschriftet()
    {
        var a = ArgsBuilder.BuildEncode(Profile.Nvidia(), Plan(Video(1920, 1080, "24000/1001")), "in", "out");
        Assert.DoesNotContain("-level:v", a);
        Assert.DoesNotContain("-tier:v", a);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
        Assert.Single(a, x => x.StartsWith("-bsf:v", StringComparison.Ordinal));
    }

    [Fact]
    public void AMF_behaelt_die_Vorgabe_und_wird_zusaetzlich_beschriftet()
    {
        var a = ArgsBuilder.BuildEncode(Profile.Amd(), Plan(Video(1920, 1080, "25/1"), "nv12"), "in", "out");
        Assert.Equal("4.1", a[a.IndexOf("-level:v") + 1]);
        Assert.Equal("hevc_metadata=level=123", a[a.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public void Prozessor_und_Intel_bleiben_ohne_Level_Angabe()
    {
        foreach (var p in new[] { Profile.Cpu(), Profile.Intel() })
        {
            var a = ArgsBuilder.BuildEncode(p, Plan(Video(1920, 1080, "24/1")), "in", "out");
            Assert.DoesNotContain("-level:v", a);
            Assert.DoesNotContain(a, x => x.StartsWith("-bsf:v", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void Dolby_Vision_entfernen_und_Level_stehen_in_einer_Kette()
    {
        var dv = new StreamInfo { Index = 0, CodecType = "video", CodecName = "hevc", Width = 1920, Height = 1080, PixFmt = "yuv420p10le", AvgFrameRate = "24/1", SideDataTypes = ["DOVI configuration record"], DvProfile = 8, DvCompatibility = 1 };
        var nv = ArgsBuilder.BuildEncode(Profile.Nvidia(), Plan(dv, "p010le", "main10"), "in", "out");
        Assert.Single(nv, x => x.StartsWith("-bsf:v", StringComparison.Ordinal));
        Assert.Equal("dovi_rpu=strip=1,hevc_metadata=level=123", nv[nv.IndexOf("-bsf:v") + 1]);
        var cpu = ArgsBuilder.BuildEncode(Profile.Cpu(), Plan(dv, "yuv420p10le", "main10"), "in", "out");
        Assert.Equal("dovi_rpu=strip=1", cpu[cpu.IndexOf("-bsf:v") + 1]);
    }

    [Fact]
    public void Level_aus_der_echten_Bildrate_nicht_aus_dem_Zeitraster()
    {
        var p = Profile.Nvidia();
        p.MaxWidth = 0; p.MaxHeight = 0;
        string Label(StreamInfo v) { var a = ArgsBuilder.BuildEncode(p, Plan(v), "in", "out"); return a[a.IndexOf("-bsf:v") + 1]; }
        Assert.Equal("hevc_metadata=level=123", Label(Video(1920, 1080, "2990/100", "120/1")));
        Assert.Equal("hevc_metadata=level=153", Label(Video(3840, 2160, "0/0", "60/1")));
        Assert.Equal("hevc_metadata=level=150", Label(Video(3840, 2160, "0/0", "1000/1")));
        Assert.Equal("hevc_metadata=level=150", Label(Video(3840, 2160, "", "")));
        Assert.Equal(30, HevcLevel.FpsFor(Video(1, 1, "0/0", "0/0")));
    }

    [Fact]
    public void Analyse_liest_Zeitraster_und_Beginn()
    {
        var info = MediaInfo.Parse("x.mkv", """{"format":{"duration":"13.0","start_time":"2.995000","size":"1000"},"streams":[{"index":0,"codec_type":"video","codec_name":"h264","width":1920,"height":1080,"avg_frame_rate":"2990/100","r_frame_rate":"120/1"}]}""", 1000);
        Assert.Equal("120/1", info.Video!.RFrameRate);
        Assert.Equal(29.9, info.Fps, 3);
        Assert.Equal(2.995, info.StartSeconds, 3);
    }

    [Fact]
    public async Task Beschriftung_wirkt_am_Ausgang_des_Encoders()
    {
        var plan = Plan(Video(640, 360, "24/1"));
        var output = Path.Combine(m.Dir, "label-" + Guid.NewGuid().ToString("N")[..6] + ".mkv");
        var a = ArgsBuilder.BuildEncode(Profile.Nvidia(), plan, m.Sdr8, output);
        var from = a.IndexOf("-c:v");
        var to = a.IndexOf("-bsf:v");
        a.RemoveRange(from, to - from);
        a.InsertRange(from, ["-c:v", "libx265", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-x265-params", "log-level=error:level-idc=62"]);
        await m.Ff(a);
        var o = (await m.Tools.ProbeAsync(output))!;
        Assert.Equal("hevc", o.Video!.CodecName);
        Assert.Equal(123, o.Video.Level);
    }

    private static MediaInfo Hevc(int w, int h, string fps, int level, string codec = "hevc") => new()
    {
        Path = "x.mkv", DurationSeconds = 100,
        Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = codec, Width = w, Height = h, PixFmt = "yuv420p10le", AvgFrameRate = fps, Level = level }],
    };

    [Fact]
    public void Nur_eindeutig_falsche_Level_werden_korrigiert()
    {
        Assert.Null(HevcLevel.FixFor(Hevc(3840, 2160, "24000/1001", 153)));
        Assert.Null(HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 150)));
        Assert.Null(HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 153)));
        Assert.Null(HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 123)));
        Assert.Equal(123, HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 186))!.Idc);
        Assert.Equal(123, HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 156))!.Idc);
        Assert.Equal(153, HevcLevel.FixFor(Hevc(3840, 2160, "60/1", 186))!.Idc);
        Assert.Equal(150, HevcLevel.FixFor(Hevc(3840, 2160, "24/1", 183))!.Idc);
        Assert.Null(HevcLevel.FixFor(Hevc(3840, 2160, "100/1", 156)));
        Assert.Null(HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 0)));
        Assert.Null(HevcLevel.FixFor(Hevc(1920, 1080, "24/1", 186, "h264")));
    }
}

[Collection("media")]
public sealed class SubtitleDefaultTests(TestMedia m)
{
    private static StreamInfo Sub(int index, string codec, bool isDefault = false, bool forced = false, string title = "", string lang = "ger") => new()
    {
        Index = index, CodecType = "subtitle", CodecName = codec, IsDefault = isDefault, IsForced = forced,
        Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["language"] = lang, ["title"] = title },
    };

    private static MediaInfo Media(params StreamInfo[] subs) => new()
    {
        Path = "x.mkv", DurationSeconds = 100,
        Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080, PixFmt = "yuv420p" }, .. subs],
    };

    private static string?[] Disp(SubtitlePlan p) => p.Tracks.Select(t => t.Disposition).ToArray();

    [Fact]
    public void Erste_Spur_erzwungen_waehlt_die_erste_Text_Spur()
    {
        var p = Profile.Nvidia();
        p.ForcedSubtitles = ForcedSubtitleMode.First;
        var plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle", isDefault: true), Sub(2, "subrip"), Sub(3, "subrip", isDefault: true)), p);
        Assert.Equal(["0", "forced", "0"], Disp(plan));
        Assert.True(plan.Tracks[0].DefaultRemoved);
        Assert.Equal([false, true, false], plan.Tracks.Select(t => t.Forced));
        var a = ArgsBuilder.BuildEncode(p, new EncodePlan(new StreamInfo { Index = 0, CodecType = "video" }, null, 8, "yuv420p", "main", new AudioPlan([]), "", "", plan), "in", "out");
        Assert.DoesNotContain("-disposition:s", a);
        Assert.Equal("forced", a[a.IndexOf("-disposition:s:1") + 1]);
        Assert.Equal("0", a[a.IndexOf("-disposition:s:0") + 1]);
    }

    [Fact]
    public void Ohne_Text_Spur_wird_keine_gewaehlt_und_erzwungen_aus_der_Quelle_bleibt()
    {
        var p = Profile.Nvidia();
        p.ForcedSubtitles = ForcedSubtitleMode.First;
        var plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle"), Sub(2, "dvd_subtitle", forced: true)), p);
        Assert.Equal(["0", "forced"], Disp(plan));
    }

    [Fact]
    public void Erzwungene_als_Standard_gilt_nur_fuer_Text()
    {
        var p = Profile.Nvidia();
        p.ForcedSubtitlesDefault = true;
        var plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle", forced: true), Sub(2, "subrip", title: "Forced"), Sub(3, "subrip", isDefault: true)), p);
        Assert.Equal(["forced", "default+forced", "0"], Disp(plan));

        plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle", isDefault: true, forced: true), Sub(2, "hdmv_pgs_subtitle")), p);
        Assert.Equal(["forced", "0"], Disp(plan));
        Assert.True(plan.Tracks[0].DefaultRemoved);
    }

    [Fact]
    public void Standard_aus_der_Quelle_entfaellt_auf_Bild_Spuren_mit_Hinweis_Text_bleibt()
    {
        var p = Profile.Nvidia();
        p.ForcedSubtitles = ForcedSubtitleMode.Keep;
        var plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle", isDefault: true), Sub(2, "subrip", isDefault: true), Sub(3, "dvb_subtitle", isDefault: true, forced: true)), p);
        Assert.Equal(["0", null, "forced"], Disp(plan));
        Assert.True(plan.Changed);
        Assert.Contains("Standard", plan.Text);
        plan = AudioPlanner.PlanSubtitles(Media(Sub(1, "hdmv_pgs_subtitle"), Sub(2, "subrip", isDefault: true)), p);
        Assert.Equal([null, null], Disp(plan));
        Assert.False(plan.Changed);
    }

    [Fact]
    public async Task Kodierte_Datei_hat_den_Bild_Untertitel_nicht_mehr_als_Standard()
    {
        var root = Path.Combine(m.Dir, "pgs-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "in");
        Directory.CreateDirectory(input);
        var sup = Path.Combine(root, "bild.sup");
        File.WriteAllBytes(sup, PgsSample.Build());
        var srt = Path.Combine(root, "text.srt");
        File.WriteAllText(srt, "1\n00:00:00,500 --> 00:00:02,000\nHallo\n\n");
        await m.Ff(["-i", m.Sdr8, "-i", sup, "-i", srt, "-map", "0:v", "-map", "0:a", "-map", "1", "-map", "2", "-c", "copy",
            "-disposition:s:0", "default", "-disposition:s:1", "default", Path.Combine(input, "Datei.mkv")]);
        var src = (await m.Tools.ProbeAsync(Path.Combine(input, "Datei.mkv")))!;
        Assert.Equal(["hdmv_pgs_subtitle", "subrip"], src.Subtitles.Select(s => s.CodecName));
        Assert.All(src.Subtitles, s => Assert.True(s.IsDefault));

        var p = Profile.Cpu();
        p.InputDir = input;
        p.OutputDir = Path.Combine(root, "out");
        p.CpuPreset = "ultrafast"; p.Quality = 30; p.MinSizeMb = 0; p.SkipNewerThanMinutes = 0; p.MinFreeSpaceGb = 0; p.EarlyAbortPercent = 0;
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null }.RunAsync(CancellationToken.None);
        var end = rec.EndOf("Datei");
        Assert.True(end.Outcome == Outcome.Encoded, end.Message + " | " + string.Join(" / ", rec.Lines.Select(l => l.Text)));
        var o = (await m.Tools.ProbeAsync(Path.Combine(p.OutputDir, "Datei.mkv")))!;
        var subs = o.Subtitles.ToList();
        Assert.False(subs[0].IsDefault);
        Assert.True(subs[1].IsDefault);
        Assert.Contains(rec.Lines, l => l.Label == "Untertitel" && l.Text.Contains("nicht mehr als Standard"));
    }
}

[Collection("media")]
public sealed class AudioRulesTests(TestMedia m)
{
    private static StreamInfo Audio(int index, string codec, string lang = "", bool isDefault = false, string title = "", int ch = 6, string profile = "", bool comment = false) => new()
    {
        Index = index, CodecType = "audio", CodecName = codec, Profile = profile, Channels = ch, IsDefault = isDefault, IsComment = comment,
        Tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["language"] = lang, ["title"] = title },
    };

    private static MediaInfo Media(params StreamInfo[] audio) => new()
    {
        Path = "x.mkv", DurationSeconds = 100,
        Streams = [new StreamInfo { Index = 0, CodecType = "video", CodecName = "h264", Width = 1920, Height = 1080, PixFmt = "yuv420p" }, .. audio],
    };

    [Fact]
    public void Nur_TV_taugliche_Formate_werden_kopiert()
    {
        var media = Media(Audio(1, "aac"), Audio(2, "aac_latm"), Audio(3, "ac3"), Audio(4, "eac3"), Audio(5, "mp3"), Audio(6, "mp2"),
            Audio(7, "opus"), Audio(8, "vorbis"), Audio(9, "wmav2"), Audio(10, "adpcm_ima_wav"), Audio(11, "dts", profile: "DTS"),
            Audio(12, "flac"), Audio(13, "truehd"), Audio(14, "pcm_s16le"), Audio(15, "dts", profile: "DTS-HD MA"));
        var p = new Profile();
        Assert.Equal([false, true, false, false, false, false, true, true, true, true, true, true, true, true, true],
            AudioPlanner.Plan(media, p).Tracks.Select(t => t.Convert));

        p.ConvertDts = false;
        Assert.Equal([12, 13, 14, 15], AudioPlanner.Plan(media, p).Tracks.Where(t => t.Convert).Select(t => t.SourceIndex));
        p.ConvertDts = true;
        p.ConvertLosslessAudio = false;
        Assert.Equal([2, 7, 8, 9, 10, 11], AudioPlanner.Plan(media, p).Tracks.Where(t => t.Convert).Select(t => t.SourceIndex));
    }

    [Fact]
    public void Was_ffmpeg_nicht_dekodieren_kann_wird_kopiert_statt_die_Datei_scheitern_zu_lassen()
    {
        var plan = AudioPlanner.Plan(Media(Audio(1, ""), Audio(2, "none"), Audio(3, "ac4"), Audio(4, "dts", profile: "DTS")), new Profile());
        Assert.Equal([false, false, false, true], plan.Tracks.Select(t => t.Convert));
    }

    [Fact]
    public void Neue_Profile_wandeln_nach_AC3_mit_640_gespeicherte_bleiben()
    {
        Assert.Equal(AudioTarget.Ac3, new Profile().AudioTarget);
        Assert.Equal(AudioTarget.Ac3, Profile.Nvidia().AudioTarget);
        Assert.Equal(640, new Profile().AudioBitrateKbit);
        var t = AudioPlanner.Plan(Media(Audio(1, "truehd", ch: 8)), new Profile()).Tracks.Single();
        Assert.Equal(("ac3", 6, 640), (t.Codec, t.OutChannels, t.BitrateKbit));

        var saved = System.Text.Json.JsonSerializer.Deserialize<Profile>("""{"Name":"X","AudioTarget":"Eac3"}""", JsonOptions.Default)!;
        Assert.Equal(AudioTarget.Eac3, saved.AudioTarget);
        var old = System.Text.Json.JsonSerializer.Deserialize<Profile>("""{"Name":"X"}""", JsonOptions.Default)!;
        Assert.Equal(AudioTarget.Eac3, old.AudioTarget);
        Assert.Equal(AudioTarget.Eac3, old.Clone().AudioTarget);
        Assert.Equal(AudioTarget.Ac3, new Profile().Clone().AudioTarget);
    }

    [Fact]
    public void Standardspur_wird_zuerst_abgebildet_der_Rest_behaelt_die_Reihenfolge()
    {
        var media = Media(Audio(1, "ac3", "eng", isDefault: true), Audio(2, "ac3", "ger", comment: true), Audio(3, "ac3", "ger"), Audio(4, "ac3", "fre"));
        var p = new Profile { DefaultAudioLanguage = "de" };
        var plan = AudioPlanner.Plan(media, p);
        Assert.Equal([3, 1, 2, 4], plan.Tracks.Select(t => t.SourceIndex));
        Assert.Equal([0, 1, 2, 3], plan.Tracks.Select(t => t.OutputIndex));
        Assert.Equal(["default", "0", "comment", "0"], plan.Tracks.Select(t => t.Disposition));
        Assert.True(plan.MovedFirst);
        Assert.False(plan.AllMapped);
        Assert.True(plan.Changed);

        var a = ArgsBuilder.BuildEncode(p, new EncodePlan(media.Video!, null, 8, "yuv420p", "main", plan, "", ""), "in", "out");
        var maps = a.Select((x, i) => (x, i)).Where(t => t.x == "-map").Select(t => a[t.i + 1]).ToArray();
        Assert.Equal(["0:0", "0:3", "0:1", "0:2", "0:4", "0:s?"], maps);
        Assert.Equal("default", a[a.IndexOf("-disposition:a:0") + 1]);

        p.DefaultAudioLanguage = "en";
        plan = AudioPlanner.Plan(media, p);
        Assert.True(plan.AllMapped);
        Assert.False(plan.MovedFirst);
        plan = AudioPlanner.Plan(media, new Profile());
        Assert.True(plan.AllMapped);
        Assert.All(plan.Tracks, t => Assert.Null(t.Disposition));
    }

    [Theory]
    [InlineData("DTS 5.1", "dts", "ac3", false, "")]
    [InlineData("TrueHD 7.1", "truehd", "eac3", true, "")]
    [InlineData("Dolby TrueHD Atmos 7.1", "truehd", "ac3", true, "")]
    [InlineData("Deutsch DTS-HD MA 5.1", "dts", "ac3", false, "Deutsch 5.1")]
    [InlineData("DTS-HD Master Audio 7.1 English", "dts", "ac3", true, "English")]
    [InlineData("German / DTS-HD Master Audio / 5.1 / 48 kHz / 3814 kbps / 24-bit", "dts", "ac3", false, "German / 5.1")]
    [InlineData("Commentary (DTS)", "dts", "ac3", false, "Commentary")]
    [InlineData("Surround 7.1", "flac", "ac3", true, "Surround")]
    [InlineData("English (FLAC Lossless)", "flac", "eac3", false, "English")]
    [InlineData("Stereo PCM", "pcm_s16le", "ac3", false, "Stereo")]
    [InlineData("Opus 5.1 @ 320k", "opus", "ac3", false, "")]
    [InlineData("AAC", "aac_latm", "ac3", false, "")]
    [InlineData("LPCM 2.0", "pcm_bluray", "flac", false, "")]
    public void Titel_verliert_die_Angaben_zum_alten_Format(string title, string source, string target, bool channelsChanged, string expected) =>
        Assert.Equal(expected, TrackTitles.CleanAudio(title, source, target, channelsChanged));

    [Theory]
    [InlineData("Deutsch", "dts")]
    [InlineData("Director's Commentary", "dts")]
    [InlineData("Magnum Opus Kommentar", "dts")]
    [InlineData("Soundtrack 5.1", "dts")]
    [InlineData("", "dts")]
    public void Titel_ohne_Format_Namen_bleibt(string title, string source) =>
        Assert.Null(TrackTitles.CleanAudio(title, source, "ac3", false));

    [Fact]
    public void Nur_umgewandelte_Spuren_bekommen_einen_neuen_Titel()
    {
        var media = Media(Audio(1, "dts", "ger", title: "Deutsch DTS 5.1", profile: "DTS"), Audio(2, "ac3", "eng", title: "AC3 5.1"), Audio(3, "truehd", "eng", title: "TrueHD 7.1", ch: 8));
        var plan = AudioPlanner.Plan(media, new Profile());
        Assert.Equal(["Deutsch 5.1", null, ""], plan.Tracks.Select(t => t.NewTitle));
        var args = plan.Args().ToList();
        Assert.Equal("title=Deutsch 5.1", args[args.IndexOf("-metadata:s:a:0") + 1]);
        Assert.Equal("title=", args[args.IndexOf("-metadata:s:a:2") + 1]);
        Assert.DoesNotContain("-metadata:s:a:1", args);
        Assert.Contains("DTS 5.1", plan.Text);
    }

    [Fact]
    public async Task Kodierte_Datei_hat_die_Standardspur_vorn_im_neuen_Format_und_mit_passendem_Titel()
    {
        var root = Path.Combine(m.Dir, "ord-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "in");
        Directory.CreateDirectory(input);
        await m.Ff(["-i", m.Multi, "-map", "0", "-c", "copy", "-metadata:s:a:1", "title=English DTS 2.0", Path.Combine(input, "Datei.mkv")]);
        var p = Profile.Cpu();
        p.InputDir = input;
        p.OutputDir = Path.Combine(root, "out");
        p.CpuPreset = "ultrafast"; p.Quality = 30; p.MinSizeMb = 0; p.SkipNewerThanMinutes = 0; p.MinFreeSpaceGb = 0; p.EarlyAbortPercent = 0;
        p.DefaultAudioLanguage = "en";
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec) { Cache = null }.RunAsync(CancellationToken.None);
        var end = rec.EndOf("Datei");
        Assert.True(end.Outcome == Outcome.Encoded, end.Message + " | " + string.Join(" / ", rec.Lines.Select(l => l.Text)));
        var a = (await m.Tools.ProbeAsync(Path.Combine(p.OutputDir, "Datei.mkv")))!.Audio.ToList();
        Assert.Equal(4, a.Count);
        Assert.Equal(("eng", "ac3", true), (a[0].Language, a[0].CodecName, a[0].IsDefault));
        Assert.Equal("English 2.0", a[0].Title);
        Assert.Equal(["ger", "ger", "ger"], a.Skip(1).Select(x => x.Language));
        Assert.All(a.Skip(1), x => Assert.False(x.IsDefault));
        Assert.True(AudioPlanner.IsCommentary(a[2]));
        Assert.True(AudioPlanner.IsAudioDescription(a[3]));
    }
}
