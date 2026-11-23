using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
using FilmKonverter.App.Pages;
using FilmKonverter.App.ViewModels;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App;

public partial class MainWindow
{
    public bool StartHidden { get; set; }

    public bool ClosedForReal { get; private set; }

    private static App? CurrentApp => Application.Current as App;

    public MainWindow()
    {
        InitializeComponent();
        if (App.ScreenshotFolder is null)
        {
            var wa = SystemParameters.WorkArea;
            MinWidth = Math.Min(MinWidth, wa.Width);
            MinHeight = Math.Min(MinHeight, wa.Height);
            Width = Math.Min(Width, wa.Width);
            Height = Math.Min(Height, wa.Height);
        }
        TaskbarItemInfo = new TaskbarItemInfo();
        Loaded += OnLoaded;
        Closing += OnClosing;
        Closed += (_, _) => ClosedForReal = true;
        StateChanged += OnStateChanged;
        AppState.Instance.ToolsPage.NavigateToDashboard += () => RootNavigation.Navigate(typeof(DashboardPage));
        AppState.Instance.Library.NavigateToDashboard += () => RootNavigation.Navigate(typeof(DashboardPage));
        AppState.Instance.Run.PropertyChanged += OnRunChanged;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        ThemeHelper.Apply(AppState.Instance.Settings.Theme);
        RootNavigation.IsPaneOpen = false;
        if (App.ScreenshotFolder is not null)
        {
            await TakeScreenshotsAsync(App.ScreenshotFolder);
            Application.Current.Shutdown(0);
            return;
        }
        RootNavigation.IsPaneOpen = AppState.Instance.Settings.SidebarExpanded;
        RootNavigation.PaneOpened += (_, _) => RememberSidebar(true);
        RootNavigation.PaneClosed += (_, _) => RememberSidebar(false);
        RootNavigation.Navigate(typeof(DashboardPage));
        HideContentFocusFrame();
        RootNavigation.Navigated += (_, _) => HideContentFocusFrame();
        if (StartHidden) Hide();

        AppState.Instance.SaveIfChanged();
        var autosave = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        autosave.Tick += (_, _) => { try { AppState.Instance.SaveIfChanged(); } catch { } };
        autosave.Start();

        if (AppState.Instance.Store.ReadOnlyFallback)
            AppState.Instance.Run.SettingsWarning = L.T("Die Einstellungen konnten nicht gelesen werden (Datei gesperrt, z.B. durch einen Virenscanner). Damit nichts überschrieben wird, speichert das Programm in dieser Sitzung keine Änderungen. Bitte neu starten.",
                "The settings could not be read (file locked, e.g. by a virus scanner). To avoid overwriting them, the program saves no changes in this session. Please restart.");
        await AppState.Instance.EnsureToolsAsync();
        await AppState.Instance.LoadProbeCacheAsync();
        _ = AppState.Instance.SettingsPage.CheckUpdatesDailyAsync(atStart: true);
        var updates = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromHours(1) };
        updates.Tick += async (_, _) => { try { await AppState.Instance.SettingsPage.CheckUpdatesDailyAsync(); } catch { } };
        updates.Start();
        AppState.Instance.Run.ReadPeers();

