namespace FilmKonverter.Core.Util;

public static class Languages
{
    private static readonly Dictionary<string, string> Alias = Build(
        ("ger", "de deu ger german deutsch"),
        ("eng", "en eng english englisch"),
        ("fre", "fr fra fre french franzoesisch französisch"),
        ("spa", "es spa spanish spanisch"),
        ("ita", "it ita italian italienisch"),
        ("jpn", "ja jpn japanese japanisch"),
        ("dut", "nl nld dut dutch niederlaendisch niederländisch"),
        ("pol", "pl pol polish polnisch"),
        ("rus", "ru rus russian russisch"),
        ("tur", "tr tur turkish tuerkisch türkisch"),
        ("por", "pt por portuguese portugiesisch"),
        ("kor", "ko kor korean koreanisch"),
        ("chi", "zh zho chi cmn yue chinese chinesisch mandarin kantonesisch"),
        ("swe", "sv swe swedish schwedisch"),
        ("dan", "da dan danish daenisch dänisch"),
        ("nor", "no nor nob nno norwegian norwegisch"),
        ("fin", "fi fin finnish finnisch"),
        ("cze", "cs ces cze czech tschechisch"),
        ("hun", "hu hun hungarian ungarisch"),
        ("gre", "el ell gre greek griechisch"),
        ("ara", "ar ara arabic arabisch"),
        ("hin", "hi hin hindi"),
        ("ukr", "uk ukr ukrainian ukrainisch"),
        ("rum", "ro ron rum mol romanian rumaenisch rumänisch"),
        ("slo", "sk slk slo slovak slowakisch"),
        ("slv", "sl slv slovenian slowenisch"),
        ("hrv", "hr hrv croatian kroatisch"),
        ("srp", "sr srp serbian serbisch"),
        ("bos", "bs bos bosnian bosnisch"),
        ("bul", "bg bul bulgarian bulgarisch"),
        ("mac", "mk mkd mac macedonian mazedonisch"),
        ("alb", "sq sqi alb albanian albanisch"),
        ("heb", "he heb iw hebrew hebraeisch hebräisch"),
        ("per", "fa fas per persian farsi persisch"),
        ("ice", "is isl ice icelandic islaendisch isländisch"),
        ("est", "et est estonian estnisch"),
        ("lav", "lv lav latvian lettisch"),
        ("lit", "lt lit lithuanian litauisch"),
        ("tha", "th tha thai"),
        ("vie", "vi vie vietnamese vietnamesisch"),
        ("ind", "id ind indonesian indonesisch"),
        ("may", "ms msa may malay malaiisch"),
        ("fil", "fil tl tgl filipino tagalog"),
        ("cat", "ca cat catalan katalanisch"),
        ("baq", "eu eus baq basque baskisch"),
        ("glg", "gl glg galician galicisch"),
        ("wel", "cy cym wel welsh walisisch"),
        ("gle", "ga gle irish irisch"),
        ("ben", "bn ben bengali"),
        ("tam", "ta tam tamil"),
        ("tel", "te tel telugu"),
        ("urd", "ur urd urdu"),
        ("kur", "ku kur kurdish kurdisch"),
        ("arm", "hy hye arm armenian armenisch"),
        ("geo", "ka kat geo georgian georgisch"),
        ("lat", "la lat latin latein"),
        ("bel", "be bel belarusian belarussisch"),
        ("kaz", "kk kaz kazakh kasachisch"));

    private static readonly Dictionary<string, string> NamesEn = new()
    {
        ["ger"] = "German", ["eng"] = "English", ["fre"] = "French", ["spa"] = "Spanish", ["ita"] = "Italian",
        ["jpn"] = "Japanese", ["dut"] = "Dutch", ["pol"] = "Polish", ["rus"] = "Russian", ["tur"] = "Turkish",
        ["por"] = "Portuguese", ["kor"] = "Korean", ["chi"] = "Chinese",
    };

    private static readonly Dictionary<string, string> Names = new()
    {
        ["ger"] = "Deutsch", ["eng"] = "Englisch", ["fre"] = "Französisch", ["spa"] = "Spanisch", ["ita"] = "Italienisch",
        ["jpn"] = "Japanisch", ["dut"] = "Niederländisch", ["pol"] = "Polnisch", ["rus"] = "Russisch", ["tur"] = "Türkisch",
        ["por"] = "Portugiesisch", ["kor"] = "Koreanisch", ["chi"] = "Chinesisch",
    };

    private static Dictionary<string, string> Build(params (string Canon, string Aliases)[] groups)
    {
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (canon, aliases) in groups)
            foreach (var a in aliases.Split(' ', StringSplitOptions.RemoveEmptyEntries)) d[a] = canon;
        return d;
    }

    public static string Normalize(string? code)
    {
        var c = (code ?? "").Trim().ToLowerInvariant();
        return Alias.TryGetValue(c, out var canon) ? canon : c;
    }

    public static bool IsUnknown(string? code) => Normalize(code) is "" or "und" or "mis" or "zxx" or "mul" or "qaa";

    public static HashSet<string> ParseList(string? text) =>
        (text ?? "").Split(new[] { ',', ';', ' ', '/' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(Normalize).Where(x => x.Length > 0).ToHashSet();

    public static bool Allowed(string? code, HashSet<string> list) =>
        list.Count == 0 || IsUnknown(code) || list.Contains(Normalize(code));

    public static bool Matches(string? code, string? wanted) =>
        !string.IsNullOrWhiteSpace(wanted) && !IsUnknown(code) && Normalize(code) == Normalize(wanted);

    public static bool IsKnown(string? code) => !IsUnknown(code) && Names.ContainsKey(Normalize(code));

    public static string Display(string? code)
    {
        if (IsUnknown(code)) return "";
        var n = Normalize(code);
        return (L.German ? Names : NamesEn).TryGetValue(n, out var name) ? name : n;
    }
}
