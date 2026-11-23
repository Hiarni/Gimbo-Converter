using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Library;

public enum AnalysisState
{
    Open,
    Done,
    Skip,
    InProgress,
    Unreadable,
}

public sealed record AnalyzedFile(
    string Path,
    string Relative,
    string Group,
    long SourceBytes,
    double DurationSeconds,
    string Codec,
    int Height,
    AnalysisState State,
    long OutputBytes,
    long EstimatedBytes,
    string Reason)
{
    public int? Season { get; init; }
    public int? Episode { get; init; }
    public int? EpisodeEnd { get; init; }
    public string MovieKey { get; init; } = "";
    public string MovieLabel { get; init; } = "";

    public IEnumerable<int> Episodes => Episode is int e ? Enumerable.Range(e, Math.Max(1, (EpisodeEnd ?? e) - e + 1)) : [];

    public string ResolutionText => Height > 0 ? LibraryAnalyzer.ResolutionClass(Height) : "?";

    public double BytesPerSecond => DurationSeconds > 0 ? SourceBytes / DurationSeconds : 0;

    public long EstimatedSaving => State == AnalysisState.Open ? Math.Max(0, SourceBytes - EstimatedBytes) : 0;

    public long ActualSaving => State == AnalysisState.Done ? Math.Max(0, SourceBytes - OutputBytes) : 0;
}

public sealed class GroupSummary
{
    public string Name { get; init; } = "";
    public string SourcePath { get; init; } = "";
    public int Files { get; set; }
    public int Done { get; set; }
    public int Open { get; set; }
    public int Skip { get; set; }
    public int InProgress { get; set; }
    public int Unreadable { get; set; }
    public long SourceBytes { get; set; }
    public long OpenBytes { get; set; }
    public long SavedBytes { get; set; }
    public long EstimatedSaving { get; set; }
    public double DurationSeconds { get; set; }
    public Dictionary<string, int> Codecs { get; } = new(StringComparer.OrdinalIgnoreCase);

    public Dictionary<string, int> SkipReasons { get; } = new(StringComparer.Ordinal);

    public double AvgMbit => DurationSeconds > 0 ? SourceBytes * 8d / DurationSeconds / 1e6 : 0;

    public double DonePercent => Files > 0 ? Done * 100d / Files : 0;

    public string CodecText => string.Join(", ", Codecs.OrderByDescending(c => c.Value).Take(3)
        .Select(c => Codecs.Count == 1 ? c.Key : $"{c.Key} {c.Value}"));
}

public sealed record AnalyzeProgress(int Done, int Total, string Current);

public sealed record DuplicateSet(string Group, string Label, List<AnalyzedFile> Files)
{
    public long Bytes => Files.Sum(f => f.SourceBytes);
    public long Reclaimable => Bytes - (Files.Count > 0 ? Files[0].SourceBytes : 0);

    public static int ResolutionRank(int height) => height >= 1800 ? 4 : height >= 900 ? 3 : height >= 650 ? 2 : height > 0 ? 1 : 0;

    public static int CodecRank(string codec) => codec.ToLowerInvariant().Replace(".", "") switch { "av1" => 3, "hevc" => 2, "h264" => 1, _ => 0 };
}

public sealed record MissingSet(string Group, int Season, int Have, int Max, List<int> Missing)
{
    public string MissingText => string.Join(", ", Ranges(Missing));

    private static IEnumerable<string> Ranges(List<int> eps)
    {
        for (var i = 0; i < eps.Count;)
        {
            var j = i;
            while (j + 1 < eps.Count && eps[j + 1] == eps[j] + 1) j++;
            yield return j == i ? $"E{eps[i]:00}" : $"E{eps[i]:00}–E{eps[j]:00}";
            i = j + 1;
        }
    }
}

public sealed class LibraryAnalysis
{
    public string InputDir { get; init; } = "";
    public List<AnalyzedFile> Files { get; init; } = [];
    public List<GroupSummary> Groups { get; init; } = [];
    public double Calibration { get; init; } = 1;
    public int CalibrationSamples { get; init; }
    public DateTime Created { get; init; } = DateTime.Now;
    public List<DuplicateSet> Duplicates { get; init; } = [];
    public List<MissingSet> Missing { get; init; } = [];
    public TimeSpan Took { get; init; }
    public int FromCache { get; init; }

