using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using FilmKonverter.Core.Library;
using FilmKonverter.Core.Settings;
using Microsoft.Win32;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.ViewModels;

public sealed record EncoderOption(EncoderKind Kind, string Text);
public sealed record BasisOption(RateBasis Basis, string Text);
public sealed record OrderOption(FileOrder Order, string Text);
public sealed record ForcedOption(ForcedSubtitleMode Mode, string Text);
public sealed record ResolutionOption(int Width, int Height, string Text);
public sealed record IntOption(int Value, string Text);
public sealed record DeinterlaceOption(DeinterlaceMode Mode, string Text);
public sealed record AudioTargetOption(AudioTarget Target, string Text);

public sealed partial class ProfilesViewModel : ObservableObject
{
    private readonly AppState _app;

    public ProfilesViewModel(AppState app)
    {
        _app = app;
        foreach (var p in app.Settings.Profiles) Items.Add(p);
        _selected = app.Settings.FindProfile(app.Settings.SelectedProfile) ?? Items.FirstOrDefault();
        _ = CheckAsync();
    }

    public ObservableCollection<Profile> Items { get; } = [];

    public IReadOnlyList<EncoderOption> Encoders { get; } =
    [
        new(EncoderKind.Nvenc, "NVIDIA (hevc_nvenc)"),
        new(EncoderKind.Amf, "AMD (hevc_amf)"),
        new(EncoderKind.Qsv, "Intel (hevc_qsv)"),
        new(EncoderKind.Cpu, L.T("Prozessor (libx265, langsam)", "Processor (libx265, slow)")),
    ];

    public IReadOnlyList<BasisOption> Bases { get; } =
    [
        new(RateBasis.Video, L.T("Video-Datenrate", "Video bitrate")),
        new(RateBasis.Total, L.T("Gesamtdatenrate", "Total bitrate")),
    ];

    public IReadOnlyList<OrderOption> Orders { get; } =
    [
        new(FileOrder.Name, L.T("Alphabetisch (auf allen PCs gleich)", "Alphabetical (same on all PCs)")),
        new(FileOrder.HighestRateFirst, L.T("Größtes Sparpotenzial zuerst (höchste Datenrate)", "Largest saving first (highest bitrate)")),
        new(FileOrder.LargestFirst, L.T("Größte zuerst (schnell viel Platz)", "Largest first (much space quickly)")),
        new(FileOrder.NewestFirst, L.T("Neueste zuerst", "Newest first")),
        new(FileOrder.OldestFirst, L.T("Älteste zuerst", "Oldest first")),
    ];

    public IReadOnlyList<ResolutionOption> Resolutions { get; } =
    [
        new(0, 0, L.T("Original (nie verkleinern)", "Original (never downscale)")),
        new(3840, 2160, "4K · 3840 x 2160"),
        new(2560, 1440, "1440p · 2560 x 1440"),
        new(1920, 1080, "1080p · 1920 x 1080 (Full HD)"),
        new(1280, 720, "720p · 1280 x 720"),
        new(1024, 576, "576p · 1024 x 576 (PAL)"),
        new(854, 480, "480p · 854 x 480"),
        new(-1, -1, L.T("Eigene …", "Custom …")),
    ];

    public IReadOnlyList<IntOption> FpsOptions { get; } =
    [
        new(0, "Original"),
        new(60, "max. 60 fps"),
        new(50, "max. 50 fps"),
        new(30, "max. 30 fps"),
        new(25, "max. 25 fps"),
        new(24, "max. 24 fps"),
    ];

    public IReadOnlyList<DeinterlaceOption> DeinterlaceOptions { get; } =
    [
        new(DeinterlaceMode.Auto, L.T("Automatisch (nur interlaced Quellen)", "Automatic (interlaced sources only)")),
        new(DeinterlaceMode.Off, L.T("Aus", "Off")),
        new(DeinterlaceMode.Always, L.T("Immer", "Always")),
    ];

    private bool _customResolution;

