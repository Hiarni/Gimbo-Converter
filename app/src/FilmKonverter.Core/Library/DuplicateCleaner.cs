using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public sealed record DuplicateDecision(string File, string Keep, long Bytes, string? Reason)
{
    public bool WillDelete => Reason is null;
    public string Why { get; init; } = "";
    public bool IsKept { get; init; }
    public bool Gone { get; init; }
    public long KeepBytes { get; init; } = -1;
    public DateTime? Changed { get; init; }
    public DateTime? KeepChanged { get; init; }
    public DuplicateSet? Set { get; init; }
}

public sealed class DuplicateResult
{
    public List<DuplicateDecision> Deleted { get; } = [];
    public long Bytes { get; set; }
    public List<(DuplicateDecision Decision, string Message)> Failed { get; } = [];
    public List<string> Errors { get; } = [];
    public List<string> Outputs { get; } = [];

    public void Deconstruct(out int deleted, out long bytes, out List<string> errors)
    {
        deleted = Deleted.Count;
        bytes = Bytes;
        errors = Errors;
    }
}

public static class DuplicateCleaner
{
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private sealed class Item(AnalyzedFile file, long size, DateTime changed)
    {
        public AnalyzedFile File { get; } = file;
        public string Path => File.Path;
        public long Size { get; } = size;
        public DateTime Changed { get; } = changed;
        public bool Fresh;
        public bool Loaded;
        public MediaInfo? Media;
        public double Dur;
        public double? End;
        public double? Len;
        public bool Whole;
        public int VideoIndex => Media?.Video?.Index ?? -1;
    }