    public long SourceBytes => Files.Sum(f => f.SourceBytes);
    public long SavedBytes => Files.Sum(f => f.ActualSaving);
    public long EstimatedSaving => Files.Sum(f => f.EstimatedSaving);
    public long OpenBytes => Files.Where(f => f.State == AnalysisState.Open).Sum(f => f.SourceBytes);
    public int Count(AnalysisState s) => Files.Count(f => f.State == s);
    public double DurationSeconds => Files.Sum(f => f.DurationSeconds);

    public List<(string Codec, int Files, long Bytes)> CodecShare => Files
        .GroupBy(f => f.Codec, StringComparer.OrdinalIgnoreCase)
        .Select(g => (g.Key, g.Count(), g.Sum(f => f.SourceBytes)))
        .OrderByDescending(x => x.Item3).ToList();

    public List<(string Name, int Files, long Bytes)> ResolutionShare => Files
        .GroupBy(f => LibraryAnalyzer.ResolutionClass(f.Height))
        .Select(g => (g.Key, g.Count(), g.Sum(f => f.SourceBytes)))
        .OrderByDescending(x => x.Item3).ToList();
}

public static class LibraryAnalyzer
{
    public const double Base1080Kbit = 2600;

    public static string CodecName(string codec) => codec.ToLowerInvariant() switch
    {
        "" => "?",
        "h264" => "H.264",
        "hevc" => "HEVC",
        "av1" => "AV1",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "mpeg1video" => "MPEG-1",
        "vc1" => "VC-1",
        "vp9" => "VP9",
        "vp8" => "VP8",
        "msmpeg4v3" => "DivX 3",
        var c => c.ToUpperInvariant(),
    };

    public static string ResolutionClass(int height) => height switch
    {
        <= 0 => "?",
        <= 576 => "SD",
        <= 720 => "720p",
        <= 1080 => "1080p",
        <= 1440 => "1440p",
        _ => "2160p",
    };

    public static long Estimate(MediaInfo m, EncodePlan plan, Profile p)
    {
        var v = plan.Video;
        double w = Math.Max(1, v.DisplayWidth), h = Math.Max(1, v.DisplayHeight);
        if (p.LimitsResolution && (w > p.MaxWidth || h > p.MaxHeight))
        {
            var f = Math.Min(p.MaxWidth / w, p.MaxHeight / h);
            w *= f;
            h *= f;
        }
        var fps = plan.OutputFps > 0 ? plan.OutputFps : m.Fps;
        if (fps <= 0 || fps > 120) fps = 24;
        var videoKbit = Base1080Kbit * Math.Pow(w * h / (1920d * 1080d), 0.75) * Math.Sqrt(fps / 24d);
        if (m.VideoRate.Kbit is double src && src > 0) videoKbit = plan.VideoCopy ? src : Math.Min(videoKbit, src * 0.9);

        double audioKbit = 0;
        foreach (var t in plan.Audio.Tracks)
        {
            if (t.Convert && t.Codec != "flac") { audioKbit += t.BitrateKbit; continue; }
            var s = m.Streams.FirstOrDefault(x => x.Index == t.SourceIndex);
            var known = s?.KnownRateKbit;
            audioKbit += known ?? (t.Lossless || t.Codec == "flac" ? 1500 : (s?.Channels ?? 2) > 2 ? 640 : 192);
        }
        return (long)((videoKbit + audioKbit) * 1000 / 8 * Math.Max(0, m.DurationSeconds));
    }

