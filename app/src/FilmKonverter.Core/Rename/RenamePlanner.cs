using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Rename;

public enum RenameStatus
{
    Unchanged,
    Ready,
    Check,
    Unknown,
    Conflict,
}

public enum RenameKind
{
    Series,
    Movies,
}

public sealed record FileMove(string From, string To);

public sealed record OrphanFile(string File, string Suffix);

public sealed record RenameLibrary(string InputRoot, string OutputRoot);

public sealed class RenameItem
{
    public required string Source { get; init; }
    public required string RelativeSource { get; init; }
    public required EpisodeInfo Info { get; set; }
    public RenameLibrary? Library { get; init; }
    public string OldName => Path.GetFileNameWithoutExtension(Source);

    public string NewName { get; set; } = "";
    public bool Edited { get; set; }
    public RenameStatus Status { get; set; }
    public List<string> AnalysisNotes { get; } = [];
    public List<string> Notes { get; } = [];
    public bool Selected { get; set; }

    public List<OrphanFile> Orphans { get; } = [];

    public bool NameChanges => !string.Equals(OldName, NewName, StringComparison.Ordinal) && NewName.Length > 0;
    public bool Changes => NewName.Length > 0 && (NameChanges || Orphans.Count > 0);
    public bool CanRename => Status is RenameStatus.Ready or RenameStatus.Check && Changes;
}

public sealed record RenamePreset(string Name, string Template, bool BuiltIn = false);

public sealed class RenameOptions
{
    public string Template { get; init; } = DefaultTemplate;

    public const string DefaultTemplate = "S{S}E{E} - {Titel}";

    public const string OldDefaultTemplate = "{Serie} - S{S}E{E} - {Titel}";

    public RenameKind Kind { get; init; } = RenameKind.Series;

    public const string DefaultMovieTemplate = "{Titel} ({Jahr}) - {Edition}";

    public static string DefaultFor(RenameKind k) => k == RenameKind.Movies ? DefaultMovieTemplate : DefaultTemplate;

    public static IReadOnlyList<RenamePreset> BuiltInFor(RenameKind k) => k == RenameKind.Movies ? BuiltInMoviePresets : BuiltInPresets;

    public static IReadOnlyList<RenamePreset> BuiltInMoviePresets =>
    [
        new(L.T("Standard", "Standard"), DefaultMovieTemplate, true),
        new(L.T("Titel und Jahr", "Title and year"), "{Titel} ({Jahr})", true),
        new(L.T("Fassung in Klammern", "Edition in brackets"), "{Titel} ({Jahr}) [{Edition}]", true),
        new(L.T("Nur Titel", "Title only"), "{Titel}", true),
        new(L.T("Jahr zuerst", "Year first"), "{Jahr} - {Titel} - {Edition}", true),
    ];

    public static IReadOnlyList<RenamePreset> BuiltInPresets =>
    [
        new(L.T("Standard", "Standard"), DefaultTemplate, true),
        new(L.T("Nur Staffel und Folge", "Season and episode only"), "S{S}E{E}", true),
        new(L.T("Mit Serie", "With series"), "{Serie} - S{S}E{E} - {Titel}", true),
        new(L.T("Mit Serie und Jahr", "With series and year"), "{Serie} ({Jahr}) - S{S}E{E} - {Titel}", true),
        new(L.T("Kurz 2x09", "Short 2x09"), "{Staffel}x{E} - {Titel}", true),
    ];

    public bool UseNfo { get; init; } = true;

    public bool IncludeTarget { get; init; } = true;

    public string SeriesFilter { get; init; } = "";

    public IEnumerable<string> Extensions { get; init; } = [".mkv", ".mp4", ".m2ts", ".avi"];

    public IReadOnlyList<RenameLibrary> Libraries { get; init; } = [];

    public string? LocalJournalDir { get; init; }
}

public static class RenamePlanner
{
    public const string JournalFolder = "_Umbenennung";

    private static readonly string[] CompanionExtensions = [".nfo", ".srt", ".ass", ".ssa", ".sub", ".idx", ".sup", ".vtt", ".smi", ".ttml", ".jpg", ".jpeg", ".png", ".webp", ".tbn", ".xml"];

