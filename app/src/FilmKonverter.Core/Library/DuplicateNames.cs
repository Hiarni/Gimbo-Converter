using System.Text.RegularExpressions;

namespace FilmKonverter.Core.Library;

public sealed record EpisodeName(bool Found, string Part, string SeriesKey, string TitleKey);

public static partial class DuplicateNames
{
    [GeneratedRegex(@"\.reparatur\.tmp$", RegexOptions.IgnoreCase)]
    private static partial Regex RepairTemp();
    [GeneratedRegex(@"\s*\((?<n>\d{1,2})\)$")]
    private static partial Regex CopyNo();
    [GeneratedRegex(@"\s*-\s*(Kopie|Copy)(\s*\(\d+\))?$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyWord();
    [GeneratedRegex(@"[\s._-]+sample$", RegexOptions.IgnoreCase)]
    private static partial Regex SampleEnd();
    [GeneratedRegex(@"(?<![a-z0-9])sample(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex SampleWord();

    [GeneratedRegex(@"(?<![A-Za-z0-9])S[0-9]{1,2}[ ._-]?E[0-9]{1,3}(?:(?:[ ._-]?-?[ ._-]?E|-)[0-9]{1,3}(?![0-9A-DF-Za-df-z]))*(?:(?<part>[a-f])(?![A-Za-z0-9]))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex SxE();
    [GeneratedRegex(@"(?<![A-Za-z0-9])[0-9]{1,2}x[0-9]{2,3}(?:(?<part>[a-f])(?![A-Za-z0-9]))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex NxN();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:Folge|Episode|Ep\.?|E)[ ._-]?[0-9]{1,3}(?:(?<part>[a-f])(?![A-Za-z0-9]))?(?![0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex EpOnly();

    [GeneratedRegex(@"^(\d{3,4}[pi]|4k|uhd|webrip|webdl|webhd|hdtv|pdtv|dvdrip|bluray|bdrip|brrip|remux|hdrip|tvrip|satrip|x264|x265|h264|h265|hevc|avc|xvid|divx|10bit|8bit|hdr10|aac|ac3|ac3d|eac3|ddp?\d*|dts|dtshd|truehd|flac|amzn|dsnp|atvp|hmax|repack)$")]
    private static partial Regex Strong();
    [GeneratedRegex(@"^(german|deutsch|english|englisch|french|italian|spanish|multi|dual|dubbed|subbed|synced|dl|ml|web|dvd|hdr|dv|sdr|av1|atmos|mp3|opus|nf|proper|internal|complete|remastered|retail)$")]
    private static partial Regex Weak();
    [GeneratedRegex(@"h[ ._]?26([45])", RegexOptions.IgnoreCase)]
    private static partial Regex H26x();
    [GeneratedRegex(@"web[ ._-]?dl", RegexOptions.IgnoreCase)]
    private static partial Regex WebDl();
    [GeneratedRegex(@"blu[ ._-]?ray", RegexOptions.IgnoreCase)]
    private static partial Regex BluRay();
    [GeneratedRegex(@"[\p{L}\p{Nd}]+")]
    private static partial Regex Word();
    [GeneratedRegex(@"^(19|20)\d{2}$")]
    private static partial Regex YearWord();
    [GeneratedRegex(@"^(cd|disc|disk|part|pt)\d{1,2}$")]
    private static partial Regex PartWord();

    private static string Stem(string file)
    {
        var n = Path.GetFileName(file);
        if (n.EndsWith(".reparatur-tmp", StringComparison.OrdinalIgnoreCase)) n = n[..^".reparatur-tmp".Length];
        return RepairTemp().Replace(Path.GetFileNameWithoutExtension(n), "");
    }

    public static string CleanName(string file) => Clean(file, keepNumber: false);

    private static string Clean(string file, bool keepNumber)
    {
        var n = Stem(file);
        var no = CopyNo().Match(n);
        if (no.Success) n = n[..no.Index];
        n = CopyWord().Replace(n, "");
        n = SampleEnd().Replace(n, "");
        return keepNumber && no.Success ? n.Trim() + " (" + no.Groups["n"].Value + ")" : n.Trim();
    }

    public static bool IsSample(string file) => SampleWord().IsMatch(Path.GetFileName(file));

    public static Dictionary<string, string> Identities(IEnumerable<string> filesOfOneFolder)
    {
        var files = filesOfOneFolder.ToList();
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in files.GroupBy(f => CleanName(f), StringComparer.OrdinalIgnoreCase))
        {
            var plain = g.Any(f => !CopyNo().IsMatch(Stem(f)));
            foreach (var f in g) result[f] = (plain ? g.Key : Clean(f, keepNumber: true)).ToLowerInvariant();
        }
        return result;
    }

    public static EpisodeName Episode(string name)
    {
        var m = SxE().Match(name);
        if (!m.Success) m = NxN().Match(name);
        if (!m.Success) m = EpOnly().Match(name);
        if (!m.Success) return new EpisodeName(false, "", "", Key(Words(name), 0, stopAtTags: false));
        return new EpisodeName(true, m.Groups["part"].Value.ToLowerInvariant(),
            SeriesKeyOf(name[..m.Index]), Key(Words(name[(m.Index + m.Length)..]), 0, stopAtTags: true));
    }

    public static string MovieTail(string name, string year)
    {
        var words = Words(name).Where(w => !PartWord().IsMatch(w)).ToList();
        var firstTag = words.FindIndex(w => Strong().IsMatch(w));
        if (firstTag < 0) firstTag = words.Count;
        var at = -1;
        for (var i = firstTag - 1; i >= 1 && at < 0; i--)
            if (words[i] == year) at = i;
        for (var i = Math.Max(1, firstTag); i < words.Count && at < 0; i++)
            if (words[i] == year) at = i;
        return year.Length > 0 && at >= 1 ? Key(words, at + 1, stopAtTags: true) : Key(words, 0, stopAtTags: true);
    }

    private static string SeriesKeyOf(string before)
    {
        var words = Words(Regex.Replace(before, @"^\s*\[[^\]]*\]", "")).Where(w => w != "sample" && !Strong().IsMatch(w) && !Weak().IsMatch(w)).ToList();
        if (words.Any(w => !YearWord().IsMatch(w))) words.RemoveAll(w => YearWord().IsMatch(w));
        return Fold(string.Concat(words));
    }

    private static List<string> Words(string text)
    {
        try { text = text.Normalize(System.Text.NormalizationForm.FormC); } catch (ArgumentException) { }
        var s = H26x().Replace(text, "h26$1");
        s = WebDl().Replace(s, "webdl");
        s = BluRay().Replace(s, "bluray");
        return Word().Matches(s).Select(m => m.Value.ToLowerInvariant()).ToList();
    }

    private static string Key(List<string> words, int start, bool stopAtTags)
    {
        var kept = new List<string>();
        for (var i = start; i < words.Count; i++)
        {
            var w = words[i];
            if (stopAtTags)
            {
                if (Strong().IsMatch(w)) break;
                if (Weak().IsMatch(w) && (i + 1 >= words.Count || Strong().IsMatch(words[i + 1]) || Weak().IsMatch(words[i + 1]))) break;
            }
            if (w == "sample") continue;
            kept.Add(w);
        }
        return Fold(string.Concat(kept));
    }

    private static string Fold(string s) => s.Replace("ä", "ae").Replace("ö", "oe").Replace("ü", "ue").Replace("ß", "ss");
}