    public static async Task<LibraryAnalysis> AnalyzeAsync(Profile p, FfmpegTools tools, ProbeCache cache,
        IProgress<AnalyzeProgress>? progress, CancellationToken ct, int parallel = 4)
    {
        var started = DateTime.Now;
        if (!Directory.Exists(p.InputDir)) throw new DirectoryNotFoundException(L.T($"Quellordner nicht erreichbar: {p.InputDir}", $"Source folder not reachable: {p.InputDir}"));
        var filter = ScanFilter.FromProfile(p);
        var scan = await Task.Run(() => Scanner.Find(p.InputDir, new ScanFilter
        {
            Extensions = filter.Extensions,
            ExcludePatterns = filter.ExcludePatterns,
            MinBytes = filter.MinBytes,
            ExcludeDir = filter.ExcludeDir,
        }), ct).ConfigureAwait(false);
        var files = scan.Files.Concat(scan.DroppedDuplicates).ToList();
        var mainCount = scan.Files.Count;
        if (filter.ExcludePatterns.Length > 0)
        {
            var known = new HashSet<string>(files, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var everything = await Task.Run(() => Scanner.Find(p.InputDir, new ScanFilter { Extensions = filter.Extensions, ExcludeDir = filter.ExcludeDir }), ct).ConfigureAwait(false);
            files.AddRange(everything.Files.Concat(everything.DroppedDuplicates).Where(f => !known.Contains(f) && DuplicateNames.IsSample(f)));
        }
        var results = new (AnalyzedFile File, MediaInfo? Media, EncodePlan? Plan)[files.Count];
        var perDir = files.GroupBy(f => Path.GetDirectoryName(f) ?? "", StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.Count(), StringComparer.OrdinalIgnoreCase);
        var done = 0;
        var fromCache = 0;
        progress?.Report(new AnalyzeProgress(0, files.Count, ""));

        await Parallel.ForEachAsync(Enumerable.Range(0, files.Count),
            new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(parallel, 1, 16), CancellationToken = ct },
            async (i, token) =>
            {
                var f = files[i];
                if (cache.Get(f) is not null) Interlocked.Increment(ref fromCache);
                MediaInfo? media;
                try { media = await cache.GetOrProbeAsync(tools, f, token).ConfigureAwait(false); }
                catch (Exception ex) when (ex is not OperationCanceledException) { media = null; }
                results[i] = Classify(p, f, media, perDir);
                var n = Interlocked.Increment(ref done);
                if (n % 10 == 0 || n == files.Count) progress?.Report(new AnalyzeProgress(n, files.Count, Path.GetRelativePath(p.InputDir, f)));
            }).ConfigureAwait(false);

        var ratios = results
            .Where(r => r.File.State == AnalysisState.Done && r.Media is not null && r.Plan is { VideoCopy: false } && r.File.OutputBytes > 0)
            .Select(r => r.File.OutputBytes / (double)Math.Max(1, Estimate(r.Media!, r.Plan!, p)))
            .Where(x => x is > 0.05 and < 20)
            .OrderBy(x => x).ToList();
        var factor = ratios.Count >= 5 ? Math.Clamp(ratios[ratios.Count / 2], 0.25, 4) : 1;

        var all = results.Select(r => r.File.State == AnalysisState.Open && r.Plan is not { VideoCopy: true }
            ? r.File with { EstimatedBytes = Math.Min(r.File.SourceBytes, (long)(r.File.EstimatedBytes * factor)) }
            : r.File).ToList();
        var list = all.Take(mainCount).ToList();
        var dropped = all.Skip(mainCount).Select(f => f with
        {
            State = AnalysisState.Skip,
            Reason = DuplicateNames.IsSample(f.Path)
                ? L.T("Sample: wird nicht kodiert", "sample: not encoded")
                : L.T("gleicher Name, andere Endung: wird nicht kodiert", "same name, other extension: not encoded"),
        }).ToList();

        cache.Prune(p.InputDir, new HashSet<string>(files, OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal));
        cache.Save();

        return new LibraryAnalysis
        {
            InputDir = p.InputDir,
            Files = list,
            Groups = Summarize(p.InputDir, list),
            Duplicates = FindDuplicates(list.Concat(dropped)),
            Missing = FindMissing(list.Concat(dropped)),
            Calibration = factor,
            CalibrationSamples = ratios.Count,
            Took = DateTime.Now - started,
            FromCache = fromCache,
        };
    }

