using System.Globalization;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Converting;

public enum ConvertTarget
{
    RemuxMkv,
    RemuxMp4,
    Compatible,
    Mobile,
    Apple,
    Av1,
    AudioMp3,
    AudioM4a,
    AudioFlac,
}

public sealed class ConvertCaps
{
    public string? H264 { get; init; }
    public string? Hevc { get; init; }
    public string? Av1 { get; init; }
    public bool Mp3 { get; init; }
    public bool Hevc10 { get; init; }
    public bool DoviStrip { get; init; }
    public bool DoviSafeH264 { get; init; }
    public bool Tonemap { get; init; }

    public static readonly string[] H264Order = ["h264_nvenc", "h264_qsv", "h264_amf", "libx264"];
    public static readonly string[] HevcOrder = ["hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265"];
    public static readonly string[] Av1Order = ["av1_nvenc", "av1_qsv", "libsvtav1"];

    public static bool IsCpu(string? encoder) => encoder is not null && encoder.StartsWith("lib", StringComparison.Ordinal);


    public static async Task<ConvertCaps> DetectAsync(FfmpegTools tools, CancellationToken ct = default)
    {
        async Task<string?> First(string[] order, bool tenBit = false)
        {
            foreach (var e in order)
            {
                if (!tools.Encoders.Contains(e)) continue;
                if ((await tools.TestCodecAsync(e, ct, tenBit)).Ok) return e;
            }
            return null;
        }
        var filters = "";
        try { filters = (await ProcessRunner.RunAsync(tools.FfmpegPath, ["-hide_banner", "-filters"], TimeSpan.FromSeconds(20), ct)).StdOut; }
        catch (Exception ex) when (ex is not OperationCanceledException) { }
        bool HasFilter(string name) => filters.Split('\n').Any(l =>
        {
            var p = l.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            return p.Length >= 2 && p[1] == name;
        });
        var hevc = await First(HevcOrder);
        return new ConvertCaps
        {
            H264 = await First(H264Order),
            Hevc = hevc,
            Hevc10 = hevc is not null && (await tools.TestCodecAsync(hevc, ct, tenBit: true)).Ok,
            Av1 = await First(Av1Order, tenBit: true),
            Mp3 = tools.Encoders.Contains("libmp3lame"),
            DoviStrip = tools.CanStripDolbyVision,
            DoviSafeH264 = tools.Major >= 8,
            Tonemap = HasFilter("zscale") && HasFilter("tonemap"),
        };
    }

    public bool Available(ConvertTarget t) => t switch
    {
        ConvertTarget.Compatible or ConvertTarget.Mobile => H264 is not null,
        ConvertTarget.Apple => Hevc is not null,
        ConvertTarget.Av1 => Av1 is not null,
        ConvertTarget.AudioMp3 => Mp3,
        _ => true,
    };
}

public sealed class ConvertPlan
{
    public string? Error { get; init; }
    public string Extension { get; init; } = "";
    public List<string> PreInput { get; init; } = [];
    public List<string> Output { get; init; } = [];
    public List<string> Changes { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
    public bool HasVideo { get; init; }
    public int AudioCount { get; init; }
    public bool Encodes { get; init; }

    public bool Ok => Error is null;

    public static ConvertPlan Fail(string error) => new() { Error = error };
}

public static class ConvertPlanner
{
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static readonly HashSet<string> Mp4Video = new(StringComparer.OrdinalIgnoreCase) { "h264", "hevc", "av1", "mpeg4", "mpeg2video", "mpeg1video", "vp9" };

    public static readonly HashSet<string> Mp4Audio = new(StringComparer.OrdinalIgnoreCase) { "aac", "mp3", "ac3", "eac3", "alac", "flac", "opus" };

    public static readonly HashSet<string> MkvAudioToFlac = new(StringComparer.OrdinalIgnoreCase) { "pcm_bluray", "pcm_dvd", "s302m" };

    public static readonly HashSet<string> MkvSubs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "webvtt", "hdmv_pgs_subtitle", "dvd_subtitle", "dvb_subtitle" };