    public static async Task<List<DuplicateDecision>> PlanAsync(IEnumerable<DuplicateSet> sets, FfmpegTools tools, CancellationToken ct,
        string? inputRoot = null, string? outputRoot = null, IProgress<AnalyzeProgress>? progress = null, TimeSpan skipNewerThan = default)
    {
        var list = sets.ToList();
        var result = new List<DuplicateDecision>();
        var items = new Dictionary<string, Item>(PathComparer);
        var originals = new HashSet<string>(PathComparer);
        var doomed = new HashSet<string>(PathComparer);
        for (var i = 0; i < list.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var set = list[i];
            progress?.Report(new AnalyzeProgress(i, list.Count, set.Group == set.Label ? set.Label : $"{set.Group} · {set.Label}"));
            foreach (var dirGroup in set.Files.GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase))
            {
                var files = dirGroup.ToList();
                if (files.Count < 2)
                {
                    result.Add(Stay(set, files[0].Path, files[0].SourceBytes, L.T("liegt in einem anderen Ordner als die übrigen Fassungen. Verglichen und gelöscht wird nur innerhalb eines Ordners.",
                        "is in a different folder than the other versions. Files are only compared and deleted within one folder.")));
                    continue;
                }
                result.AddRange(await DecideAsync(set, files, tools, items, originals, doomed, inputRoot, outputRoot, skipNewerThan, ct).ConfigureAwait(false));
            }
        }
        for (var i = 0; i < result.Count; i++)
            if (!result[i].WillDelete && doomed.Contains(result[i].File))
                result[i] = result[i] with { IsKept = false, Reason = L.T("wird in einer anderen Gruppe gelöscht, dort steht der Grund", "is deleted in another group, the reason is shown there") };
        progress?.Report(new AnalyzeProgress(list.Count, list.Count, ""));
        return result;
    }

    private static DuplicateDecision Stay(DuplicateSet set, string file, long bytes, string reason, string keep = "", long keepBytes = -1) =>
        new(file, keep, bytes, reason) { Set = set, KeepBytes = keepBytes };

    private static async Task<List<DuplicateDecision>> DecideAsync(DuplicateSet set, List<AnalyzedFile> files, FfmpegTools tools,
        Dictionary<string, Item> known, HashSet<string> originals, HashSet<string> doomed, string? inputRoot, string? outputRoot, TimeSpan skipNewerThan, CancellationToken ct)
    {
        var result = new List<DuplicateDecision>();
        var present = new List<Item>();
        foreach (var f in files)
        {
            if (known.TryGetValue(f.Path, out var seen)) { present.Add(seen); continue; }
            Item item;
            try
            {
                var fi = new FileInfo(f.Path);
                if (!fi.Exists)
                {
                    result.Add(Stay(set, f.Path, f.SourceBytes, L.T("gibt es nicht mehr", "no longer exists")) with { Gone = true });
                    continue;
                }
                item = new Item(f, fi.Length, fi.LastWriteTimeUtc) { Fresh = Scanner.IsFresh(fi, skipNewerThan) };
            }
            catch
            {
                result.Add(Stay(set, f.Path, f.SourceBytes, Unreadable()));
                continue;
            }
            present.Add(known[f.Path] = item);
        }
        if (present.Count < 2)
        {
            foreach (var x in present)
                result.Add(Stay(set, x.Path, x.Size, L.T("die andere Fassung gibt es nicht mehr", "the other version no longer exists")));
            return result;
        }

        if (present.Any(x => IsBusy(x.Path, inputRoot, outputRoot)))
        {
            foreach (var x in present) result.Add(Stay(set, x.Path, x.Size, BusyText()));
            return result;
        }
        if (present.Any(x => x.Fresh))
        {
            foreach (var x in present)
                result.Add(Stay(set, x.Path, x.Size, x.Fresh
                    ? L.T("wurde eben erst kopiert oder geändert: später noch einmal prüfen", "was copied or changed just now: check again later")
                    : L.T("eine andere Fassung wurde eben erst kopiert oder geändert: später noch einmal prüfen", "another version was copied or changed just now: check again later")));
            return result;
        }

        var sorted = present.OrderBy(x => x.Size).ThenBy(x => Path.GetFileName(x.Path).Length).ThenBy(x => Path.GetFileName(x.Path), StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var x in sorted) await LoadAsync(x, tools, ct).ConfigureAwait(false);

        var lens = sorted.Where(x => x.Len is not null).Select(x => x.Len!.Value).ToList();
        var maxLen = lens.Count > 0 ? lens.Max() : 0;
        var tol = Math.Max(3, maxLen * 0.01);
        var keep = lens.Count > 0 ? sorted.FirstOrDefault(x => x.Whole && x.Len >= maxLen - tol) : null;
        if (keep is null)
        {
            foreach (var x in sorted)
                result.Add(Stay(set, x.Path, x.Size, x.Media is null ? Unreadable()
                    : L.T("keine Fassung ist bis zum Ende lesbar: nichts wird gelöscht, bitte selbst prüfen", "no version is readable to its end: nothing is deleted, please check yourself")));
            return result;
        }

        result.Add(new DuplicateDecision(keep.Path, keep.Path, keep.Size, L.T("die kleinste vollständige Fassung", "the smallest complete version")) { IsKept = true, KeepBytes = keep.Size, Changed = keep.Changed, KeepChanged = keep.Changed, Set = set });
        var names = DuplicateNames.Identities(sorted.Select(x => x.Path));
        foreach (var x in sorted)
        {
            if (ReferenceEquals(x, keep)) continue;
            ct.ThrowIfCancellationRequested();
            var sameName = names[x.Path] == names[keep.Path];
            var (delete, stay) = await JudgeAsync(tools, keep, x, tol, sameName, ct).ConfigureAwait(false);
            if (delete is not null)
            {
                if (doomed.Contains(keep.Path)) { delete = null; stay = L.T("die Fassung, mit der verglichen wurde, wird selbst gelöscht: erst danach erneut prüfen", "the version it was compared with is deleted itself: check again afterwards"); }
                else if (originals.Contains(x.Path)) { delete = null; stay = L.T("wird in einer anderen Gruppe als Original gebraucht", "is needed as the original in another group"); }
            }
            if (delete is null)
            {
                result.Add(Stay(set, x.Path, x.Size, stay!, keep.Path, keep.Size));
                continue;
            }
            doomed.Add(x.Path);
            originals.Add(keep.Path);
            result.Add(new DuplicateDecision(x.Path, keep.Path, x.Size, null) { Why = delete, KeepBytes = keep.Size, Changed = x.Changed, KeepChanged = keep.Changed, Set = set });
        }
        return result;
    }

    private static string Unreadable() => L.T("nicht lesbar (wird vielleicht gerade kopiert, oder das Laufwerk war kurz weg), bitte selbst prüfen",
        "not readable (maybe still being copied, or the drive was briefly away), please check yourself");

    private static string EndUnreadable() => L.T("Ende nicht lesbar, bitte selbst prüfen", "end not readable, please check yourself");

    private static string BusyText() => L.T("wird gerade kodiert, nachgebessert, umgewandelt oder kopiert: später noch einmal prüfen",
        "is being encoded, touched up, converted or copied right now: check again later");

    private static async Task LoadAsync(Item x, FfmpegTools tools, CancellationToken ct)
    {
        if (x.Loaded) return;
        x.Media = await ContentCheck.ProbeAsync(tools, x.Path, ct).ConfigureAwait(false);
        if (x.Media is { DurationSeconds: > 0 } m)
        {
            x.Dur = m.DurationSeconds;
            x.End = await ContentCheck.TailEndAsync(tools, x.Path, x.Dur, ct).ConfigureAwait(false);
            x.Whole = ContentCheck.IsWhole(x.Dur, x.End);
            if (x.End is double e) x.Len = x.Whole ? x.Dur : Math.Min(x.Dur, e);
        }
        x.Loaded = true;
    }

    private static async Task<(string? Delete, string? Stay)> JudgeAsync(FfmpegTools tools, Item keep, Item x, double tol, bool sameName, CancellationToken ct)
    {
        if (x.Media?.Video is null) return (null, Unreadable());
        if (x.Dur <= 0) return (null, L.T("Laufzeit unbekannt, bitte selbst prüfen", "duration unknown, please check yourself"));
        var keepLen = keep.Len!.Value;
        var len = x.Len ?? x.Dur;
        if (len < keepLen - tol)
        {
            var times = L.T($"{Fmt.Time(len)} statt {Fmt.Time(keepLen)}", $"{Fmt.Time(len)} instead of {Fmt.Time(keepLen)}");
            if (DuplicateNames.IsSample(x.Path) && !DuplicateNames.IsSample(keep.Path) && len <= keepLen * 0.5)
                return (L.T($"kurzer Ausschnitt (Sample) neben dem Original ({times})", $"short excerpt (sample) next to its original ({times})"), null);
            if (!sameName)
                return (null, L.T($"andere Laufzeit ({times}): andere Folge oder Fassung", $"different duration ({times}): another episode or version"));
            if (WhyNot(keep.Media, x.Media, keep.Size, x.Size, sameLength: false) is { } other) return (null, L.T($"kürzer ({times}); ", $"shorter ({times}); ") + other);
            var prefix = await ContentCheck.SamePictureAsync(tools, keep.Path, x.Path, len, keep.VideoIndex, x.VideoIndex, ct).ConfigureAwait(false);
            if (prefix == PictureMatch.Different)
                return (null, L.T($"kürzer ({times}) und anderes Bild: eine andere Aufnahme", $"shorter ({times}) and a different picture: another recording"));
            if (prefix != PictureMatch.Same)
                return (null, L.T($"kürzer ({times}), das Bild ließ sich nicht vergleichen: bitte selbst prüfen", $"shorter ({times}), the picture could not be compared: please check yourself"));
            if (x.Whole)
                return (L.T($"unvollständige Kopie: endet nach {Fmt.Time(len)}, das Original läuft {Fmt.Time(keepLen)}, das Bild stimmt überein",
                    $"incomplete copy: ends after {Fmt.Time(len)}, the original runs {Fmt.Time(keepLen)}, the picture matches"), null);
            if (x.End is not double end) return (null, EndUnreadable());
            if (await ContentCheck.TailEndAsync(tools, x.Path, x.Dur, ct).ConfigureAwait(false) is not double again || Math.Abs(again - end) > 1)
                return (null, L.T("das Ende ließ sich nicht zweimal gleich lesen, bitte selbst prüfen", "the end could not be read the same way twice, please check yourself"));
            return (L.T($"unvollständige Kopie: nur bis {Fmt.Time(end)} von {Fmt.Time(x.Dur)} lesbar, das Bild stimmt mit dem Original überein",
                $"incomplete copy: readable only up to {Fmt.Time(end)} of {Fmt.Time(x.Dur)}, the picture matches the original"), null);
        }
        if (!x.Whole && x.Size <= keep.Size) return (null, EndUnreadable());
        if (WhyNot(keep.Media, x.Media, keep.Size, x.Size) is { } why) return (null, why);
        var same = await ContentCheck.SamePictureAsync(tools, keep.Path, x.Path, keepLen, keep.VideoIndex, x.VideoIndex, ct).ConfigureAwait(false);
        if (same == PictureMatch.Different)
            return (null, L.T("gleiche Laufzeit, aber anderes Bild: andere Folge oder Fassung", "same duration but a different picture: another episode or version"));
        if (same != PictureMatch.Same)
            return (null, L.T("das Bild ließ sich nicht vergleichen, bitte selbst prüfen", "the picture could not be compared, please check yourself"));
        var checkedText = L.T("Laufzeit, Auflösung, Ton, Sprachen, Untertitel und Bild verglichen", "duration, resolution, audio, languages, subtitles and picture compared");
        return (x.Size == keep.Size
            ? L.T($"gleich groß und gleichwertig, eine Kopie ({checkedText})", $"same size and equivalent, a copy ({checkedText})")
            : L.T($"größer, sonst gleichwertig ({checkedText})", $"larger, otherwise equivalent ({checkedText})"), null);
    }

    public static string? WhyNot(MediaInfo? keep, MediaInfo? other, long keepBytes, long otherBytes, bool sameLength = true)
    {
        if (keep?.Video is not { } kv) return L.T("behaltene Datei nicht lesbar", "kept file not readable");
        if (other?.Video is not { } ov) return Unreadable();
        var d = other.DurationSeconds;
        if (sameLength && (keep.DurationSeconds <= 0 || d <= 0 || Math.Abs(keep.DurationSeconds - d) > Math.Max(1, keep.DurationSeconds * 0.001)))
            return L.T($"Laufzeit weicht ab ({Fmt.Time(d)} statt {Fmt.Time(keep.DurationSeconds)}): andere Folge oder Fassung", $"duration differs ({Fmt.Time(d)} instead of {Fmt.Time(keep.DurationSeconds)}): another episode or version");
        if (sameLength && otherBytes < keepBytes) return L.T("kleiner als die behaltene Fassung", "smaller than the kept version");
        if (kv.DisplayWidth != ov.DisplayWidth || kv.DisplayHeight != ov.DisplayHeight)
            return L.T($"andere Auflösung ({ov.DisplayWidth}x{ov.DisplayHeight} statt {kv.DisplayWidth}x{kv.DisplayHeight}): zwei Fassungen", $"different resolution ({ov.DisplayWidth}x{ov.DisplayHeight} instead of {kv.DisplayWidth}x{kv.DisplayHeight}): two versions");
        if (keep.Hdr != other.Hdr)
            return L.T($"andere Bildart ({MediaInfo.HdrText(other.Hdr)} statt {MediaInfo.HdrText(keep.Hdr)}): zwei Fassungen", $"different picture type ({MediaInfo.HdrText(other.Hdr)} instead of {MediaInfo.HdrText(keep.Hdr)}): two versions");
        int ka = keep.Audio.Count(), oa = other.Audio.Count();
        if (ka < oa) return L.T($"hat mehr Tonspuren als die kleinere Fassung ({oa} statt {ka})", $"has more audio tracks than the smaller version ({oa} instead of {ka})");
        int kc = keep.Audio.Select(a => a.Channels).DefaultIfEmpty().Max(), oc = other.Audio.Select(a => a.Channels).DefaultIfEmpty().Max();
        if (kc < oc) return L.T($"hat mehr Tonkanäle als die kleinere Fassung ({oc} statt {kc})", $"has more audio channels than the smaller version ({oc} instead of {kc})");
        int ks = keep.Subtitles.Count(), os = other.Subtitles.Count();
        if (ks < os) return L.T($"hat mehr Untertitel als die kleinere Fassung ({os} statt {ks})", $"has more subtitles than the smaller version ({os} instead of {ks})");
        var have = LanguagesOf(keep);
        var missing = LanguagesOf(other).Where(l => !have.Contains(l)).ToList();
        if (missing.Count > 0)
        {
            var text = string.Join(", ", missing.Select(l => Languages.Display(l) is { Length: > 0 } n ? n : l));
            return L.T($"andere Sprache ({text} fehlt in der kleineren Fassung)", $"different language ({text} is missing in the smaller version)");
        }
        return null;
    }

    public static HashSet<string> LanguagesOf(MediaInfo m)
    {
        var set = m.Audio.Where(a => !Languages.IsUnknown(a.Language)).Select(a => Languages.Normalize(a.Language)).ToHashSet();
        if (set.Count > 0) return set;
        var name = Path.GetFileName(m.Path);
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "(?<![a-z])(german|deutsch)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) set.Add("ger");
        if (System.Text.RegularExpressions.Regex.IsMatch(name, "(?<![a-z])(english|englisch)(?![a-z])", System.Text.RegularExpressions.RegexOptions.IgnoreCase)) set.Add("eng");
        return set;
    }

    public static LibraryAnalysis WithoutDeleted(LibraryAnalysis a, IEnumerable<DuplicateDecision> deleted)
    {
        var keepOf = new Dictionary<string, string>(PathComparer);
        foreach (var d in deleted) keepOf[d.File] = d.Keep;
        if (keepOf.Count == 0) return a;
        var listed = new HashSet<string>(a.Files.Select(f => f.Path), PathComparer);
        var inSets = new Dictionary<string, AnalyzedFile>(PathComparer);
        foreach (var f in a.Duplicates.SelectMany(s => s.Files)) inSets[f.Path] = f;
        var files = new List<AnalyzedFile>();
        var replaced = new Dictionary<string, AnalyzedFile>(PathComparer);
        foreach (var f in a.Files)
        {
            if (!keepOf.TryGetValue(f.Path, out var keep)) { files.Add(f); continue; }
            if (listed.Contains(keep) || !inSets.TryGetValue(keep, out var k) || !PathComparer.Equals(Path.ChangeExtension(keep, null), Path.ChangeExtension(f.Path, null))) continue;
            listed.Add(keep);
            files.Add(replaced[keep] = k with { State = f.State, OutputBytes = f.OutputBytes, EstimatedBytes = Math.Min(k.SourceBytes, f.EstimatedBytes), Reason = f.Reason });
        }
        var sets = a.Duplicates
            .Select(s => s.Files.Any(f => keepOf.ContainsKey(f.Path) || replaced.ContainsKey(f.Path))
                ? new DuplicateSet(s.Group, s.Label, s.Files.Where(f => !keepOf.ContainsKey(f.Path)).Select(f => replaced.GetValueOrDefault(f.Path, f)).ToList())
                : s)
            .Where(s => s.Files.Count > 1)
            .OrderByDescending(s => s.Reclaimable).ToList();
        return new LibraryAnalysis
        {
            InputDir = a.InputDir,
            Files = files,
            Groups = LibraryAnalyzer.Summarize(a.InputDir, files),
            Calibration = a.Calibration,
            CalibrationSamples = a.CalibrationSamples,
            Created = a.Created,
            Duplicates = sets,
            Missing = a.Missing,
            Took = a.Took,
            FromCache = a.FromCache,
        };
    }

    private static bool Exists(string path) => Directory.Exists(path) || File.Exists(path);

    public static bool IsBusy(string file, string? inputRoot, string? outputRoot)
    {
        var dir = Path.GetDirectoryName(file) ?? "";
        var stem = Path.GetFileNameWithoutExtension(file);
        foreach (var s in new[] { stem, DuplicateNames.CleanName(file) }.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Exists(Path.Combine(dir, s + ".lock")) || Exists(Path.Combine(dir, s + ".reparatur.lock"))) return true;
        if (File.Exists(Path.Combine(dir, stem + ".part.mkv")) || File.Exists(Jobs.CleanupRun.TempFor(file))) return true;
        if (File.Exists(file) && Scanner.StillBeingWritten(file)) return true;
        try
        {
            if (Directory.EnumerateFiles(dir, "*.gimbo-tmp").Any(t => Path.GetFileName(t).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase))) return true;
        }
        catch
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(inputRoot) || string.IsNullOrWhiteSpace(outputRoot) || !Scanner.IsInside(file, inputRoot)) return false;
        var p = new LibraryPaths(inputRoot, outputRoot, file);
        return Exists(p.Lock) || File.Exists(p.Temp) || File.Exists(Jobs.CleanupRun.TempFor(p.Output));
    }

    public static DuplicateResult Execute(IEnumerable<DuplicateDecision> plan, string? inputRoot, string? outputRoot)
    {
        var todo = plan.Where(d => d.WillDelete && !d.IsKept).ToList();
        var result = new DuplicateResult();
        var keeps = new HashSet<string>(todo.Where(d => d.Keep.Length > 0).Select(d => d.Keep), PathComparer);
        var gone = new HashSet<string>(PathComparer);
        var library = !string.IsNullOrWhiteSpace(inputRoot) && !string.IsNullOrWhiteSpace(outputRoot)
                      && !(Scanner.IsInside(inputRoot, outputRoot) && Scanner.IsInside(outputRoot, inputRoot));
        foreach (var d in todo)
        {
            if (gone.Contains(d.File)) continue;
            try
            {
                if (d.Keep.Length == 0 || PathComparer.Equals(d.File, d.Keep)) throw new IOException(L.T("keine behaltene Fassung", "no kept version"));
                if (keeps.Contains(d.File)) throw new IOException(L.T("wird für eine andere Datei als Original gebraucht, nicht gelöscht", "is needed as the original for another file, not deleted"));
                if (gone.Contains(d.Keep) || !File.Exists(d.Keep)) throw new IOException(L.T("behaltene Datei fehlt inzwischen, nicht gelöscht", "kept file is missing meanwhile, not deleted"));
                var now = new FileInfo(d.File);
                if (!now.Exists) throw new IOException(L.T("gibt es nicht mehr", "no longer exists"));
                if (IsBusy(d.File, inputRoot, outputRoot) || IsBusy(d.Keep, inputRoot, outputRoot))
                    throw new IOException(L.T("wird inzwischen kodiert, nachgebessert oder umgewandelt, nicht gelöscht", "is being encoded, touched up or converted meanwhile, not deleted"));
                var keepNow = new FileInfo(d.Keep);
                if (now.Length != d.Bytes || (d.KeepBytes >= 0 && keepNow.Length != d.KeepBytes)
                    || (d.Changed is { } t && now.LastWriteTimeUtc != t) || (d.KeepChanged is { } kt && keepNow.LastWriteTimeUtc != kt))
                    throw new IOException(L.T("hat sich seit der Prüfung geändert, nicht gelöscht", "changed since the check, not deleted"));
                File.Delete(d.File);
                gone.Add(d.File);
                result.Deleted.Add(d);
                result.Bytes += d.Bytes;
            }
            catch (Exception ex)
            {
                result.Failed.Add((d, ex.Message));
                result.Errors.Add($"{Path.GetFileName(d.File)}: {ex.Message}");
                continue;
            }
            if (!library || !Scanner.IsInside(d.File, inputRoot!)) continue;
            try
            {
                var paths = new LibraryPaths(inputRoot!, outputRoot!, d.File);
                var keepPaths = new LibraryPaths(inputRoot!, outputRoot!, d.Keep);
                if (PathComparer.Equals(paths.Output, keepPaths.Output) || keeps.Contains(paths.Output)) continue;
                if (Scanner.IsInside(paths.Output, inputRoot!) && !Scanner.IsInside(outputRoot!, inputRoot!)) continue;
                MarkList.Clear(outputRoot!, paths.Output);
                if (!File.Exists(paths.Output) || Exists(paths.Lock) || !(File.Exists(keepPaths.Output) || File.Exists(d.Keep))) continue;
                var size = new FileInfo(paths.Output).Length;
                File.Delete(paths.Output);
                result.Outputs.Add(paths.Output);
                try { File.Delete(MarkList.VerifyFile(outputRoot!, paths.Output, size)); } catch { }
            }
            catch (Exception ex)
            {
                result.Errors.Add(L.T($"{Path.GetFileName(d.File)}: komprimierte Fassung im Zielordner nicht gelöscht ({ex.Message})", $"{Path.GetFileName(d.File)}: compressed version in the target folder not deleted ({ex.Message})"));
            }
        }
        return result;
    }
}
