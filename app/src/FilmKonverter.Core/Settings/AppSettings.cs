using System.Text.Json;

namespace FilmKonverter.Core.Settings;

public enum ThemeMode
{
    System,
    Light,
    Dark,
}

public enum WorkPriority
{
    Normal,
    BelowNormal,
    Idle,
}

public enum AfterRunAction
{
    Nothing,
    Sleep,
    Hibernate,
    Shutdown,
    CloseApp,
}

public sealed class AppSettings
{
    public int SchemaVersion { get; set; }

    public Media.FfmpegMode FfmpegMode { get; set; } = Media.FfmpegMode.Auto;

    public string FfmpegDir { get; set; } = "";

    public string FfmpegUpdateDir { get; set; } = "";

    public string FfmpegPreviousDir { get; set; } = "";

    public bool CheckForAppUpdates { get; set; } = true;

    public string AppUpdateFolder { get; set; } = "";

    public DateTime LastAppUpdateCheckUtc { get; set; }

    public List<Profile> Profiles { get; set; } = [];

    public string SelectedProfile { get; set; } = "";

    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    public bool SidebarExpanded { get; set; }

    public WindowPlace? Window { get; set; }

    public Util.UiLanguage Language { get; set; } = Util.UiLanguage.Auto;

    // ---------------- Start und Hintergrund ----------------
    public bool ShowTrayIcon { get; set; } = true;
    public bool StartWithWindows { get; set; }
    public bool AutostartMinimized { get; set; } = true;
    public bool StartMinimized { get; set; }
    public bool MinimizeToTray { get; set; } = true;
    public bool CloseToTray { get; set; }
    public bool AutoStartRun { get; set; }

    // ---------------- Arbeitsweise ----------------
    public WorkPriority Priority { get; set; } = WorkPriority.BelowNormal;
    public bool PreventSleep { get; set; } = true;

    public bool ContinuousMode { get; set; }
    public int RescanMinutes { get; set; } = 60;

    public bool ScheduleEnabled { get; set; }
    public string ScheduleFrom { get; set; } = "22:00";
    public string ScheduleTo { get; set; } = "07:00";

    public AfterRunAction AfterRun { get; set; } = AfterRunAction.Nothing;

    public int ParallelFiles { get; set; } = 1;

    public bool PauseWhenFullscreen { get; set; } = true;

    public string FullscreenIgnore { get; set; } = "";

    public bool PauseWhenUserActive { get; set; }

    public int UserIdleMinutes { get; set; } = 5;

    public bool ShareData { get; set; } = true;

    public bool ShareStatus { get; set; } = true;

    // ---------------- Hinweise ----------------
    public bool Notifications { get; set; } = true;
    public bool NotifyEachFile { get; set; }
    public bool TaskbarProgress { get; set; } = true;
    public bool ConfirmExit { get; set; } = true;
    public int LogRetentionDays { get; set; } = 60;

    // ---------------- Umbenennen ----------------
    public string RenameTemplate { get; set; } = Rename.RenameOptions.DefaultTemplate;
    public bool RenameUseNfo { get; set; } = true;
    public bool RenameIncludeTarget { get; set; } = true;
    public List<Rename.RenamePreset> RenamePresets { get; set; } = [];

    public Rename.RenameKind RenameKind { get; set; } = Rename.RenameKind.Series;
    public string RenameMovieTemplate { get; set; } = Rename.RenameOptions.DefaultMovieTemplate;
    public List<Rename.RenamePreset> RenameMoviePresets { get; set; } = [];

    public List<string> RenameRecent { get; set; } = [];

    public bool TrayHintShown { get; set; }

    // ---------------- Fertige Dateien nachbessern ----------------
    public string CleanupFolder { get; set; } = "";
    public bool CleanupApplyRules { get; set; } = true;

    // ---------------- Datei-Konverter ----------------
    public Converting.ConvertTarget ConvertTarget { get; set; } = Converting.ConvertTarget.Compatible;
    public bool ConvertToFolder { get; set; }
    public string ConvertOutputDir { get; set; } = "";
    public bool ConvertKeepStructure { get; set; } = true;
    public string ConvertSuffix { get; set; } = "";
    public int ConvertParallel { get; set; } = 1;
    public string RepairAllowedAudio { get; set; } = "aac ac3 eac3 mp3 mp2";
    public AudioTarget RepairTarget { get; set; } = AudioTarget.Ac3;
    public int RepairBitrate { get; set; } = 640;

