using FilmKonverter.Core.Library;

namespace FilmKonverter.Core.Jobs;

public sealed record StatsBucket(string Key, int Files, long GainBytes, long SourceBytes, long OutputBytes, double EncodeSeconds, double DurationSeconds, int Errors = 0)
{
    public double Ratio => SourceBytes > 0 ? OutputBytes / (double)SourceBytes : 0;
    public double Speed => EncodeSeconds > 0 ? DurationSeconds / EncodeSeconds : 0;
}

public sealed record StatsDay(DateTime Date, int Files, long GainBytes);

public enum StatsPeriod
{
    Week,
    Month,
    Quarter,
    ThisYear,
    TwelveMonths,
    All,
}

public enum StatsGrain
{
    Day,
    Week,
    Month,
    Year,
}

public sealed class StatsSummary
{
    public StatsPeriod Period { get; init; }
    public StatsGrain Grain { get; init; }
    public DateTime? From { get; init; }

    public int Files { get; init; }
    public int AudioOnly { get; init; }
    public int Cleaned { get; init; }
    public int Skipped { get; init; }
    public int Errors { get; init; }
    public long GainBytes { get; init; }
    public long SourceBytes { get; init; }
    public long OutputBytes { get; init; }
    public double EncodeSeconds { get; init; }
    public double DurationSeconds { get; init; }
    public DateTime? First { get; init; }
    public DateTime? Last { get; init; }

    public double Ratio => SourceBytes > 0 ? OutputBytes / (double)SourceBytes : 0;
    public double ErrorRate => Files + Errors > 0 ? Errors / (double)(Files + Errors) : 0;

    public List<StatsDay> Buckets { get; init; } = [];
    public List<StatsDay> Cumulative { get; init; } = [];

    public List<StatsBucket> Machines { get; init; } = [];
    public List<StatsBucket> Codecs { get; init; } = [];
    public List<StatsBucket> Resolutions { get; init; } = [];
    public List<StatsBucket> Groups { get; init; } = [];

    public List<StatsRecord> TopSavings { get; init; } = [];
    public List<StatsRecord> WorstRatios { get; init; } = [];

    public double SourceBytesPerDay { get; init; }
    public double GainBytesPerDay { get; init; }

    public double? DaysLeft(long openBytes) => SourceBytesPerDay > 0 && openBytes > 0 ? openBytes / SourceBytesPerDay : null;

    public static DateTime? StartOf(StatsPeriod p, DateTime nowLocal) => p switch
    {
        StatsPeriod.Week => nowLocal.Date.AddDays(-6),
        StatsPeriod.Month => nowLocal.Date.AddDays(-29),
        StatsPeriod.Quarter => nowLocal.Date.AddDays(-89),
        StatsPeriod.ThisYear => new DateTime(nowLocal.Year, 1, 1),
        StatsPeriod.TwelveMonths => new DateTime(nowLocal.Year, nowLocal.Month, 1).AddMonths(-11),
        _ => null,
    };

    private static DateTime GrainStart(DateTime d, StatsGrain g) => g switch
    {
        StatsGrain.Day => d.Date,
        StatsGrain.Week => d.Date.AddDays(-(((int)d.DayOfWeek + 6) % 7)),
        StatsGrain.Month => new DateTime(d.Year, d.Month, 1),
        _ => new DateTime(d.Year, 1, 1),
    };

    private static DateTime Next(DateTime d, StatsGrain g) => g switch
    {
        StatsGrain.Day => d.AddDays(1),
        StatsGrain.Week => d.AddDays(7),
        StatsGrain.Month => d.AddMonths(1),
        _ => d.AddYears(1),
    };

    public static StatsSummary Build(IReadOnlyList<StatsRecord> records, DateTime nowLocal) => Build(records, nowLocal, StatsPeriod.Month);

