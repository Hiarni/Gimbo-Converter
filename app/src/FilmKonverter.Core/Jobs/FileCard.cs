using System.Text.RegularExpressions;
using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public static partial class FileCard
{
    public const string Sep = " · ";

    public static string Codec(string? codec) => (codec ?? "").ToLowerInvariant() switch
    {
        "" => "?",
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "mpeg2video" => "MPEG-2",
        "mpeg1video" => "MPEG-1",
        "mpeg4" => "MPEG-4",
        "msmpeg4v3" or "msmpeg4v2" => "DivX",
        "vc1" => "VC-1",
        "wmv3" or "wmv2" => "WMV",
        var c => c.ToUpperInvariant(),
    };

    [GeneratedRegex(@"p0?(?<b>1[0-6]|9)(le|be)?$")]
    private static partial Regex DepthRx();

    public static int BitDepth(string? pixFmt)
    {
        var m = DepthRx().Match(pixFmt ?? "");
        return m.Success ? int.Parse(m.Groups["b"].Value) : 8;
    }

    public static string Chroma(string? pixFmt)
    {
        var p = pixFmt ?? "";
        if (p.Contains("444")) return "4:4:4";
        if (p.Contains("422")) return "4:2:2";
        return "";
    }

    public static string Resolution(int width, int height, bool interlaced = false)
    {
        if (width <= 0 || height <= 0) return "?";
        var cls = LibraryAnalyzer.ResolutionClass(Settings.Profile.ClassHeight(width, height));
        if (interlaced && cls.EndsWith('p')) cls = cls[..^1] + "i";
        return $"{width}×{height} ({cls})";
    }

    public static string Depth(string? pixFmt, HdrKind hdr)
    {
        var parts = new List<string> { $"{BitDepth(pixFmt)} Bit" };
        if (Chroma(pixFmt) is { Length: > 0 } c) parts.Add(c);
        if (hdr != HdrKind.None) parts.Add(MediaInfo.HdrText(hdr));
        return string.Join(Sep, parts);
    }

    public static string Source(MediaInfo media, long bytes)
    {
        var v = media.Video;
        if (v is null) return L.T("kein Video", "no video") + Sep + Fmt.Size(bytes);
        var parts = new List<string>
        {
            Codec(v.CodecName),
            Resolution(v.DisplayWidth, v.DisplayHeight, v.IsInterlaced),
            Depth(v.PixFmt, media.Hdr),
        };
        if (media.Fps > 0) parts.Add(media.Fps.ToString("0.###", Fmt.De) + " fps");
        if (media.DurationSeconds > 0) parts.Add(Fmt.Time(media.DurationSeconds));
        parts.Add(Fmt.Size(bytes));
        return string.Join(Sep, parts);
    }

    public static string Rate(MediaInfo media)
    {
        var (video, how) = media.VideoRate;
        var total = media.TotalRateKbit;
        if (video is null && total is null) return "";
        var parts = new List<string>();
        if (video is double vk) parts.Add(L.T("Video ", "video ") + (how == "geschaetzt" ? L.T("ca. ", "approx. ") : "") + Fmt.Mbit(vk) + " Mbit/s");
        if (total is double tk) parts.Add(L.T("gesamt ", "total ") + Fmt.Mbit(tk) + " Mbit/s");
        return string.Join(Sep, parts);
    }

    public static string Target(string codecProfile, int bitDepth, string quality, int srcW, int srcH, int outW, int outH)
    {
        var size = srcW == outW && srcH == outH
            ? L.T($"{outW}×{outH}, Größe bleibt", $"{outW}×{outH}, size kept")
            : $"{srcW}×{srcH} → {outW}×{outH}";
        return string.Join(Sep, $"HEVC {codecProfile}", $"{bitDepth} Bit", quality, size);
    }

    public static string Output(MediaInfo info, long bytes)
    {
        var v = info.Video;
        if (v is null) return L.T("kein Video", "no video");
        var parts = new List<string> { Codec(v.CodecName), Resolution(v.DisplayWidth, v.DisplayHeight), Depth(v.PixFmt, info.Hdr) };
        if (bytes > 0) parts.Add(Fmt.Size(bytes));
        return string.Join(Sep, parts);
    }

    public static string Audio(AudioPlan plan) => plan.Changed
        ? plan.Text
        : plan.Tracks.Count == 0
            ? L.T("kein Ton", "no audio")
            : string.Join(" | ", plan.Tracks.Select(t => t.SourceText)) + Sep + L.T("unverändert", "unchanged");

    public static string Subtitles(SubtitlePlan plan)
    {
        if (plan.Changed) return plan.Text;
        if (plan.Tracks.Count == 0) return "";
        var forced = plan.Tracks.Count(t => t.Forced);
        var list = plan.Tracks.Count <= 4
            ? string.Join(" | ", plan.Tracks.Select(t => t.Text))
            : L.T($"{plan.Tracks.Count} Spuren", $"{plan.Tracks.Count} tracks") + ": " + string.Join(", ", plan.Tracks.Select(t => Lang(t.SourceText)).Where(l => l.Length > 0).Distinct())
              + (forced > 0 ? L.T($" ({forced} erzwungen)", $" ({forced} forced)") : "");
        return list + Sep + L.T("unverändert", "unchanged");
    }

    private static string Lang(string sourceText)
    {
        var i = sourceText.LastIndexOf(' ');
        return i > 0 ? sourceText[..i] : "";
    }

    public static string Size(long srcBytes, long newBytes, long? oldBytes = null)
    {
        var change = srcBytes > 0 ? (newBytes - srcBytes) * 100d / srcBytes : 0;
        var text = $"{Fmt.Size(srcBytes)} » {Fmt.Size(newBytes)}{Sep}{change.ToString("+0.0;-0.0", Fmt.De)} %";
        if (newBytes < srcBytes) text += Sep + L.T($"{Fmt.Size(srcBytes - newBytes)} gespart", $"{Fmt.Size(srcBytes - newBytes)} saved");
        if (oldBytes is long o) text += Sep + L.T($"vorher {Fmt.Size(o)}", $"before {Fmt.Size(o)}");
        return text;
    }

    public static string Estimate(long estimated, long source)
    {
        if (estimated <= 0) return "";
        var text = "~" + Fmt.Size(estimated);
        if (source > 0)
        {
            var change = (estimated - source) * 100d / source;
            text += L.T($" von {Fmt.Size(source)}", $" of {Fmt.Size(source)}") + Sep + change.ToString("+0;-0", Fmt.De) + " %";
        }
        return text;
    }
}
