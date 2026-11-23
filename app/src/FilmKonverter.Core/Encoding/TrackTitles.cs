using System.Text.RegularExpressions;

namespace FilmKonverter.Core.Encoding;

public static partial class TrackTitles
{
    private const string B = @"(?<![\p{L}\p{N}])";
    private const string E = @"(?![\p{L}\p{N}])";

    private static Regex Words(string alternatives, RegexOptions extra = RegexOptions.Compiled) =>
        new(B + "(?:" + alternatives + ")" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | extra);

    private static readonly Regex Dts = Words(
        @"DTS[-:. ]?HD(?:[-. ]?(?:Master[- ]?Audio|High[- ]?Res(?:olution)?(?:[- ]?Audio)?|MA|HRA|HR))?|DTS[-:. ]?(?:X|ES|EX|96/24|Express)|DTS|DCA");
    private static readonly Regex TrueHd = Words(@"(?:Dolby[- ]?)?True[- ]?HD|MLP|(?:Dolby[- ]?)?Atmos");
    private static readonly Regex Flac = Words("FLAC");
    private static readonly Regex Alac = Words("ALAC|Apple[- ]Lossless");
    private static readonly Regex Pcm = Words("[LB]?PCM|WAV");
    private static readonly Regex Aac = Words(@"HE[- ]?AAC(?:[- ]?v2)?|AAC(?:[- ]?(?:LATM|LC|HE))?");
    private static readonly Regex Opus = Words("Opus");
    private static readonly Regex Vorbis = Words("Vorbis|OGG");
    private static readonly Regex Wma = Words(@"WMA(?:[- ]?(?:Pro|Lossless|Voice))?|Windows[- ]Media[- ]Audio");
    private static readonly Regex Adpcm = Words("ADPCM");

    private static Regex? FormatWords(string sourceCodec) => sourceCodec.ToLowerInvariant() switch
    {
        "dts" => Dts,
        "truehd" or "mlp" => TrueHd,
        "flac" => Flac,
        "alac" => Alac,
        "aac" or "aac_latm" => Aac,
        "opus" => Opus,
        "vorbis" => Vorbis,
        var c when c.StartsWith("pcm_", StringComparison.Ordinal) || c == "s302m" => Pcm,
        var c when c.StartsWith("wma", StringComparison.Ordinal) => Wma,
        var c when c.StartsWith("adpcm", StringComparison.Ordinal) => Adpcm,
        var c when c.Length >= 3 => Words(Regex.Escape(c), RegexOptions.None),
        _ => null,
    };

    [GeneratedRegex(B + @"(?:lossless|verlustfrei)" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LosslessWords();

    [GeneratedRegex(B + @"(?:\d{2,5}(?:[.,]\d+)? ?(?:kbps|kbit/s|kb/s|kbit|k|mbps|mbit/s)|\d{2,3}(?:[.,]\d)? ?khz|(?:16|20|24|32)[- ]?bits?)" + E, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TechSpecs();

    [GeneratedRegex(B + @"[1-9]\.[0-2](?:\.[0-9])?" + E)]
    private static partial Regex Layout();

    [GeneratedRegex(@"[(\[{]\s*[)\]}]")]
    private static partial Regex EmptyBrackets();

    [GeneratedRegex(@"\s*([/|,;·@+–—-])(?:\s*[/|,;·@+–—-])+\s*")]
    private static partial Regex RepeatedSeparators();

    [GeneratedRegex(@"\s{2,}")]
    private static partial Regex Spaces();

    private static readonly char[] Edge = [' ', '\t', '/', '|', ',', ';', '·', '@', '+', '-', '–', '—', ':', '.', '_'];

    public static string? CleanAudio(string title, string sourceCodec, string targetCodec, bool channelsChanged)
    {
        if (string.IsNullOrWhiteSpace(title)) return null;
        var text = title;
        if (FormatWords(sourceCodec) is { } words) text = words.Replace(text, "");
        if (targetCodec != "flac") text = LosslessWords().Replace(text, "");
        if (channelsChanged) text = Layout().Replace(text, "");
        if (text == title) return null;

        text = TechSpecs().Replace(text, "");
        string prev;
        do
        {
            prev = text;
            text = EmptyBrackets().Replace(text, " ");
            text = RepeatedSeparators().Replace(text, " $1 ");
            text = Spaces().Replace(text, " ").Trim(Edge);
        } while (text != prev);
        return text.Any(char.IsLetter) ? text : "";
    }
}