    public static StatsSummary Build(IReadOnlyList<StatsRecord> allRecords, DateTime nowLocal, StatsPeriod period)
    {
        var from = StartOf(period, nowLocal);
        var records = from is { } f0 ? allRecords.Where(r => r.TimeUtc.ToLocalTime() >= f0).ToList() : allRecords.ToList();
        var ok = records.Where(r => r.IsSuccess || r.Outcome == "Cleaned").ToList();
        var real = ok.Where(r => r.IsSuccess).ToList();
        var today = nowLocal.Date;

        var firstData = records.Count > 0 ? records.Min(r => r.TimeUtc).ToLocalTime().Date : today;
        var grain = period switch
        {
            StatsPeriod.Week or StatsPeriod.Month => StatsGrain.Day,
            StatsPeriod.Quarter => StatsGrain.Week,
            StatsPeriod.ThisYear or StatsPeriod.TwelveMonths => StatsGrain.Month,
            _ => (today - firstData).TotalDays > 3 * 365 ? StatsGrain.Year : (today - firstData).TotalDays > 62 ? StatsGrain.Month : StatsGrain.Day,
        };
        var start = GrainStart(from ?? firstData, grain);
        var buckets = new List<StatsDay>();
        for (var d = start; d <= today && buckets.Count < 400; d = Next(d, grain))
        {
            var end = Next(d, grain);
            var inB = ok.Where(r => { var t = r.TimeUtc.ToLocalTime(); return t >= d && t < end; }).ToList();
            buckets.Add(new StatsDay(d, inB.Count(r => r.IsSuccess), inB.Sum(r => r.GainBytes)));
        }

        var cumulative = new List<StatsDay>();
        if (ok.Count > 0)
        {
            var cStart = (from ?? firstData).Date;
            var span = Math.Max(1, (today - cStart).Days + 1);
            var step = Math.Max(1, (int)Math.Ceiling(span / 60d));
            var ordered = ok.OrderBy(r => r.TimeUtc).ToList();
            long sum = 0;
            var idx = 0;
            for (var d = cStart; d <= today; d = d.AddDays(step))
            {
                var until = d.AddDays(step);
                while (idx < ordered.Count && ordered[idx].TimeUtc.ToLocalTime() < until) { sum += ordered[idx].GainBytes; idx++; }
                cumulative.Add(new StatsDay(d, 0, sum));
            }
        }

        static int DistinctFiles(IEnumerable<StatsRecord> rs, string outcome) =>
            rs.Where(r => r.Outcome == outcome).Select(r => r.File).Distinct(StringComparer.OrdinalIgnoreCase).Count();

        StatsBucket Bucket(string key, IEnumerable<StatsRecord> rs)
        {
            var l = rs.ToList();
            var s = l.Where(r => r.IsSuccess).ToList();
            return new StatsBucket(key, s.Count, s.Sum(r => r.GainBytes), s.Sum(r => r.SourceBytes), s.Sum(r => r.OutputBytes),
                s.Sum(r => r.EncodeSeconds), s.Where(r => r.EncodeSeconds > 0).Sum(r => r.DurationSeconds), DistinctFiles(l, "Error"));
        }

        var since = nowLocal.ToUniversalTime().AddDays(-14);
        var recent = allRecords.Where(r => r.IsSuccess && r.TimeUtc >= since).ToList();
        var windowDays = recent.Count == 0 ? 14 : Math.Clamp((nowLocal.ToUniversalTime() - recent.Min(r => r.TimeUtc)).TotalDays, 1, 14);

        return new StatsSummary
        {
            Period = period,
            Grain = grain,
            From = from,
            Files = real.Count,
            AudioOnly = real.Count(r => r.AudioOnly),
            Cleaned = ok.Count(r => r.Outcome == "Cleaned"),
            Skipped = DistinctFiles(records, "Skipped"),
            Errors = DistinctFiles(records, "Error"),
            GainBytes = ok.Sum(r => r.GainBytes),
            SourceBytes = real.Sum(r => r.SourceBytes),
            OutputBytes = real.Sum(r => r.OutputBytes),
            EncodeSeconds = real.Sum(r => r.EncodeSeconds),
            DurationSeconds = real.Sum(r => r.DurationSeconds),
            First = records.Count > 0 ? records.Min(r => r.TimeUtc).ToLocalTime() : null,
            Last = records.Count > 0 ? records.Max(r => r.TimeUtc).ToLocalTime() : null,
            Buckets = buckets,
            Cumulative = cumulative,
            Machines = records.GroupBy(r => r.Machine, StringComparer.OrdinalIgnoreCase).Select(g => Bucket(g.Key, g)).OrderByDescending(b => b.GainBytes).ToList(),
            Codecs = records.Where(r => !r.AudioOnly).GroupBy(r => LibraryAnalyzer.CodecName(r.SourceCodec), StringComparer.OrdinalIgnoreCase)
                .Select(g => Bucket(g.Key, g)).Where(b => b.Files > 0).OrderByDescending(b => b.GainBytes).ToList(),
            Resolutions = records.Where(r => !r.AudioOnly).GroupBy(r => LibraryAnalyzer.ResolutionClass(r.SourceHeight))
                .Select(g => Bucket(g.Key, g)).Where(b => b.Files > 0).OrderByDescending(b => b.GainBytes).ToList(),
            Groups = records.GroupBy(r => r.Group, StringComparer.OrdinalIgnoreCase).Select(g => Bucket(g.Key, g))
                .Where(b => b.Files > 0).OrderByDescending(b => b.GainBytes).Take(12).ToList(),
            TopSavings = real.OrderByDescending(r => r.GainBytes).Take(5).ToList(),
            WorstRatios = real.Where(r => !r.AudioOnly && r.SourceBytes > 50L * 1024 * 1024).OrderByDescending(r => r.Ratio).Take(5).ToList(),
            SourceBytesPerDay = recent.Sum(r => r.SourceBytes) / windowDays,
            GainBytesPerDay = recent.Sum(r => r.GainBytes) / windowDays,
        };
    }
}
