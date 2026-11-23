using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.App.Services;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.ViewModels;

public sealed record ThemeOption(ThemeMode Mode, string Text);
public sealed record PriorityOption(WorkPriority Value, string Text);
public sealed record AfterRunOption(AfterRunAction Value, string Text);
public sealed record LanguageOption(UiLanguage Value, string Text);

public sealed partial class SettingsViewModel(AppState app) : ObservableObject
{
    private AppSettings S => app.Settings;

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(ThemeMode.Dark, L.T("Dunkel", "Dark")),
        new(ThemeMode.Light, L.T("Hell", "Light")),
        new(ThemeMode.System, L.T("Wie Windows", "Like Windows")),
    ];

    public IReadOnlyList<PriorityOption> Priorities { get; } =
    [
        new(WorkPriority.Normal, "Normal"),
        new(WorkPriority.BelowNormal, L.T("Niedriger als normal (empfohlen)", "Below normal (recommended)")),
        new(WorkPriority.Idle, L.T("Nur im Leerlauf", "Idle only")),
    ];

    public IReadOnlyList<AfterRunOption> AfterRunOptions { get; } =
    [
        new(AfterRunAction.Nothing, L.T("Nichts", "Nothing")),
        new(AfterRunAction.CloseApp, L.T("Programm beenden", "Exit program")),
        new(AfterRunAction.Sleep, L.T("Energie sparen (Standby)", "Sleep")),
        new(AfterRunAction.Hibernate, L.T("Ruhezustand", "Hibernate")),
        new(AfterRunAction.Shutdown, L.T("Herunterfahren", "Shut down")),
    ];

    public IReadOnlyList<LanguageOption> Languages { get; } =
    [
        new(UiLanguage.Auto, L.T("Automatisch (wie Windows)", "Automatic (like Windows)")),
        new(UiLanguage.German, "Deutsch"),
        new(UiLanguage.English, "English"),
    ];

    public IReadOnlyList<IntOption> ParallelOptions { get; } =
    [
        new(1, L.T("1 Datei (Standard)", "1 file (default)")),
        new(2, L.T("2 Dateien gleichzeitig", "2 files at once")),
        new(3, L.T("3 Dateien gleichzeitig", "3 files at once")),
    ];

    [ObservableProperty] private string _message = "";

    [ObservableProperty] private bool _restartNeeded;

    public UiLanguage Language
    {
        get => S.Language;
        set => Set(S.Language, value, v =>
        {
            S.Language = v;
            var german = v switch { UiLanguage.German => true, UiLanguage.English => false, _ => L.Detect() };
            RestartNeeded = german != L.German;
        });
    }

    [RelayCommand]
    private void RestartNow() => (System.Windows.Application.Current as App)?.Restart();

    private void Set<T>(T current, T value, Action<T> apply, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(current, value)) return;
        apply(value);
        app.Save();
        OnPropertyChanged(name);
    }

    // ---------------- Darstellung ----------------

    public ThemeMode Theme
    {
        get => S.Theme;
        set => Set(S.Theme, value, v => { S.Theme = v; ThemeHelper.Apply(v); });
    }

    public bool SidebarExpanded
    {
        get => S.SidebarExpanded;
        set => Set(S.SidebarExpanded, value, v =>
        {
            S.SidebarExpanded = v;
            if (System.Windows.Application.Current?.MainWindow is MainWindow mw && mw.RootNavigation.IsPaneOpen != v) mw.RootNavigation.IsPaneOpen = v;
        });
    }

    // ---------------- Start und Hintergrund ----------------

    public bool ShowTrayIcon
    {
        get => S.ShowTrayIcon;
        set => Set(S.ShowTrayIcon, value, v =>
        {
            S.ShowTrayIcon = v;
            if (System.Windows.Application.Current is App a && a.Tray is not null) a.Tray.Visible = v;
            OnPropertyChanged(nameof(TrayNote));
        });
    }

    public string TrayNote => S.ShowTrayIcon ? "" : L.T("Ohne Symbol im Infobereich wird in die Taskleiste minimiert und normal geschlossen.", "Without the notification area icon the window minimizes to the taskbar and closes normally.");

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set => Set(S.StartWithWindows, value, v =>
        {
            var err = Autostart.Set(v);
            if (err is not null) { Message = L.T("Autostart konnte nicht geändert werden: ", "Could not change autostart: ") + err; return; }
            S.StartWithWindows = v;
            Message = v ? L.T("Startet ab jetzt mit Windows.", "Starts with Windows from now on.") : L.T("Autostart entfernt.", "Autostart removed.");
        });
    }

    public bool AutostartMinimized { get => S.AutostartMinimized; set => Set(S.AutostartMinimized, value, v => S.AutostartMinimized = v); }
    public bool StartMinimized { get => S.StartMinimized; set => Set(S.StartMinimized, value, v => S.StartMinimized = v); }
    public bool MinimizeToTray { get => S.MinimizeToTray; set => Set(S.MinimizeToTray, value, v => S.MinimizeToTray = v); }
    public bool CloseToTray { get => S.CloseToTray; set => Set(S.CloseToTray, value, v => S.CloseToTray = v); }
    public bool AutoStartRun { get => S.AutoStartRun; set => Set(S.AutoStartRun, value, v => S.AutoStartRun = v); }

    // ---------------- Arbeitsweise ----------------

    public WorkPriority Priority { get => S.Priority; set => Set(S.Priority, value, v => S.Priority = v); }
    public bool PreventSleep { get => S.PreventSleep; set => Set(S.PreventSleep, value, v => S.PreventSleep = v); }
    public bool ContinuousMode { get => S.ContinuousMode; set => Set(S.ContinuousMode, value, v => S.ContinuousMode = v); }
    public int ParallelFiles { get => S.ParallelFiles; set => Set(S.ParallelFiles, Math.Clamp(value, 1, 3), v => S.ParallelFiles = v); }
    public bool PauseWhenFullscreen { get => S.PauseWhenFullscreen; set => Set(S.PauseWhenFullscreen, value, v => S.PauseWhenFullscreen = v); }
    public string FullscreenIgnore
    {
        get => S.FullscreenIgnore;
        set
        {
            Set(S.FullscreenIgnore, FullscreenRules.Clean(value), v => S.FullscreenIgnore = v);
            OnPropertyChanged();
        }
    }

    public bool PauseWhenUserActive { get => S.PauseWhenUserActive; set => Set(S.PauseWhenUserActive, value, v => S.PauseWhenUserActive = v); }
    public int UserIdleMinutes { get => S.UserIdleMinutes; set => Set(S.UserIdleMinutes, Math.Clamp(value, 1, 240), v => S.UserIdleMinutes = v); }
    public bool ShareStatus { get => S.ShareStatus; set => Set(S.ShareStatus, value, v => S.ShareStatus = v); }

    public bool ShareData
    {
        get => S.ShareData;
        set => Set(S.ShareData, value, v =>
        {
            S.ShareData = v;
            FilmKonverter.Core.Library.DataStore.Share = v;
            FilmKonverter.Core.Library.DataStore.ResetUsable();
        });
    }

    public int RescanMinutes
    {
        get => S.RescanMinutes;
        set => Set(S.RescanMinutes, Math.Clamp(value, 1, 24 * 60), v => S.RescanMinutes = v);
    }

    public bool ScheduleEnabled
    {
        get => S.ScheduleEnabled;
        set => Set(S.ScheduleEnabled, value, v => { S.ScheduleEnabled = v; OnPropertyChanged(nameof(ScheduleText)); });
    }

    public string ScheduleFrom
    {
        get => S.ScheduleFrom;
        set => SetTime(value, true);
    }

    public string ScheduleTo
    {
        get => S.ScheduleTo;
        set => SetTime(value, false);
    }

    private void SetTime(string value, bool from)
    {
        if (!TimeWindow.TryParseTime(value, out var t))
        {
            Message = L.T($"\"{value}\" ist keine Uhrzeit (z.B. 22:00).", $"\"{value}\" is not a time (e.g. 22:00).");
            OnPropertyChanged(from ? nameof(ScheduleFrom) : nameof(ScheduleTo));
            return;
        }
        var text = t.ToString(@"hh\:mm");
        if (from) S.ScheduleFrom = text; else S.ScheduleTo = text;
        app.Save();
        Message = "";
        OnPropertyChanged(from ? nameof(ScheduleFrom) : nameof(ScheduleTo));
        OnPropertyChanged(nameof(ScheduleText));
    }

    public string ScheduleText
    {
        get
        {
            var w = TimeWindow.Parse(S.ScheduleFrom, S.ScheduleTo);
            if (w is null) return L.T("Uhrzeiten prüfen.", "Check the times.");
            if (!S.ScheduleEnabled) return L.T($"Aus: es wird rund um die Uhr gearbeitet. Eingeschaltet gilt {w}.", $"Off: work runs around the clock. When switched on, {w} applies.");
            if (w.From == w.To) return L.T("Gleiche Zeiten = ganzer Tag.", "Same times = whole day.");
            var open = w.IsOpen(DateTime.Now);
            return open ? L.T($"Jetzt offen. Neue Dateien werden nur zwischen {w} begonnen, eine laufende Datei wird fertig gemacht.", $"Open now. New files are only started between {w}, a file in progress is finished.")
                        : L.T($"Jetzt geschlossen, nächster Beginn {w.NextOpen(DateTime.Now):dd.MM. HH:mm}. Eine laufende Datei wird fertig gemacht.", $"Closed now, next start {w.NextOpen(DateTime.Now):dd.MM. HH:mm}. A file in progress is finished.");
        }
    }

    public AfterRunAction AfterRun { get => S.AfterRun; set => Set(S.AfterRun, value, v => S.AfterRun = v); }

    // ---------------- Hinweise ----------------

    public bool Notifications { get => S.Notifications; set => Set(S.Notifications, value, v => S.Notifications = v); }
    public bool NotifyEachFile { get => S.NotifyEachFile; set => Set(S.NotifyEachFile, value, v => S.NotifyEachFile = v); }
    public bool TaskbarProgress { get => S.TaskbarProgress; set => Set(S.TaskbarProgress, value, v => S.TaskbarProgress = v); }
    public bool ConfirmExit { get => S.ConfirmExit; set => Set(S.ConfirmExit, value, v => S.ConfirmExit = v); }

    public int LogRetentionDays
    {
        get => S.LogRetentionDays;
        set => Set(S.LogRetentionDays, Math.Max(0, value), v => S.LogRetentionDays = v);
    }

    // ---------------- Daten ----------------

    public string DataFolder => App.ScreenshotFolder is null ? app.Store.Folder : @"C:\Users\Gimbo\AppData\Roaming\GimboKonverter";
    public string ComputerName => App.ScreenshotFolder is null ? Environment.MachineName : "PC-1";
    public string AppVersion => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public string TotalSaved => app.Stats.BytesSaved > 0 ? Fmt.Size(app.Stats.BytesSaved) : "0 GB";
    public string TotalFiles => (app.Stats.FilesEncoded + app.Stats.FilesReplaced).ToString("N0", Fmt.De);
    public string TotalReplaced => app.Stats.FilesReplaced.ToString("N0", Fmt.De);
    public string TotalTime => TimeSpan.FromSeconds(app.Stats.EncodeSeconds).TotalHours.ToString("N1", Fmt.De) + " h";
    public string Since => app.Stats.FirstRun?.ToString("dd.MM.yyyy") ?? "-";

    public void RefreshStats()
    {
        OnPropertyChanged(nameof(TotalSaved));
        OnPropertyChanged(nameof(TotalFiles));
        OnPropertyChanged(nameof(TotalReplaced));
        OnPropertyChanged(nameof(TotalTime));
        OnPropertyChanged(nameof(Since));
        OnPropertyChanged(nameof(ScheduleText));
    }

    // ---------------- Programm-Update ----------------

    private AppRelease? _appOffer;

    [ObservableProperty] private string _appUpdateText = L.T("Noch nicht gesucht.", "Not checked yet.");
    [ObservableProperty] private bool _appUpdateAvailable;
    [ObservableProperty] private bool _appUpdating;
    [ObservableProperty] private double _appUpdateProgress;

    public bool CheckForAppUpdates { get => S.CheckForAppUpdates; set => Set(S.CheckForAppUpdates, value, v => S.CheckForAppUpdates = v); }

    public string AppUpdateFolder
    {
        get => S.AppUpdateFolder;
        set => Set(S.AppUpdateFolder, (value ?? "").Trim().Trim('"'), v => S.AppUpdateFolder = v);
    }

    private static Version CurrentVersion => AppUpdate.Norm(typeof(App).Assembly.GetName().Version ?? new Version(0, 0, 0));

    private async Task<(AppRelease? Offer, string Text)> FindAppUpdateAsync()
    {
        AppRelease? fromGitHub = null, fromFolder = null;
        string? githubError = null;
        try
        {
            using var http = AppUpdate.CreateClient();
            fromGitHub = await AppUpdate.CheckGitHubAsync(http);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            githubError = ex is InvalidOperationException ? ex.Message : L.T("GitHub nicht erreichbar.", "GitHub not reachable.");
        }
        var folder = S.AppUpdateFolder;
        if (folder.Length > 0) fromFolder = await Task.Run(() => AppUpdate.CheckFolder(folder));
        var offer = AppUpdate.NewerThan(CurrentVersion, fromGitHub, fromFolder);
        if (offer is not null)
            return (offer, L.T($"Neue Version {offer.VersionText} verfügbar ({SourceText(offer)}).", $"New version {offer.VersionText} available ({SourceText(offer)})."));
        var newest = new[] { fromGitHub, fromFolder }.Where(r => r is not null).Select(r => r!.VersionText).FirstOrDefault();
        if (newest is not null || githubError is null)
            return (null, L.T($"Aktuell (Version {CurrentVersion.ToString(3)}).", $"Up to date (version {CurrentVersion.ToString(3)})."));
        return (null, folder.Length > 0 ? L.T($"{githubError} Im Update-Ordner liegt keine neuere Version.", $"{githubError} The update folder holds no newer version.") : githubError);
    }

    private static string SourceText(AppRelease r) => r.LocalFile is null ? "GitHub" : L.T("Update-Ordner", "update folder");

    [RelayCommand]
    private async Task CheckAppUpdateAsync()
    {
        if (AppUpdating) return;
        AppUpdateText = L.T("Suche …", "Checking …");
        S.LastAppUpdateCheckUtc = DateTime.UtcNow;
        (_appOffer, AppUpdateText) = await FindAppUpdateAsync();
        AppUpdateAvailable = _appOffer is not null;
    }

    private bool _dailyBusy;
    private readonly HashSet<string> _notified = [];

    public async Task CheckUpdatesDailyAsync(bool atStart = false)
    {
        if (App.ScreenshotFolder is not null || !S.CheckForAppUpdates || _dailyBusy) return;
        if (DateTime.UtcNow - S.LastAppUpdateCheckUtc < TimeSpan.FromHours(20)) return;
        _dailyBusy = true;
        try
        {
            if (atStart) await Task.Delay(TimeSpan.FromSeconds(20));
            S.LastAppUpdateCheckUtc = DateTime.UtcNow;
            app.Save();
            var notes = new List<string>();

            try
            {
                (_appOffer, AppUpdateText) = await FindAppUpdateAsync();
                AppUpdateAvailable = _appOffer is not null;
                if (_appOffer is { } o)
                    notes.Add(L.T($"Gimbo {o.VersionText} (Einstellungen → Daten und Info → Jetzt aktualisieren)", $"Gimbo {o.VersionText} (Settings → Data and info → Update now)"));
            }
            catch (Exception ex)
            {
                app.WriteLog("Programm-Update: Suche fehlgeschlagen: " + ex.Message);
            }

            if (await app.Ffmpeg.CheckQuietAsync() is { } ff)
                notes.Add(L.T($"ffmpeg {ff} (Seite ffmpeg → Aktualisieren)", $"ffmpeg {ff} (page ffmpeg → Update)"));

            if (notes.Count == 0) return;
            app.Run.UpdateNotice = L.T("Neue Version verfügbar: ", "New version available: ") + string.Join(L.T(" · ", " · "), notes)
                                   + L.T(". Installiert wird erst auf Knopfdruck.", ". Nothing is installed until you press the button.");
            var key = string.Join("|", notes);
            if (_notified.Add(key))
                (System.Windows.Application.Current as App)?.Tray?.Notify(App.Title, L.T("Neue Version verfügbar: ", "New version available: ") + string.Join(", ", notes.Select(n => n.Split(" (")[0])));
        }
        finally
        {
            _dailyBusy = false;
        }
    }

    [RelayCommand]
    private async Task InstallAppUpdateAsync()
    {
        if (_appOffer is not { } offer || AppUpdating) return;
        var exe = Environment.ProcessPath;
        if (exe is null) return;
        if (app.WorkInProgress)
        {
            AppUpdateText = L.T("Gerade wird gearbeitet. Bitte nach dem laufenden Vorgang aktualisieren.", "Work is in progress. Please update after the current task.");
            return;
        }
        if (!AppUpdate.CanWrite(exe))
        {
            AppUpdateText = L.T($"In den Programmordner darf nicht geschrieben werden ({Path.GetDirectoryName(exe)}). Bitte die neue Version von Hand entpacken.",
                                $"The program folder is not writable ({Path.GetDirectoryName(exe)}). Please unpack the new version by hand.");
            return;
        }
        AppUpdating = true;
        AppUpdateProgress = 0;
        AppUpdateText = L.T($"Lade Version {offer.VersionText} …", $"Downloading version {offer.VersionText} …");
        string? newExe = null;
        try
        {
            using var http = AppUpdate.CreateClient();
            var loading = AppUpdateText;
            newExe = await AppUpdate.PrepareAsync(http, offer, exe, new Progress<double>(p => AppUpdateProgress = p * 100),
                detail: new Progress<string>(d => AppUpdateText = loading + " " + d));
            var ok = System.Windows.MessageBox.Show(
                L.T($"Version {offer.VersionText} ist geladen und geprüft. Gimbo wird jetzt beendet und neu gestartet.", $"Version {offer.VersionText} is downloaded and checked. Gimbo now closes and restarts."),
                App.Title, System.Windows.MessageBoxButton.OKCancel, System.Windows.MessageBoxImage.Information, System.Windows.MessageBoxResult.OK);
            if (ok != System.Windows.MessageBoxResult.OK || app.WorkInProgress)
            {
                AppUpdateText = L.T("Update abgebrochen, es wurde nichts geändert.", "Update cancelled, nothing was changed.");
                AppUpdate.CleanupOld(exe);
                return;
            }
            AppUpdate.Swap(exe, newExe);
            app.WriteLog($"Programm-Update: {CurrentVersion.ToString(3)} -> {offer.VersionText} ({SourceText(offer)})");
            (System.Windows.Application.Current as App)?.Restart("--nach-update");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            AppUpdateText = ex is InvalidOperationException ? ex.Message : L.T("Update fehlgeschlagen: ", "Update failed: ") + ex.Message;
            app.WriteLog("Programm-Update fehlgeschlagen: " + ex.Message);
            AppUpdate.CleanupOld(exe);
        }
        finally
        {
            AppUpdating = false;
        }
    }

    [RelayCommand]
    private void PickAppUpdateFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = L.T("Ordner wählen, in den neue Versionen gelegt werden", "Choose the folder new versions are put into") };
        if (Directory.Exists(AppUpdateFolder)) dlg.InitialDirectory = AppUpdateFolder;
        if (dlg.ShowDialog() == true) AppUpdateFolder = dlg.FolderName;
    }

    [RelayCommand]
    private void OpenDataFolder() =>
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{DataFolder}\"") { UseShellExecute = true });

    [RelayCommand]
    private void OpenTodayLog()
    {
        var f = Path.Combine(app.Store.LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log");
        if (!File.Exists(f)) { Message = L.T("Heute wurde noch nichts protokolliert.", "Nothing has been logged today."); return; }
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(f) { UseShellExecute = true });
    }

    [RelayCommand]
    private void ResetStats()
    {
        var answer = System.Windows.MessageBox.Show(L.T("Gesamtstatistik auf null setzen?", "Reset the overall statistics to zero?"), App.Title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        app.Stats.BytesSaved = 0;
        app.Stats.FilesEncoded = 0;
        app.Stats.FilesReplaced = 0;
        app.Stats.EncodeSeconds = 0;
        app.Stats.FirstRun = null;
        app.SaveStats();
        RefreshStats();
    }
}

public static class ThemeHelper
{
    public static void Apply(ThemeMode mode)
    {
        var window = System.Windows.Application.Current?.MainWindow;
        if (mode == ThemeMode.System)
        {
            if (window is not null) Wpf.Ui.Appearance.SystemThemeWatcher.Watch(window);
            var sys = Wpf.Ui.Appearance.ApplicationThemeManager.GetSystemTheme();
            var dark = sys == Wpf.Ui.Appearance.SystemTheme.Dark;
            Wpf.Ui.Appearance.ApplicationThemeManager.Apply(dark ? Wpf.Ui.Appearance.ApplicationTheme.Dark : Wpf.Ui.Appearance.ApplicationTheme.Light);
            return;
        }
        if (window is not null) Wpf.Ui.Appearance.SystemThemeWatcher.UnWatch(window);
        Wpf.Ui.Appearance.ApplicationThemeManager.Apply(mode == ThemeMode.Light
            ? Wpf.Ui.Appearance.ApplicationTheme.Light
            : Wpf.Ui.Appearance.ApplicationTheme.Dark);
    }
}
