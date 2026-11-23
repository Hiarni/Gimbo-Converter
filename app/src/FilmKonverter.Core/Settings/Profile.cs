using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmKonverter.Core.Settings;

public enum EncoderKind
{
    Nvenc,
    Amf,
    Qsv,
    Cpu,
}

public enum RateBasis
{
    Video,
    Total,
}

public enum ForcedSubtitleMode
{
    Keep,
    Auto,
    First,
}

public enum DeinterlaceMode
{
    Auto,
    Off,
    Always,
}

public enum AudioTarget
{
    Eac3,
    Ac3,
}

public enum FileOrder
{
    Name,
    LargestFirst,
    NewestFirst,
    OldestFirst,
    HighestRateFirst,
}

public sealed class Profile : IJsonOnDeserialized
{
    public string Name { get; set; } = "Neues Profil";
    public string InputDir { get; set; } = "";
    public string OutputDir { get; set; } = "";

    // ---------------- Encoder ----------------
    public EncoderKind Encoder { get; set; } = EncoderKind.Nvenc;

    public int Quality { get; set; } = 27;

    public int QpI { get; set; } = 23;

    public int QpP { get; set; } = 25;

    public string NvencPreset { get; set; } = "p7";
    public int AqStrength { get; set; } = 8;
    public int Lookahead { get; set; } = 32;
    public string CpuPreset { get; set; } = "medium";
    public string QsvPreset { get; set; } = "slower";

    // ---------------- Bild ----------------
    public int MaxWidth { get; set; } = 1920;
    public int MaxHeight { get; set; } = 1080;

    public int MaxFps { get; set; }

    public DeinterlaceMode Deinterlace { get; set; } = DeinterlaceMode.Auto;

    public bool TenBit { get; set; }

    public bool HardwareDecode { get; set; }

    [JsonIgnore]
    public bool LimitsResolution => MaxWidth > 0 && MaxHeight > 0;

    // ---------------- Untertitel ----------------
    public ForcedSubtitleMode ForcedSubtitles { get; set; } = ForcedSubtitleMode.Auto;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? ForceFirstSubtitle
    {
        get => null;
        set { if (value is bool b) ForcedSubtitles = b ? ForcedSubtitleMode.First : ForcedSubtitleMode.Keep; }
    }

    public bool ForcedSubtitlesDefault { get; set; }

    public string SubtitleLanguages { get; set; } = "";

    public bool RemoveImageSubtitles { get; set; }

    public bool KeepAttachments { get; set; } = true;

    // ---------------- Ueberspringen ----------------
    public bool SkipAv1 { get; set; } = true;
    public bool SkipHevc { get; set; }

    public bool SkipHdr { get; set; }

    public bool AudioOnlyForSkipped { get; set; } = true;

    // ---------------- Qualitaet nach Aufloesung der Quelle ----------------
    public int QualityAdjustSd { get; set; }
    public int QualityAdjust720 { get; set; }
    public int QualityAdjust1080 { get; set; }
    public int QualityAdjust4K { get; set; }

    [JsonIgnore]
    public bool HasQualityRules => QualityAdjustSd != 0 || QualityAdjust720 != 0 || QualityAdjust1080 != 0 || QualityAdjust4K != 0;

    public static int ClassHeight(int width, int height) => Math.Max(height, (int)Math.Round(width * 9 / 16d));

    public int QualityAdjustFor(int width, int height) => QualityAdjustFor(ClassHeight(width, height));

    public int QualityAdjustFor(int height) => height switch
    {
        <= 0 => 0,
        <= 576 => QualityAdjustSd,
        <= 720 => QualityAdjust720,
        <= 1080 => QualityAdjust1080,
        _ => QualityAdjust4K,
    };

    public Profile AdjustedFor(int width, int height) => AdjustedFor(ClassHeight(width, height));

    public Profile AdjustedFor(int height)
    {
        var d = QualityAdjustFor(height);
        if (d == 0) return this;
        var c = Clone();
        c.Quality = Math.Clamp(Quality + d, 0, 51);
        c.QpI = Math.Clamp(QpI + d, 0, 51);
        c.QpP = Math.Clamp(QpP + d, 0, 51);
        return c;
    }

    // ---------------- Qualitaetskontrolle ----------------
    public bool SsimCheck { get; set; }

    public double SsimMin { get; set; } = 0.95;

    public int SsimSamples { get; set; } = 3;

    // ---------------- Regeln ----------------
    public int EarlyAbortPercent { get; set; } = 95;

    public int EarlyAbortFromPercent { get; set; } = 10;

    public bool CheckExisting { get; set; } = true;

    public bool UseMarkList { get; set; } = true;

    public bool FullDecodeTest { get; set; } = true;

    public int LockStaleHours { get; set; } = 24;

    public int MinFreeSpaceGb { get; set; } = 20;

    public bool PreserveDates { get; set; } = true;

