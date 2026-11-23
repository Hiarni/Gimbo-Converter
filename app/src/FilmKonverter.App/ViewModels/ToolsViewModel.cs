using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Jobs;
using FilmKonverter.Core.Settings;
using FilmKonverter.Core.Util;
using Microsoft.Win32;

namespace FilmKonverter.App.ViewModels;

public sealed record TestRowItem(string Clip, string Size, string Ratio, string Full, bool IsReference)
{
    public string Ssim { get; init; } = "";
    public string File { get; init; } = "";
    public bool CanCompare => !IsReference && File.Length > 0;

    public static string SsimText(double? v) => v switch
    {
        null => "",
        >= 0.985 => v.Value.ToString("0.000", Fmt.De) + L.T(" · kaum zu unterscheiden", " · hardly distinguishable"),
        >= 0.97 => v.Value.ToString("0.000", Fmt.De) + L.T(" · sehr gut", " · very good"),
        >= 0.95 => v.Value.ToString("0.000", Fmt.De) + L.T(" · gut", " · good"),
        _ => v.Value.ToString("0.000", Fmt.De) + L.T(" · sichtbare Verluste", " · visible loss"),
    };
}

public sealed partial class ToolsViewModel : ObservableObject
{
    private readonly AppState app;

    public ToolsViewModel(AppState app)
    {
        this.app = app;
        app.Run.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(RunViewModel.IsBusy) || app.Run.IsBusy || _busyNote.Length == 0) return;
            if (Message == _busyNote) Message = "";
            _busyNote = "";
        };
    }

    public ObservableCollection<TestRowItem> TestRows { get; } = [];

    [ObservableProperty] private int _verifyDays;
    [ObservableProperty] private bool _verifyDelete;

    [ObservableProperty] private string _testFile = "";

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TestedText))] private string _testedFile = "";

    public string TestedText => TestedFile.Length == 0 ? "" : L.T($"Ergebnis für: {TestedFile}", $"Result for: {TestedFile}");

    [ObservableProperty, NotifyCanExecuteChangedFor(nameof(OpenTestFolderCommand))] private string _testFolder = "";
    [ObservableProperty] private string _message = "";

    public string ProfileName => app.Settings.SelectedProfile;

    private Profile? SelectedProfile => app.Settings.FindProfile(app.Settings.SelectedProfile) ?? app.Settings.Profiles.FirstOrDefault();

    public event Action? NavigateToDashboard;

    private string _busyNote = "";

    private bool RefuseWhileBusy()
    {
        if (!app.Run.IsBusy)
        {
            Message = "";
            return false;
        }
        _busyNote = L.T("Es läuft bereits etwas (siehe Startseite). Bitte starten, sobald es fertig ist.", "Something is already running (see the start page). Please start once it has finished.");
        Message = _busyNote;
        return true;
    }

    [RelayCommand]
    private async Task StartVerifyAsync()
    {
        if (RefuseWhileBusy()) return;
        NavigateToDashboard?.Invoke();
        await app.Run.StartVerifyAsync(new VerifyOptions { Days = VerifyDays, DeleteBroken = VerifyDelete });
    }

    [ObservableProperty] private bool _cleanupDryRun = true;

    public string CleanupFolder
    {
        get => app.Settings.CleanupFolder;
        set
        {
            var folder = (value ?? "").Trim();
            if (app.Settings.CleanupFolder == folder) return;
            app.Settings.CleanupFolder = folder;
            if (app.Settings.CleanupApplyRules && !TouchUpInfo.IsProfileTarget(folder, SelectedProfile))
            {
                app.Settings.CleanupApplyRules = false;
                OnPropertyChanged(nameof(CleanupApplyRules));
                Message = L.T("Dieser Ordner gehört nicht zum Zielordner des Profils. Die Ton- und Untertitel-Regeln des Profils sind deshalb erst einmal ausgeschaltet: es wird nur die Abspielbarkeit korrigiert, keine Spur entfällt. Bei Bedarf wieder einschalten.",
                              "This folder is not part of the profile's target folder. The profile's audio and subtitle rules are therefore switched off for now: only playability is corrected, no track is dropped. Switch them on again if needed.");
            }
            app.Save();
            OnPropertyChanged();
        }
    }

    public bool CleanupApplyRules
    {
        get => app.Settings.CleanupApplyRules;
        set { if (app.Settings.CleanupApplyRules == value) return; app.Settings.CleanupApplyRules = value; app.Save(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private void PickCleanupFolder()
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner zum Nachbessern wählen (z.B. die Bibliothek des Medienservers)", "Choose the folder to touch up (e.g. the media server library)") };
        if (Io.DirExistsQuick(CleanupFolder, 800)) dlg.InitialDirectory = CleanupFolder;
        if (dlg.ShowDialog() == true) CleanupFolder = dlg.FolderName;
    }

    [RelayCommand]
    private async Task StartCleanupAsync()
    {
        if (RefuseWhileBusy()) return;
        var profile = SelectedProfile;
        if (profile is null) { Message = L.T("Kein Profil vorhanden.", "No profile available."); return; }
        var folder = CleanupFolder;
        if (folder.Length > 0 && !await Io.DirExistsAsync(folder, 8000)) { Message = L.T($"Ordner nicht erreichbar: {folder}", $"Folder not reachable: {folder}"); return; }
        var rules = CleanupApplyRules;
        if (!CleanupDryRun)
        {
            var ok = System.Windows.MessageBox.Show(TouchUpInfo.ConfirmText(profile, folder, rules),
                App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
            if (ok != System.Windows.MessageBoxResult.Yes) return;
            if (RefuseWhileBusy()) return;
        }
        NavigateToDashboard?.Invoke();
        await app.Run.StartCleanupAsync(new CleanupOptions { DryRun = CleanupDryRun, Folder = folder.Length > 0 ? folder : null, ApplyProfileRules = rules });
    }

    [RelayCommand]
    private void PickTestFile()
    {
        var p = SelectedProfile;
        var types = (p?.Extensions is { Count: > 0 } own ? own : [.. Profile.DefaultExtensions]).Select(e => "*" + (e.StartsWith('.') ? e : "." + e));
        var dlg = new OpenFileDialog
        {
            Title = L.T("Datei für den Testmodus wählen", "Choose a file for the test mode"),
            Filter = "Videos|" + string.Join(";", types) + L.T("|Alle Dateien|*.*", "|All files|*.*"),
        };
        if (p is not null && Io.DirExistsQuick(p.InputDir)) dlg.InitialDirectory = p.InputDir;
        if (dlg.ShowDialog() == true) TestFile = dlg.FileName;
    }

    private static TestRowItem ToItem(TestRow r) => new(
        r.IsReference ? L.T("Quelle", "Source") : r.Clip,
        Fmt.Size(r.SizeBytes),
        r.RatioToSource is double q ? (q * 100).ToString("N0", Fmt.De) + " %" : "?",
        r.ProjectedFullBytes is long f ? Fmt.Size(f) : "?",
        r.IsReference) { Ssim = r.IsReference ? L.T("Original", "original") : TestRowItem.SsimText(r.Ssim), File = r.File };

    [RelayCommand]
    private async Task StartTestAsync()
    {
        if (RefuseWhileBusy()) return;
        var file = TestFile.Trim().Trim('"');
        if (file.Length == 0 || !await Io.RunAsync(() => File.Exists(file), false, 4000)) { Message = L.T("Bitte zuerst eine Datei wählen.", "Please choose a file first."); return; }
        TestRows.Clear();
        TestedFile = file;
        TestFolder = "";
        Message = L.T("Testmodus läuft, Fortschritt auf der Startseite.", "Test mode is running, progress on the start page.");
        var rows = await app.Run.StartTestAsync(file);
        foreach (var r in rows) TestRows.Add(ToItem(r));
        TestFolder = rows.Count > 0 ? app.Run.LastTestFolder : "";
        Message = rows.Count == 0 ? L.T("Kein Ergebnis - Meldung auf der Startseite prüfen.", "No result - check the message on the start page.") : L.T("Bildqualität und Größe abwägen, mit dem Vergleichs-Knopf Einzelbilder ansehen (oder die Ausschnitte am Fernseher), dann den Wert im Profil eintragen.", "Weigh picture quality against size, look at single frames with the compare button (or the clips on the TV), then enter the value in the profile.");
    }

    [RelayCommand]
    private async Task CompareAsync(TestRowItem? row)
    {
        var source = TestedFile;
        if (row is null || !row.CanCompare || !await Io.ExistsAsync(row.File) || !await Io.ExistsAsync(source)) { Message = L.T("Ausschnitt oder Quelle nicht gefunden.", "Clip or source not found."); return; }
        var tools = await app.EnsureToolsAsync();
        if (tools is null) return;
        var (start, len) = app.Run.LastTestClip;
        new CompareWindow(tools, source, row.File, start, len) { Owner = System.Windows.Application.Current.MainWindow }.Show();
    }

    private bool CanOpenTestFolder() => TestFolder.Length > 0;

    [RelayCommand(CanExecute = nameof(CanOpenTestFolder))]
    private void OpenTestFolder()
    {
        if (Io.DirExistsQuick(TestFolder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{TestFolder}\"") { UseShellExecute = true });
        else
            Message = L.T($"Ordner nicht erreichbar: {TestFolder}", $"Folder not reachable: {TestFolder}");
    }

    public void FillDemo()
    {
        const long mb = 1024 * 1024;
        const double gb = 1024d * mb;
        TestFile = DemoText.T(@"\\NAS\Serien\Serie C\Staffel 7\Serie.C.S07E01.German.DL.1080p.BluRay.x264.mkv");
        TestedFile = TestFile;
        TestFolder = DemoText.T(@"\\NAS\Komprimiert\_Test\Serie.C.S07E01.German.DL.1080p.BluRay.x264");
        TestRows.Clear();
        TestRows.Add(ToItem(new TestRow("00_Quelle", 98 * mb, 1, (long)(3.41 * gb), true)));
        TestRows.Add(ToItem(new TestRow("CQ25", 79 * mb, 0.81, (long)(2.75 * gb), false) { File = "x", Ssim = 0.9871 }));
        TestRows.Add(ToItem(new TestRow("CQ27", 66 * mb, 0.67, (long)(2.30 * gb), false) { File = "x", Ssim = 0.9812 }));
        TestRows.Add(ToItem(new TestRow("CQ29", 55 * mb, 0.56, (long)(1.92 * gb), false) { File = "x", Ssim = 0.9693 }));
    }
}