    private static (AnalyzedFile, MediaInfo?, EncodePlan?) Classify(Profile p, string file, MediaInfo? media, IReadOnlyDictionary<string, int>? perDir = null)
    {
        var rel = Path.GetRelativePath(p.InputDir, file);
        var parts = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var group = parts.Length > 1 ? parts[0] : Path.GetFileName(file);
        long size = 0;
        try { size = new FileInfo(file).Length; } catch { }
        var v = media?.Video;
        var codec = CodecName(v?.CodecName ?? "");
        var height = v is null ? 0 : Profile.ClassHeight(v.DisplayWidth, v.DisplayHeight);
        var dur = media?.DurationSeconds ?? 0;
        var stem = Path.GetFileNameWithoutExtension(file);
        var parent = Path.GetFileName(Path.GetDirectoryName(file) ?? "");
        var ep = Rename.EpisodeParser.Parse(stem, parent);
        string movieKey = "", movieLabel = "";
        if (ep.Season is null || ep.Episode is null)
        {
            var mvStem = Rename.EpisodeParser.ParseMovie(stem);
            var mv = mvStem;
            var alone = perDir is null || perDir.GetValueOrDefault(Path.GetDirectoryName(file) ?? "") == 1;
            if (mv.Year.Length == 0 && parts.Length > 1 && (alone || mvStem.Part is not null))
                mv = Rename.EpisodeParser.ParseMovie(parent) with { Part = mvStem.Part };
            if (mv.Title.Length > 0 && mv.Year.Length > 0)
            {
                movieKey = new string(mv.Title.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray()) + "|" + mv.Year + "|" + mv.Part;
                movieLabel = $"{mv.Title} ({mv.Year})" + (mv.Part is int pt ? $" CD{pt}" : "");
            }
        }
        AnalyzedFile Make(AnalysisState s, long outBytes = 0, long est = 0, string reason = "") =>
            new(file, rel, group, size, dur, codec, height, s, outBytes, est, reason)
            {
                Season = ep.Season, Episode = ep.Season is null ? null : ep.Episode, EpisodeEnd = ep.EpisodeEnd,
                MovieKey = movieKey, MovieLabel = movieLabel,
            };

        var paths = string.IsNullOrWhiteSpace(p.OutputDir) ? null : new LibraryPaths(p.InputDir, p.OutputDir, file);
        if (paths is not null)
        {
            try
            {
                var o = new FileInfo(paths.Output);
                if (o.Exists) return (Make(AnalysisState.Done, o.Length), media, media is null ? null : Planner.Decide(media, p).Plan);
                if (Directory.Exists(paths.Lock)) return (Make(AnalysisState.InProgress, reason: L.T("in Arbeit", "in progress")), media, null);
            }
            catch
            {
            }
        }
        if (media is null) return (Make(AnalysisState.Unreadable, reason: L.T("nicht lesbar", "not readable")), null, null);

        var d = Planner.Decide(media, p);
        if (!d.Encode) return (Make(AnalysisState.Skip, reason: d.Message), media, null);
        if (paths is not null && p.UseMarkList && MarkList.IsMarked(paths, p.MarkTag))
            return (Make(AnalysisState.Skip, reason: L.T("Ausnahmeliste: schon versucht, nicht klein genug.", "Skip list: already tried, not small enough.")), media, d.Plan);
        return (Make(AnalysisState.Open, est: Estimate(media, d.Plan!, p)), media, d.Plan);
    }

    private sealed record Unit(string Name, AnalyzedFile Head, List<AnalyzedFile> Files);

