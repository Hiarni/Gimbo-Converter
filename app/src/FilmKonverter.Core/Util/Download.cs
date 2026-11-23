using System.Diagnostics;
using System.Net.Http.Headers;

namespace FilmKonverter.Core.Util;

public sealed record DownloadStatus(long Done, long Total, double BytesPerSecond)
{
    public double Fraction => Total > 0 ? Math.Min(1, Done / (double)Total) : -1;

    public string Text
    {
        get
        {
            var speed = BytesPerSecond > 0 ? (BytesPerSecond / 1024 / 1024).ToString("N1", Fmt.De) + " MB/s" : "";
            var size = Total > 0
                ? L.T($"{Done / 1024 / 1024:N0} von {Total / 1024 / 1024:N0} MB", $"{Done / 1024 / 1024:N0} of {Total / 1024 / 1024:N0} MB")
                : $"{Done / 1024 / 1024:N0} MB";
            return speed.Length > 0 ? speed + " · " + size : size;
        }
    }
}

public static class Download
{
    public const long SplitFrom = 8L * 1024 * 1024;

    public const int MaxParts = 8;

    public static async Task DownloadAsync(HttpClient http, Uri url, string file, Action<DownloadStatus>? onProgress = null, int parts = MaxParts, CancellationToken ct = default)
    {
        long total = -1;
        Uri finalUrl = url;
        var ranges = false;
        using (var probe = new HttpRequestMessage(HttpMethod.Get, url))
        {
            probe.Headers.Range = new RangeHeaderValue(0, 0);
            using var resp = await http.SendAsync(probe, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            if (resp.StatusCode == System.Net.HttpStatusCode.PartialContent && resp.Content.Headers.ContentRange is { Length: long len })
            {
                total = len;
                ranges = true;
                finalUrl = resp.RequestMessage?.RequestUri ?? url;
            }
            else
            {
                resp.EnsureSuccessStatusCode();
            }
        }

        var meter = new Meter(total, onProgress);
        if (!ranges || total < SplitFrom || parts <= 1)
        {
            await SingleAsync(http, url, file, meter, ct).ConfigureAwait(false);
        }
        else
        {
            var n = (int)Math.Clamp(total / (4L * 1024 * 1024), 2, parts);
            var size = (total + n - 1) / n;
            using (var handle = File.OpenHandle(file, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, total))
            {
                var tasks = Enumerable.Range(0, n).Select(i =>
                {
                    var from = i * size;
                    var to = Math.Min(total, from + size) - 1;
                    return PartAsync(http, finalUrl, handle, from, to, meter, ct);
                }).ToArray();
                await Task.WhenAll(tasks).ConfigureAwait(false);
            }
            if (new FileInfo(file).Length != total)
                throw new IOException(L.T("Der Download ist unvollständig.", "The download is incomplete."));
        }
        meter.Finish();
    }

    private static async Task SingleAsync(HttpClient http, Uri url, string file, Meter meter, CancellationToken ct)
    {
        using var resp = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        if (resp.Content.Headers.ContentLength is long len) meter.SetTotal(len);
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(file, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buf = new byte[1 << 20];
        int read;
        while ((read = await src.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buf.AsMemory(0, read), ct).ConfigureAwait(false);
            meter.Add(read);
        }
    }

    private static async Task PartAsync(HttpClient http, Uri url, Microsoft.Win32.SafeHandles.SafeFileHandle handle, long from, long to, Meter meter, CancellationToken ct)
    {
        var pos = from;
        var buf = new byte[1 << 19];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.Range = new RangeHeaderValue(pos, to);
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                    throw new IOException(L.T("Der Server liefert keine Teilbereiche.", "The server does not deliver partial ranges."));
                await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                int read;
                while (pos <= to && (read = await src.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, to - pos + 1)), ct).ConfigureAwait(false)) > 0)
                {
                    await RandomAccess.WriteAsync(handle, buf.AsMemory(0, read), pos, ct).ConfigureAwait(false);
                    pos += read;
                    meter.Add(read);
                }
                if (pos > to) return;
                throw new IOException(L.T("Verbindung vorzeitig beendet.", "Connection ended early."));
            }
            catch (Exception) when (!ct.IsCancellationRequested && attempt < 3)
            {
                await Task.Delay(500 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }
    }

    private sealed class Meter(long total, Action<DownloadStatus>? onProgress)
    {
        private readonly Stopwatch _sw = Stopwatch.StartNew();
        private long _total = total;
        private long _done;
        private long _lastReport = -1000;
        private long _lastBytes;
        private double _speed;
        private long _lastSpeedAt;

        public void SetTotal(long t) => Interlocked.Exchange(ref _total, t);

        public void Add(long n)
        {
            var done = Interlocked.Add(ref _done, n);
            var now = _sw.ElapsedMilliseconds;
            var last = Interlocked.Read(ref _lastReport);
            if (now - last < 100 || Interlocked.CompareExchange(ref _lastReport, now, last) != last) return;
            if (now - _lastSpeedAt >= 500)
            {
                var cur = (done - _lastBytes) * 1000.0 / Math.Max(1, now - _lastSpeedAt);
                _speed = _speed <= 0 ? cur : _speed * 0.5 + cur * 0.5;
                _lastBytes = done;
                _lastSpeedAt = now;
            }
            onProgress?.Invoke(new DownloadStatus(done, Interlocked.Read(ref _total), _speed));
        }

        public void Finish()
        {
            var done = Interlocked.Read(ref _done);
            var secs = Math.Max(0.001, _sw.Elapsed.TotalSeconds);
            onProgress?.Invoke(new DownloadStatus(done, Math.Max(done, Interlocked.Read(ref _total)), done / secs));
        }
    }
}
