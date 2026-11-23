using System.Net;
using System.Text.RegularExpressions;

namespace FilmKonverter.Core.Rename;

public sealed record EpisodeInfo
{
    public string Series { get; init; } = "";
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int? EpisodeEnd { get; init; }
    public string PartLetter { get; init; } = "";
    public string Title { get; init; } = "";
    public string Year { get; init; } = "";

    public bool YearUncertain { get; init; }
    public string TitleSource { get; init; } = "";
    public bool SeasonFromFolder { get; init; }
    public List<string> Notes { get; init; } = [];

    // ---------------- Filme ----------------

    public bool Movie { get; init; }
    public string Edition { get; init; } = "";
    public int? Part { get; init; }

    public bool Recognized => Movie ? Title.Length > 0 : Season is not null && Episode is not null;
}

public static partial class EpisodeParser
{
    [GeneratedRegex(@"(?<![A-Za-z0-9])S(?<s>[0-9]{1,2})[ ._-]?E(?<e>[0-9]{1,3})(?<p>[a-d](?![A-Za-z]))?(?:(?:[ ._-]?-?[ ._-]?E|-)(?<e2>[0-9]{1,3})(?![0-9A-DF-Za-df-z]))*(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxE();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<s>[0-9]{1,2})x(?<e>[0-9]{2,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NxN();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:Folge|Episode|Ep\.?|E)[ ._-]?(?<e>[0-9]{1,3})(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpOnly();

    [GeneratedRegex(@"^(?:Staffel|Season|Serie|S)[ ._-]?(?<s>[0-9]{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonFolder();

    [GeneratedRegex(@"\((?<t>[^()]+)\)")]
    private static partial Regex Parens();

    [GeneratedRegex(@"^[ ._-]*(?:teil|part|pt|cd)[ ._-]?(?<n>[1-9])(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex PartAfter();

    [GeneratedRegex(@"^(?<e>[0-9]{1,3})(?:$|\s*[-.)_]\s*|\s+)(?<t>.*)$")]
    private static partial Regex LeadingNumber();

    private static readonly HashSet<string> ReleaseTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "german", "deutsch", "ger", "dl", "ml", "multi", "dubbed", "subbed", "dual", "english", "eng",
        "2160p", "1080p", "1080i", "720p", "576p", "576i", "480p", "4k", "uhd", "hd", "fhd", "sd",
        "hdr", "hdr10", "hdr10plus", "hdr10+", "dv", "dovi", "sdr", "hlg",
        "web", "webrip", "web-dl", "webdl", "web-rip", "bluray", "blu-ray", "bdrip", "brrip", "bd", "dvdrip", "dvd", "hdtv", "tvrip", "remux",
        "x264", "x265", "h264", "h265", "h.264", "h.265", "hevc", "avc", "av1", "xvid", "divx", "10bit", "8bit",
        "aac", "ac3", "eac3", "dd", "dd+", "ddp", "ddp5", "dd5", "dts", "dts-hd", "dtshd", "truehd", "atmos", "flac", "mp3",
        "amzn", "nf", "dsnp", "atvp", "hmax", "hulu", "pcok", "rtl", "ard", "zdf", "joyn",
        "repack", "proper", "internal", "complete", "extended", "uncut", "remastered", "read.nfo", "subs",
    };

    public static int? SeasonFromFolderName(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        var f = folder.Trim();
        if (f.Equals("Specials", StringComparison.OrdinalIgnoreCase) || f.Equals("Extras", StringComparison.OrdinalIgnoreCase)) return 0;
        var m = SeasonFolder().Match(f);
        return m.Success ? int.Parse(m.Groups["s"].Value) : null;
    }

    public static EpisodeInfo Parse(string fileNameWithoutExt, string? seasonFolder)
    {
        var name = fileNameWithoutExt;
        var folderSeason = SeasonFromFolderName(seasonFolder);
        int? season = null, ep = null, ep2 = null;
        var part = "";
        var titleStart = -1;
        var fromFolder = false;

        var m = SxE().Match(name);
        if (m.Success)
        {
            season = int.Parse(m.Groups["s"].Value);
            ep = int.Parse(m.Groups["e"].Value);
            if (m.Groups["e2"].Success) ep2 = int.Parse(m.Groups["e2"].Value);
            if (m.Groups["p"].Success) part = m.Groups["p"].Value.ToLowerInvariant();
            titleStart = m.Index + m.Length;
            if (part.Length == 0 && PartAfter().Match(name[titleStart..]) is { Success: true } pm2)
            {
                part = ((char)('a' + int.Parse(pm2.Groups["n"].Value) - 1)).ToString();
                titleStart += pm2.Length;
            }
            else if (Regex.IsMatch(name[titleStart..], @"^\.[1-9] ")) titleStart += 2;
        }
        else if ((m = NxN().Match(name)).Success && !LooksLikeResolution(name, m))
        {
            season = int.Parse(m.Groups["s"].Value);
            ep = int.Parse(m.Groups["e"].Value);
            titleStart = m.Index + m.Length;
        }
        else if (folderSeason is not null && (m = LeadingNumber().Match(name)).Success && !LeadingIsNotEpisode(m.Groups["t"].Value))
        {
            ep = int.Parse(m.Groups["e"].Value);
            season = folderSeason;
            fromFolder = true;
            titleStart = m.Groups["t"].Index;
        }
        else if ((m = EpOnly().Match(name)).Success)
        {
            ep = int.Parse(m.Groups["e"].Value);
            season = folderSeason;
            fromFolder = folderSeason is not null;
            titleStart = m.Index + m.Length;
        }

        var title = "";
        if (titleStart >= 0)
        {
            var rest = name[titleStart..];
            var pm = Parens().Match(rest);
            title = pm.Success && rest[..pm.Index].Trim(' ', '-', '.', '_').Length == 0 ? pm.Groups["t"].Value : CleanTitle(rest);
        }
        return new EpisodeInfo
        {
            Season = season,
            Episode = ep,
            EpisodeEnd = ep2 is not null && ep2 > ep ? ep2 : null,
            PartLetter = part,
            Title = title.Trim(),
            TitleSource = title.Trim().Length > 0 ? "name" : "",
            SeasonFromFolder = fromFolder,
        };
    }

    private static bool LeadingIsNotEpisode(string rest) =>
        Regex.IsMatch(rest, @"^[pPiIkK](?![A-Za-zÄÖÜäöüß])") || Regex.IsMatch(rest, @"^[dD](?![A-Za-zÄÖÜäöüß])");

    private static bool LooksLikeResolution(string name, Match m)
    {
        var after = m.Index + m.Length;
        return after < name.Length && char.IsAsciiDigit(name[after]) || m.Groups["s"].Value.Length > 2;
    }

    public static string CleanTitle(string rest)
    {
        var s = rest.Trim(' ', '-', '.', '_');
        var tokens = s.Split(new[] { '.', ' ', '_' }, StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var t in tokens)
        {
            var bare = t.Trim('-', '[', ']', '(', ')');
            var core = bare.Contains('-') ? bare[..bare.IndexOf('-')] : bare;
            if (ReleaseTags.Contains(bare) || ReleaseTags.Contains(core) || IsYearOrResolution(bare)) break;
            var tail = bare.Contains('-') ? bare[(bare.IndexOf('-') + 1)..] : "";
            if (tail.Length > 0 && (ReleaseTags.Contains(tail) || IsYearOrResolution(tail)))
            {
                if (core.Length > 0) kept.Add(core);
                break;
            }
            kept.Add(t);
        }
        var title = string.Join(" ", kept).Trim(' ', '-');
        return title.StartsWith("- ", StringComparison.Ordinal) ? title[2..].Trim() : title;
    }

    private static bool IsYearOrResolution(string t) => Regex.IsMatch(t, @"^[0-9]{3,4}[pi]$", RegexOptions.IgnoreCase);

    // ---------------- Filme ----------------

    [GeneratedRegex(@"^\(?(?<y>(?:19|20)[0-9]{2})\)?$")]
    private static partial Regex YearToken();

    [GeneratedRegex(@"^(?:cd|disc|disk|part|pt)[-]?(?<n>[0-9]{1,2})$", RegexOptions.IgnoreCase)]
    private static partial Regex PartToken();

    private static readonly (Regex Rx, string Name)[] Editions =
    [
        (new(@"(?<![a-z])director'?s[ ._-]*cut(?![a-z])", RegexOptions.IgnoreCase), "Director's Cut"),
        (new(@"(?<![a-z])final[ ._-]*cut(?![a-z])", RegexOptions.IgnoreCase), "Final Cut"),
        (new(@"(?<![a-z])ultimate[ ._-]*(?:edition|cut)(?![a-z])", RegexOptions.IgnoreCase), "Ultimate Edition"),
        (new(@"(?<![a-z])special[ ._-]*edition(?![a-z])", RegexOptions.IgnoreCase), "Special Edition"),
        (new(@"(?<![a-z])collector'?s[ ._-]*edition(?![a-z])", RegexOptions.IgnoreCase), "Collector's Edition"),
        (new(@"(?<![a-z])anniversary[ ._-]*edition(?![a-z])", RegexOptions.IgnoreCase), "Anniversary Edition"),
        (new(@"(?<![a-z])extended(?:[ ._-]*(?:edition|cut|version|fassung))?(?![a-z])", RegexOptions.IgnoreCase), "Extended"),
        (new(@"(?<![a-z])(?:kinofassung|theatrical(?:[ ._-]*cut)?)(?![a-z])", RegexOptions.IgnoreCase), "Kinofassung"),
        (new(@"(?<![a-z])uncut(?![a-z])", RegexOptions.IgnoreCase), "Uncut"),
        (new(@"(?<![a-z])unrated(?![a-z])", RegexOptions.IgnoreCase), "Unrated"),
        (new(@"(?<![a-z])remastered(?![a-z])", RegexOptions.IgnoreCase), "Remastered"),
        (new(@"(?<![a-z])imax(?![a-z])", RegexOptions.IgnoreCase), "IMAX"),
    ];

    private static readonly HashSet<string> WeakTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "german", "deutsch", "ger", "english", "eng", "multi", "dual", "dubbed", "subbed", "subs", "complete", "proper", "repack", "internal",
        "hd", "sd", "web", "dd", "nf", "rtl", "ard", "zdf", "joyn", "hulu", "dl", "ml",
    };

    private static readonly HashSet<string> ClearMarkers = new(StringComparer.OrdinalIgnoreCase) { "dl", "ml", "dubbed", "subbed", "multi", "dual" };

    private static readonly HashSet<string> EditionWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "director's", "directors", "extended", "kinofassung", "theatrical", "uncut", "unrated", "remastered", "imax",
    };

    public static string FindEdition(string text)
    {
        foreach (var (rx, n) in Editions)
            if (rx.IsMatch(text)) return n;
        return "";
    }

    public static EpisodeInfo ParseMovie(string fileNameWithoutExt)
    {
        var name = fileNameWithoutExt.Trim();
        name = Regex.Replace(name, @"^\[[^\]]*\]\s*", "");
        var seps = name.Contains(' ') ? new[] { ' ', '_' } : new[] { '.', ' ', '_' };
        var tokens = name.Split(seps, StringSplitOptions.RemoveEmptyEntries).ToList();
        int strong = tokens.Count, weak = tokens.Count;
        int? part = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var bare = tokens[i].Trim('-', '[', ']', '(', ')');
            var core = bare.Split('-', '.')[0];
            if (PartToken().Match(bare) is { Success: true } pm) { part ??= int.Parse(pm.Groups["n"].Value); strong = Math.Min(strong, i); continue; }
            if (i == 0) continue;
            var isTag = ReleaseTags.Contains(bare) || ReleaseTags.Contains(core);
            var isWeak = isTag && (WeakTags.Contains(bare) || WeakTags.Contains(core));
            if (isWeak) weak = Math.Min(weak, i);
            else if (isTag || IsYearOrResolution(bare) || EditionWords.Contains(bare)) strong = Math.Min(strong, i);
        }
        var maxYear = DateTime.Now.Year + 1;
        bool IsYear(string t, out string y)
        {
            y = "";
            if (YearToken().Match(t.Trim('-')) is not { Success: true } m) return false;
            y = m.Groups["y"].Value;
            return int.Parse(y) <= maxYear;
        }

        var year = "";
        var titleEnd = -1;
        for (var i = strong - 1; i >= 1; i--)
        {
            if (IsYear(tokens[i], out var y)) { year = y; titleEnd = i; break; }
        }
        if (titleEnd > 1)
        {
            var runStart = titleEnd;
            while (runStart > 1 && WeakTags.Contains(tokens[runStart - 1].Trim('-', '[', ']', '(', ')'))) runStart--;
            if (runStart < titleEnd && tokens.Skip(runStart).Take(titleEnd - runStart)
                    .Any(t => ClearMarkers.Contains(t.Trim('-', '[', ']', '(', ')'))))
                titleEnd = runStart;
        }
        if (titleEnd < 0)
        {
            titleEnd = Math.Min(strong, weak);
            for (var i = titleEnd; i < Math.Min(tokens.Count, titleEnd + 4); i++)
                if (IsYear(tokens[i], out var y)) { year = y; break; }
        }

        var title = string.Join(" ", tokens.Take(titleEnd)).Trim(' ', '-');
        title = Regex.Replace(title, @"[\s\-(\[]+$", "").Trim();
        var edition = FindEdition(string.Join(" ", tokens.Skip(titleEnd)));
        var lastIsYear = year.Length > 0 && titleEnd == tokens.Count - 1 && !tokens[^1].StartsWith('(');
        return new EpisodeInfo
        {
            Movie = true,
            Title = title,
            TitleSource = title.Length > 0 ? "name" : "",
            Year = year,
            YearUncertain = lastIsYear,
            Edition = edition,
            Part = part,
        };
    }

    [GeneratedRegex(@"<movie[\s>]", RegexOptions.IgnoreCase)]
    private static partial Regex NfoMovieRoot();
    [GeneratedRegex(@"<year>\s*(?<v>[0-9]{4})\s*</year>|<premiered>\s*(?<v>[0-9]{4})", RegexOptions.IgnoreCase)]
    private static partial Regex NfoYear();
    [GeneratedRegex(@"<edition>(?<v>.*?)</edition>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NfoEdition();

    public sealed record MovieNfo(string Title, string Year, string Edition);

    public static MovieNfo? ReadMovieNfo(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            if (!NfoMovieRoot().IsMatch(text)) return null;
            string Get(Regex rx) => rx.Match(text) is { Success: true } m ? Decode(m.Groups["v"].Value) : "";
            return new MovieNfo(Get(NfoTitle()), Get(NfoYear()), Get(NfoEdition()));
        }
        catch
        {
            return null;
        }
    }

    // ---------------- .nfo ----------------

    [GeneratedRegex(@"<title>(?<v>.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NfoTitle();
    [GeneratedRegex(@"<showtitle>(?<v>.*?)</showtitle>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex NfoShowTitle();
    [GeneratedRegex(@"<season>\s*(?<v>-?[0-9]+)\s*</season>", RegexOptions.IgnoreCase)]
    private static partial Regex NfoSeason();
    [GeneratedRegex(@"<episode>\s*(?<v>[0-9]+)\s*</episode>", RegexOptions.IgnoreCase)]
    private static partial Regex NfoEpisode();

    public sealed record NfoData(string Title, string ShowTitle, int? Season, int? Episode);

    public static NfoData? ReadNfo(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var text = File.ReadAllText(file);
            string Get(Regex rx) => rx.Match(text) is { Success: true } m ? Decode(m.Groups["v"].Value) : "";
            int? Num(Regex rx) => rx.Match(text) is { Success: true } m && int.TryParse(m.Groups["v"].Value, out var n) ? n : null;
            return new NfoData(Get(NfoTitle()), Get(NfoShowTitle()), Num(NfoSeason()), Num(NfoEpisode()));
        }
        catch
        {
            return null;
        }
    }

    private static string Decode(string s)
    {
        var t = s.Trim();
        if (t.StartsWith("<![CDATA[", StringComparison.Ordinal) && t.EndsWith("]]>", StringComparison.Ordinal)) t = t[9..^3];
        return WebUtility.HtmlDecode(t).Trim();
    }
}