    public static List<DuplicateSet> FindDuplicates(IEnumerable<AnalyzedFile> files)
    {
        var units = new List<Unit>();
        foreach (var dir in files.GroupBy(f => Path.GetDirectoryName(f.Path) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            var names = DuplicateNames.Identities(dir.Select(f => f.Path));
            foreach (var u in dir.GroupBy(f => names[f.Path], StringComparer.Ordinal))
            {
                var fs = u.ToList();
                units.Add(new Unit(u.Key, fs.FirstOrDefault(f => f.Season is not null) ?? fs.FirstOrDefault(f => f.MovieKey.Length > 0) ?? fs[0], fs));
            }
        }

        var sets = new List<DuplicateSet>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var grouped = new HashSet<Unit>();
        void Add(string group, string label, IEnumerable<Unit> members)
        {
            var us = members.ToList();
            var fs = us.SelectMany(u => u.Files).Distinct().ToList();
            if (fs.Count < 2) return;
            grouped.UnionWith(us);
            var sig = string.Join("|", fs.Select(f => f.Path).Order(StringComparer.OrdinalIgnoreCase));
            if (seen.Add(sig)) sets.Add(new DuplicateSet(group, label, fs.OrderByDescending(f => DuplicateSet.ResolutionRank(f.Height)).ThenByDescending(f => DuplicateSet.CodecRank(f.Codec)).ThenBy(f => f.SourceBytes).ToList()));
        }
        static IEnumerable<List<T>> Split<T>(List<T> items, Func<T, string> key)
        {
            var by = items.GroupBy(key, StringComparer.Ordinal).ToList();
            return by.Count(g => g.Key.Length > 0) <= 1 ? [items] : by.Select(g => g.ToList());
        }

        var episodes = units.Where(u => u.Head.Season is not null).Select(u => (Unit: u, Name: DuplicateNames.Episode(u.Name))).ToList();
        foreach (var g in episodes
                     .SelectMany(x => x.Unit.Head.Episodes.Select(e => (Key: $"{x.Unit.Head.Group}|{x.Unit.Head.Season}|{e}|{x.Name.Part}", Label: $"S{x.Unit.Head.Season:00}E{e:00}{x.Name.Part}", x.Unit, x.Name)))
                     .GroupBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            var first = g.First();
            foreach (var series in Split(g.ToList(), x => x.Name.SeriesKey))
                foreach (var title in Split(series, x => x.Name.TitleKey))
                    Add(first.Unit.Head.Group, first.Label, title.Select(x => x.Unit));
        }
        foreach (var g in units.Where(u => u.Head.Season is null && u.Head.MovieKey.Length > 0).GroupBy(u => u.Head.MovieKey, StringComparer.OrdinalIgnoreCase))
        {
            var year = g.Key.Split('|') is { Length: > 1 } parts ? parts[1] : "";
            foreach (var tail in Split(g.ToList(), u => DuplicateNames.MovieTail(u.Name, year)))
                Add(g.First().Head.MovieLabel, g.First().Head.MovieLabel, tail);
        }
        foreach (var u in units.Where(u => u.Files.Count > 1 && !grouped.Contains(u)))
        {
            var label = DuplicateNames.CleanName(u.Head.Path);
            var inFolder = u.Head.Relative.Contains(Path.DirectorySeparatorChar) || u.Head.Relative.Contains(Path.AltDirectorySeparatorChar);
            Add(inFolder ? u.Head.Group : label, label, [u]);
        }
        return sets.OrderByDescending(s => s.Reclaimable).ToList();
    }

    public static List<MissingSet> FindMissing(IEnumerable<AnalyzedFile> files)
    {
        var result = new List<MissingSet>();
        foreach (var g in files.Where(f => f.Season is > 0).GroupBy(f => (f.Group.ToLowerInvariant(), f.Season!.Value)))
        {
            var have = g.SelectMany(f => f.Episodes).ToHashSet();
            if (have.Count < 2) continue;
            var max = have.Max();
            if (max > 300) continue;
            var missing = Enumerable.Range(1, max).Where(e => !have.Contains(e)).ToList();
            if (missing.Count == 0 || missing.Count > max / 2) continue;
            result.Add(new MissingSet(g.First().Group, g.Key.Item2, have.Count, max, missing));
        }
        return result.OrderBy(m => m.Group, StringComparer.CurrentCultureIgnoreCase).ThenBy(m => m.Season).ToList();
    }

    public static List<GroupSummary> Summarize(string inputDir, IEnumerable<AnalyzedFile> files) => files
        .GroupBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
        .Select(g =>
        {
            var first = g.First();
            var isFolder = first.Relative.Contains(Path.DirectorySeparatorChar) || first.Relative.Contains(Path.AltDirectorySeparatorChar);
            var s = new GroupSummary { Name = isFolder ? g.Key : Path.GetFileNameWithoutExtension(g.Key), SourcePath = isFolder ? Path.Combine(inputDir, g.Key) : first.Path };
            foreach (var f in g)
            {
                s.Files++;
                s.SourceBytes += f.SourceBytes;
                s.DurationSeconds += f.DurationSeconds;
                s.Codecs[f.Codec] = s.Codecs.GetValueOrDefault(f.Codec) + 1;
                switch (f.State)
                {
                    case AnalysisState.Done: s.Done++; s.SavedBytes += f.ActualSaving; break;
                    case AnalysisState.Open: s.Open++; s.OpenBytes += f.SourceBytes; s.EstimatedSaving += f.EstimatedSaving; break;
                    case AnalysisState.Skip:
                        s.Skip++;
                        if (f.Reason.Length > 0) s.SkipReasons[f.Reason] = s.SkipReasons.GetValueOrDefault(f.Reason) + 1;
                        break;
                    case AnalysisState.InProgress: s.InProgress++; break;
                    default: s.Unreadable++; break;
                }
            }
            return s;
        })
        .OrderByDescending(s => s.EstimatedSaving).ThenByDescending(s => s.OpenBytes).ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
        .ToList();
}