    public Profile? FindProfile(string name) =>
        Profiles.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));

    public const int CurrentSchema = 7;

    public void Normalize()
    {
        Profiles = (Profiles ?? []).Where(p => p is not null).ToList();
        FillNulls(this, CreateDefault());
        var template = new Profile();
        foreach (var p in Profiles) FillNulls(p, template);
        RenamePresets = (RenamePresets ?? []).Where(p => p is not null).ToList();
        RenameMoviePresets = (RenameMoviePresets ?? []).Where(p => p is not null).ToList();
        RenameRecent = (RenameRecent ?? []).Where(p => p is not null).ToList();
    }

    private static void FillNulls(object target, object defaults)
    {
        foreach (var prop in target.GetType().GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite || prop.GetIndexParameters().Length > 0) continue;
            if (prop.PropertyType != typeof(string) && prop.PropertyType != typeof(List<string>)) continue;
            var value = prop.GetValue(target);
            if (value is null) { prop.SetValue(target, prop.GetValue(defaults) is List<string> l ? new List<string>(l) : prop.GetValue(defaults) ?? ""); continue; }
            if (value is List<string> list) list.RemoveAll(x => x is null);
        }
    }

    public static AppSettings CreateDefault() => new()
    {
        SchemaVersion = CurrentSchema,
        Profiles = DefaultProfiles(),
        SelectedProfile = "NVIDIA",
    };

    public static List<Profile> DefaultProfiles() => [Profile.Nvidia(), Profile.Amd(), Profile.Intel(), Profile.Cpu()];

    public bool Migrate()
    {
        if (SchemaVersion >= CurrentSchema) return false;
        if (SchemaVersion < 2) MigrateFrom1();
        if (Profiles.Count == 0) Profiles = DefaultProfiles();

        if (SchemaVersion < 5)
            foreach (var p in new[] { Profile.Intel(), Profile.Cpu() })
                if (!Profiles.Any(x => x.Encoder == p.Encoder) && FindProfile(p.Name) is null) Profiles.Add(p);

        if (FindProfile(SelectedProfile) is null) SelectedProfile = Profiles[0].Name;

        if (SchemaVersion < 5) FfmpegMode = Media.FfmpegMode.Auto;

        if (SchemaVersion < 6)
            foreach (var p in Profiles.Where(p => p.Extensions.Count == Profile.OldDefaultExtensions.Length
                                                  && Profile.OldDefaultExtensions.All(e => p.Extensions.Contains(e, StringComparer.OrdinalIgnoreCase))))
                p.Extensions = [.. Profile.DefaultExtensions];

        if (SchemaVersion < 7)
            foreach (var p in Profiles)
            {
                p.ConvertLosslessAudio = true;
                p.ConvertDts = true;
            }

        if (RenameTemplate == Rename.RenameOptions.OldDefaultTemplate) RenameTemplate = Rename.RenameOptions.DefaultTemplate;
        SchemaVersion = CurrentSchema;
        return true;
    }

    private void MigrateFrom1()
    {
        foreach (var (old, neu) in new[] { ("Serien (NVIDIA)", "NVIDIA"), ("Serien (AMD)", "AMD") })
        {
            var p = FindProfile(old);
            if (p is null || FindProfile(neu) is not null) continue;
            p.Name = neu;
            if (SelectedProfile.Equals(old, StringComparison.OrdinalIgnoreCase)) SelectedProfile = neu;
        }
    }
}

public sealed record WindowPlace(int Left, int Top, int Right, int Bottom, bool Maximized)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsUsable => Right - Left >= 200 && Bottom - Top >= 150;
}

public sealed class TotalStats
{
    public long FilesEncoded { get; set; }
    public long FilesReplaced { get; set; }
    public long BytesSaved { get; set; }
    public double EncodeSeconds { get; set; }
    public DateTime? FirstRun { get; set; }
    public DateTime? LastRun { get; set; }
}

public sealed class SettingsStore
{
    public const string FolderName = "GimboKonverter";
    public const string OldFolderName = "FilmKonverter";

    public string Folder { get; }
    public string SettingsFile => Path.Combine(Folder, "settings.json");
    public string StatsFile => Path.Combine(Folder, "stats.json");
    public string LogFolder => Path.Combine(Folder, "logs");
    public string ProbeCacheFile => Path.Combine(Folder, "analyse-cache.json.gz");
    public string SnapshotsFile => Path.Combine(Folder, "analyse-stand.json");

