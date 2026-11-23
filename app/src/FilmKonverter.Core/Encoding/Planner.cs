using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Encoding;

public enum SkipReason
{
    None,
    NoVideo,
    RateTooLow,
    RateUnknown,
    Av1,
    Hevc,
    Hdr,
    PixelFormat,
    DolbyVision,
}

public sealed record EncodePlan(
    StreamInfo Video,
    string? VideoFilter,
    int BitDepth,
    string PixFmtOut,
    string CodecProfile,
    AudioPlan Audio,
    string ScaleText,
    string RateText,
    SubtitlePlan? Subtitles = null,
    IReadOnlyList<int>? Attachments = null,
    double OutputFps = 0,
    bool VideoCopy = false)
{
    public SubtitlePlan Subs => Subtitles ?? SubtitlePlan.Empty;

    public HevcLevel.Level? FixLevel { get; init; }

    public (int W, int H) OutputSize(Settings.Profile p) => p.LimitsResolution
        ? HevcLevel.Fit(Video.DisplayWidth, Video.DisplayHeight, p.MaxWidth, p.MaxHeight)
        : (Video.DisplayWidth, Video.DisplayHeight);

    public HevcLevel.Level TargetLevel(Settings.Profile p)
    {
        var (w, h) = OutputSize(p);
        return HevcLevel.For(w, h, OutputFps > 0 ? OutputFps : HevcLevel.FpsFor(Video));
    }

    public IReadOnlyList<int> ExtraVideo { get; init; } = [];

    public IReadOnlyList<CoverFile> Covers { get; init; } = [];

    public IReadOnlyList<int> AttachmentIndexes => Attachments ?? [];
}

public sealed record CoverFile(string File, string MimeType, string FileName, string Title = "");

public sealed record Decision(SkipReason Skip, string Message, EncodePlan? Plan)
{
    public bool Encode => Plan is not null;
}

public static class Planner
{
    public const string ScaleFilterTemplate =
        "scale={0}:{1}:force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos";

    public static string DeinterlaceFilter(bool all) => $"bwdif=mode=send_frame:parity=auto:deint={(all ? "all" : "interlaced")}";