    private static readonly Regex YearRx = new(@"<year>\s*(?<y>[0-9]{4})\s*</year>|<premiered>\s*(?<y>[0-9]{4})", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ---------------- Quellen ----------------

    public static List<string> CollectFiles(IEnumerable<string> sources, IEnumerable<string> extensions)
    {
        var exts = new HashSet<string>(extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();
        foreach (var src in sources.Where(s => !string.IsNullOrWhiteSpace(s)))
        {
            IEnumerable<string> files = Directory.Exists(src)
                ? Scanner.Find(src, new ScanFilter { Extensions = exts }).Files
                : File.Exists(src) && exts.Contains(Path.GetExtension(src)) ? [Path.GetFullPath(src)] : [];
            foreach (var f in files)
                if (seen.Add(f)) list.Add(f);
        }
        return list;
    }

    public static (string SeriesDir, string? SeasonFolder) ResolveFolders(string file)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var name = Path.GetFileName(dir);
        if (EpisodeParser.SeasonFromFolderName(name) is not null)
            return (Path.GetDirectoryName(dir) is { Length: > 0 } parent ? parent : dir, name);
        return (dir, null);
    }

    // ---------------- Analyse ----------------

    public static List<RenameItem> Analyze(string root, RenameOptions opt, string? outputRoot = null, CancellationToken ct = default)
    {
        var o = outputRoot is null ? opt : new RenameOptions
        {
            Kind = opt.Kind, Template = opt.Template, UseNfo = opt.UseNfo, IncludeTarget = opt.IncludeTarget, SeriesFilter = opt.SeriesFilter,
            Extensions = opt.Extensions, LocalJournalDir = opt.LocalJournalDir, Libraries = [.. opt.Libraries, new RenameLibrary(root, outputRoot)],
        };
        return Analyze([root], o, ct);
    }

    public static List<RenameItem> Analyze(IEnumerable<string> sources, RenameOptions opt, CancellationToken ct = default)
    {
        var files = CollectFiles(sources, opt.Extensions);
        if (opt.Kind == RenameKind.Movies) return AnalyzeMovies(files, opt, ct);
        var shows = new Dictionary<string, (string Title, string Year)>(StringComparer.OrdinalIgnoreCase);
        var items = new List<RenameItem>();
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var (seriesDir, seasonFolder) = ResolveFolders(f);
            var seriesFolder = Path.GetFileName(seriesDir);
            if (opt.SeriesFilter.Length > 0 && !seriesFolder.Contains(opt.SeriesFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var info = EpisodeParser.Parse(Path.GetFileNameWithoutExtension(f), seasonFolder);
            var notes = new List<string>();

            if (!shows.TryGetValue(seriesDir, out var show))
            {
                var title = "";
                var year = "";
                var nfoFile = Path.Combine(seriesDir, "tvshow.nfo");
                if (opt.UseNfo && EpisodeParser.ReadNfo(nfoFile) is { } tv)
                {
                    title = tv.Title;
                    try { if (YearRx.Match(File.ReadAllText(nfoFile)) is { Success: true } ym) year = ym.Groups["y"].Value; } catch { }
                }
                if (title.Length == 0) title = seriesFolder;
                if (year.Length == 0 && Regex.Match(seriesFolder, @"\((?<y>[0-9]{4})\)") is { Success: true } fy) year = fy.Groups["y"].Value;
                shows[seriesDir] = show = (title, year);
            }

            if (opt.UseNfo && Sibling(f, ".nfo") is { } nfoPath && EpisodeParser.ReadNfo(nfoPath) is { } nfo)
            {
                if (nfo.Title.Length > 0 && !LooksLikeFileName(nfo.Title, Path.GetFileNameWithoutExtension(f))) info = info with { Title = nfo.Title, TitleSource = "nfo" };
                if (info.SeasonFromFolder && nfo.Season == info.Season && nfo.Episode == info.Episode) info = info with { SeasonFromFolder = false };
                if (info.Season is null && nfo.Season is not null) info = info with { Season = nfo.Season };
                if (info.Episode is null && nfo.Episode is not null) info = info with { Episode = nfo.Episode };
                if (nfo.Season is not null && info.Season is not null && nfo.Season != info.Season
                    || nfo.Episode is not null && info.Episode is not null && nfo.Episode != info.Episode && info.EpisodeEnd is null)
                    notes.Add(L.T($".nfo sagt S{nfo.Season:00}E{nfo.Episode:00}, der Dateiname etwas anderes", $".nfo says S{nfo.Season:00}E{nfo.Episode:00}, the file name says otherwise"));
            }
            info = info with { Series = show.Title, Year = show.Year };

            var library = opt.IncludeTarget ? opt.Libraries.FirstOrDefault(l => Scanner.IsInside(f, l.InputRoot)) : null;
            var display = Path.GetRelativePath(Path.GetDirectoryName(seriesDir) is { Length: > 0 } p ? p : seriesDir, f);
            var item = new RenameItem { Source = f, RelativeSource = display, Info = info, Library = library };
            item.AnalysisNotes.AddRange(notes);
            if (info.Season is null || info.Episode is null) item.AnalysisNotes.Add(L.T("Staffel/Folge nicht erkennbar", "Season/episode not recognised"));
            else
            {
                if (info.SeasonFromFolder) item.AnalysisNotes.Add(L.T("Staffel aus dem Ordnernamen", "Season taken from the folder name"));
                if (info.Title.Length == 0) item.AnalysisNotes.Add(L.T("kein Folgentitel gefunden", "no episode title found"));
            }
            items.Add(item);
        }
        PreferNameTitles(items);
        FindOrphans(items);
        Render(items, opt.Template, initial: true);
        Validate(items);
        return items;
    }

    private static bool LooksLikeFileName(string title, string fileName)
    {
        if (title.Contains(' ') || title.IndexOfAny(['.', '-', '_']) < 0) return false;
        static string N(string x) => Regex.Replace(x.ToLowerInvariant(), "[^a-z0-9äöüß]", "");
        var t = N(title);
        return t.Length > 0 && N(fileName).StartsWith(t, StringComparison.Ordinal);
    }

    private static void PreferNameTitles(List<RenameItem> items)
    {
        var groups = items.Where(i => i.Info.Recognized && i.Info.TitleSource == "nfo")
            .GroupBy(i => (Path.GetDirectoryName(i.Source)!.ToLowerInvariant(), i.Info.Season, i.Info.Episode, i.Info.EpisodeEnd, i.Info.PartLetter, i.Info.Title.ToLowerInvariant()))
            .Where(g => g.Count() > 1);
        foreach (var g in groups)
        {
            var named = g.Select(i => (Item: i, Name: EpisodeParser.Parse(Path.GetFileNameWithoutExtension(i.Source), ResolveFolders(i.Source).SeasonFolder).Title)).ToList();
            var withName = named.Where(n => n.Name.Length > 0).ToList();
            if (withName.Count == 0 || named.Count - withName.Count > 1) continue;
            if (withName.Select(n => n.Name.ToLowerInvariant()).Distinct().Count() < withName.Count) continue;
            foreach (var (item, name) in withName)
            {
                item.Info = item.Info with { Title = name, TitleSource = "name" };
                item.AnalysisNotes.Add(L.T("Mehrere Dateien zu dieser Folge: Titel aus dem Dateinamen statt aus der .nfo", "Several files for this episode: title from the file name instead of the .nfo"));
            }
        }
    }

    private static readonly Regex FolderYear = new(@"\((?<y>(?:19|20)[0-9]{2})\)\s*$", RegexOptions.Compiled);

    private static List<RenameItem> AnalyzeMovies(List<string> files, RenameOptions opt, CancellationToken ct)
    {
        var items = new List<RenameItem>();
        var exts = new HashSet<string>(opt.Extensions.Select(e => e.StartsWith('.') ? e : "." + e), StringComparer.OrdinalIgnoreCase);
        var perFolder = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        int VideosIn(string dir)
        {
            if (perFolder.TryGetValue(dir, out var n)) return n;
            try
            {
                n = Directory.EnumerateFiles(dir).Count(f => exts.Contains(Path.GetExtension(f))
                    && !f.EndsWith(".part.mkv", StringComparison.OrdinalIgnoreCase)
                    && !Regex.IsMatch(Path.GetFileNameWithoutExtension(f), @"(?<![a-z])(sample|trailer)(?![a-z])", RegexOptions.IgnoreCase));
            }
            catch
            {
                n = 99;
            }
            return perFolder[dir] = n;
        }
        foreach (var f in files)
        {
            ct.ThrowIfCancellationRequested();
            var dir = Path.GetDirectoryName(f) ?? "";
            var folder = Path.GetFileName(dir);
            var stem = Path.GetFileNameWithoutExtension(f);
            if (opt.SeriesFilter.Length > 0 && !folder.Contains(opt.SeriesFilter, StringComparison.OrdinalIgnoreCase)
                && !stem.Contains(opt.SeriesFilter, StringComparison.OrdinalIgnoreCase)) continue;

            var notes = new List<string>();
            var info = EpisodeParser.ParseMovie(stem);
            var fromFolder = EpisodeParser.ParseMovie(folder);
            var ownFolder = VideosIn(dir) == 1 || info.Part is not null;
            if (ownFolder && fromFolder.Year.Length > 0 && FolderYear.IsMatch(folder))
            {
                info = info with { Title = fromFolder.Title, Year = fromFolder.Year, TitleSource = "folder",
                                   Edition = info.Edition.Length > 0 ? info.Edition : fromFolder.Edition };
            }
            else if (info.Year.Length == 0 && ownFolder && fromFolder.Year.Length > 0)
            {
                info = info with { Title = fromFolder.Title, Year = fromFolder.Year, TitleSource = "folder" };
            }

            if (opt.UseNfo)
            {
                var nfo = EpisodeParser.ReadMovieNfo(Sibling(f, ".nfo") ?? Path.ChangeExtension(f, ".nfo"))
                          ?? (ownFolder ? EpisodeParser.ReadMovieNfo(Path.Combine(dir, "movie.nfo")) : null);
                if (nfo is not null)
                {
                    if (nfo.Title.Length > 0) info = info with { Title = nfo.Title, TitleSource = "nfo" };
                    if (nfo.Year.Length > 0)
                    {
                        if (info.Year.Length > 0 && info.Year != nfo.Year)
                            notes.Add(L.T($".nfo sagt {nfo.Year}, der Name {info.Year}", $".nfo says {nfo.Year}, the name says {info.Year}"));
                        info = info with { Year = nfo.Year };
                    }
                    if (nfo.Edition.Length > 0 && info.Edition.Length == 0) info = info with { Edition = nfo.Edition };
                }
            }

            var sample = Regex.IsMatch(stem, @"(?<![a-z])(sample|trailer)(?![a-z])", RegexOptions.IgnoreCase);
            if (sample)
            {
                info = info with { Title = "" };
                notes.Add(L.T("Sample oder Trailer: wird nicht umbenannt", "Sample or trailer: not renamed"));
            }

            var library = opt.IncludeTarget ? opt.Libraries.FirstOrDefault(l => Scanner.IsInside(f, l.InputRoot)) : null;
            var display = Path.GetRelativePath(Path.GetDirectoryName(dir) is { Length: > 0 } p ? p : dir, f);
            var item = new RenameItem { Source = f, RelativeSource = display, Info = info, Library = library };
            item.AnalysisNotes.AddRange(notes);
            if (sample) { items.Add(item); continue; }
            if (!info.Recognized) item.AnalysisNotes.Add(L.T("Titel nicht erkennbar", "Title not recognised"));
            else if (info.Year.Length == 0) item.AnalysisNotes.Add(L.T("kein Jahr gefunden", "no year found"));
            else if (info.YearUncertain && info.TitleSource == "name")
                item.AnalysisNotes.Add(L.T($"{info.Year} könnte zum Titel gehören (z.B. „Wonder Woman 1984“)", $"{info.Year} could be part of the title (e.g. “Wonder Woman 1984”)"));
            items.Add(item);
        }
        Render(items, opt.Template, initial: true);
        Validate(items);
        return items;
    }

    public static void Render(IEnumerable<RenameItem> items, string template, bool initial = false)
    {
        foreach (var it in items)
        {
            it.Notes.Clear();
            it.Notes.AddRange(it.AnalysisNotes);
            if (!it.Info.Recognized)
            {
                it.NewName = it.OldName;
                it.Status = RenameStatus.Unknown;
            }
            else
            {
                if (!it.Edited) it.NewName = BuildName(template, it.Info);
                if (it.Orphans.Count > 0)
                    it.Notes.Add(it.Orphans.Count == 1
                        ? L.T($"1 Begleitdatei mit altem Namen wird mit umbenannt ({Path.GetFileName(it.Orphans[0].File)})", $"1 companion file with an old name is renamed as well ({Path.GetFileName(it.Orphans[0].File)})")
                        : L.T($"{it.Orphans.Count} Begleitdateien mit altem Namen werden mit umbenannt", $"{it.Orphans.Count} companion files with an old name are renamed as well"));
                it.Status = !it.Changes ? RenameStatus.Unchanged
                    : it.Edited ? RenameStatus.Ready
                    : it.AnalysisNotes.Count > 0 ? RenameStatus.Check
                    : RenameStatus.Ready;
            }
            if (initial) it.Selected = it.Status == RenameStatus.Ready;
            else if (!it.CanRename) it.Selected = false;
        }
    }

    public static readonly string[] Tokens = ["{S}", "{E}", "{Titel}", "{Serie}", "{Jahr}", "{Staffel}", "{Folge}"];

    public static readonly string[] MovieTokens = ["{Titel}", "{Jahr}", "{Edition}", "{Teil}"];

    public static IReadOnlyList<string> TokensFor(RenameKind k) => k == RenameKind.Movies ? MovieTokens : Tokens;

    public static string? TemplateProblem(string template, RenameKind kind = RenameKind.Series)
    {
        var t = template ?? "";
        if (kind == RenameKind.Movies)
        {
            var bad = Regex.Matches(t, @"\{(?<k>[^}]*)\}").Select(m => m.Groups["k"].Value)
                .Where(k => !new[] { "Titel", "Title", "Jahr", "Year", "Edition", "Fassung", "Teil", "Part" }.Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
            if (bad.Count > 0) return L.T($"Unbekannter Platzhalter: {{{bad[0]}}}", $"Unknown placeholder: {{{bad[0]}}}");
            if (!Regex.IsMatch(t, @"\{(Titel|Title)\}", RegexOptions.IgnoreCase)) return L.T("Ohne {Titel} bekommen alle Filme denselben Namen.", "Without {Titel} all movies get the same name.");
            return null;
        }
        var hasSeason = Regex.IsMatch(t, @"\{(S|Staffel|Season)\}", RegexOptions.IgnoreCase);
        var hasEpisode = Regex.IsMatch(t, @"\{(E|Folge|Episode)\}", RegexOptions.IgnoreCase);
        var unknown = Regex.Matches(t, @"\{(?<k>[^}]*)\}").Select(m => m.Groups["k"].Value)
            .Where(k => !new[] { "S", "E", "Titel", "Title", "Serie", "Series", "Jahr", "Year", "Staffel", "Season", "Folge", "Episode" }
                .Contains(k, StringComparer.OrdinalIgnoreCase)).ToList();
        if (unknown.Count > 0) return L.T($"Unbekannter Platzhalter: {{{unknown[0]}}}", $"Unknown placeholder: {{{unknown[0]}}}");
        if (!hasEpisode) return L.T("Ohne {E} oder {Folge} bekommen alle Folgen einer Staffel denselben Namen.", "Without {E} or {Folge} all episodes of a season get the same name.");
        if (!hasSeason) return L.T("Ohne {S} oder {Staffel} ist die Staffel nur noch am Ordner erkennbar.", "Without {S} or {Staffel} the season can only be told by the folder.");
        return null;
    }

    public static string BuildName(string template, EpisodeInfo info)
    {
        if (info.Movie) return BuildMovieName(template, info);
        var t = string.IsNullOrWhiteSpace(template) ? RenameOptions.DefaultTemplate : template;
        var digits = info.Episode >= 100 || info.EpisodeEnd >= 100 ? "000" : "00";
        var e = info.Episode!.Value.ToString(digits);
        if (info.EpisodeEnd is int e2) e += "-E" + e2.ToString(digits);
        e += info.PartLetter;
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["S"] = info.Season!.Value.ToString("00"),
            ["E"] = e,
            ["Staffel"] = info.Season!.Value.ToString(), ["Season"] = info.Season!.Value.ToString(),
            ["Folge"] = info.Episode!.Value + (info.EpisodeEnd is int x ? "-" + x : "") + info.PartLetter,
            ["Episode"] = info.Episode!.Value + (info.EpisodeEnd is int y ? "-" + y : "") + info.PartLetter,
            ["Serie"] = info.Series, ["Series"] = info.Series,
            ["Titel"] = info.Title, ["Title"] = info.Title,
            ["Jahr"] = info.Year, ["Year"] = info.Year,
        };
        return Finish(Regex.Replace(t, @"\{(?<k>[A-Za-z]+)\}", m => values.TryGetValue(m.Groups["k"].Value, out var v) ? v : m.Value));
    }

    private static string BuildMovieName(string template, EpisodeInfo info)
    {
        var t = string.IsNullOrWhiteSpace(template) ? RenameOptions.DefaultMovieTemplate : template;
        var part = info.Part is int n ? n.ToString() : "";
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Titel"] = info.Title, ["Title"] = info.Title,
            ["Jahr"] = info.Year, ["Year"] = info.Year,
            ["Edition"] = info.Edition, ["Fassung"] = info.Edition,
            ["Teil"] = part, ["Part"] = part,
        };
        var name = Regex.Replace(t, @"\{(?<k>[A-Za-z]+)\}", m => values.TryGetValue(m.Groups["k"].Value, out var v) ? v : m.Value);
        if (info.Part is int p && !Regex.IsMatch(t, @"\{(Teil|Part)\}", RegexOptions.IgnoreCase)) name += $" - CD{p}";
        return Finish(name);
    }

    private static string Finish(string name)
    {
        name = Regex.Replace(name, @"\(\s*\)|\[\s*\]", "");
        name = Regex.Replace(name, @"(\s*-\s*){2,}", " - ");
        name = Regex.Replace(name, @"\s{2,}", " ").Trim(' ', '-', '.', '_');
        name = Regex.Replace(name, @"\s+-$", "");
        return Sanitize(name);
    }

    public static string Sanitize(string name)
    {
        var sb = new StringBuilder(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            switch (c)
            {
                case ':':
                    if (i > 0 && char.IsDigit(name[i - 1]) && i + 1 < name.Length && char.IsDigit(name[i + 1])) sb.Append('.');
                    else { TrimEnd(sb); sb.Append(" - "); while (i + 1 < name.Length && name[i + 1] == ' ') i++; }
                    break;
                case '/' or '\\' or '|': sb.Append('-'); break;
                case '"': sb.Append('\''); break;
                case '?' or '*' or '<' or '>': break;
                default:
                    if (c >= 32) sb.Append(c);
                    break;
            }
        }
        var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ");
        s = s.Replace(" - - ", " - ");
        return s.Trim().TrimEnd('.', ' ');
    }

    private static void TrimEnd(StringBuilder sb)
    {
        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
    }

    // ---------------- Begleitdateien und mitwandernde Dateien ----------------

    private static readonly string[] VideoExtensions = [".mkv", ".mp4", ".m4v", ".avi", ".m2ts", ".ts", ".mov", ".wmv", ".mpg", ".mpeg", ".webm"];

    public static List<string> Companions(string videoFile)
    {
        var dir = Path.GetDirectoryName(videoFile)!;
        var name = Nfc(Path.GetFileNameWithoutExtension(videoFile));
        var result = new List<string>();
        List<string> all;
        try { all = Directory.Exists(dir) ? Directory.EnumerateFiles(dir).ToList() : []; }
        catch { return result; }
        var candidates = all.Where(f => Nfc(Path.GetFileName(f)).StartsWith(name, StringComparison.OrdinalIgnoreCase)).ToList();
        var longer = candidates.Where(f => !string.Equals(f, videoFile, StringComparison.OrdinalIgnoreCase)
                                           && VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(f => Nfc(Path.GetFileNameWithoutExtension(f))).Where(n => n.Length > name.Length).ToList();
        foreach (var f in candidates)
        {
            if (string.Equals(f, videoFile, StringComparison.OrdinalIgnoreCase)) continue;
            var own = Nfc(Path.GetFileName(f));
            if (longer.Any(l => StartsWithName(own, l))) continue;
            if (own.Length <= name.Length) continue;
            var next = own[name.Length];
            if (next != '.' && next != '-' && next != '_') continue;
            if (!CompanionExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
            var rest = own[(name.Length + 1)..];
            if (next == '-' && !rest.StartsWith("thumb", StringComparison.OrdinalIgnoreCase)
                            && !rest.StartsWith("poster", StringComparison.OrdinalIgnoreCase)
                            && !rest.StartsWith("fanart", StringComparison.OrdinalIgnoreCase)
                            && !rest.StartsWith("chapters", StringComparison.OrdinalIgnoreCase)) continue;
            if (next == '_' && !rest.StartsWith("chapters", StringComparison.OrdinalIgnoreCase)) continue;
            result.Add(f);
        }
        return result;
    }

    private static bool StartsWithName(string fileName, string videoName) =>
        fileName.Length > videoName.Length && fileName.StartsWith(videoName, StringComparison.OrdinalIgnoreCase) && fileName[videoName.Length] is '.' or '-' or '_';

    private static string Nfc(string s) => s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC);

    public static string SuffixAfter(string fileName, string baseName)
    {
        var f = Nfc(fileName);
        var b = Nfc(baseName);
        return f.Length >= b.Length && f.StartsWith(b, StringComparison.OrdinalIgnoreCase) ? f[b.Length..] : Path.GetExtension(fileName);
    }

    public static string? Sibling(string file, string ext)
    {
        var exact = Path.ChangeExtension(file, ext);
        if (File.Exists(exact)) return exact;
        var want = Nfc(Path.GetFileName(exact));
        try
        {
            return Directory.EnumerateFiles(Path.GetDirectoryName(file)!)
                .FirstOrDefault(f => string.Equals(Nfc(Path.GetFileName(f)), want, StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static readonly string[] SuffixMarks = ["_chapters", ".chapters", "-chapters", "-thumb", "-poster", "-fanart"];
    private static readonly HashSet<string> SubtitleFlags = new(StringComparer.OrdinalIgnoreCase) { "forced", "sdh", "hi", "cc", "default", "full" };

    public static string OrphanSuffix(string file)
    {
        var ext = Path.GetExtension(file);
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var mark in SuffixMarks)
            if (stem.EndsWith(mark, StringComparison.OrdinalIgnoreCase)) return stem[^mark.Length..] + ext;
        var tokens = stem.Split('.');
        var tail = new List<string>();
        for (var i = tokens.Length - 1; i > 0; i--)
        {
            var t = tokens[i];
            if (SubtitleFlags.Contains(t) || t.Length is 2 or 3 && Languages.IsKnown(t)) tail.Insert(0, t);
            else break;
        }
        return (tail.Count > 0 ? "." + string.Join(".", tail) : "") + ext;
    }

    private static void FindOrphans(List<RenameItem> items)
    {
        var places = new List<(string Dir, RenameItem Item)>();
        foreach (var it in items.Where(i => i.Info.Recognized))
        {
            places.Add((Path.GetDirectoryName(it.Source)!, it));
            if (it.Library is { } lib && Directory.Exists(lib.OutputRoot))
            {
                var target = new LibraryPaths(lib.InputRoot, lib.OutputRoot, it.Source).TargetDir;
                if (Directory.Exists(target)) places.Add((target, it));
            }
        }
        foreach (var group in places.GroupBy(p => p.Dir, p => p.Item, StringComparer.OrdinalIgnoreCase))
        {
            List<string> files;
            try { files = Directory.EnumerateFiles(group.Key).ToList(); }
            catch { continue; }
            var videos = files.Where(f => VideoExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .Select(f => Nfc(Path.GetFileNameWithoutExtension(f))).ToList();
            foreach (var f in files)
            {
                if (!CompanionExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase)) continue;
                var own = Nfc(Path.GetFileName(f));
                if (videos.Any(v => StartsWithName(own, v))) continue;
                var info = EpisodeParser.Parse(Path.GetFileNameWithoutExtension(f), null);
                if (info.Season is null || info.Episode is null) continue;
                var match = group.Where(i => i.Info.Season == info.Season && i.Info.Episode == info.Episode).ToList();
                if (match.Count != 1) continue;
                match[0].Orphans.Add(new OrphanFile(f, OrphanSuffix(f)));
            }
        }
    }

    public static List<FileMove> Moves(RenameItem item)
    {
        var moves = new List<FileMove>();
        var oldName = item.OldName;
        var newName = item.NewName;
        string Swap(string file) => Path.Combine(Path.GetDirectoryName(file)!, newName + SuffixAfter(Path.GetFileName(file), oldName));

        moves.Add(new FileMove(item.Source, Swap(item.Source)));
        foreach (var c in Companions(item.Source)) moves.Add(new FileMove(c, Swap(c)));

        if (item.Library is { } lib && Directory.Exists(lib.OutputRoot))
        {
            var paths = new LibraryPaths(lib.InputRoot, lib.OutputRoot, item.Source);
            if (File.Exists(paths.Output))
            {
                moves.Add(new FileMove(paths.Output, Path.Combine(paths.TargetDir, newName + ".mkv")));
                foreach (var c in Companions(paths.Output)) moves.Add(new FileMove(c, Swap(c)));
            }
            foreach (var f in MarkList.OwnMarks(paths.MarkDir, oldName)) moves.Add(new FileMove(f, Swap(f)));
            var verifyDir = paths.RelativeDir.Length == 0
                ? DataStore.VerifyMarks(paths.OutputRoot)
                : Path.Combine(DataStore.VerifyMarks(paths.OutputRoot), paths.RelativeDir);
            foreach (var f in SafeEnum(verifyDir, $"{oldName}.mkv.*.ok")) moves.Add(new FileMove(f, Swap(f)));
        }

        foreach (var o in item.Orphans)
        {
            var to = Path.Combine(Path.GetDirectoryName(o.File)!, newName + o.Suffix);
            if (!File.Exists(o.File) || File.Exists(to) || moves.Any(m => string.Equals(m.To, to, StringComparison.OrdinalIgnoreCase))) continue;
            moves.Add(new FileMove(o.File, to));
        }
        return moves.Where(m => !string.Equals(m.From, m.To, StringComparison.Ordinal))
            .DistinctBy(m => m.From, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static IEnumerable<string> SafeEnum(string dir, string pattern)
    {
        try { return Directory.Exists(dir) ? Directory.EnumerateFiles(dir, pattern).ToList() : []; }
        catch { return []; }
    }

    public static void Validate(List<RenameItem> items, RenameItem? only = null)
    {
        void Conflict(RenameItem it, string note)
        {
            it.Status = RenameStatus.Conflict;
            it.Selected = false;
            if (!it.Notes.Contains(note)) it.Notes.Add(note);
        }

        var wanted = new Dictionary<string, List<RenameItem>>(StringComparer.OrdinalIgnoreCase);
        foreach (var it in items.Where(i => i.Status != RenameStatus.Unknown))
        {
            var key = Path.Combine(Path.GetDirectoryName(it.Source)!, it.NewName);
            if (!wanted.TryGetValue(key, out var l)) wanted[key] = l = [];
            l.Add(it);
        }
        foreach (var (_, list) in wanted.Where(w => w.Value.Count > 1 && (only is null || w.Value.Contains(only))))
            foreach (var it in list.Where(i => i.Changes))
                Conflict(it, L.T($"{list.Count} Folgen ergeben denselben Namen", $"{list.Count} episodes end up with the same name"));

        foreach (var it in items.Where(i => i.CanRename && (only is null || i == only)))
        {
            var newFile = Path.Combine(Path.GetDirectoryName(it.Source)!, it.NewName + Path.GetExtension(it.Source));
            if (File.Exists(newFile) && !string.Equals(newFile, it.Source, StringComparison.OrdinalIgnoreCase))
            {
                Conflict(it, L.T("Eine Datei mit dem neuen Namen gibt es schon", "A file with the new name already exists"));
                continue;
            }
            if (it.Library is { } lib && Directory.Exists(lib.OutputRoot))
            {
                var paths = new LibraryPaths(lib.InputRoot, lib.OutputRoot, it.Source);
                if (Directory.Exists(paths.Lock) || File.Exists(paths.Temp))
                {
                    var owner = FileLock.ReadOwner(paths.Lock);
                    Conflict(it, owner.Length > 0 ? L.T($"gerade bei {owner} in Arbeit", $"currently in progress on {owner}") : L.T("wird gerade kodiert", "being encoded right now"));
                }
            }
        }
    }

    // ---------------- Ausfuehren und rueckgaengig ----------------

    public sealed record RenameResult(int Renamed, int Files, int Failed, List<string> Errors, string? Journal);

    public static string JournalDirFor(IEnumerable<RenameItem> items, RenameOptions opt)
    {
        if (!string.IsNullOrEmpty(opt.LocalJournalDir)) return opt.LocalJournalDir;
        if (items.Select(i => i.Library).FirstOrDefault(l => l is not null && Directory.Exists(l.OutputRoot)) is { } lib)
            return Path.Combine(lib.OutputRoot, JournalFolder);
        var first = items.FirstOrDefault();
        return Path.Combine(first is null ? Path.GetTempPath() : ResolveFolders(first.Source).SeriesDir, JournalFolder);
    }

    public static RenameResult Execute(IEnumerable<RenameItem> items, RenameOptions opt,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default)
    {
        var list = items.Where(i => i.Selected && i.CanRename).ToList();
        if (list.Count == 0) return new RenameResult(0, 0, 0, [], null);
        var journalDir = JournalDirFor(list, opt);
        Directory.CreateDirectory(journalDir);
        var journal = Path.Combine(journalDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm-ss}_{FileLock.ComputerName}.jsonl");
        using var w = new StreamWriter(journal, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        var lib0 = list.Select(i => i.Library).FirstOrDefault(l => l is not null);
        w.WriteLine(JsonSerializer.Serialize(new JournalHeader(lib0?.InputRoot ?? ResolveFolders(list[0].Source).SeriesDir, lib0?.OutputRoot ?? "", FileLock.ComputerName, DateTime.Now, list.Count)));

        int renamed = 0, files = 0, failed = 0, done = 0;
        var errors = new List<string>();
        foreach (var item in list)
        {
            ct.ThrowIfCancellationRequested();
            FileLock? lk = null;
            try
            {
                if (item.Library is { } lib && Directory.Exists(lib.OutputRoot))
                {
                    var paths = new LibraryPaths(lib.InputRoot, lib.OutputRoot, item.Source);
                    if (Directory.Exists(paths.TargetDir))
                    {
                        lk = FileLock.TryAcquire(paths.Lock, 24, out var owner, "Umbenennen");
                        if (lk is null) throw new IOException(L.T($"gerade bei {owner} in Arbeit", $"currently in progress on {owner}"));
                        if (File.Exists(paths.Temp)) throw new IOException(L.T("wird gerade kodiert (.part.mkv vorhanden)", "being encoded right now (.part.mkv exists)"));
                    }
                }
                var moves = Moves(item);
                foreach (var m in moves)
                    if (File.Exists(m.To) && !string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase))
                        throw new IOException(L.T($"existiert schon: {Path.GetFileName(m.To)}", $"already exists: {Path.GetFileName(m.To)}"));

                var doneMoves = new List<FileMove>();
                try
                {
                    foreach (var m in moves)
                    {
                        File.Move(m.From, m.To);
                        doneMoves.Add(m);
                        w.WriteLine(JsonSerializer.Serialize(m));
                    }
                }
                catch
                {
                    for (var i = doneMoves.Count - 1; i >= 0; i--)
                    {
                        try { File.Move(doneMoves[i].To, doneMoves[i].From); w.WriteLine(JsonSerializer.Serialize(new FileMove(doneMoves[i].To, doneMoves[i].From))); }
                        catch { }
                    }
                    throw;
                }
                renamed++;
                files += moves.Count;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                errors.Add($"{item.RelativeSource}: {ex.Message}");
            }
            finally
            {
                lk?.Dispose();
                progress?.Report((++done, list.Count));
            }
        }
        return new RenameResult(renamed, files, failed, errors, journal);
    }

    public sealed record JournalHeader(string InputRoot, string OutputRoot, string Computer, DateTime Time, int Count)
    {
        public string Type { get; init; } = "header";
    }

    public sealed record JournalEntry(string File, DateTime Time, string Computer, int Moves, bool Undone, string Root);

    public static List<JournalEntry> Journals(IEnumerable<string?> journalDirs)
    {
        var result = new List<JournalEntry>();
        foreach (var dir in journalDirs.Where(d => !string.IsNullOrEmpty(d)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            foreach (var f in SafeEnum(dir!, "*.jsonl"))
            {
                try
                {
                    var lines = File.ReadAllLines(f);
                    if (lines.Length == 0) continue;
                    var h = JsonSerializer.Deserialize<JournalHeader>(lines[0]);
                    if (h is null) continue;
                    result.Add(new JournalEntry(f, h.Time, h.Computer, lines.Length - 1, f.EndsWith(".rueckgaengig.jsonl", StringComparison.OrdinalIgnoreCase), h.InputRoot));
                }
                catch { }
            }
        }
        return result.OrderByDescending(j => j.Time).ToList();
    }

    public static int DeleteJournals(IEnumerable<string> files)
    {
        var n = 0;
        foreach (var f in files.Where(f => !string.IsNullOrEmpty(f) && f.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                if (!File.Exists(f)) continue;
                using (var r = new StreamReader(f))
                    if (r.ReadLine() is not { } first || JsonSerializer.Deserialize<JournalHeader>(first) is null) continue;
                File.Delete(f);
                n++;
            }
            catch { }
        }
        return n;
    }

    public static List<JournalEntry> Journals(string? outputRoot, string inputRoot) =>
        Journals([outputRoot is null ? null : Path.Combine(outputRoot, JournalFolder), Path.Combine(inputRoot, JournalFolder)]);

    public static RenameResult Undo(string journalFile, CancellationToken ct = default)
    {
        var lines = File.ReadAllLines(journalFile);
        var moves = lines.Skip(1).Select(l => JsonSerializer.Deserialize<FileMove>(l)).Where(m => m is not null).Select(m => m!).ToList();
        int ok = 0, skipped = 0;
        var errors = new List<string>();
        foreach (var m in moves)
        {
            var dir = Path.GetDirectoryName(m.To) ?? "";
            var stem = Path.GetFileNameWithoutExtension(m.To);
            if (Directory.Exists(Path.Combine(dir, stem + ".lock")) || File.Exists(Path.Combine(dir, stem + ".part.mkv")))
                return new RenameResult(0, 0, moves.Count, [L.T($"{Path.GetFileName(m.To)} wird gerade bearbeitet. Bitte später erneut rückgängig machen.", $"{Path.GetFileName(m.To)} is being processed right now. Please undo again later.")], journalFile);
        }
        for (var i = moves.Count - 1; i >= 0; i--)
        {
            ct.ThrowIfCancellationRequested();
            var m = moves[i];
            if (!File.Exists(m.To)) { skipped++; continue; }
            if (File.Exists(m.From) && !string.Equals(m.From, m.To, StringComparison.OrdinalIgnoreCase)) { skipped++; errors.Add(L.T($"alter Name belegt: {Path.GetFileName(m.From)}", $"old name taken: {Path.GetFileName(m.From)}")); continue; }
            try { File.Move(m.To, m.From); ok++; }
            catch (Exception ex) { skipped++; errors.Add($"{Path.GetFileName(m.To)}: {ex.Message}"); }
        }
        try { File.Move(journalFile, journalFile.Replace(".jsonl", ".rueckgaengig.jsonl")); } catch { }
        return new RenameResult(ok, ok, skipped, errors, journalFile);
    }
}
