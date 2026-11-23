using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;
using Microsoft.Win32;

namespace FilmKonverter.App.ViewModels;

public sealed partial class EncoderItem(EncoderKind kind, string title) : ObservableObject
{
    public EncoderKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string Name => FfmpegTools.EncoderName(Kind);
    [ObservableProperty] private bool _available;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk;
}

public sealed record FfmpegModeOption(FfmpegMode Mode, string Text);

public sealed record InstallationItem(string Folder, string Version, bool Bundled, bool Active)
{
    public string Title => (Bundled ? L.T("Integriert", "Built-in") : L.T("Installiert", "Installed")) + "  ·  " + Version;
    public string State => Active ? L.T("aktiv", "active") : "";
}

public sealed partial class FfmpegViewModel : ObservableObject
{
    private readonly AppState _app;

    public FfmpegViewModel(AppState app)
    {
        _app = app;
        _folder = app.Settings.FfmpegDir;
    }

    public ObservableCollection<EncoderItem> Encoders { get; } =
    [
        new(EncoderKind.Nvenc, "NVIDIA NVENC"),
        new(EncoderKind.Amf, "AMD AMF"),
        new(EncoderKind.Qsv, "Intel Quick Sync"),
        new(EncoderKind.Cpu, L.T("Prozessor (x265)", "Processor (x265)")),
    ];

    public IReadOnlyList<FfmpegModeOption> Modes { get; } =
    [
        new(FfmpegMode.Auto, L.T("Automatisch: neueste passende Version", "Automatic: newest suitable version")),
        new(FfmpegMode.Bundled, L.T("Immer die integrierte", "Always the built-in one")),
        new(FfmpegMode.Custom, L.T("Eigener Ordner", "Custom folder")),
    ];

    public ObservableCollection<InstallationItem> Installations { get; } = [];

    [ObservableProperty] private string _folder;
    [ObservableProperty] private string _version = L.T("nicht geladen", "not loaded");
    [ObservableProperty] private string _activeText = "";
    [ObservableProperty] private bool _loaded;
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private bool _busy;