        var s = AppState.Instance.Settings;
        if (s.AutoStartRun)
        {
            await Task.Delay(TimeSpan.FromSeconds(App.StartedByAutostart ? 30 : 3));
            var run = AppState.Instance.Run;
            var waitedSince = DateTime.Now;
            while (run.IsIdle)
            {
                var p = AppState.Instance.Settings.FindProfile(AppState.Instance.Settings.SelectedProfile);
                if (p is null || await Io.DirExistsAsync(p.InputDir, 8000)) break;
                run.StatusText = L.T($"Warte auf den Quellordner {p.InputDir} (seit {waitedSince:HH:mm}) …", $"Waiting for the source folder {p.InputDir} (since {waitedSince:HH:mm}) …");
                await Task.Delay(TimeSpan.FromSeconds(60));
            }
            if (run.StartCommand.CanExecute(null)) run.StartCommand.Execute(null);
        }
    }

    // ---------------- Tray ----------------

    private bool TrayAvailable => CurrentApp?.Tray?.Visible == true;

    private void OnStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized && AppState.Instance.Settings.MinimizeToTray && TrayAvailable) Hide();
    }

    public void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnExitClick(object sender, RoutedEventArgs e)
    {
        if (CurrentApp is { } app) app.ExitApp();
        else Close();
    }

    private static void RememberSidebar(bool open)
    {
        var s = AppState.Instance.SettingsPage;
        if (s.SidebarExpanded != open) s.SidebarExpanded = open;
    }

    private void HideContentFocusFrame()
    {
        if (FindChild<Wpf.Ui.Controls.NavigationViewContentPresenter>(RootNavigation, _ => true) is { } presenter)
        {
            presenter.Focusable = false;
            presenter.FocusVisualStyle = null;
        }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (App.ScreenshotFolder is not null) return;
        if (CurrentApp?.SessionIsEnding == true) return;
        var app = AppState.Instance;
        var s = app.Settings;

        if (CurrentApp?.ExitRequested != true && s.CloseToTray && TrayAvailable)
        {
            e.Cancel = true;
            Hide();
            if (!s.TrayHintShown)
            {
                s.TrayHintShown = true;
                app.Save();
                CurrentApp?.Tray?.Notify(App.Title, L.T("Läuft im Infobereich weiter. Beenden über „Beenden“ unten in der Seitenleiste oder das Symbol (Rechtsklick → Beenden).", "Keeps running in the notification area. Exit via “Exit” at the bottom of the sidebar or the icon (right-click → Exit)."));
            }
            return;
        }

        var run = app.Run;
        var converting = app.Convert.IsRunning;
        if (app.Rename.IsExecuting)
        {
            if (!IsVisible) ShowFromTray();
            MessageBox.Show(L.T("Gerade wird umbenannt. Bitte warten, bis es fertig ist.", "Renaming is in progress. Please wait until it has finished."), App.Title, MessageBoxButton.OK, MessageBoxImage.Information);
            e.Cancel = true;
            return;
        }
        if ((run.IsRunning || converting) && s.ConfirmExit)
        {
            if (!IsVisible) ShowFromTray();
            var answer = MessageBox.Show(
                L.T("Es wird gerade gearbeitet. Beim Beenden wird die laufende Arbeit abgebrochen (Temp-Dateien werden gelöscht, nichts geht verloren).\n\nTrotzdem beenden?", "Work is in progress. Exiting cancels the running work (temp files are deleted, nothing is lost).\n\nExit anyway?"),
                App.Title, MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }
        if (run.IsRunning) run.CancelAndWait(TimeSpan.FromSeconds(10));
        if (converting) app.Convert.CancelAndWait(TimeSpan.FromSeconds(10));
        app.Save();
    }

    // ---------------- Fortschritt in der Taskleiste ----------------

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.BeginInvoke(() => OnRunChanged(sender, e)); return; }
        if (e.PropertyName is not (nameof(RunViewModel.IsRunning) or nameof(RunViewModel.ActiveFraction) or nameof(RunViewModel.Done)
            or nameof(RunViewModel.Total) or nameof(RunViewModel.IsPaused) or nameof(RunViewModel.CountErrors))) return;
        var run = AppState.Instance.Run;
        var info = TaskbarItemInfo;
        if (info is null) return;
        if (!AppState.Instance.Settings.TaskbarProgress || !run.IsRunning || run.Total <= 0)
        {
            info.ProgressState = TaskbarItemProgressState.None;
            return;
        }
        info.ProgressValue = Math.Clamp((run.Done + run.ActiveFraction) / run.Total, 0, 1);
        info.ProgressState = run.IsPaused ? TaskbarItemProgressState.Paused : TaskbarItemProgressState.Normal;
    }

    // ---------------- Screenshot-Modus (fuer den automatischen Test beim Build) ----------------

    private async Task TakeScreenshotsAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        foreach (var p in AppState.Instance.Settings.Profiles)
        {
            p.InputDir = DemoText.T(@"\\NAS\Serien");
            p.OutputDir = DemoText.T(@"\\NAS\Komprimiert");
        }
        await AppState.Instance.EnsureToolsAsync();
        AppState.Instance.Run.FillDemo();
        AppState.Instance.ToolsPage.FillDemo();
        AppState.Instance.Rename.FillDemo();
        AppState.Instance.Library.FillDemo();
        AppState.Instance.Statistics.FillDemo();
        AppState.Instance.Convert.FillDemo();

        var pages = new (Type Page, string Name)[]
        {
            (typeof(DashboardPage), "1-start"),
            (typeof(ProfilesPage), "2-profile"),
            (typeof(LibraryPage), "3-bibliothek"),
            (typeof(StatsPage), "3-statistik"),
            (typeof(ToolsPage), "3-werkzeuge"),
            (typeof(ConvertPage), "3-konvertieren"),
            (typeof(RenamePage), "4-umbenennen"),
            (typeof(FfmpegPage), "4-ffmpeg"),
            (typeof(SettingsPage), "5-einstellungen"),
        };
        foreach (var (page, name) in pages)
        {
            RootNavigation.Navigate(page);
            await Task.Delay(2000);
            SavePng(this, Path.Combine(folder, name + ".png"));

            if (FindChild<TabControl>(RootNavigation, _ => true) is { } tabs)
            {
                for (var i = 0; i < tabs.Items.Count; i++)
                {
                    tabs.SelectedIndex = i;
                    await Task.Delay(700);
                    SavePng(this, Path.Combine(folder, $"{name}-tab{i + 1}.png"));
                }
                tabs.SelectedIndex = 0;
                continue;
            }

            if (FindChild<ScrollViewer>(RootNavigation, sv => Behaviors.Wheel.GetScrollPage(sv) && sv.ScrollableHeight > 0) is { } scroll)
            {
                foreach (var ex in FindChildren<Wpf.Ui.Controls.CardExpander>(scroll)) ex.IsExpanded = true;
                await Task.Delay(800);
                if (scroll.Content is FrameworkElement content)
                    SavePng(content, Path.Combine(folder, name + "-ganz.png"));
            }
        }

        AppState.Instance.Rename.FillMovieDemo();
        RootNavigation.Navigate(typeof(DashboardPage));
        await Task.Delay(300);
        RootNavigation.Navigate(typeof(RenamePage));
        await Task.Delay(1500);
        SavePng(this, Path.Combine(folder, "4-umbenennen-filme.png"));

        await ShowcaseAllAsync(Path.Combine(folder, "showcase"));

        ThemeHelper.Apply(Core.Settings.ThemeMode.Light);
        RootNavigation.Navigate(typeof(DashboardPage));
        await Task.Delay(2000);
        SavePng(this, Path.Combine(folder, "6-start-hell.png"));
        RootNavigation.Navigate(typeof(ProfilesPage));
        await Task.Delay(1500);
        SavePng(this, Path.Combine(folder, "7-profile-hell.png"));
    }

    // ---------------- Praesentations-Bilder ----------------

    private async Task ShowcaseAllAsync(string folder)
    {
        Directory.CreateDirectory(folder);
        AppState.Instance.Rename.FillDemo();
        var shots = new (Type Page, string Name, int Tab, double Height, bool AlsoOpen)[]
        {
            (typeof(DashboardPage), "01-start", -1, 900, true),
            (typeof(LibraryPage), "02-bibliothek", 0, 960, true),
            (typeof(LibraryPage), "03-bibliothek-doppelte", 1, 760, false),
            (typeof(LibraryPage), "04-bibliothek-fehlende-folgen", 2, 640, false),
            (typeof(LibraryPage), "05-bibliothek-probleme", 3, 640, false),
            (typeof(StatsPage), "06-statistik", -1, 900, true),
            (typeof(ProfilesPage), "07-profil-qualitaet", 1, 900, false),
            (typeof(ProfilesPage), "08-profil-ton", 3, 900, false),
            (typeof(RenamePage), "09-umbenennen-serien", -1, 860, true),
            (typeof(ToolsPage), "11-werkzeuge", -1, 900, false),
            (typeof(ConvertPage), "16-konvertieren", -1, 900, true),
            (typeof(SettingsPage), "12-einstellungen-arbeitsweise", 1, 900, false),
            (typeof(FfmpegPage), "13-ffmpeg", -1, 760, false),
        };
        foreach (var (page, name, tab, height, alsoOpen) in shots)
        {
            await ShowcaseAsync(folder, page, name, tab, height, false);
            if (alsoOpen) await ShowcaseAsync(folder, page, name + "-seitenleiste", tab, height, true);
        }
        AppState.Instance.Rename.FillMovieDemo();
        await ShowcaseAsync(folder, typeof(RenamePage), "10-umbenennen-filme", -1, 860, false);

        ThemeHelper.Apply(Core.Settings.ThemeMode.Light);
        await ShowcaseAsync(folder, typeof(DashboardPage), "14-start-hell", -1, 900, false);
        await ShowcaseAsync(folder, typeof(StatsPage), "15-statistik-hell", -1, 900, false);
        ThemeHelper.Apply(Core.Settings.ThemeMode.Dark);
        RootNavigation.IsPaneOpen = false;
    }

    private async Task ShowcaseAsync(string folder, Type page, string name, int tab, double minHeight, bool paneOpen)
    {
        const double width = 1440;
        const double scale = 1.5;
        RootNavigation.IsPaneOpen = paneOpen;
        RootNavigation.Navigate(page);
        await Task.Delay(1200);
        if (tab >= 0 && FindChild<TabControl>(RootNavigation, _ => true) is { } tabs && tab < tabs.Items.Count)
        {
            tabs.SelectedIndex = tab;
            await Task.Delay(700);
        }
        if (Content is not FrameworkElement root) return;

        var bg = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black;
        Content = null;
        var host = new Border { Background = bg, Child = root };
        try
        {
            var height = minHeight;
            void Layout()
            {
                host.Measure(new Size(width, height));
                host.Arrange(new Rect(0, 0, width, height));
                host.UpdateLayout();
            }
            Layout();
            await Task.Delay(400);
            for (var pass = 0; pass < 6; pass++)
            {
                Layout();
                var sv = FindChildren<ScrollViewer>(host).Where(x => Behaviors.Wheel.GetScrollPage(x) && x.ActualHeight > 0).OrderByDescending(x => x.ScrollableHeight).FirstOrDefault();
                if (sv is null || sv.ScrollableHeight <= 1) break;
                height = Math.Min(9000, height + sv.ScrollableHeight + 2);
                await Task.Delay(200);
            }
            Layout();
            await Task.Delay(300);
            Layout();
            var rtb = new RenderTargetBitmap((int)Math.Ceiling(width * scale), (int)Math.Ceiling(height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            rtb.Render(host);
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(rtb));
            using var fs = File.Create(Path.Combine(folder, name + ".png"));
            enc.Save(fs);
        }
        finally
        {
            host.Child = null;
            Content = root;
            UpdateLayout();
        }
    }

    private const int WmGetMinMaxInfo = 0x0024;

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MinMaxInfo
    {
        public int ReservedX, ReservedY, MaxSizeX, MaxSizeY, MaxPositionX, MaxPositionY, MinTrackX, MinTrackY, MaxTrackX, MaxTrackY;
    }

    private static IntPtr AllowHugeWindow(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WmGetMinMaxInfo) return IntPtr.Zero;
        var mmi = System.Runtime.InteropServices.Marshal.PtrToStructure<MinMaxInfo>(lParam);
        mmi.MaxTrackX = 20000;
        mmi.MaxTrackY = 20000;
        System.Runtime.InteropServices.Marshal.StructureToPtr(mmi, lParam, false);
        handled = true;
        return IntPtr.Zero;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        if (App.ScreenshotFolder is not null && PresentationSource.FromVisual(this) is System.Windows.Interop.HwndSource src)
            src.AddHook(AllowHugeWindow);
    }

    private void SavePng(FrameworkElement element, string file, double scale = 0)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var sx = scale > 0 ? scale : dpi.DpiScaleX;
        var sy = scale > 0 ? scale : dpi.DpiScaleY;
        var w = Math.Max(1, element.ActualWidth);
        var h = Math.Max(1, element.ActualHeight);
        var rtb = new RenderTargetBitmap((int)Math.Ceiling(w * sx), (int)Math.Ceiling(h * sy), 96 * sx, 96 * sy, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            var bg = TryFindResource("ApplicationBackgroundBrush") as Brush ?? Brushes.Black;
            dc.DrawRectangle(bg, null, new Rect(0, 0, w, h));
            dc.DrawRectangle(new VisualBrush(element) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top }, null, new Rect(0, 0, w, h));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    private static T? FindChild<T>(DependencyObject root, Func<T, bool> match) where T : DependencyObject =>
        FindChildren<T>(root).FirstOrDefault(match);

    private static IEnumerable<T> FindChildren<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var c = VisualTreeHelper.GetChild(root, i);
            if (c is T t) yield return t;
            foreach (var x in FindChildren<T>(c)) yield return x;
        }
    }
}
