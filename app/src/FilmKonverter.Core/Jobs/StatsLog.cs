using System.Text;
using System.Text.Json;
using FilmKonverter.Core.Library;

namespace FilmKonverter.Core.Jobs;

public sealed class StatsRecord
{
    public DateTime TimeUtc { get; set; }
    public string Machine { get; set; } = "";
    public string Profile { get; set; } = "";
    public string Encoder { get; set; } = "";
    public string File { get; set; } = "";
    public string Group { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string Reason { get; set; } = "";
    public long SourceBytes { get; set; }
    public long OutputBytes { get; set; }
    public long GainBytes { get; set; }
    public double DurationSeconds { get; set; }
    public double EncodeSeconds { get; set; }
    public string SourceCodec { get; set; } = "";
    public int SourceHeight { get; set; }
    public int OutputHeight { get; set; }
    public bool AudioOnly { get; set; }
    public double? Ssim { get; set; }
    public bool SsimLow { get; set; }

    public bool IsSuccess => Outcome is "Encoded" or "Replaced";

    public double Ratio => SourceBytes > 0 && OutputBytes > 0 ? OutputBytes / (double)SourceBytes : 0;

    public double Speed => EncodeSeconds > 0 ? DurationSeconds / EncodeSeconds : 0;
}

public static class StatsLog
{
    private static readonly object WriteLock = new();
    private static readonly JsonSerializerOptions Json = new();

    public static string Folder(string outputRoot) => DataStore.Stats(outputRoot);

    public static bool Append(string outputRoot, StatsRecord r)
    {
        if (string.IsNullOrWhiteSpace(outputRoot)) return false;
        var bad = Path.GetInvalidFileNameChars();
        var name = new string(r.Machine.Select(c => bad.Contains(c) ? '_' : c).ToArray()) + ".jsonl";
        var line = JsonSerializer.Serialize(r, Json) + "\n";
        lock (WriteLock)
        {
            var (dir, backup) = DataStore.StatsDirs(outputRoot);
            var saved = backup is not null && AppendLine(Path.Combine(backup, name), line);
            return AppendLine(Path.Combine(dir, name), line) || saved;
        }
    }

    private static bool AppendLine(string file, string line)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            using var fs = new FileStream(file, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read);
            if (fs.Length > 0)
            {
                fs.Seek(-1, SeekOrigin.End);
                if (fs.ReadByte() != '\n') line = "\n" + line;
            }
            fs.Seek(0, SeekOrigin.End);
            var bytes = new UTF8Encoding(false).GetBytes(line);
            fs.Write(bytes, 0, bytes.Length);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static int Archive(IEnumerable<string> outputRoots)
    {
        var n = 0;
        var now = DateTime.UtcNow;
        var stamp = now.ToLocalTime().ToString("yyyy-MM-dd_HH-mm-ss");
        foreach (var root in outputRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (WriteLock)
            {
                var (dir, backup) = DataStore.StatsDirs(root);
                try
                {
                    if (Directory.Exists(dir))
                    {
                        n += DataStore.ArchiveLogs(dir, stamp);
                        DataStore.WriteReset(dir, now);
                    }
                }
                catch { }
                try
                {
                    if (backup is not null && Directory.Exists(backup))
                    {
                        DataStore.ArchiveLogs(backup, stamp);
                        DataStore.WriteReset(backup, now);
                    }
                }
                catch { }
            }
        }
        return n;
    }

    public static List<StatsRecord> ReadAll(IEnumerable<string> outputRoots)
    {
        var list = new List<StatsRecord>();
        var seenFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in outputRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var dir = Folder(root);
            try
            {
                if (!Directory.Exists(dir)) continue;
                var since = DataStore.ReadReset(dir);
                foreach (var f in Directory.EnumerateFiles(dir, "*.jsonl"))
                {
                    var fi = new FileInfo(f);
                    if (!seenFiles.Add($"{fi.Name}|{fi.Length}|{fi.LastWriteTimeUtc.Ticks}")) continue;
                    using var fs = new FileStream(f, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    using var sr = new StreamReader(fs);
                    while (sr.ReadLine() is { } line)
                    {
                        if (line.Length < 2) continue;
                        try
                        {
                            if (JsonSerializer.Deserialize<StatsRecord>(line, Json) is { } r && r.TimeUtc >= since) list.Add(r);
                        }
                        catch
                        {
                        }
                    }
                }
            }
            catch
            {
            }
        }
        return list.OrderBy(r => r.TimeUtc).ToList();
    }
}
