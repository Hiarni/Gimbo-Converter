using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilmKonverter.Core.Media;

public sealed class ProbeCache
{
    public sealed class Entry
    {
        public long Size { get; set; }
        public long MtimeTicks { get; set; }
        public MediaInfo Media { get; set; } = new();
    }

    private static readonly StringComparer KeyComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    private static readonly JsonSerializerOptions Json = new() { IgnoreReadOnlyProperties = true, PreferredObjectCreationHandling = JsonObjectCreationHandling.Populate };

    private static readonly string[] KeepTags = ["language", "title", "mimetype", "rotate", "filename"];

    private readonly ConcurrentDictionary<string, Entry> _map = new(KeyComparer);
    private readonly string? _file;
    private readonly object _saveLock = new();
    private volatile bool _dirty;

    public static ProbeCache? Shared { get; set; }

    public ProbeCache(string? file = null) => _file = file;

    public int Count => _map.Count;

    public static ProbeCache Load(string file)
    {
        var c = new ProbeCache(file);
        try
        {
            if (!File.Exists(file)) return c;
            using var fs = File.OpenRead(file);
            using var gz = new GZipStream(fs, CompressionMode.Decompress);
            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(gz, Json);
            if (data is not null)
                foreach (var (k, v) in data)
                    if (v?.Media is not null) c._map[k] = v;
        }
        catch
        {
        }
        return c;
    }

    public void Save()
    {
        if (_file is null || !_dirty) return;
        lock (_saveLock)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
                var tmp = _file + ".tmp";
                _dirty = false;
                using (var fs = File.Create(tmp))
                using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                    JsonSerializer.Serialize(gz, new Dictionary<string, Entry>(_map, KeyComparer), Json);
                File.Move(tmp, _file, overwrite: true);
            }
            catch
            {
                _dirty = true;
            }
        }
    }

    private static long Ticks(DateTime utc) => utc.ToUniversalTime().Ticks;

    public MediaInfo? Get(string path, long size, DateTime lastWriteUtc) =>
        _map.TryGetValue(path, out var e) && e.Size == size && e.MtimeTicks == Ticks(lastWriteUtc) ? e.Media : null;

    public MediaInfo? Get(string path)
    {
        try
        {
            var fi = new FileInfo(path);
            return fi.Exists ? Get(path, fi.Length, fi.LastWriteTimeUtc) : null;
        }
        catch
        {
            return null;
        }
    }

    public void Put(string path, MediaInfo media)
    {
        try
        {
            var fi = new FileInfo(path);
            if (!fi.Exists) return;
            Put(path, media, fi.Length, fi.LastWriteTimeUtc);
        }
        catch
        {
        }
    }

    public void Put(string path, MediaInfo media, long size, DateTime lastWriteUtc)
    {
        Trim(media);
        _map[path] = new Entry { Size = size, MtimeTicks = Ticks(lastWriteUtc), Media = media };
        _dirty = true;
    }

    public async Task<MediaInfo?> GetOrProbeAsync(FfmpegTools tools, string path, CancellationToken ct)
    {
        FileInfo fi;
        try { fi = new FileInfo(path); if (!fi.Exists) return null; }
        catch { return null; }
        var cached = Get(path, fi.Length, fi.LastWriteTimeUtc);
        if (cached is not null) return cached;
        var m = await tools.ProbeAsync(path, ct).ConfigureAwait(false);
        if (m is not null) Put(path, m, fi.Length, fi.LastWriteTimeUtc);
        return m;
    }

    public int Prune(string root, IReadOnlySet<string> alive)
    {
        var prefix = root.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
        var n = 0;
        foreach (var k in _map.Keys)
        {
            if (!k.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)) continue;
            if (alive.Contains(k)) continue;
            if (_map.TryRemove(k, out _)) n++;
        }
        if (n > 0) _dirty = true;
        return n;
    }

    private static void Trim(MediaInfo m)
    {
        m.FormatTags.Clear();
        foreach (var s in m.Streams)
        {
            foreach (var k in s.Tags.Keys.ToList())
            {
                var keep = KeepTags.Contains(k, StringComparer.OrdinalIgnoreCase)
                           || k.Equals("BPS", StringComparison.OrdinalIgnoreCase)
                           || k.StartsWith("BPS-", StringComparison.OrdinalIgnoreCase);
                if (!keep) s.Tags.Remove(k);
            }
        }
    }
}