    public ResolutionOption? Resolution
    {
        get
        {
            if (Selected is null) return null;
            var match = Resolutions.FirstOrDefault(r => r.Width == Selected.MaxWidth && r.Height == Selected.MaxHeight);
            return _customResolution || match is null ? Resolutions[^1] : match;
        }
        set
        {
            if (Selected is null || value is null) return;
            _customResolution = value.Width < 0;
            if (!_customResolution)
            {
                Selected.MaxWidth = value.Width;
                Selected.MaxHeight = value.Height;
            }
            else if (Selected.MaxWidth <= 0)
            {
                Selected.MaxWidth = 1920;
                Selected.MaxHeight = 1080;
            }
            NotifyVideo();
        }
    }

    public bool IsCustomResolution => Resolution?.Width < 0;

    public int CustomWidth
    {
        get => Selected?.MaxWidth ?? 0;
        set { if (Selected is not null) { Selected.MaxWidth = Math.Max(0, value); NotifyVideo(); } }
    }

    public int CustomHeight
    {
        get => Selected?.MaxHeight ?? 0;
        set { if (Selected is not null) { Selected.MaxHeight = Math.Max(0, value); NotifyVideo(); } }
    }

    public int MaxFps
    {
        get => Selected?.MaxFps ?? 0;
        set { if (Selected is not null) { Selected.MaxFps = value; NotifyVideo(); } }
    }

    public DeinterlaceMode DeinterlaceValue
    {
        get => Selected?.Deinterlace ?? DeinterlaceMode.Auto;
        set { if (Selected is not null) { Selected.Deinterlace = value; NotifyVideo(); } }
    }

    public bool TenBit
    {
        get => Selected?.TenBit ?? false;
        set { if (Selected is not null) { Selected.TenBit = value; NotifyVideo(); } }
    }

    public bool HardwareDecode
    {
        get => Selected?.HardwareDecode ?? false;
        set { if (Selected is not null) { Selected.HardwareDecode = value; NotifyVideo(); } }
    }

    public string VideoSummary
    {
        get
        {
            if (Selected is null) return "";
            var parts = new List<string> { Selected.LimitsResolution ? $"max. {Selected.MaxWidth}x{Selected.MaxHeight}" : L.T("Originalauflösung", "original resolution") };
            if (Selected.MaxFps > 0) parts.Add($"max. {Selected.MaxFps} fps");
            if (Selected.Deinterlace == DeinterlaceMode.Off) parts.Add(L.T("kein Deinterlacing", "no deinterlacing"));
            if (Selected.TenBit && !IsAmf) parts.Add("10 Bit");
            if (Selected.HardwareDecode) parts.Add(L.T("GPU-Dekodierung", "GPU decoding"));
            return string.Join(" · ", parts);
        }
    }

    private void NotifyVideo()
    {
        foreach (var n in new[] { nameof(Resolution), nameof(IsCustomResolution), nameof(CustomWidth), nameof(CustomHeight), nameof(MaxFps),
                     nameof(DeinterlaceValue), nameof(TenBit), nameof(HardwareDecode), nameof(VideoSummary), nameof(SelectedSummary) })
            OnPropertyChanged(n);
    }

    public IReadOnlyList<ForcedOption> ForcedModes { get; } =
    [
        new(ForcedSubtitleMode.Auto, L.T("Automatisch erkennen (empfohlen)", "Detect automatically (recommended)")),
        new(ForcedSubtitleMode.Keep, L.T("Unverändert lassen", "Leave unchanged")),
        new(ForcedSubtitleMode.First, L.T("Erste Spur immer erzwingen", "Always force the first track")),
    ];

    public IReadOnlyList<AudioTargetOption> AudioTargets { get; } =
    [
        new(AudioTarget.Eac3, "E-AC3 (Dolby Digital Plus)"),
        new(AudioTarget.Ac3, "AC3 (Dolby Digital, max. 640 kbit/s)"),
    ];