    public FfmpegMode Mode
    {
        get => _app.Settings.FfmpegMode;
        set
        {
            if (_app.Settings.FfmpegMode == value) return;
            _app.Settings.FfmpegMode = value;
            _app.Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsCustom));
            OnPropertyChanged(nameof(ModeHint));
            if (value != FfmpegMode.Custom || Directory.Exists(Folder)) _ = ReloadAsync();
        }
    }

    public bool IsCustom => Mode == FfmpegMode.Custom;

    public string ModeHint => Mode switch
    {
        FfmpegMode.Auto => L.T("Vergleicht die integrierte mit installierten Versionen und nimmt die neueste, die den Encoder des Profils kennt. Bei gleicher Version die integrierte.",
                               "Compares the built-in version with installed ones and takes the newest that has the profile's encoder. With equal versions, the built-in one."),
        FfmpegMode.Bundled => L.T("Unabhängig davon, was sonst auf dem PC installiert ist.", "Regardless of what else is installed on the PC."),
        _ => L.T("Der unten gewählte Ordner. Klappt das nicht, wird automatisch gewählt.", "The folder chosen below. If that fails, the choice is automatic."),
    };

    public void Refresh()
    {
        var t = _app.Tools;
        Loaded = t is not null;
        Version = t is null ? L.T("nicht geladen", "not loaded") : t.Version;
        ActiveText = t is null ? L.T("Kein ffmpeg gefunden.", "No ffmpeg found.")
            : (_app.ToolsAreBundled ? L.T("Integriertes ffmpeg", "Built-in ffmpeg") : L.T("Installiertes ffmpeg", "Installed ffmpeg")) + "  ·  " + t.Folder;
        if (Mode != FfmpegMode.Custom && t is not null && !_app.ToolsAreBundled) Folder = t.Folder;
        foreach (var e in Encoders) e.Available = t?.HasEncoder(e.Kind) ?? false;
        Installations.Clear();
        foreach (var c in _app.Candidates.OrderByDescending(c => c.ParsedVersion ?? new Version(0, 0)).ThenByDescending(c => c.Bundled))
            Installations.Add(new InstallationItem(c.Tools.Folder, c.Tools.Version, c.Bundled,
                t is not null && string.Equals(c.Tools.Folder, t.Folder, StringComparison.OrdinalIgnoreCase)));
    }

    private async Task ReloadAsync()
    {
        Busy = true;
        try
        {
            var t = await _app.EnsureToolsAsync(reload: true);
            Message = t is null ? L.T("Kein ffmpeg gefunden.", "No ffmpeg found.") : L.T($"ffmpeg {t.Version} aktiv.", $"ffmpeg {t.Version} active.");
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private Task SearchAsync() => ReloadAsync();

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (await FfmpegTools.TryLoadAsync(Folder) is null)
        {
            Message = L.T("In diesem Ordner wurden ffmpeg und ffprobe nicht gefunden.", "ffmpeg and ffprobe were not found in this folder.");
            return;
        }
        _app.Settings.FfmpegDir = Folder;
        _app.Settings.FfmpegMode = FfmpegMode.Custom;
        _app.Save();
        OnPropertyChanged(nameof(Mode));
        OnPropertyChanged(nameof(IsCustom));
        OnPropertyChanged(nameof(ModeHint));
        await ReloadAsync();
    }

    [RelayCommand]
    private async Task BrowseAsync()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner mit ffmpeg.exe und ffprobe.exe wählen", "Choose the folder with ffmpeg.exe and ffprobe.exe") };
        if (Directory.Exists(Folder)) dlg.InitialDirectory = Folder;
        if (dlg.ShowDialog() != true) return;
        Folder = dlg.FolderName;
        await LoadAsync();
    }

    [RelayCommand]
    private async Task UseInstallationAsync(InstallationItem? item)
    {
        if (item is null) return;
        if (item.Bundled)
        {
            Mode = FfmpegMode.Bundled;
            return;
        }
        Folder = item.Folder;
        await LoadAsync();
    }

    // ---------------- Integriertes ffmpeg aktualisieren ----------------

    private FfmpegRelease? _offer;
    private bool _checkedThisSession;

    [ObservableProperty] private string _updateText = L.T("Noch nicht gesucht.", "Not checked yet.");
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _updating;
    [ObservableProperty] private double _updateProgress;

    [ObservableProperty] private string _bundledVersionText = "";

    public bool CanRollback => !string.IsNullOrWhiteSpace(_app.Settings.FfmpegUpdateDir) && !Updating;

    partial void OnUpdatingChanged(bool value) => OnPropertyChanged(nameof(CanRollback));

    public async Task CheckOnOpenAsync()
    {
        if (_checkedThisSession || App.ScreenshotFolder is not null) return;
        _checkedThisSession = true;
        await CheckUpdateAsync();
    }

    private async Task<FfmpegTools?> BundledToolsAsync()
    {
        var folder = await _app.EnsureBundledAsync();
        var t = folder is null ? null : await FfmpegTools.TryLoadAsync(folder);
        BundledVersionText = t is null ? "" : (string.IsNullOrWhiteSpace(_app.Settings.FfmpegUpdateDir)
            ? L.T($"Integriert: {t.Version}", $"Built-in: {t.Version}")
            : L.T($"Integriert (aktualisiert): {t.Version}", $"Built-in (updated): {t.Version}"));
        return t;
    }

    public async Task<string?> CheckQuietAsync()
    {
        if (Updating) return null;
        try
        {
            var current = await BundledToolsAsync();
            using var http = FfmpegUpdate.CreateClient();
            var offer = await FfmpegUpdate.CheckAsync(http);
            if (offer is null || !FfmpegUpdate.IsNewer(offer, current?.Version)) return null;
            _offer = offer;
            UpdateAvailable = true;
            UpdateText = L.T($"Neue Version {offer.Version} verfügbar. Vor dem Umstellen wird geprüft, ob sie alles kann, was Gimbo braucht.",
                             $"New version {offer.Version} available. Before switching, it is checked that it can do everything Gimbo needs.");
            return offer.Version;
        }
        catch (Exception ex)
        {
            _app.WriteLog("ffmpeg-Update: taegliche Suche fehlgeschlagen: " + ex.Message);
            return null;
        }
    }

    [RelayCommand]
    private async Task CheckUpdateAsync()
    {
        if (Updating) return;
        UpdateAvailable = false;
        UpdateText = L.T("Suche …", "Checking …");
        var current = await BundledToolsAsync();
        try
        {
            using var http = FfmpegUpdate.CreateClient();
            _offer = await FfmpegUpdate.CheckAsync(http);
            if (_offer is null) { UpdateText = L.T("Die Quelle liefert gerade keine Versionsangabe.", "The source currently gives no version."); return; }
            UpdateAvailable = FfmpegUpdate.IsNewer(_offer, current?.Version);
            UpdateText = UpdateAvailable
                ? L.T($"Neue Version {_offer.Version} verfügbar. Vor dem Umstellen wird geprüft, ob sie alles kann, was Gimbo braucht.",
                      $"New version {_offer.Version} available. Before switching, it is checked that it can do everything Gimbo needs.")
                : L.T($"Aktuell (neueste stabile Version: {_offer.Version}).", $"Up to date (newest stable version: {_offer.Version}).");
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            UpdateText = L.T("Update-Quelle nicht erreichbar (keine Internetverbindung?).", "Update source not reachable (no internet connection?).");
            _app.WriteLog("ffmpeg-Update: Suche fehlgeschlagen: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task InstallUpdateAsync()
    {
        if (_offer is null || Updating) return;
        if (_app.WorkInProgress)
        {
            UpdateText = L.T("Gerade wird gearbeitet. Bitte nach dem laufenden Vorgang aktualisieren.", "Work is in progress. Please update after the current task.");
            return;
        }
        Updating = true;
        UpdateProgress = 0;
        var loading = L.T($"Lade ffmpeg {_offer.Version} von {_offer.Source} …", $"Downloading ffmpeg {_offer.Version} from {_offer.Source} …");
        UpdateText = loading;
        try
        {
            var reference = await BundledToolsAsync();
            using var http = FfmpegUpdate.CreateClient();
            var progress = new Progress<double>(p => UpdateProgress = p * 100);
            var detail = new Progress<string>(d => UpdateText = loading + " " + d);
            var folder = await FfmpegUpdate.InstallAsync(http, _offer, AppState.FfmpegUpdateRoot, reference, progress, detail: detail);
            if (_app.WorkInProgress)
            {
                UpdateText = L.T("Inzwischen wurde ein Vorgang gestartet. Das Update liegt bereit: bitte danach noch einmal „Aktualisieren“.",
                                 "A task was started in the meantime. The update is ready: please press “Update” again afterwards.");
                return;
            }
            await _app.SwitchBundledAsync(folder, _app.Settings.FfmpegUpdateDir);
            _app.WriteLog($"ffmpeg-Update: integriertes ffmpeg ist jetzt {_offer.Version} ({folder})");
            UpdateAvailable = false;
            await BundledToolsAsync();
            UpdateText = L.T($"Aktualisiert auf {_offer.Version}. Die bisherige Version bleibt für „Zurück zur vorherigen“.",
                             $"Updated to {_offer.Version}. The previous version is kept for “Back to previous”.");
            Refresh();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or InvalidOperationException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            UpdateText = ex is InvalidOperationException ? ex.Message : L.T("Update fehlgeschlagen: ", "Update failed: ") + ex.Message;
            _app.WriteLog("ffmpeg-Update fehlgeschlagen: " + ex.Message);
        }
        finally
        {
            Updating = false;
            OnPropertyChanged(nameof(CanRollback));
        }
    }

    [RelayCommand]
    private async Task RollbackAsync()
    {
        if (!CanRollback) return;
        if (_app.WorkInProgress)
        {
            UpdateText = L.T("Gerade wird gearbeitet. Bitte danach zurückstellen.", "Work is in progress. Please switch back afterwards.");
            return;
        }
        Updating = true;
        try
        {
            await _app.SwitchBundledAsync(_app.Settings.FfmpegPreviousDir, "");
            var t = await BundledToolsAsync();
            _app.WriteLog($"ffmpeg-Update: zurueck zur vorherigen Version {t?.Version}");
            UpdateText = L.T($"Zurück auf {t?.Version}.", $"Back to {t?.Version}.");
            _offer = null;
            UpdateAvailable = false;
            Refresh();
        }
        finally
        {
            Updating = false;
            OnPropertyChanged(nameof(CanRollback));
        }
    }

    [RelayCommand]
    private async Task TestAsync(EncoderItem item)
    {
        var t = _app.Tools ?? await _app.EnsureToolsAsync();
        if (t is null) { item.TestResult = L.T("ffmpeg nicht geladen", "ffmpeg not loaded"); return; }
        item.TestResult = L.T("teste ...", "testing ...");
        var (ok, msg) = await t.TestEncoderAsync(item.Kind);
        item.TestOk = ok;
        item.TestResult = ok ? L.T("funktioniert auf diesem PC", "works on this PC") : msg;
    }
}