    public SettingsStore(string? folder = null)
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Folder = folder ?? Path.Combine(appData, FolderName);
        if (folder is null) MigrateFolder(Path.Combine(appData, OldFolderName), Folder);
        Directory.CreateDirectory(Folder);
    }

    public static void MigrateFolder(string oldFolder, string newFolder)
    {
        try
        {
            if (Directory.Exists(newFolder) || !Directory.Exists(oldFolder)) return;
            Directory.CreateDirectory(newFolder);
            foreach (var f in Directory.EnumerateFiles(oldFolder, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(newFolder, Path.GetRelativePath(oldFolder, f));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(f, target, overwrite: false);
            }
        }
        catch
        {
        }
    }

    public bool ReadOnlyFallback { get; private set; }

    public AppSettings LoadSettings()
    {
        if (!File.Exists(SettingsFile)) return AppSettings.CreateDefault();
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions.Default);
                s?.Normalize();
                if (s is not null && s.Profiles.Count > 0)
                {
                    if (s.Migrate()) SaveSettings(s);
                    return s;
                }
                break;
            }
            catch (JsonException)
            {
                TryBackup(SettingsFile);
                return TryLoadBackup() ?? AppSettings.CreateDefault();
            }
            catch (IOException)
            {
                Thread.Sleep(300);
            }
            catch (Exception ex) when (ex is NullReferenceException or ArgumentException or InvalidOperationException)
            {
                TryBackup(SettingsFile);
                return TryLoadBackup() ?? AppSettings.CreateDefault();
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(300);
            }
        }
        ReadOnlyFallback = true;
        return TryLoadBackup() ?? AppSettings.CreateDefault();
    }

    private AppSettings? TryLoadBackup()
    {
        try
        {
            var bak = SettingsFile + ".bak";
            if (!File.Exists(bak)) return null;
            var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(bak), JsonOptions.Default);
            s?.Normalize();
            if (s is null || s.Profiles.Count == 0) return null;
            s.Migrate();
            return s;
        }
        catch
        {
            return null;
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        if (ReadOnlyFallback) return;
        WriteAtomic(SettingsFile, JsonSerializer.Serialize(settings, JsonOptions.Default), keepBackup: true);
    }

    public TotalStats LoadStats()
    {
        try
        {
            if (File.Exists(StatsFile))
                return JsonSerializer.Deserialize<TotalStats>(File.ReadAllText(StatsFile), JsonOptions.Default) ?? new();
        }
        catch
        {
            TryBackup(StatsFile);
        }
        return new();
    }

    public void SaveStats(TotalStats stats) => WriteAtomic(StatsFile, JsonSerializer.Serialize(stats, JsonOptions.Default));

    public int CleanLogs(int days)
    {
        if (days <= 0 || !Directory.Exists(LogFolder)) return 0;
        var cut = DateTime.Now.AddDays(-days);
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(LogFolder, "*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(f) >= cut) continue;
                File.Delete(f);
                n++;
            }
            catch { }
        }
        return n;
    }

    public static void ExportProfiles(string file, IEnumerable<Profile> profiles) =>
        File.WriteAllText(file, JsonSerializer.Serialize(profiles.ToList(), JsonOptions.Default));

    public static List<Profile> ImportProfiles(string file)
    {
        var text = File.ReadAllText(file).TrimStart();
        if (text.StartsWith('{'))
            return [JsonSerializer.Deserialize<Profile>(text, JsonOptions.Default) ?? throw new InvalidDataException("Leere Datei.")];
        return JsonSerializer.Deserialize<List<Profile>>(text, JsonOptions.Default) ?? [];
    }

    private static readonly object WriteLock = new();

    private static void WriteAtomic(string path, string content, bool keepBackup = false)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var tmp = path + "." + Environment.ProcessId + ".tmp";
            using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var w = new StreamWriter(fs, new System.Text.UTF8Encoding(false)))
            {
                w.Write(content);
                w.Flush();
                fs.Flush(flushToDisk: true);
            }
            if (keepBackup && File.Exists(path))
            {
                try { File.Copy(path, path + ".bak", overwrite: true); } catch { }
            }
            File.Move(tmp, path, overwrite: true);
        }
    }

    private static void TryBackup(string path)
    {
        try { File.Copy(path, path + ".defekt", overwrite: true); } catch { }
    }
}
