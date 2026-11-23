using FilmKonverter.Core.Encoding;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.Core.Jobs;

public sealed class VerifyOptions
{
    public int Days { get; init; }

    public bool DeleteBroken { get; init; }
}

public sealed class VerifyRun(Profile profile, FfmpegTools tools, IRunObserver observer)
{
    private FfmpegRun? _current;
    public RunCounters Counters { get; } = new();
    public bool StopAfterCurrent { get; set; }
    public System.Diagnostics.ProcessPriorityClass Priority { get; init; } = System.Diagnostics.ProcessPriorityClass.Normal;
    public bool Pause() => _current?.Pause() ?? false;
    public bool Resume() => _current?.Resume() ?? false;

    public string ReportFile => Path.Combine(profile.OutputDir, "_Pruefbericht.txt");

    private static string Verified => L.T("GEPRÜFT", "VERIFIED");

    public async Task RunAsync(VerifyOptions opt, CancellationToken ct)
    {
        var cancelled = false;
        try
        {
            var root = profile.OutputDir;
            await Task.Run(() => DataStore.Sync(root), ct);
            observer.ScanStarted(root);
            var files = await Task.Run(() => Scanner.Find(root, [".mkv"]), ct);
            if (opt.Days > 0)
            {
                var cut = DateTime.Now.AddDays(-opt.Days);
                files = files.Where(f => File.GetLastWriteTime(f) >= cut || File.GetCreationTime(f) >= cut).ToList();
            }
            Counters.Total = files.Count;
            observer.ScanDone(files.Count);

            for (var i = 0; i < files.Count; i++)
            {
                if (ct.IsCancellationRequested || StopAfterCurrent) { cancelled = true; break; }
                var file = files[i];
                var start = new FileStart(i + 1, files.Count, Path.GetRelativePath(root, file), file);
                observer.FileStarted(start);
                var end = await VerifyFileAsync(root, file, opt, ct);
                Counters.Count(end);
                observer.FileFinished(start, end);
                if (end.Outcome == Outcome.Cancelled) { cancelled = true; break; }
            }
        }
        finally
        {
            observer.RunFinished(Counters, cancelled || ct.IsCancellationRequested);
        }
    }

    private async Task<FileEnd> VerifyFileAsync(string root, string file, VerifyOptions opt, CancellationToken ct)
    {
        long size;
        try { size = new FileInfo(file).Length; } catch { return new FileEnd(Outcome.Error, L.T("Datei nicht lesbar.", "File not readable.")); }
        if (MarkList.IsVerified(root, file, size)) return new FileEnd(Outcome.AlreadyOk, L.T("Bereits geprüft und seitdem unverändert.", "Already checked and unchanged since.")) { Label = Verified };

        FileLock? lkTry;
        string owner;
        try { lkTry = FileLock.TryAcquire(Path.ChangeExtension(file, ".lock"), profile.LockStaleHours, out owner, "Prueflauf"); }
        catch (Exception ex) { return new FileEnd(Outcome.Error, L.T("Sperre nicht möglich: ", "Lock not possible: ") + ex.Message); }
        using var lk = lkTry;
        if (lk is null) return new FileEnd(Outcome.OtherPc, L.T($"bei {owner} in Arbeit", $"in progress on {owner}"));

        MediaInfo? media;
        try { media = await tools.ProbeAsync(file, ct); }
        catch (Exception ex) when (ex is not OperationCanceledException) { media = null; observer.Line(new InfoLine("ffprobe", ex.Message, LineKind.Warn)); }
        if (media is null)
            return new FileEnd(Outcome.Error, L.T("Prüfung nicht möglich (ffprobe liefert nichts). Nichts verändert.", "Check not possible (ffprobe returned nothing). Nothing changed."));

        var run = new FfmpegRun(tools.FfmpegPath, ArgsBuilder.BuildDecodeTest(file, true, FfmpegTools.NullTarget),
            new RunOptions { DurationSeconds = media.DurationSeconds, SourceFps = media.Fps, Priority = Priority });
        _current = run;
        var res = await run.RunAsync(new SyncProgress<ProgressInfo>(pi => observer.Progress("Decode", pi)), ct);
        _current = null;
        if (res.Cancelled) return new FileEnd(Outcome.Cancelled, L.T("Abgebrochen.", "Cancelled."));
        if (!res.Started) throw new InvalidOperationException(res.ErrorLines.FirstOrDefault() ?? "ffmpeg");

        var errors = ArgsBuilder.DecodeErrors(res.ErrorLines);
        if (res.ErrorLines.Any(FfmpegErrors.IsReadError))
        {
            foreach (var l in res.ErrorLines.TakeLast(5)) observer.Line(new InfoLine("ffmpeg", l, LineKind.Warn));
            return new FileEnd(Outcome.Error, L.T("Lesefehler (Netzwerk?). Nichts verändert, beim nächsten Prüflauf erneut.", "Read error (network?). Nothing changed, checked again next time."));
        }
        if (errors.Count == 0 && res.ExitCode == 0)
        {
            MarkList.MarkVerified(root, file, size, "im Prueflauf");
            return new FileEnd(Outcome.Encoded, L.T("Vollständig dekodiert, keine Fehler.", "Fully decoded, no errors.")) { Label = Verified };
        }
        foreach (var l in (errors.Count > 0 ? errors : res.ErrorLines).TakeLast(15)) observer.Line(new InfoLine("ffmpeg", l, LineKind.Error));
        if (errors.Count == 0)
        {
            return new FileEnd(Outcome.Error, L.T($"Prüfung unvollständig (Exit-Code {res.ExitCode}). Nichts verändert.", $"Check incomplete (exit code {res.ExitCode}). Nothing changed."));
        }

        try { File.AppendAllText(ReportFile, $"{DateTime.Now:yyyy-MM-dd HH:mm}  DEFEKT  {file}{Environment.NewLine}"); } catch { }
        if (!opt.DeleteBroken) return new FileEnd(Outcome.Error, L.T("Beschädigt. Im Prüfbericht vermerkt.", "Damaged. Noted in the verification report."));

        var target = Path.Combine(root, LibraryPaths.BrokenFolder, Path.GetRelativePath(root, file));
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Move(file, target, overwrite: true);
        }
        catch (Exception ex)
        {
            return new FileEnd(Outcome.Error, L.T("Beschädigt, Verschieben fehlgeschlagen: ", "Damaged, moving failed: ") + ex.Message);
        }
        MarkList.Clear(root, file);
        return new FileEnd(Outcome.Error, L.T($"Beschädigt, nach {LibraryPaths.BrokenFolder} verschoben. Der nächste normale Lauf kodiert die Quelle neu.",
                                               $"Damaged, moved to {LibraryPaths.BrokenFolder}. The next normal run re-encodes the source."));
    }
}
