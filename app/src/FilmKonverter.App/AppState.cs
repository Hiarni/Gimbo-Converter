using FilmKonverter.App.ViewModels;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Settings;

namespace FilmKonverter.App;

public sealed class AppState
{
    public static AppState Instance { get; } = new();

    public SettingsStore Store { get; } = new(App.ScreenshotFolder is null ? null : Path.Combine(Path.GetTempPath(), "GimboKonverter-Screenshots-" + Guid.NewGuid().ToString("N")));
    public AppSettings Settings { get; private set; } = AppSettings.CreateDefault();
    public TotalStats Stats { get; private set; } = new();
    public FfmpegTools? Tools { get; set; }

    public RunViewModel Run { get; }
    public ProfilesViewModel Profiles { get; }
    public FfmpegViewModel Ffmpeg { get; }
    public ToolsViewModel ToolsPage { get; }
    public SettingsViewModel SettingsPage { get; }
    public RenameViewModel Rename { get; }
    public LibraryViewModel Library { get; }
    public StatsViewModel Statistics { get; }
    public ConvertViewModel Convert { get; }
    public RepairViewModel Repair { get; }

    public Dictionary<string, Core.Library.AnalysisSnapshot> Snapshots { get; private set; } = new(StringComparer.OrdinalIgnoreCase);

    public void SaveSnapshot(Core.Library.AnalysisSnapshot snap)
    {
        Snapshots[snap.Profile] = snap;
        Core.Library.SpaceForecast.Save(Store.SnapshotsFile, Snapshots);
    }