    public static readonly HashSet<string> TextSubs = new(StringComparer.OrdinalIgnoreCase) { "subrip", "srt", "ass", "ssa", "mov_text", "webvtt", "text", "tx3g" };

    public static string Extension(ConvertTarget t) => t switch
    {
        ConvertTarget.RemuxMkv or ConvertTarget.Av1 => ".mkv",
        ConvertTarget.AudioMp3 => ".mp3",
        ConvertTarget.AudioM4a => ".m4a",
        ConvertTarget.AudioFlac => ".flac",
        _ => ".mp4",
    };

    public static string Tag(ConvertTarget t) => t switch
    {
        ConvertTarget.RemuxMkv or ConvertTarget.RemuxMp4 => "remux",
        ConvertTarget.Compatible => L.T("kompatibel", "compatible"),
        ConvertTarget.Mobile => L.T("Handy", "mobile"),
        ConvertTarget.Apple => "Apple",
        ConvertTarget.Av1 => "AV1",
        _ => L.T("Ton", "audio"),
    };

    public static bool IsAudioOnly(ConvertTarget t) => t is ConvertTarget.AudioMp3 or ConvertTarget.AudioM4a or ConvertTarget.AudioFlac;

    public static string Describe(MediaInfo m)
    {
        var parts = new List<string>();
        if (m.Video is { } v)
        {
            var res = v.DisplayHeight >= 2000 ? "4K" : v.DisplayHeight > 0 ? $"{v.DisplayHeight}p" : "";
            parts.Add($"{CodecName(v.CodecName)} {res}".Trim());
            if (m.Hdr != HdrKind.None) parts.Add(MediaInfo.HdrText(m.Hdr));
        }
        var audio = m.Audio.ToList();
        if (audio.Count == 1) parts.Add($"{CodecName(audio[0].CodecName)} {Channels(audio[0].Channels)}".Trim());
        else if (audio.Count > 1) parts.Add(L.T($"{audio.Count} Tonspuren", $"{audio.Count} audio tracks"));
        var subs = m.Subtitles.Count();
        if (subs > 0) parts.Add(L.T($"{subs} UT", $"{subs} subs"));
        if (m.DurationSeconds > 0) parts.Add(Fmt.Time(m.DurationSeconds));
        return string.Join(" · ", parts);
    }

    public static string CodecName(string c) => c.ToLowerInvariant() switch
    {
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "mpeg4" => "MPEG-4",
        "msmpeg4v3" or "msmpeg4v2" => "DivX 3",
        "mpeg2video" => "MPEG-2",
        "vc1" => "VC-1",
        "wmv3" => "WMV",
        "vp9" => "VP9",
        "aac" => "AAC",
        "ac3" => "AC3",
        "eac3" => "E-AC3",
        "dts" or "dca" => "DTS",
        "truehd" => "TrueHD",
        "flac" => "FLAC",
        "mp3" => "MP3",
        "opus" => "Opus",
        "vorbis" => "Vorbis",
        "alac" => "ALAC",
        var x when x.StartsWith("pcm_", StringComparison.Ordinal) => "PCM",
        var x when x.StartsWith("wma", StringComparison.Ordinal) => "WMA",
        "subrip" => "SRT",
        "ass" or "ssa" => "ASS",
        "mov_text" => "Text",
        "hdmv_pgs_subtitle" => "PGS",
        "dvd_subtitle" => "VobSub",
        "dvb_subtitle" => "DVB",
        _ => c.ToUpperInvariant(),
    };

    private static string Channels(int ch) => ch switch { <= 0 => "", 1 => "Mono", 2 => "Stereo", 6 => "5.1", 8 => "7.1", _ => ch + "ch" };

    public static int AacKbit(int channels) => channels <= 1 ? 128 : channels <= 2 ? 192 : 384;

    public static StreamInfo? MainAudio(MediaInfo m) => m.Audio.FirstOrDefault(a => a.IsDefault) ?? m.Audio.FirstOrDefault();

