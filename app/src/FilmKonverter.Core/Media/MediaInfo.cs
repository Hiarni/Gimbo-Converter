using System.Text.Json;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Media;

public sealed class StreamInfo
{
    public int Index { get; init; }
    public string CodecType { get; init; } = "";
    public string CodecName { get; init; } = "";
    public string Profile { get; init; } = "";
    public string PixFmt { get; init; } = "";
    public int Level { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public string AvgFrameRate { get; init; } = "";
    public string RFrameRate { get; init; } = "";
    public string ColorTransfer { get; init; } = "";
    public string FieldOrder { get; init; } = "";
    public bool IsInterlaced => FieldOrder is "tt" or "bb" or "tb" or "bt";
    public long BitRate { get; init; }
    public int Channels { get; init; }
    public bool AttachedPic { get; init; }
    public Dictionary<string, string> Tags { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public List<string> SideDataTypes { get; init; } = [];

    public int Rotation { get; init; }

    public bool IsRotated90 => Math.Abs(Rotation) % 180 == 90;
    public int DisplayWidth => IsRotated90 ? Height : Width;
    public int DisplayHeight => IsRotated90 ? Width : Height;

    public int DvProfile { get; init; } = -1;
    public int DvCompatibility { get; init; } = -1;

    public double? KnownRateKbit
    {
        get
        {
            if (BitRate > 0) return BitRate / 1000d;
            foreach (var (k, v) in Tags)
            {
                if (k.Equals("BPS", StringComparison.OrdinalIgnoreCase) || k.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase))
                {
                    var r = Fmt.ParseDouble(v, 0);
                    if (r > 0) return r / 1000d;
                }
            }
            return null;
        }
    }

    public string RateSource => BitRate > 0 ? "Spur" : "Tag";

    // ---------------- Spur-Kennzeichen (disposition) und Angaben ----------------
    public bool IsDefault { get; init; }
    public bool IsForced { get; init; }
    public bool IsComment { get; init; }
    public bool IsVisualImpaired { get; init; }
    public bool IsHearingImpaired { get; init; }

    public string Language => Tags.TryGetValue("language", out var l) ? l.Trim().ToLowerInvariant() : "";

    public string Title => Tags.TryGetValue("title", out var t) ? t.Trim() : "";

    public bool IsImageSubtitle => CodecName is "hdmv_pgs_subtitle" or "dvd_subtitle" or "dvb_subtitle" or "xsub";

    public bool HasDolbyVision => SideDataTypes.Any(t => t.Contains("DOVI", StringComparison.OrdinalIgnoreCase));

    public string MimeType => Tags.TryGetValue("mimetype", out var m) ? m : "";
}

public enum HdrKind
{
    None,
    Pq,
    Hlg,
    DolbyVision,
}

public sealed class MediaInfo
{
    public string Path { get; init; } = "";
    public double DurationSeconds { get; init; }
    public long SizeBytes { get; init; }
    public List<StreamInfo> Streams { get; init; } = [];
    public Dictionary<string, string> FormatTags { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public StreamInfo? Video => Streams.FirstOrDefault(s => s.CodecType == "video" && !s.AttachedPic);
    public IEnumerable<StreamInfo> Audio => Streams.Where(s => s.CodecType == "audio");
    public IEnumerable<StreamInfo> Subtitles => Streams.Where(s => s.CodecType == "subtitle");
    public IEnumerable<StreamInfo> Attachments => Streams.Where(s => s.CodecType == "attachment");
    public bool HasAudio => Audio.Any();
    public bool HasSubtitles => Subtitles.Any();

    public double Fps => Fmt.ParseRate(Video?.AvgFrameRate);

    public double StartSeconds { get; init; }

    public string FormatName { get; init; } = "";

    public bool DurationFromTimestamps => FormatName is "mpegts" or "mpeg" or "mpegtsraw";

    public double? TotalRateKbit => DurationSeconds > 0 && SizeBytes > 0 ? SizeBytes * 8d / DurationSeconds / 1000d : null;

    public (double? Kbit, string Source) VideoRate
    {
        get
        {
            var v = Video;
            if (v?.KnownRateKbit is double known) return (known, v.RateSource);
            if (TotalRateKbit is not double total) return (null, "unbekannt");
            var other = Streams.Where(s => s.CodecType is "audio" or "subtitle").Sum(s => s.KnownRateKbit ?? 0);
            return (Math.Max(0, total - other), "geschaetzt");
        }
    }

    public HdrKind Hdr
    {
        get
        {
            var v = Video;
            if (v is null) return HdrKind.None;
            if (v.SideDataTypes.Any(t => t.Contains("DOVI", StringComparison.OrdinalIgnoreCase))) return HdrKind.DolbyVision;
            if (v.ColorTransfer.Equals("smpte2084", StringComparison.OrdinalIgnoreCase)) return HdrKind.Pq;
            if (v.ColorTransfer.Equals("arib-std-b67", StringComparison.OrdinalIgnoreCase)) return HdrKind.Hlg;
            return HdrKind.None;
        }
    }

    public bool DolbyVisionWithoutBase => Hdr == HdrKind.DolbyVision && (Video!.DvProfile == 5 || Video.DvCompatibility == 0);

    public static string HdrText(HdrKind k) => k switch
    {
        HdrKind.Pq => "HDR10 (PQ)",
        HdrKind.Hlg => "HLG",
        HdrKind.DolbyVision => "Dolby Vision",
        _ => "SDR",
    };

    // ---------------- JSON von ffprobe einlesen ----------------

    public static MediaInfo Parse(string path, string json, long fileSize)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        double duration = 0, start = 0;
        var formatName = "";
        long size = fileSize;
        var formatTags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (root.TryGetProperty("format", out var fmt))
        {
            duration = Fmt.ParseDouble(Str(fmt, "duration"), 0);
            start = Fmt.ParseDouble(Str(fmt, "start_time"), 0);
            formatName = Str(fmt, "format_name");
            var s = Fmt.ParseDouble(Str(fmt, "size"), 0);
            if (s > 0) size = (long)s;
            ReadTags(fmt, formatTags);
        }

        var streams = new List<StreamInfo>();
        if (root.TryGetProperty("streams", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var st in arr.EnumerateArray())
            {
                var tags = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                ReadTags(st, tags);
                var side = new List<string>();
                int rotation = 0, dvProfile = -1, dvCompat = -1;
                if (st.TryGetProperty("side_data_list", out var sdl) && sdl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var sd in sdl.EnumerateArray())
                    {
                        var t = Str(sd, "side_data_type");
                        if (!string.IsNullOrEmpty(t)) side.Add(t);
                        if (sd.TryGetProperty("rotation", out _)) rotation = (int)Math.Round(Fmt.ParseDouble(Str(sd, "rotation"), 0));
                        if (sd.TryGetProperty("dv_profile", out _)) dvProfile = Int(sd, "dv_profile");
                        if (sd.TryGetProperty("dv_bl_signal_compatibility_id", out _)) dvCompat = Int(sd, "dv_bl_signal_compatibility_id");
                    }
                }
                if (rotation == 0 && tags.TryGetValue("rotate", out var rot)) rotation = (int)Math.Round(Fmt.ParseDouble(rot, 0));
                var hasDisp = st.TryGetProperty("disposition", out var disp);
                bool D(string n) => hasDisp && Int(disp, n) == 1;
                streams.Add(new StreamInfo
                {
                    Index = Int(st, "index"),
                    CodecType = Str(st, "codec_type"),
                    CodecName = Str(st, "codec_name"),
                    Profile = Str(st, "profile"),
                    PixFmt = Str(st, "pix_fmt"),
                    Level = Int(st, "level"),
                    Width = Int(st, "width"),
                    Height = Int(st, "height"),
                    AvgFrameRate = Str(st, "avg_frame_rate"),
                    RFrameRate = Str(st, "r_frame_rate"),
                    ColorTransfer = Str(st, "color_transfer"),
                    FieldOrder = Str(st, "field_order"),
                    BitRate = (long)Fmt.ParseDouble(Str(st, "bit_rate"), 0),
                    Channels = Int(st, "channels"),
                    AttachedPic = D("attached_pic"),
                    IsDefault = D("default"),
                    IsForced = D("forced"),
                    IsComment = D("comment"),
                    IsVisualImpaired = D("visual_impaired"),
                    IsHearingImpaired = D("hearing_impaired"),
                    Tags = tags,
                    SideDataTypes = side,
                    Rotation = rotation,
                    DvProfile = dvProfile,
                    DvCompatibility = dvCompat,
                });
            }
        }
        return new MediaInfo { Path = path, DurationSeconds = duration, StartSeconds = start, FormatName = formatName, SizeBytes = size, Streams = streams, FormatTags = formatTags };
    }

    private static void ReadTags(JsonElement e, Dictionary<string, string> into)
    {
        if (!e.TryGetProperty("tags", out var tags) || tags.ValueKind != JsonValueKind.Object) return;
        foreach (var p in tags.EnumerateObject()) into[p.Name] = p.Value.ToString();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? "",
            JsonValueKind.Number => v.GetRawText(),
            _ => "",
        } : "";

    private static int Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i)) return i;
        if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var j)) return j;
        return 0;
    }
}
