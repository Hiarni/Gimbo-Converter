using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.Core.Tests;

public sealed class Recorder : IRunObserver
{
    public List<(FileStart Start, FileEnd End)> Files { get; } = [];
    public List<InfoLine> Lines { get; } = [];
    public List<ProgressInfo> Progresses { get; } = [];
    public int Scanned { get; private set; }
    public bool Finished { get; private set; }

    public void ScanStarted(string folder) { }
    public void ScanDone(int total) => Scanned = total;
    public List<FileStart> Starts { get; } = [];
    public void FileStarted(FileStart start) { lock (Starts) Starts.Add(start); }
    public void Line(InfoLine line) { lock (Lines) Lines.Add(line); }
    public void Progress(string stage, ProgressInfo info) { lock (Progresses) Progresses.Add(info); }
    public void FileFinished(FileStart start, FileEnd end) { lock (Files) Files.Add((start, end)); }
    public List<string> Statuses { get; } = [];
    public void Status(string text) { lock (Statuses) Statuses.Add(text); }
    public void RunFinished(RunCounters counters, bool cancelled) => Finished = true;
    public FileEnd EndOf(string nameContains) => Files.Single(f => f.Start.RelativePath.Contains(nameContains)).End;
}

[Collection("media")]
public sealed class LibraryTests(TestMedia m)
{
    private (Profile P, string In, string Out) NewLibrary(params (string Rel, string Src)[] files)
    {
        var root = Path.Combine(m.Dir, "lib-" + Guid.NewGuid().ToString("N")[..6]);
        var input = Path.Combine(root, "Serien");
        var output = Path.Combine(root, "Komprimiert");
        foreach (var (rel, src) in files)
        {
            var dst = Path.Combine(input, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(src, dst);
        }
        var p = Profile.Nvidia();
        p.Name = "Test";
        p.InputDir = input;
        p.OutputDir = output;
        DataStore.Link(output, input);
        p.Encoder = EncoderKind.Cpu;
        p.CpuPreset = "ultrafast";
        p.Quality = 30;
        p.MinSizeMb = 0;
        p.SkipNewerThanMinutes = 0;
        p.MinFreeSpaceGb = 0;
        return (p, input, output);
    }

    private async Task<Recorder> Run(Profile p)
    {
        var rec = new Recorder();
        await new LibraryRun(p, m.Tools, rec).RunAsync(CancellationToken.None);
        return rec;
    }

    [Fact]
    public async Task Neue_Datei_wird_kodiert_geprueft_und_vermerkt_inklusive_Ausrufezeichen()
    {
        var (p, _, output) = NewLibrary(
            (@"Gute Chemie/Staffel 4/Folge 10 (Prost!).mkv", m.Sdr8),
            (@"Serie/Folge 1 – Über & Co.mkv", m.Sdr8));
        var rec = await Run(p);

        Assert.Equal(2, rec.Scanned);
        Assert.All(rec.Files, f => Assert.Equal(Outcome.Encoded, f.End.Outcome));
        var outFile = Path.Combine(output, "Gute Chemie", "Staffel 4", "Folge 10 (Prost!).mkv");
        Assert.True(File.Exists(outFile));
        Assert.False(File.Exists(Path.ChangeExtension(outFile, null) + ".part.mkv"));
        Assert.False(Directory.Exists(Path.Combine(output, "Gute Chemie", "Staffel 4", "Folge 10 (Prost!).lock")));
        var o = (await m.Tools.ProbeAsync(outFile))!;
        Assert.Equal("hevc", o.Video!.CodecName);
        Assert.True(o.HasAudio);
        Assert.True(MarkList.IsVerified(output, outFile, new FileInfo(outFile).Length));

        var rec2 = await Run(p);
        Assert.All(rec2.Files, f => Assert.Equal(Outcome.AlreadyOk, f.End.Outcome));
    }

    [Fact]
    public async Task Zu_grosse_vorhandene_Datei_wird_aus_der_Quelle_ersetzt()
    {
        var (p, input, output) = NewLibrary(("A/Film.mkv", m.Sdr8));
        var outFile = Path.Combine(output, "A", "Film.mkv");
        Directory.CreateDirectory(Path.GetDirectoryName(outFile)!);
        File.Copy(Path.Combine(input, "A", "Film.mkv"), outFile);
        var before = new FileInfo(outFile).Length;

        var rec = await Run(p);
        var end = rec.EndOf("Film");
        Assert.Equal(Outcome.Replaced, end.Outcome);
        Assert.True(new FileInfo(outFile).Length < before);
        Assert.True(end.GainBytes > 0);
        Assert.Contains(rec.Lines, l => l.Label == "Vorhanden" && l.Text.Contains("neu kodieren"));
    }

    [Fact]
    public async Task Fruehabbruch_vermerkt_in_Merkliste_und_wird_danach_uebersprungen()
    {
        var (p, _, output) = NewLibrary(("B/Lang.mkv", m.Long720));
        p.CpuPreset = "medium";
        p.EarlyAbortPercent = 1;
        var rec = await Run(p);
        var end = rec.EndOf("Lang");
        Assert.Equal(Outcome.Skipped, end.Outcome);
        Assert.Contains("Früh abgebrochen", end.Message);
        Assert.True(File.Exists(Path.Combine(DataStore.Marks(output), "B", "Lang.G1.txt")));
        Assert.False(File.Exists(Path.Combine(output, "B", "Lang.mkv")));

        var rec2 = await Run(p);
        Assert.Contains("Ausnahmeliste", rec2.EndOf("Lang").Message);
    }

    [Fact]
    public async Task Alte_Merkliste_der_Batch_wird_erkannt()
    {
        var (p, _, output) = NewLibrary(("C/Folge.mkv", m.Sdr8));
        var mark = Path.Combine(DataStore.Marks(output), "C");
        Directory.CreateDirectory(mark);
        File.WriteAllText(Path.Combine(mark, "Folge.QP22-24_G95.txt"), "Fruehabbruch");
        var rec = await Run(p);
        Assert.Equal(Outcome.Skipped, rec.EndOf("Folge").Outcome);
    }

    [Fact]
    public async Task Sperre_eines_anderen_PCs_wird_respektiert_eigene_alte_aufgeraeumt()
    {
        var (p, _, output) = NewLibrary(("D/Fremd.mkv", m.Sdr8), ("D/Eigen.mkv", m.Sdr8));
        var d = Path.Combine(output, "D");
        Directory.CreateDirectory(Path.Combine(d, "Fremd.lock"));
        File.WriteAllText(Path.Combine(d, "Fremd.lock", "info.txt"), "ANDERERPC 10.11.2026 7:00");
        Directory.CreateDirectory(Path.Combine(d, "Eigen.lock"));
        File.WriteAllText(Path.Combine(d, "Eigen.lock", "info.txt"), FileLock.ComputerName + " 09.11.2026 23:00");

        var rec = await Run(p);
        Assert.Equal(Outcome.OtherPc, rec.EndOf("Fremd").Outcome);
        Assert.Contains("ANDERERPC", rec.EndOf("Fremd").Message);
        Assert.Equal(Outcome.Encoded, rec.EndOf("Eigen").Outcome);
        Assert.True(Directory.Exists(Path.Combine(d, "Fremd.lock")));
        Assert.False(Directory.Exists(Path.Combine(d, "Eigen.lock")));
    }

    [Fact]
    public void Sperre_ist_exklusiv()
    {
        var dir = Path.Combine(m.Dir, "lock-" + Guid.NewGuid().ToString("N")[..6]);
        using var first = FileLock.TryAcquire(dir, 24, out _);
        Assert.NotNull(first);
        File.WriteAllText(Path.Combine(dir, "info.txt"), "ANDERERPC heute");
        var second = FileLock.TryAcquire(dir, 24, out var owner);
        Assert.Null(second);
        Assert.Equal("ANDERERPC", owner);
    }

    [Fact]
    public void Eigene_laufende_Sperre_wird_nicht_als_verwaist_uebernommen()
    {
        var dir = Path.Combine(m.Dir, "lock-" + Guid.NewGuid().ToString("N")[..6]);
        var first = FileLock.TryAcquire(dir, 24, out _);
        Assert.NotNull(first);
        Assert.Null(FileLock.TryAcquire(dir, 24, out var owner));
        Assert.Equal(FileLock.ComputerName, owner);
        first!.Dispose();
        using var again = FileLock.TryAcquire(dir, 24, out _);
        Assert.NotNull(again);
    }

    [Fact]
    public async Task Prueflauf_findet_beschaedigte_Datei_und_ueberspringt_gepruefte()
    {
        var root = Path.Combine(m.Dir, "verify-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "S"));
        var good = Path.Combine(root, "S", "gut.mkv");
        var bad = Path.Combine(root, "S", "kaputt.mkv");
        await m.Ff(["-i", m.Long720, "-map", "0:v", "-c:v", "libx265", "-preset", "ultrafast", "-x265-params", "log-level=error", good]);
        File.Copy(good, bad);
        using (var fs = new FileStream(bad, FileMode.Open))
        {
            var rnd = new Random(42);
            for (var i = 1; i < 40; i++)
            {
                fs.Position = fs.Length * i / 41;
                var junk = new byte[600];
                rnd.NextBytes(junk);
                fs.Write(junk);
            }
        }
        File.Copy(good, Path.Combine(root, "S", "temp.part.mkv"));

        var p = Profile.Nvidia();
        p.OutputDir = root;
        var rec = new Recorder();
        var run = new VerifyRun(p, m.Tools, rec);
        await run.RunAsync(new VerifyOptions(), CancellationToken.None);

        Assert.Equal(2, rec.Scanned);
        Assert.Equal(Outcome.Encoded, rec.EndOf("gut").Outcome);
        Assert.Equal(Outcome.Error, rec.EndOf("kaputt").Outcome);
        Assert.Contains("kaputt.mkv", File.ReadAllText(run.ReportFile));

        var rec2 = new Recorder();
        await new VerifyRun(p, m.Tools, rec2).RunAsync(new VerifyOptions { DeleteBroken = true }, CancellationToken.None);
        Assert.Equal(Outcome.AlreadyOk, rec2.EndOf("gut").Outcome);
        Assert.False(File.Exists(bad));
    }

    [Fact]
    public async Task Testmodus_erzeugt_Clips_und_Tabelle()
    {
        var (p, input, _) = NewLibrary(("E/Probe.mkv", m.Long720));
        p.TestStartSeconds = 5;
        p.TestLengthSeconds = 4;
        p.TestValues = "28 34";
        var rec = new Recorder();
        var rows = await new TestRun(p, m.Tools, rec).RunAsync(Path.Combine(input, "E", "Probe.mkv"), CancellationToken.None);
        Assert.Equal(new[] {"00_Quelle", "CRF28", "CRF34"}, rows.Select(r => r.Clip).ToArray());
        var q28 = rows.Single(r => r.Clip == "CRF28").RatioToSource!.Value;
        var q34 = rows.Single(r => r.Clip == "CRF34").RatioToSource!.Value;
        Assert.True(q34 < q28 && q28 < 1.0, $"{q28} {q34}");
        Assert.Equal(1.0, rows.Single(r => r.IsReference).RatioToSource!.Value, 2);
    }

    [Fact]
    public async Task Fortschritt_aus_Frames_und_Pause()
    {
        var args = new List<string> { "-hide_banner", "-y", "-i", m.Long720, "-map", "0:v", "-c:v", "libx265", "-preset", "fast",
            "-x265-params", "log-level=error", "-f", "null", FfmpegTools.NullTarget };
        var run = new FfmpegRun(m.Tools.FfmpegPath, args, new RunOptions { DurationSeconds = 24, SourceFps = 24 });
        var seen = new List<ProgressInfo>();
        var task = run.RunAsync(new Util.SyncProgress<ProgressInfo>(pi => { lock (seen) seen.Add(pi); }), CancellationToken.None);
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            lock (seen) if (seen.Count > 0 && !seen[^1].Final && seen[^1].Fraction < 0.5) break;
            await Task.Delay(50);
        }
        var paused = run.Pause();
        if (!paused)
        {
            await Task.WhenAny(task, Task.Delay(5000));
            if (task.IsCompleted) return;
        }
        Assert.True(paused);
        await Task.Delay(800);
        int count;
        lock (seen) count = seen.Count;
        await Task.Delay(2000);
        lock (seen) Assert.True(seen.Count == count, $"waehrend der Pause darf ffmpeg nicht weiterrechnen ({count} -> {seen.Count})");
        Assert.True(run.Resume());
        var res = await task;
        Assert.True(res.Success);
        Assert.Equal(1.0, seen.Last().Fraction, 2);
        Assert.True(seen.Last().Final);
    }

    [Fact]
    public async Task Begleitdateien_und_Datum_werden_uebernommen()
    {
        var (p, input, output) = NewLibrary(("S/Folge.mkv", m.Sdr8));
        File.WriteAllText(Path.Combine(input, "S", "Folge.de.forced.srt"), "1\n00:00:01,000 --> 00:00:02,000\nHallo\n");
        File.WriteAllText(Path.Combine(input, "S", "Folge.nfo"), "<episodedetails/>");
        File.WriteAllText(Path.Combine(input, "S", "Folge2.nfo"), "fremd");
        var date = new DateTime(2019, 5, 4, 12, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(Path.Combine(input, "S", "Folge.mkv"), date);

        var rec = await Run(p);
        Assert.Equal(Outcome.Encoded, rec.EndOf("Folge").Outcome);
        Assert.True(File.Exists(Path.Combine(output, "S", "Folge.de.forced.srt")));
        Assert.True(File.Exists(Path.Combine(output, "S", "Folge.nfo")));
        Assert.False(File.Exists(Path.Combine(output, "S", "Folge2.nfo")));
        Assert.Equal(date, File.GetLastWriteTimeUtc(Path.Combine(output, "S", "Folge.mkv")));
    }

    [Fact]
    public async Task Ziel_innerhalb_der_Quelle_wird_nicht_erneut_kodiert()
    {
        var (p, input, _) = NewLibrary(("A/Eins.mkv", m.Sdr8));
        p.OutputDir = Path.Combine(input, "_komprimiert");
        var rec = await Run(p);
        Assert.Equal(Outcome.Encoded, rec.EndOf("Eins").Outcome);
        var rec2 = await Run(p);
        Assert.Equal(1, rec2.Scanned);
    }

    [Fact]
    public async Task Quelle_im_Ziel_wird_abgelehnt()
    {
        var (p, input, _) = NewLibrary(("A/Eins.mkv", m.Sdr8));
        p.OutputDir = input;
        await Assert.ThrowsAsync<InvalidOperationException>(() => new LibraryRun(p, m.Tools, new Recorder()).RunAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Kaputte_vorhandene_Datei_wird_ersetzt()
    {
        var (p, _, output) = NewLibrary(("S/Film.mkv", m.Sdr8));
        Directory.CreateDirectory(Path.Combine(output, "S"));
        File.WriteAllBytes(Path.Combine(output, "S", "Film.mkv"), new byte[2000]);
        var rec = await Run(p);
        Assert.Equal(Outcome.Replaced, rec.EndOf("Film").Outcome);
        Assert.True(new FileInfo(Path.Combine(output, "S", "Film.mkv")).Length > 2000);
    }

    [Fact]
    public async Task Prueflauf_ohne_ffmpeg_veraendert_nichts()
    {
        var toolDir = Path.Combine(m.Dir, "tools-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(toolDir);
        File.Copy(m.Tools.FfmpegPath, Path.Combine(toolDir, Path.GetFileName(m.Tools.FfmpegPath)));
        File.Copy(m.Tools.FfprobePath, Path.Combine(toolDir, Path.GetFileName(m.Tools.FfprobePath)));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(Path.Combine(toolDir, Path.GetFileName(m.Tools.FfmpegPath)), UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
            File.SetUnixFileMode(Path.Combine(toolDir, Path.GetFileName(m.Tools.FfprobePath)), UnixFileMode.UserRead | UnixFileMode.UserExecute | UnixFileMode.UserWrite);
        }
        var tools = (await FfmpegTools.TryLoadAsync(toolDir))!;
        File.Delete(tools.FfmpegPath);

        var root = Path.Combine(m.Dir, "vfy-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(root);
        var good = Path.Combine(root, "gut.mkv");
        File.Copy(m.Sdr8, good);
        var p = Profile.Nvidia();
        p.OutputDir = root;
        await Assert.ThrowsAnyAsync<Exception>(() => new VerifyRun(p, tools, new Recorder()).RunAsync(new VerifyOptions { DeleteBroken = true }, CancellationToken.None));
        Assert.True(File.Exists(good));
    }

    [Fact]
    public async Task Prueflauf_sortiert_kaputte_aus_und_leert_die_Merkliste()
    {
        var root = Path.Combine(m.Dir, "vfy-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(Path.Combine(root, "S"));
        var bad = Path.Combine(root, "S", "kaputt.mkv");
        File.Copy(m.Long720, bad);
        using (var fs = new FileStream(bad, FileMode.Open))
        {
            var rnd = new Random(7);
            for (var i = 1; i < 40; i++) { fs.Position = fs.Length * i / 41; var junk = new byte[600]; rnd.NextBytes(junk); fs.Write(junk); }
        }
        Directory.CreateDirectory(Path.Combine(DataStore.Marks(root), "S"));
        File.WriteAllText(Path.Combine(DataStore.Marks(root), "S", "kaputt.G95.txt"), "x");
        var p = Profile.Nvidia();
        p.OutputDir = root;
        var rec = new Recorder();
        await new VerifyRun(p, m.Tools, rec).RunAsync(new VerifyOptions { DeleteBroken = true }, CancellationToken.None);
        Assert.Equal(Outcome.Error, rec.EndOf("kaputt").Outcome);
        Assert.False(File.Exists(bad));
        Assert.True(File.Exists(Path.Combine(root, "_Defekt", "S", "kaputt.mkv")));
        Assert.False(File.Exists(Path.Combine(DataStore.Marks(root), "S", "kaputt.G95.txt")));
    }
}


public static class TestProfiles
{
    public static Profile NvidiaFilm()
    {
        var p = Profile.Nvidia();
        p.MinRateMbit = 8;
        p.RateBasis = RateBasis.Video;
        p.ConvertLosslessAudio = true;
        p.AudioTarget = AudioTarget.Eac3;
        return p;
    }
}