    public static ConvertPlan Plan(MediaInfo m, ConvertTarget target, ConvertCaps caps, string sourcePath)
    {
        if (!caps.Available(target)) return ConvertPlan.Fail(L.T("Auf diesem PC nicht verfügbar (kein passender Encoder).", "Not available on this PC (no suitable encoder)."));
        if (IsAudioOnly(target)) return PlanAudioOnly(m, target);
        var v = m.Video;
        if (v is null) return ConvertPlan.Fail(L.T("Kein Video gefunden. Für reine Tondateien „Nur Ton“ wählen.", "No video found. For audio-only files choose “Audio only”."));

        var pre = new List<string>();
        var ext = System.IO.Path.GetExtension(sourcePath).ToLowerInvariant();
        if (ext is ".avi" or ".divx" or ".xvid") pre.AddRange(["-fflags", "+genpts"]);

        var mp4 = Extension(target) == ".mp4";
        var output = new List<string> { "-map", "0:" + I(v.Index) };
        var changes = new List<string>();
        var warnings = new List<string>();
        var encodes = false;

        // ---------------- Video ----------------
        var videoArgs = new List<string>();
        switch (target)
        {
            case ConvertTarget.RemuxMkv:
            case ConvertTarget.RemuxMp4:
                if (mp4 && !Mp4Video.Contains(v.CodecName))
                    return ConvertPlan.Fail(L.T($"Video {CodecName(v.CodecName)} passt nicht in MP4. Die Vorlage „Maximal kompatibel“ wandelt es um.",
                                                $"Video {CodecName(v.CodecName)} does not fit into MP4. The “Maximum compatibility” preset converts it."));
                videoArgs.AddRange(["-c:v", "copy"]);
                if (v.CodecName.Equals("mpeg4", StringComparison.OrdinalIgnoreCase)) videoArgs.AddRange(["-bsf:v", "mpeg4_unpack_bframes"]);
                if (mp4 && v.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase)) videoArgs.AddRange(["-tag:v", "hvc1"]);
                changes.Add(L.T($"Video kopiert ({CodecName(v.CodecName)})", $"video copied ({CodecName(v.CodecName)})"));
                break;
            case ConvertTarget.Apple when v.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase):
                videoArgs.AddRange(["-c:v", "copy", "-tag:v", "hvc1"]);
                changes.Add(L.T("Video kopiert (HEVC)", "video copied (HEVC)"));
                break;
            case ConvertTarget.Compatible when IsCompatibleH264(m):
                videoArgs.AddRange(["-c:v", "copy"]);
                changes.Add(L.T("Video kopiert (schon H.264)", "video copied (already H.264)"));
                break;
            default:
            {
                if (m.DolbyVisionWithoutBase)
                    return ConvertPlan.Fail(L.T("Dolby Vision ohne kompatible Basis: neu kodiert wären die Farben falsch.", "Dolby Vision without a compatible base layer: re-encoded, the colours would be wrong."));
                var (encoder, codec) = target switch
                {
                    ConvertTarget.Apple => (caps.Hevc!, "HEVC"),
                    ConvertTarget.Av1 => (caps.Av1!, "AV1"),
                    _ => (caps.H264!, "H.264"),
                };
                var dv = v.HasDolbyVision;
                if (dv && (codec == "H.264" ? !caps.DoviSafeH264 : !caps.DoviStrip))
                    return ConvertPlan.Fail(L.T("Dolby Vision: mit diesem ffmpeg nicht sauber umwandelbar (die DV-Kennung bliebe ohne DV-Daten stehen). Das integrierte ffmpeg kann es.",
                                                "Dolby Vision: cannot be converted cleanly with this ffmpeg (the DV flag would remain without DV data). The bundled ffmpeg can do it."));
                var (maxW, maxH) = target switch
                {
                    ConvertTarget.Mobile => (1280, 720),
                    ConvertTarget.Compatible => (1920, 1080),
                    _ => (0, 0),
                };
                if (v.DisplayHeight > v.DisplayWidth) (maxW, maxH) = (maxH, maxW);
                var hdr = m.Hdr != HdrKind.None;
                var tenBit = target == ConvertTarget.Av1 || (hdr && target == ConvertTarget.Apple && caps.Hevc10);
                var tonemap = hdr && !tenBit;
                var filters = new List<string>();
                if (v.IsInterlaced) { filters.Add("bwdif=mode=send_frame"); changes.Add(L.T("Halbbilder entfernt", "deinterlaced")); }
                if (maxW > 0 && (v.DisplayWidth > maxW || v.DisplayHeight > maxH))
                {
                    filters.Add($"scale={I(maxW)}:{I(maxH)}:force_original_aspect_ratio=decrease:force_divisible_by=2");
                    changes.Add(L.T($"verkleinert auf höchstens {Math.Min(maxW, maxH)}p", $"scaled down to at most {Math.Min(maxW, maxH)}p"));
                }
                if (tonemap)
                {
                    if (caps.Tonemap)
                    {
                        filters.Add("zscale=t=linear:npl=100,format=gbrpf32le,zscale=p=bt709,tonemap=tonemap=hable:desat=0,zscale=t=bt709:m=bt709:r=tv");
                        changes.Add(L.T("HDR → SDR umgerechnet", "HDR → SDR converted"));
                    }
                    else warnings.Add(L.T("HDR → SDR ohne Farbumrechnung (Farben wirken blass)", "HDR → SDR without colour conversion (colours look washed out)"));
                }
                else if (hdr && m.Hdr == HdrKind.DolbyVision) warnings.Add(L.T("Dolby Vision entfällt, HDR10 bleibt", "Dolby Vision is dropped, HDR10 stays"));
                var pix = PixFmt(encoder, tenBit);
                filters.Add("format=" + pix);
                videoArgs.AddRange(["-vf", string.Join(",", filters)]);
                var (outW, outH) = maxW > 0 ? HevcLevel.Fit(v.DisplayWidth, v.DisplayHeight, maxW, maxH) : (v.DisplayWidth, v.DisplayHeight);
                var level = NeedsLevelLabel(encoder) ? HevcLevel.For(outW, outH, HevcLevel.FpsFor(v)) : null;
                videoArgs.AddRange(VideoEncoderArgs(encoder, target, tenBit, level));
                videoArgs.AddRange(["-pix_fmt", pix]);
                if (tonemap) videoArgs.AddRange(["-color_primaries", "bt709", "-color_trc", "bt709", "-colorspace", "bt709"]);
                if (mp4 && codec == "HEVC") videoArgs.AddRange(["-tag:v", "hvc1"]);
                if (VideoBitstreamFilters(encoder, dv && codec != "H.264", level) is { } bsf) videoArgs.AddRange(["-bsf:v", bsf]);
                var cpu = ConvertCaps.IsCpu(encoder) ? L.T(", Prozessor", ", CPU") : "";
                changes.Insert(0, $"Video → {codec}{(tenBit ? " 10 Bit" : "")}{cpu}");
                encodes = true;
                break;
            }
        }

        // ---------------- Ton ----------------
        var audioIn = target == ConvertTarget.Mobile ? (MainAudio(m) is { } main ? [main] : []) : m.Audio.ToList();
        if (target == ConvertTarget.Mobile && m.Audio.Count() > 1) changes.Add(L.T($"nur Tonspur „{TrackName(audioIn[0])}“", $"only audio track “{TrackName(audioIn[0])}”"));
        var audioArgs = new List<string>();
        var converted = new List<string>();
        for (var i = 0; i < audioIn.Count; i++)
        {
            var a = audioIn[i];
            output.AddRange(["-map", "0:" + I(a.Index)]);
            if (!mp4 && MkvAudioToFlac.Contains(a.CodecName))
            {
                audioArgs.AddRange([$"-c:a:{I(i)}", "flac"]);
                converted.Add($"{CodecName(a.CodecName)} → FLAC");
                continue;
            }
            var copy = target switch
            {
                ConvertTarget.RemuxMkv or ConvertTarget.Av1 => true,
                ConvertTarget.RemuxMp4 => Mp4Audio.Contains(a.CodecName),
                ConvertTarget.Apple => a.CodecName is "aac" or "ac3" or "eac3" or "alac" && a.Channels <= 8,
                ConvertTarget.Compatible => a.CodecName == "aac" && a.Channels <= 6,
                _ => a.CodecName == "aac" && a.Channels <= 2,
            };
            if (copy) { audioArgs.AddRange([$"-c:a:{I(i)}", "copy"]); continue; }
            var ch = target == ConvertTarget.Mobile ? Math.Min(2, Math.Max(1, a.Channels)) : Math.Min(6, Math.Max(1, a.Channels));
            if (ch is 3 or 4 or 5) ch = 6;
            if (a.Channels <= 0) ch = 2;
            audioArgs.AddRange([$"-c:a:{I(i)}", "aac", $"-b:a:{I(i)}", I(AacKbit(ch)) + "k"]);
            if (ch != a.Channels) audioArgs.AddRange([$"-ac:a:{I(i)}", I(ch)]);
            converted.Add($"{CodecName(a.CodecName)} → AAC{(ch != a.Channels ? " " + Channels(ch) : "")}");
            foreach (var tag in ArgsBuilder.StatsTags.Take(4)) audioArgs.AddRange([$"-metadata:s:a:{I(i)}", tag + "="]);
        }
        if (converted.Count > 0) changes.Add(L.T("Ton ", "audio ") + string.Join(", ", converted.Distinct()));
        else if (audioIn.Count > 0) changes.Add(L.T("Ton kopiert", "audio copied"));

        // ---------------- Untertitel ----------------
        var subArgs = new List<string>();
        var subOut = 0;
        int droppedImage = 0, droppedOther = 0;
        foreach (var s in m.Subtitles)
        {
            string? codec;
            if (mp4) codec = TextSubs.Contains(s.CodecName) ? "mov_text" : null;
            else codec = MkvSubs.Contains(s.CodecName) ? "copy" : TextSubs.Contains(s.CodecName) ? "srt" : null;
            if (codec is null)
            {
                if (mp4 && s.IsImageSubtitle) droppedImage++;
                else droppedOther++;
                continue;
            }
            output.AddRange(["-map", "0:" + I(s.Index)]);
            subArgs.AddRange([$"-c:s:{I(subOut)}", codec]);
            subOut++;
        }
        if (droppedImage > 0)
            warnings.Add(L.T($"{droppedImage} Bild-Untertitel entfallen (MP4 kann keine PGS/VobSub)", $"{droppedImage} image subtitles are dropped (MP4 cannot hold PGS/VobSub)"));
        if (droppedOther > 0)
            warnings.Add(L.T($"{droppedOther} Untertitel entfallen (Format nicht übertragbar, z.B. Videotext)", $"{droppedOther} subtitles are dropped (format cannot be carried over, e.g. teletext)"));

        // ---------------- Anhaenge (Schriftarten) ----------------
        var attach = m.Attachments.ToList();
        if (attach.Count > 0)
        {
            if (mp4) warnings.Add(L.T($"{attach.Count} Anhänge entfallen (z.B. Schriftarten)", $"{attach.Count} attachments are dropped (e.g. fonts)"));
            else foreach (var t in attach) output.AddRange(["-map", "0:" + I(t.Index)]);
        }

        output.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        output.AddRange(videoArgs);
        if (encodes && !mp4) foreach (var tag in ArgsBuilder.StatsTags) output.AddRange(["-metadata:s:v:0", tag + "="]);
        output.AddRange(audioArgs);
        output.AddRange(subArgs);
        if (!mp4 && attach.Count > 0) output.AddRange(["-c:t", "copy"]);
        if (mp4) output.AddRange(["-movflags", "+faststart"]);
        output.AddRange(["-max_muxing_queue_size", "9999"]);

        return new ConvertPlan
        {
            Extension = Extension(target), PreInput = pre, Output = output, Changes = changes, Warnings = warnings,
            HasVideo = true, AudioCount = audioIn.Count, Encodes = encodes || converted.Count > 0,
        };
    }

    public static bool IsCompatibleH264(MediaInfo m) => m.Video is { } v
        && v.CodecName.Equals("h264", StringComparison.OrdinalIgnoreCase)
        && v.PixFmt is "yuv420p" or "yuvj420p"
        && v.DisplayWidth <= 1920 && v.DisplayHeight <= 1080
        && !v.IsInterlaced && m.Hdr == HdrKind.None;

    private static ConvertPlan PlanAudioOnly(MediaInfo m, ConvertTarget target)
    {
        var a = MainAudio(m);
        if (a is null) return ConvertPlan.Fail(L.T("Keine Tonspur gefunden.", "No audio track found."));
        var output = new List<string> { "-map", "0:" + I(a.Index), "-vn", "-sn", "-dn", "-map_metadata", "0" };
        var changes = new List<string>();
        var ch = a.Channels <= 0 ? 2 : a.Channels;
        switch (target)
        {
            case ConvertTarget.AudioMp3:
                output.AddRange(["-c:a", "libmp3lame", "-q:a", "2"]);
                if (ch > 2) output.AddRange(["-ac", "2"]);
                changes.Add($"{CodecName(a.CodecName)} → MP3{(ch > 2 ? " Stereo" : "")}");
                break;
            case ConvertTarget.AudioM4a:
                if (a.CodecName == "aac" && ch <= 2) { output.AddRange(["-c:a", "copy"]); changes.Add(L.T("AAC kopiert", "AAC copied")); }
                else
                {
                    output.AddRange(["-c:a", "aac", "-b:a", "192k"]);
                    if (ch > 2) output.AddRange(["-ac", "2"]);
                    changes.Add($"{CodecName(a.CodecName)} → AAC{(ch > 2 ? " Stereo" : "")}");
                }
                output.AddRange(["-movflags", "+faststart", "-map_chapters", "0"]);
                break;
            default:
                if (a.CodecName == "flac") { output.AddRange(["-c:a", "copy"]); changes.Add(L.T("FLAC kopiert", "FLAC copied")); }
                else
                {
                    output.AddRange(["-c:a", "flac"]);
                    changes.Add($"{CodecName(a.CodecName)} → FLAC");
                }
                break;
        }
        var count = m.Audio.Count();
        if (count > 1) changes.Add(L.T($"Tonspur „{TrackName(a)}“ von {count}", $"audio track “{TrackName(a)}” of {count}"));
        return new ConvertPlan
        {
            Extension = Extension(target), Output = output, Changes = changes,
            HasVideo = false, AudioCount = 1, Encodes = !output.Contains("copy"),
        };
    }

    public static string TrackName(StreamInfo a)
    {
        var lang = Languages.Display(a.Language);
        if (a.Title.Length > 0) return a.Title;
        return lang.Length > 0 ? $"{lang}, {CodecName(a.CodecName)}" : CodecName(a.CodecName);
    }

    public static string PixFmt(string encoder, bool tenBit) => encoder switch
    {
        "libx264" => "yuv420p",
        "libx265" or "libsvtav1" => tenBit ? "yuv420p10le" : "yuv420p",
        _ when encoder.EndsWith("_amf", StringComparison.Ordinal) => "nv12",
        _ when encoder.EndsWith("_qsv", StringComparison.Ordinal) => tenBit ? "p010le" : "nv12",
        _ => tenBit ? "p010le" : "yuv420p",
    };

    public static int QualityFor(ConvertTarget t, string encoder) => (t, encoder) switch
    {
        (ConvertTarget.Mobile, _) => 25,
        (ConvertTarget.Apple, "libx265") => 23,
        (ConvertTarget.Apple, _) => 24,
        (ConvertTarget.Av1, "libsvtav1") => 32,
        (ConvertTarget.Av1, _) => 32,
        _ => 21,
    };

    public static bool NeedsLevelLabel(string encoder) => encoder is "hevc_nvenc" or "hevc_amf";

    public static string? VideoBitstreamFilters(string encoder, bool stripDolbyVision, HevcLevel.Level? level)
    {
        var chain = new List<string>();
        if (stripDolbyVision) chain.Add("dovi_rpu=strip=1");
        if (level is not null && NeedsLevelLabel(encoder)) chain.Add("hevc_metadata=level=" + I(level.Idc));
        return chain.Count == 0 ? null : string.Join(",", chain);
    }

    public static List<string> VideoEncoderArgs(string encoder, ConvertTarget target, bool tenBit, HevcLevel.Level? level = null)
    {
        var q = QualityFor(target, encoder);
        var hevc = encoder.StartsWith("hevc", StringComparison.Ordinal) || encoder == "libx265";
        var av1 = encoder.StartsWith("av1", StringComparison.Ordinal) || encoder == "libsvtav1";
        var profile = av1 ? null : hevc ? (tenBit ? "main10" : "main") : "high";
        var a = new List<string> { "-c:v", encoder };
        switch (encoder)
        {
            case "libx264":
                a.AddRange(["-preset", "medium", "-crf", I(q)]);
                break;
            case "libx265":
                a.AddRange(["-preset", "medium", "-crf", I(q), "-x265-params", "log-level=error"]);
                break;
            case "libsvtav1":
                a.AddRange(["-preset", "8", "-crf", I(q)]);
                break;
            case var e when e.EndsWith("_nvenc", StringComparison.Ordinal):
                a.AddRange(["-preset", "p5", "-tune", "hq", "-rc", "vbr", "-cq", I(q), "-b:v", "0", "-spatial-aq", "1"]);
                break;
            case var e when e.EndsWith("_qsv", StringComparison.Ordinal):
                a.AddRange(["-preset", "medium", "-global_quality", I(q)]);
                break;
            case var e when e.EndsWith("_amf", StringComparison.Ordinal):
                a.AddRange(["-usage", "transcoding", "-quality", "quality", "-rc", "cqp", "-qp_i", I(q - 1), "-qp_p", I(q + 1)]);
                if (hevc) a.AddRange(["-bf", "0"]);
                if (hevc && level is not null) a.AddRange(["-level:v", level.Name]);
                break;
        }
        if (profile is not null) a.AddRange(["-profile:v", profile]);
        return a;
    }

    private static readonly HashSet<char> InvalidNameChars = ['\\', '/', ':', '*', '?', '"', '<', '>', '|'];

    public static string OutputPath(string source, string? baseDir, string? outputDir, bool keepStructure, ConvertTarget target, string suffix)
    {
        var name = System.IO.Path.GetFileNameWithoutExtension(source);
        var dir = System.IO.Path.GetDirectoryName(source) ?? "";
        if (!string.IsNullOrWhiteSpace(outputDir))
        {
            dir = outputDir;
            if (keepStructure && !string.IsNullOrEmpty(baseDir))
            {
                var rel = System.IO.Path.GetRelativePath(baseDir, System.IO.Path.GetDirectoryName(source) ?? baseDir);
                if (rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(rel)) dir = System.IO.Path.Combine(outputDir, rel);
            }
        }
        suffix = new string((suffix ?? "").Where(c => !InvalidNameChars.Contains(c) && !char.IsControl(c)).ToArray()).Trim().TrimEnd('.');
        var file = name + (suffix.Length > 0 ? " " + suffix : "") + Extension(target);
        var path = System.IO.Path.Combine(dir, file);
        if (string.Equals(System.IO.Path.GetFullPath(path), System.IO.Path.GetFullPath(source), StringComparison.OrdinalIgnoreCase))
            path = System.IO.Path.Combine(dir, $"{name} ({Tag(target)}){Extension(target)}");
        return path;
    }
}