    public static Decision Decide(MediaInfo media, Profile profile)
    {
        var v = media.Video;
        if (v is null) return new(SkipReason.NoVideo, L.T("Kein Videostream.", "No video stream."), null);

        var audio = AudioPlanner.Plan(media, profile);
        var (videoKbit, rateSource) = media.VideoRate;
        var totalKbit = media.TotalRateKbit;
        var rateText = RateText(videoKbit, rateSource, totalKbit);

        if (profile.MinRateMbit > 0)
        {
            var rate = profile.RateBasis == RateBasis.Total ? totalKbit : videoKbit;
            var rateOk = rate is double r && r > profile.MinRateMbit * 1000d;
            if (!rateOk && audio.Converted > 0 && profile.AudioOnlyForSkipped)
                return SkipOrAudioOnly(SkipReason.RateTooLow, L.T($"{rateText}: Video nicht über {profile.MinRateMbit} Mbit/s.", $"{rateText}: video not above {profile.MinRateMbit} Mbit/s."));
            var pass = rateOk || audio.Converted > 0;
            if (!pass)
            {
                var basis = profile.RateBasis == RateBasis.Total ? L.T("gesamt", "total") : "Video";
                return rate is null
                    ? new(SkipReason.RateUnknown, L.T("Datenrate unbekannt, kein Ton zum Umwandeln.", "Bitrate unknown, no audio to convert."), null)
                    : new(SkipReason.RateTooLow, L.T($"{rateText}: {basis} nicht über {profile.MinRateMbit} Mbit/s, kein Ton zum Umwandeln.",
                                                     $"{rateText}: {basis} not above {profile.MinRateMbit} Mbit/s, no audio to convert."), null);
            }
        }

        Decision SkipOrAudioOnly(SkipReason reason, string message)
        {
            if (profile.AudioOnlyForSkipped && (audio.Converted > 0 || audio.Removed.Count > 0))
            {
                var depth = v.PixFmt.Contains("10", StringComparison.Ordinal) ? 10 : 8;
                return new(SkipReason.None, message, new EncodePlan(v, null, depth, v.PixFmt, "",
                    audio, L.T("Video unverändert kopiert", "video copied unchanged"), rateText,
                    AudioPlanner.PlanSubtitles(media, profile), AudioPlanner.PlanAttachments(media, profile), media.Fps, VideoCopy: true)
                    { FixLevel = HevcLevel.FixFor(media) });
            }
            return new(reason, message, null);
        }

        if (profile.SkipAv1 && v.CodecName.Equals("av1", StringComparison.OrdinalIgnoreCase))
            return SkipOrAudioOnly(SkipReason.Av1, L.T("Video ist bereits AV1.", "Video is already AV1."));

        if (profile.SkipHevc && v.CodecName.Equals("hevc", StringComparison.OrdinalIgnoreCase))
            return SkipOrAudioOnly(SkipReason.Hevc, L.T("Video ist bereits HEVC.", "Video is already HEVC."));

        if (media.DolbyVisionWithoutBase)
            return SkipOrAudioOnly(SkipReason.DolbyVision, L.T("Dolby Vision Profil 5 hat keine HDR10-Basis: ohne DV-Daten wären die Farben falsch.",
                                                   "Dolby Vision profile 5 has no HDR10 base layer: without DV data the colours would be wrong."));

        var hdr = media.Hdr;
        if (profile.EffectiveSkipHdr && hdr != HdrKind.None)
            return SkipOrAudioOnly(SkipReason.Hdr, $"{MediaInfo.HdrText(hdr)}: " + (profile.Encoder == EncoderKind.Amf
                ? L.T("AMD kann HDR nicht erhalten.", "AMD cannot keep HDR.")
                : L.T("HDR wird übersprungen.", "HDR is skipped.")));

        int depth;
        string pixOut;
        string codecProfile;
        if (profile.Encoder == EncoderKind.Amf)
        {
            depth = 8;
            pixOut = "nv12";
            codecProfile = "main";
        }
        else
        {
            switch (v.PixFmt.ToLowerInvariant())
            {
                case "yuv420p":
                case "yuvj420p":
                    depth = 8;
                    break;
                case "yuv420p10le":
                case "p010le":
                    depth = 10;
                    break;
                default:
                    return SkipOrAudioOnly(SkipReason.PixelFormat, L.T($"Pixelformat nicht unterstützt: {v.PixFmt}", $"Pixel format not supported: {v.PixFmt}"));
            }
            if (profile.TenBit) depth = 10;
            pixOut = depth == 10
                ? (profile.Encoder == EncoderKind.Cpu ? "yuv420p10le" : "p010le")
                : (profile.Encoder == EncoderKind.Qsv ? "nv12" : "yuv420p");
            codecProfile = depth == 10 ? "main10" : "main";
        }

        var filters = new List<string>();
        var notes = new List<string>();
        var deint = profile.Deinterlace == DeinterlaceMode.Always || (profile.Deinterlace == DeinterlaceMode.Auto && v.IsInterlaced);
        if (deint)
        {
            filters.Add(DeinterlaceFilter(profile.Deinterlace == DeinterlaceMode.Always));
            notes.Add(L.T("Deinterlacing (bwdif)", "deinterlacing (bwdif)"));
        }
        var srcFps = media.Fps;
        var outFps = srcFps;
        if (profile.MaxFps > 0 && srcFps > profile.MaxFps + 0.5)
        {
            filters.Add("fps=" + profile.MaxFps.ToString(Fmt.Inv));
            outFps = profile.MaxFps;
            notes.Add(L.T($"{srcFps.ToString("0.###", Fmt.De)} → {profile.MaxFps} fps", $"{srcFps.ToString("0.###", Fmt.De)} → {profile.MaxFps} fps"));
        }
        if (profile.LimitsResolution && (v.DisplayWidth > profile.MaxWidth || v.DisplayHeight > profile.MaxHeight))
        {
            filters.Add(string.Format(Fmt.Inv, ScaleFilterTemplate, profile.MaxWidth, profile.MaxHeight));
            notes.Add(L.T($"skaliert auf max. {profile.MaxWidth}x{profile.MaxHeight}, Lanczos", $"scaled to max. {profile.MaxWidth}x{profile.MaxHeight}, Lanczos"));
        }
        if (profile.Encoder == EncoderKind.Amf) filters.Add("format=nv12");
        var filter = filters.Count == 0 ? null : string.Join(",", filters);
        var scaleText = notes.Count == 0 ? L.T("keine Skalierung", "no scaling") : string.Join(" · ", notes);

        return new(SkipReason.None, "", new EncodePlan(v, filter, depth, pixOut, codecProfile, audio, scaleText, rateText,
            AudioPlanner.PlanSubtitles(media, profile), AudioPlanner.PlanAttachments(media, profile), outFps));
    }

    public static string RateText(double? videoKbit, string source, double? totalKbit)
    {
        var v = videoKbit is double vk ? (source == "geschaetzt" ? L.T("ca. ", "approx. ") : "") + Fmt.Mbit(vk) : "?";
        var t = totalKbit is double tk ? Fmt.Mbit(tk) : "?";
        return L.T($"Video {v} / gesamt {t} Mbit/s", $"video {v} / total {t} Mbit/s");
    }
}