    public bool CopySidecars { get; set; } = true;

    // ---------------- Auswahl ----------------
    public string ExcludePatterns { get; set; } = "*sample*;*trailer*";

    public int MinSizeMb { get; set; } = 50;

    public int SkipNewerThanMinutes { get; set; } = 30;

    public FileOrder Order { get; set; } = FileOrder.Name;

    public int MinRateMbit { get; set; }

    public RateBasis RateBasis { get; set; } = RateBasis.Video;

    // ---------------- Ton ----------------
    public bool ConvertLosslessAudio { get; set; } = true;

    public int AudioBitrateKbit { get; set; } = 640;

    public bool ConvertDts { get; set; } = true;

    private AudioTarget? _audioTarget;

    public AudioTarget AudioTarget
    {
        get => _audioTarget ?? AudioTarget.Ac3;
        set => _audioTarget = value;
    }

    void IJsonOnDeserialized.OnDeserialized() => _audioTarget ??= AudioTarget.Eac3;

    public string AudioLanguages { get; set; } = "";

    public string DefaultAudioLanguage { get; set; } = "";

    public bool RemoveCommentary { get; set; }

    public bool RemoveAudioDescription { get; set; }

    // ---------------- Dateien ----------------
    public List<string> Extensions { get; set; } = [.. DefaultExtensions];

    public static readonly string[] DefaultExtensions = [".mkv", ".m2ts", ".mp4", ".avi", ".ts", ".m4v", ".mov", ".mpg", ".mpeg", ".wmv"];

    public static readonly string[] OldDefaultExtensions = [".mkv", ".m2ts", ".mp4", ".avi"];

    // ---------------- Testmodus ----------------
    public int TestStartSeconds { get; set; } = 600;
    public int TestLengthSeconds { get; set; } = 120;

    public string TestValues { get; set; } = "25 26 27 28 29";

    [JsonIgnore]
    public bool EffectiveSkipHdr => SkipHdr || Encoder == EncoderKind.Amf;

    public bool NvencLegacy { get; set; }

    [JsonIgnore]
    public string QualityText => Encoder switch
    {
        EncoderKind.Amf => $"AMF QP {QpI}/{QpP}",
        EncoderKind.Nvenc => $"NVENC CQ {Quality}",
        EncoderKind.Qsv => Util.L.T($"QSV Qualität {Quality}", $"QSV quality {Quality}"),
        _ => $"x265 CRF {Quality}",
    };

    [JsonIgnore]
    public string MarkTag => "G" + EarlyAbortPercent;

    public Profile Clone()
    {
        var json = JsonSerializer.Serialize(this, JsonOptions.Default);
        return JsonSerializer.Deserialize<Profile>(json, JsonOptions.Default)!;
    }

    // ---------------- Voreinstellungen ----------------

    public static Profile Nvidia() => new()
    {
        Name = "NVIDIA",
        Encoder = EncoderKind.Nvenc,
        Quality = 27,
        SkipHevc = false,
        TestValues = "25 26 27 28 29",
    };

    public static Profile Amd() => new()
    {
        Name = "AMD",
        Encoder = EncoderKind.Amf,
        QpI = 23,
        QpP = 25,
        SkipHevc = true,
        SkipHdr = true,
        FullDecodeTest = false,
        TestValues = "20/22 22/24 23/25 24/26",
    };

    public static Profile Intel() => new()
    {
        Name = "Intel",
        Encoder = EncoderKind.Qsv,
        Quality = 25,
        TestValues = "23 25 27 29",
    };

    public static Profile Cpu() => new()
    {
        Name = "Prozessor",
        Encoder = EncoderKind.Cpu,
        Quality = 24,
        CpuPreset = "medium",
        TestValues = "22 24 26 28",
    };
}

public static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new()
    {
        WriteIndented = true,
        Converters = { new TolerantEnumConverterFactory() },
    };
}

public sealed class TolerantEnumConverterFactory : JsonConverterFactory
{
    public override bool CanConvert(Type t) => t.IsEnum;

    public override JsonConverter CreateConverter(Type t, JsonSerializerOptions o) =>
        (JsonConverter)Activator.CreateInstance(typeof(TolerantEnumConverter<>).MakeGenericType(t))!;

    private sealed class TolerantEnumConverter<T> : JsonConverter<T> where T : struct, Enum
    {
        public override T Read(ref Utf8JsonReader r, Type t, JsonSerializerOptions o)
        {
            if (r.TokenType == JsonTokenType.String && Enum.TryParse<T>(r.GetString(), true, out var v) && Enum.IsDefined(v)) return v;
            if (r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var n) && Enum.IsDefined(typeof(T), n)) return (T)Enum.ToObject(typeof(T), n);
            return default;
        }

        public override void Write(Utf8JsonWriter w, T value, JsonSerializerOptions o) => w.WriteStringValue(value.ToString());
    }
}
