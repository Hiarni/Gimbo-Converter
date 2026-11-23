using System.Globalization;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Encoding;

public static class ArgsBuilder
{
    private static string I(int v) => v.ToString(CultureInfo.InvariantCulture);

    public static readonly string[] StatsTags =
    [
        "BPS", "BPS-eng", "NUMBER_OF_BYTES", "NUMBER_OF_BYTES-eng", "NUMBER_OF_FRAMES", "NUMBER_OF_FRAMES-eng",
        "_STATISTICS_TAGS", "_STATISTICS_TAGS-eng", "_STATISTICS_WRITING_APP", "_STATISTICS_WRITING_APP-eng",
        "_STATISTICS_WRITING_DATE_UTC", "_STATISTICS_WRITING_DATE_UTC-eng",
    ];

    public static List<string> BuildEncode(Profile p, EncodePlan plan, string input, string output, IEnumerable<string>? preInput = null)
    {
        var a = new List<string> { "-hide_banner", "-y" };
        if (p.Encoder == EncoderKind.Amf)
            a.AddRange(["-analyzeduration", "100M", "-probesize", "100M"]);
        if (p.HardwareDecode && !plan.VideoCopy) a.AddRange(["-hwaccel", "auto"]);
        if (preInput is not null) a.AddRange(preInput);
        a.AddRange(["-i", input]);

        a.AddRange(["-map", "0:" + I(plan.Video.Index)]);
        foreach (var t in plan.ExtraVideo) a.AddRange(["-map", "0:" + I(t)]);
        if (plan.Audio.AllMapped) a.AddRange(["-map", "0:a?"]);
        else foreach (var t in plan.Audio.Tracks) a.AddRange(["-map", "0:" + I(t.SourceIndex)]);
        if (plan.Subs.AllMapped) a.AddRange(["-map", "0:s?"]);
        else foreach (var t in plan.Subs.Tracks) a.AddRange(["-map", "0:" + I(t.SourceIndex)]);
        foreach (var t in plan.AttachmentIndexes) a.AddRange(["-map", "0:" + I(t)]);
        a.AddRange(["-map_metadata", "0", "-map_chapters", "0"]);
        if (plan.VideoFilter is not null && !plan.VideoCopy) a.AddRange(["-vf", plan.VideoFilter]);

        var bsf = new List<string>();
        if (!plan.VideoCopy && plan.Video.HasDolbyVision) bsf.Add("dovi_rpu=strip=1");

        switch (plan.VideoCopy ? (EncoderKind)(-1) : p.Encoder)
        {
            case (EncoderKind)(-1):
                a.AddRange(["-c:v", "copy"]);
                if (plan.FixLevel is { } fix) bsf.Add("hevc_metadata=level=" + I(fix.Idc));
                break;
            case EncoderKind.Nvenc:
                a.AddRange([
                    "-c:v", "hevc_nvenc",
                    "-preset", p.NvencPreset,
                    "-tune", "hq",
                    "-rc", "vbr",
                    "-cq", I(p.Quality),
                    "-b:v", "0",
                    "-profile:v", plan.CodecProfile,
                    "-pix_fmt", plan.PixFmtOut,
                    "-spatial-aq", "1",
                ]);
                if (!p.NvencLegacy) a.AddRange(["-temporal-aq", "1"]);
                a.AddRange(["-aq-strength", I(p.AqStrength), "-rc-lookahead", I(p.Lookahead)]);
                a.AddRange(p.NvencLegacy ? ["-bf", "0"] : ["-bf", "3", "-b_ref_mode", "middle"]);
                bsf.Add(LevelLabel(p, plan));
                break;
            case EncoderKind.Amf:
                a.AddRange([
                    "-c:v", "hevc_amf",
                    "-usage", "transcoding",
                    "-quality", "quality",
                    "-rc", "cqp",
                    "-qp_i", I(p.QpI),
                    "-qp_p", I(p.QpP),
                    "-profile:v", "main",
                    "-pix_fmt", "nv12",
                    "-bf", "0",
                    "-level:v", plan.TargetLevel(p).Name,
                ]);
                bsf.Add(LevelLabel(p, plan));
                break;
            case EncoderKind.Qsv:
                a.AddRange([
                    "-c:v", "hevc_qsv",
                    "-preset", p.QsvPreset,
                    "-global_quality", I(p.Quality),
                    "-profile:v", plan.CodecProfile,
                    "-pix_fmt", plan.PixFmtOut,
                ]);
                break;
            default:
                a.AddRange([
                    "-c:v", "libx265",
                    "-preset", p.CpuPreset,
                    "-crf", I(p.Quality),
                    "-profile:v", plan.CodecProfile,
                    "-pix_fmt", plan.PixFmtOut,
                    "-x265-params", "log-level=error",
                ]);
                break;
        }

        if (bsf.Count > 0) a.AddRange([plan.ExtraVideo.Count > 0 ? "-bsf:v:0" : "-bsf:v", string.Join(",", bsf)]);

        if (!plan.VideoCopy) foreach (var tag in StatsTags) a.AddRange(["-metadata:s:v:0", tag + "="]);
        foreach (var t in plan.Audio.Tracks.Where(t => t.Convert))
            foreach (var tag in StatsTags.Take(4)) a.AddRange([$"-metadata:s:a:{t.OutputIndex}", tag + "="]);

        a.AddRange(["-c:a", "copy"]);
        a.AddRange(plan.Audio.Args());
        a.AddRange(["-c:s", "copy"]);
        a.AddRange(plan.Subs.Args());
        if (plan.AttachmentIndexes.Count > 0 || plan.Covers.Count > 0) a.AddRange(["-c:t", "copy"]);
        for (var k = 0; k < plan.Covers.Count; k++)
        {
            var c = plan.Covers[k];
            var spec = "-metadata:s:t:" + I(plan.AttachmentIndexes.Count + k);
            a.AddRange(["-attach", c.File, spec, "mimetype=" + c.MimeType, spec, "filename=" + c.FileName]);
            if (c.Title.Length > 0) a.AddRange([spec, "title=" + c.Title]);
        }
        a.AddRange(["-max_muxing_queue_size", "9999", output]);
        return a;
    }

    private static string LevelLabel(Profile p, EncodePlan plan) => "hevc_metadata=level=" + I(plan.TargetLevel(p).Idc);

    public static List<string> DecodeErrors(IEnumerable<string> lines) =>
        lines.Where(l => !l.TrimStart().StartsWith("[null @", StringComparison.Ordinal)).ToList();

    public static List<string> BuildDecodeTest(string file, bool full, string nullTarget)
    {
        var a = new List<string> { "-hide_banner", "-v", "error", "-i", file, "-map", "0:v:0" };
        if (!full) a.AddRange(["-frames:v", "10"]);
        a.AddRange(["-f", "null", nullTarget]);
        return a;
    }
}