    public void ApplyDataStore()
    {
        Core.Library.DataStore.Configure(
            Path.Combine(Store.Folder, "Daten"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimboKonverter", "Test"),
            Settings.ShareData);
        Core.Library.DataStore.Relink(Settings.Profiles.Select(p => (p.OutputDir, p.InputDir)));
    }

    private AppState()
    {
        Settings = Store.LoadSettings();
        ApplyDataStore();
        Snapshots = Core.Library.SpaceForecast.Load(Store.SnapshotsFile);
        Stats = Store.LoadStats();
        Core.Util.L.Set(App.LanguageOverride ?? Settings.Language);
        var culture = Core.Util.L.Culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentCulture = culture;
        System.Globalization.CultureInfo.DefaultThreadCurrentUICulture = culture;
        System.Globalization.CultureInfo.CurrentCulture = culture;
        System.Globalization.CultureInfo.CurrentUICulture = culture;
        try
        {
            System.Windows.FrameworkElement.LanguageProperty.OverrideMetadata(typeof(System.Windows.FrameworkElement),
                new System.Windows.FrameworkPropertyMetadata(System.Windows.Markup.XmlLanguage.GetLanguage(culture.IetfLanguageTag)));
        }
        catch
        {
        }
        Run = new RunViewModel(this);
        Profiles = new ProfilesViewModel(this);
        Ffmpeg = new FfmpegViewModel(this);
        ToolsPage = new ToolsViewModel(this);
        SettingsPage = new SettingsViewModel(this);
        Rename = new RenameViewModel(this);
        Library = new LibraryViewModel(this);
        Statistics = new StatsViewModel(this);
        Convert = new ConvertViewModel(this);
        Repair = new RepairViewModel(this);
    }

    private Task? _cacheLoad;

    public Task LoadProbeCacheAsync() => _cacheLoad ??= Task.Run(() =>
    {
        ProbeCache.Shared = ProbeCache.Load(Store.ProbeCacheFile);
    });

    private string _lastSavedJson = "";

    public void Save()
    {
        Store.SaveSettings(Settings);
        _lastSavedJson = SettingsJson();
        ApplyDataStore();
    }

    private string SettingsJson()
    {
        try { return System.Text.Json.JsonSerializer.Serialize(Settings, JsonOptions.Default); }
        catch { return ""; }
    }

    public void SaveIfChanged()
    {
        var json = SettingsJson();
        if (json.Length == 0 || json == _lastSavedJson) return;
        if (_lastSavedJson.Length == 0) { _lastSavedJson = json; return; }
        Store.SaveSettings(Settings);
        _lastSavedJson = json;
        ApplyDataStore();
    }

    public void SaveStats() => Store.SaveStats(Stats);

    public string? BundledFolder { get; private set; }

    public List<FfmpegCandidate> Candidates { get; private set; } = [];

    private readonly SemaphoreSlim _toolsLock = new(1, 1);

    public string? EmbeddedFolder { get; private set; }

    public static string FfmpegUpdateRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimboKonverter", "ffmpeg-update");

    public async Task<string?> EnsureBundledAsync()
    {
        if (BundledFolder is not null) return BundledFolder;
        if (EmbeddedFolder is null)
        {
            var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GimboKonverter", "ffmpeg");
            try
            {
                EmbeddedFolder = await Task.Run(() => BundledFfmpeg.EnsureExtracted(
                    () => typeof(AppState).Assembly.GetManifestResourceStream(BundledFfmpeg.ResourceName), root));
            }
            catch (Exception ex)
            {
                WriteLog("Integriertes ffmpeg nicht entpackbar: " + ex.Message);
            }
        }
        BundledFolder = await ChooseBundledAsync();
        return BundledFolder;
    }

    private async Task<string?> ChooseBundledAsync()
    {
        var upd = Settings.FfmpegUpdateDir;
        if (string.IsNullOrWhiteSpace(upd)) return EmbeddedFolder;
        var tools = await FfmpegTools.TryLoadAsync(upd);
        var updVersion = FfmpegLocator.ParseVersion(Path.GetFileName(upd.TrimEnd('\\', '/')));
        var embVersion = EmbeddedFolder is null ? null : FfmpegLocator.ParseVersion(Path.GetFileName(EmbeddedFolder));
        if (tools is null)
        {
            WriteLog($"Aktualisiertes ffmpeg nicht mehr nutzbar ({upd}), verwende das integrierte");
            return EmbeddedFolder;
        }
        if (EmbeddedFolder is not null && embVersion is not null && updVersion is not null && embVersion >= updVersion)
        {
            WriteLog($"Integriertes ffmpeg {embVersion} ist so neu wie der Download {updVersion}: Download wird entfernt");
            Settings.FfmpegUpdateDir = "";
            Settings.FfmpegPreviousDir = "";
            Save();
            FfmpegUpdate.Prune(FfmpegUpdateRoot);
            return EmbeddedFolder;
        }
        return upd;
    }

    public bool WorkInProgress => Run.IsBusy || Convert.IsRunning || Rename.IsExecuting || Library.IsBusy;

    public async Task SwitchBundledAsync(string updateDir, string previousDir)
    {
        Settings.FfmpegUpdateDir = updateDir;
        Settings.FfmpegPreviousDir = previousDir;
        Save();
        BundledFolder = null;
        await EnsureToolsAsync(reload: true);
        FfmpegUpdate.Prune(FfmpegUpdateRoot, Settings.FfmpegUpdateDir, Settings.FfmpegPreviousDir);
    }

    private bool _toolsPickedAutomatically;

    public async Task<FfmpegTools?> EnsureToolsAsync(bool reload = false, EncoderKind? encoder = null)
    {
        await _toolsLock.WaitAsync();
        try
        {
            if (Tools is not null && !reload)
            {
                if (_toolsPickedAutomatically && encoder is { } needed && !Tools.HasEncoder(needed)
                    && FfmpegLocator.PickAuto(Candidates, needed) is { } better && better.Tools.HasEncoder(needed))
                {
                    WriteLog($"ffmpeg {Tools.Version} kennt {FfmpegTools.EncoderName(needed)} nicht, verwende {better.Tools.Version} ({better.Tools.Folder})");
                    Tools = better.Tools;
                    Ffmpeg.Refresh();
                }
                return Tools;
            }
            var bundled = await EnsureBundledAsync();
            Tools = null;
            switch (Settings.FfmpegMode)
            {
                case FfmpegMode.Custom:
                    Tools = await FfmpegTools.TryLoadAsync(Settings.FfmpegDir);
                    break;
                case FfmpegMode.Bundled when bundled is not null:
                    Tools = await FfmpegTools.TryLoadAsync(bundled);
                    break;
            }
            Candidates = await FfmpegLocator.FindAllAsync(bundled);
            _toolsPickedAutomatically = Tools is null;
            Tools ??= FfmpegLocator.PickAuto(Candidates, encoder ?? Settings.FindProfile(Settings.SelectedProfile)?.Encoder)?.Tools;
            Ffmpeg.Refresh();
            return Tools;
        }
        finally
        {
            _toolsLock.Release();
        }
    }

    public bool ToolsAreBundled => Tools is not null && BundledFolder is not null
                                   && string.Equals(Path.GetFullPath(Tools.Folder), Path.GetFullPath(BundledFolder), StringComparison.OrdinalIgnoreCase);

    public System.Diagnostics.ProcessPriorityClass FfmpegPriority => Settings.Priority switch
    {
        WorkPriority.Idle => System.Diagnostics.ProcessPriorityClass.Idle,
        WorkPriority.BelowNormal => System.Diagnostics.ProcessPriorityClass.BelowNormal,
        _ => System.Diagnostics.ProcessPriorityClass.Normal,
    };

    public Core.Util.TimeWindow? Window =>
        Settings.ScheduleEnabled ? Core.Util.TimeWindow.Parse(Settings.ScheduleFrom, Settings.ScheduleTo) : null;

    public void WriteLog(string line)
    {
        try
        {
            Directory.CreateDirectory(Store.LogFolder);
            File.AppendAllText(Path.Combine(Store.LogFolder, $"{DateTime.Now:yyyy-MM-dd}.log"),
                $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        }
        catch
        {
        }
    }
}