    public IReadOnlyList<string> NvencPresets { get; } = ["p1", "p2", "p3", "p4", "p5", "p6", "p7"];
    public IReadOnlyList<string> CpuPresets { get; } = ["ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];
    public IReadOnlyList<string> QsvPresets { get; } = ["veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow"];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasSelection), nameof(Encoder), nameof(IsAmf), nameof(IsNotAmf), nameof(IsNvenc),
        nameof(IsCpu), nameof(IsQsv), nameof(QualityLabel), nameof(QualityHint), nameof(Basis), nameof(Order), nameof(ExtensionsText),
        nameof(Quality), nameof(QpI), nameof(QpP), nameof(SelectedSummary), nameof(EarlyAbortText), nameof(HdrHint),
        nameof(ForcedMode), nameof(Target), nameof(ForcedHint), nameof(Name), nameof(Resolution), nameof(IsCustomResolution), nameof(CustomWidth),
        nameof(CustomHeight), nameof(MaxFps), nameof(DeinterlaceValue), nameof(TenBit), nameof(HardwareDecode), nameof(VideoSummary), nameof(SsimMinText))]
    private Profile? _selected;

    [ObservableProperty] private string _savedText = "";
    [ObservableProperty] private string _warnings = "";

    public bool HasSelection => Selected is not null;

    partial void OnSelectedChanging(Profile? value)
    {
        if (Selected is not null) Persist();
    }

    partial void OnSelectedChanged(Profile? value)
    {
        _customResolution = false;
        if (value is null) return;
        _app.Settings.SelectedProfile = value.Name;
        _app.Save();
        _ = CheckAsync();
        _app.Run.RefreshTargetViews();
    }

    public string SelectedSummary => Selected is null ? "" :
        $"{Selected.QualityText} · {(Selected.LimitsResolution ? $"max. {Selected.MaxWidth}x{Selected.MaxHeight}" : L.T("Originalgröße", "original size"))}";

    // ---------------- Proxys mit Aenderungsmeldung ----------------

    public EncoderKind Encoder
    {
        get => Selected?.Encoder ?? EncoderKind.Nvenc;
        set
        {
            if (Selected is null || Selected.Encoder == value) return;
            Selected.Encoder = value;
            foreach (var n in new[] { nameof(Encoder), nameof(IsAmf), nameof(IsNotAmf), nameof(IsNvenc), nameof(IsCpu), nameof(IsQsv),
                         nameof(QualityLabel), nameof(QualityHint), nameof(SelectedSummary), nameof(HdrHint), nameof(VideoSummary) })
                OnPropertyChanged(n);
        }
    }

    public int Quality
    {
        get => Selected?.Quality ?? 27;
        set { if (Selected is null || Selected.Quality == value) return; Selected.Quality = value; Changed(nameof(Quality)); }
    }

    public int QpI
    {
        get => Selected?.QpI ?? 23;
        set { if (Selected is null || Selected.QpI == value) return; Selected.QpI = value; Changed(nameof(QpI)); }
    }

    public int QpP
    {
        get => Selected?.QpP ?? 25;
        set { if (Selected is null || Selected.QpP == value) return; Selected.QpP = value; Changed(nameof(QpP)); }
    }

    private void Changed(string name)
    {
        OnPropertyChanged(name);
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(QualityHint));
    }

    public RateBasis Basis
    {
        get => Selected?.RateBasis ?? RateBasis.Video;
        set { if (Selected is not null) { Selected.RateBasis = value; OnPropertyChanged(); } }
    }

    public string Name
    {
        get => Selected?.Name ?? "";
        set
        {
            if (Selected is null) return;
            var n = string.IsNullOrWhiteSpace(value) ? L.T("Profil", "Profile") : value.Trim();
            if (n == Selected.Name) return;
            var wasSelected = _app.Settings.SelectedProfile.Equals(Selected.Name, StringComparison.OrdinalIgnoreCase);
            Selected.Name = UniqueName(n, Selected);
            if (wasSelected) _app.Settings.SelectedProfile = Selected.Name;
            Persist();
            RefreshList();
            OnPropertyChanged();
        }
    }

    public ForcedSubtitleMode ForcedMode
    {
        get => Selected?.ForcedSubtitles ?? ForcedSubtitleMode.Auto;
        set { if (Selected is not null) { Selected.ForcedSubtitles = value; OnPropertyChanged(); OnPropertyChanged(nameof(ForcedHint)); } }
    }

    public string ForcedHint => ForcedMode switch
    {
        ForcedSubtitleMode.Auto => L.T("Erzwungen ist eine Spur, die schon so gekennzeichnet ist oder „forced“/„erzwungen“ im Titel trägt. Gibt es keine, bleibt alles unverändert.", "A track counts as forced if it is already flagged so or has forced/erzwungen in its title. If there is none, everything stays unchanged."),
        ForcedSubtitleMode.First => L.T("Markiert die erste Text-Untertitelspur als erzwungen und löscht alle anderen Kennzeichen. Ist sie ein vollständiger Untertitel, wird er immer angezeigt. Bild-Untertitel werden nie von selbst gewählt, weil Fernseher dafür das ganze Video umrechnen lassen.", "Flags the first text subtitle track as forced and clears all other flags. If it is a full subtitle, it is always shown. Image subtitles are never chosen automatically, because TVs then need the whole video transcoded."),
        _ => L.T("Die Kennzeichen der Quelle werden übernommen.", "The flags of the source are kept."),
    };

    public AudioTarget Target
    {
        get => Selected?.AudioTarget ?? AudioTarget.Eac3;
        set { if (Selected is not null) { Selected.AudioTarget = value; OnPropertyChanged(); } }
    }

    public FileOrder Order
    {
        get => Selected?.Order ?? FileOrder.Name;
        set { if (Selected is not null) { Selected.Order = value; OnPropertyChanged(); } }
    }

    public string ExtensionsText
    {
        get => Selected is null ? "" : string.Join(" ", Selected.Extensions);
        set
        {
            if (Selected is null) return;
            Selected.Extensions = value.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(e => e.StartsWith('.') ? e : "." + e).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            OnPropertyChanged();
        }
    }

    public bool IsAmf => Encoder == EncoderKind.Amf;
    public bool IsNotAmf => !IsAmf;
    public bool IsNvenc => Encoder == EncoderKind.Nvenc;
    public bool IsCpu => Encoder == EncoderKind.Cpu;
    public bool IsQsv => Encoder == EncoderKind.Qsv;

    public string QualityLabel => Encoder switch
    {
        EncoderKind.Nvenc => "CQ",
        EncoderKind.Qsv => L.T("Qualität (global_quality)", "Quality (global_quality)"),
        EncoderKind.Cpu => "CRF",
        _ => "",
    };

    public string QualityHint => Encoder switch
    {
        EncoderKind.Amf => L.T($"Kleiner = bessere Qualität, größere Datei. P sollte 1–2 über I liegen. Bewährt: 23/25 für Serien, 22/24 für Filme. Jeder Schritt ≈ 10–12 % Größe.", "Lower = better quality, larger file. P should be 1–2 above I. Proven: 23/25 for series, 22/24 for movies. Each step ≈ 10–12 % size."),
        EncoderKind.Nvenc => L.T($"Kleiner = bessere Qualität, größere Datei. Bewährt: 26–28 für Serien, 24–26 für Filme. Jeder Schritt ≈ 10–12 % Größe.", "Lower = better quality, larger file. Proven: 26–28 for series, 24–26 for movies. Each step ≈ 10–12 % size."),
        EncoderKind.Cpu => L.T("Kleiner = bessere Qualität, größere Datei. Bewährt: 22–26. x265 ist bei gleichem Wert sparsamer als die Grafikkarte, aber viel langsamer.", "Lower = better quality, larger file. Proven: 22–26. At the same value x265 is more efficient than the graphics card, but much slower."),
        _ => L.T("Kleiner = bessere Qualität, größere Datei. Bewährt: 23–27.", "Lower = better quality, larger file. Proven: 23–27."),
    };

    public string EarlyAbortText => Selected is null || Selected.EarlyAbortPercent <= 0
        ? L.T("Frühabbruch aus: es wird immer bis zum Ende kodiert.", "Early abort off: always encodes to the end. Finished files are still checked against the limit.")
        : L.T($"Bricht ab, sobald absehbar ist, dass die neue Datei über {Selected.EarlyAbortPercent} % der Quelle liegt.", $"Aborts as soon as it is foreseeable that the new file will exceed {Selected.EarlyAbortPercent} % of the source, and discards finished files above the limit.");

    public string SsimMinText
    {
        get => Selected?.SsimMin.ToString("0.00#", Fmt.De) ?? "";
        set
        {
            if (Selected is null) return;
            var t = (value ?? "").Trim().Replace(',', '.');
            if (double.TryParse(t, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v))
            {
                if (v > 1 && v <= 100) v /= 100;
                Selected.SsimMin = Math.Clamp(v, 0.5, 0.999);
            }
            OnPropertyChanged();
        }
    }

    public string HdrHint => IsAmf ? L.T("AMD (AMF) kodiert nur 8 Bit: HDR-Quellen werden immer übersprungen.", "AMD (AMF) encodes 8 bit only: HDR sources are always skipped.") : "";

    // ---------------- Befehle ----------------

    private void Persist()
    {
        _app.Settings.Profiles = Items.ToList();
        _app.Save();
    }

    [RelayCommand]
    private void Save()
    {
        if (Selected is not null)
        {
            Selected.Name = string.IsNullOrWhiteSpace(Selected.Name) ? L.T("Profil", "Profile") : Selected.Name.Trim();
            if (Items.Any(p => p != Selected && p.Name.Equals(Selected.Name, StringComparison.OrdinalIgnoreCase)))
                Selected.Name = UniqueName(Selected.Name, Selected);
            _app.Settings.SelectedProfile = Selected.Name;
        }
        Persist();
        RefreshList();
        SavedText = L.T($"Gespeichert um {DateTime.Now:HH:mm:ss}", $"Saved at {DateTime.Now:HH:mm:ss}");
        _ = CheckAsync();
    }

    [RelayCommand]
    private void AddPreset(string which)
    {
        var p = which switch
        {
            "nvidia" => Profile.Nvidia(),
            "amd" => Profile.Amd(),
            "intel" => Profile.Intel(),
            "cpu" => Profile.Cpu(),
            _ => new Profile { Name = L.T("Neues Profil", "New profile") },
        };
        p.Name = UniqueName(p.Name);
        Items.Add(p);
        Selected = p;
        Save();
    }

    [RelayCommand]
    private void Duplicate()
    {
        if (Selected is null) return;
        var p = Selected.Clone();
        p.Name = UniqueName(p.Name + L.T(" Kopie", " copy"));
        Items.Add(p);
        Selected = p;
        Save();
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected is null || Items.Count <= 1) return;
        var answer = System.Windows.MessageBox.Show(L.T($"Profil \"{Selected.Name}\" löschen?", $"Delete profile \"{Selected.Name}\"?"), App.Title,
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var i = Items.IndexOf(Selected);
        Items.Remove(Selected);
        _selected = null;
        Selected = Items[Math.Clamp(i, 0, Items.Count - 1)];
        Save();
    }

    [RelayCommand]
    private void Export()
    {
        var dlg = new SaveFileDialog
        {
            Title = L.T("Profile exportieren", "Export profiles"),
            FileName = "Gimbo-Konverter-Profile.json",
            Filter = L.T("Profile (*.json)|*.json", "Profiles (*.json)|*.json"),
        };
        if (dlg.ShowDialog() != true) return;
        Persist();
        SettingsStore.ExportProfiles(dlg.FileName, Items);
        SavedText = L.T($"{Items.Count} Profile exportiert.", $"{Items.Count} profiles exported.");
    }

    [RelayCommand]
    private void Import()
    {
        var dlg = new OpenFileDialog { Title = L.T("Profile importieren", "Import profiles"), Filter = L.T("Profile (*.json)|*.json|Alle Dateien|*.*", "Profiles (*.json)|*.json|All files|*.*") };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var list = SettingsStore.ImportProfiles(dlg.FileName);
            Profile? last = null;
            foreach (var p in list)
            {
                p.Name = UniqueName(p.Name);
                Items.Add(p);
                last = p;
            }
            if (last is not null) Selected = last;
            Save();
            SavedText = L.T($"{list.Count} Profile importiert.", $"{list.Count} profiles imported.");
        }
        catch (Exception ex)
        {
            SavedText = L.T("Import fehlgeschlagen: ", "Import failed: ") + ex.Message;
        }
    }

    [RelayCommand]
    private void BrowseInput()
    {
        if (Selected is null) return;
        var path = PickFolder(Selected.InputDir);
        if (path is null) return;
        Selected.InputDir = path;
        Reselect();
    }

    [RelayCommand]
    private void BrowseOutput()
    {
        if (Selected is null) return;
        var path = PickFolder(Selected.OutputDir);
        if (path is null) return;
        Selected.OutputDir = path;
        Reselect();
        _app.Run.RefreshTargetViews();
    }

    [RelayCommand]
    private void OpenFolder(string which)
    {
        var dir = which == "out" ? Selected?.OutputDir : Selected?.InputDir;
        if (Io.DirExistsQuick(dir))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    [RelayCommand]
    private void ResetToPreset()
    {
        if (Selected is null) return;
        var answer = System.Windows.MessageBox.Show(L.T("Alle Einstellungen dieses Profils auf die Vorlage für den gewählten Encoder zurücksetzen?\nName und Ordner bleiben erhalten.", "Reset all settings of this profile to the template for the selected encoder?\nName and folders are kept."),
            App.Title, System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question);
        if (answer != System.Windows.MessageBoxResult.Yes) return;
        var preset = Selected.Encoder switch
        {
            EncoderKind.Amf => Profile.Amd(),
            EncoderKind.Qsv => Profile.Intel(),
            EncoderKind.Cpu => Profile.Cpu(),
            _ => Profile.Nvidia(),
        };
        preset.Name = Selected.Name;
        preset.InputDir = Selected.InputDir;
        preset.OutputDir = Selected.OutputDir;
        var i = Items.IndexOf(Selected);
        Items[i] = preset;
        Selected = preset;
        Save();
    }

    private static string? PickFolder(string start)
    {
        var dlg = new OpenFolderDialog { Title = L.T("Ordner wählen", "Choose folder") };
        if (Io.DirExistsQuick(start)) dlg.InitialDirectory = start;
        return dlg.ShowDialog() == true ? dlg.FolderName : null;
    }

    private void Reselect()
    {
        var p = Selected;
        _selected = null;
        OnPropertyChanged(nameof(Selected));
        _selected = p;
        OnPropertyChanged(nameof(Selected));
        foreach (var n in new[] { nameof(SelectedSummary), nameof(ExtensionsText), nameof(Quality), nameof(QpI), nameof(QpP), nameof(Order), nameof(Basis), nameof(Encoder), nameof(ForcedMode), nameof(Target), nameof(Resolution), nameof(IsCustomResolution),
                     nameof(MaxFps), nameof(DeinterlaceValue), nameof(TenBit), nameof(HardwareDecode), nameof(VideoSummary) })
            OnPropertyChanged(n);
        Persist();
        _ = CheckAsync();
    }

    private void RefreshList()
    {
        var p = Selected;
        var copy = Items.ToList();
        _selected = null;
        Items.Clear();
        foreach (var x in copy) Items.Add(x);
        _selected = p;
        OnPropertyChanged(nameof(Selected));
        OnPropertyChanged(nameof(SelectedSummary));
        OnPropertyChanged(nameof(EarlyAbortText));
    }

    private string UniqueName(string name, Profile? except = null)
    {
        var n = name;
        var i = 2;
        while (Items.Any(p => p != except && p.Name.Equals(n, StringComparison.OrdinalIgnoreCase))) n = $"{name} ({i++})";
        return n;
    }

    private async Task CheckAsync()
    {
        var p = Selected;
        if (p is null || App.ScreenshotFolder is not null) { Warnings = ""; return; }
        var (input, output) = (p.InputDir, p.OutputDir);
        var list = await Task.Run(() =>
        {
            var w = new List<string>();
            if (string.IsNullOrWhiteSpace(input)) w.Add(L.T("Kein Quellordner eingestellt.", "No source folder set."));
            else if (!Directory.Exists(input)) w.Add(L.T($"Quellordner nicht erreichbar: {input}", $"Source folder not reachable: {input}"));
            if (string.IsNullOrWhiteSpace(output)) w.Add(L.T("Kein Zielordner eingestellt.", "No target folder set."));
            if (!string.IsNullOrWhiteSpace(input) && !string.IsNullOrWhiteSpace(output))
            {
                if (Scanner.IsInside(input, output)) w.Add(L.T("Der Quellordner liegt im Zielordner (oder ist derselbe). Das geht nicht.", "The source folder is inside the target folder (or the same). That does not work."));
                else if (Scanner.IsInside(output, input)) w.Add(L.T("Hinweis: Der Zielordner liegt in der Quelle. Er wird beim Durchsuchen ausgelassen.", "Note: the target folder is inside the source. It is skipped when scanning."));
            }
            return w;
        });
        if (Selected == p) Warnings = string.Join("\n", list);
    }
}
