using System.Windows;
using System.Windows.Threading;
using FilmKonverter.App.Services;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App;

public partial class App : Application
{
    public const string Title = "Gimbo Konverter";

    public static string? ScreenshotFolder { get; private set; }

    public static Core.Util.UiLanguage? LanguageOverride { get; private set; }

    public static bool StartedByAutostart { get; private set; }

    private SingleInstance? _instance;
    public TrayIcon? Tray { get; private set; }

    public bool ExitRequested { get; private set; }

    public bool SessionIsEnding { get; private set; }

    static App()
    {
        EventManager.RegisterClassHandler(typeof(FrameworkElement), System.Windows.Input.Keyboard.PreviewGotKeyboardFocusEvent,
            new System.Windows.Input.KeyboardFocusChangedEventHandler(OnPreviewFocus), handledEventsToo: true);
    }

    private static void OnPreviewFocus(object sender, System.Windows.Input.KeyboardFocusChangedEventArgs e)
    {
        if (e.NewFocus is FrameworkElement fe && fe.FocusVisualStyle is not null && !WantsFocusFrame(fe)) fe.FocusVisualStyle = null;
    }

    private static bool WantsFocusFrame(FrameworkElement fe) => fe is
        System.Windows.Controls.Primitives.ButtonBase or System.Windows.Controls.Primitives.TextBoxBase or System.Windows.Controls.PasswordBox or
        System.Windows.Controls.ComboBox or System.Windows.Controls.ComboBoxItem or System.Windows.Controls.ListBoxItem or
        System.Windows.Controls.TabItem or System.Windows.Controls.TreeViewItem or System.Windows.Controls.MenuItem or System.Windows.Controls.Slider;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var i = Array.IndexOf(e.Args, "--screenshots");
        if (i >= 0 && i + 1 < e.Args.Length) ScreenshotFolder = e.Args[i + 1];
        StartedByAutostart = e.Args.Contains(Autostart.Argument);
        var li = Array.IndexOf(e.Args, "--lang");
        if (li >= 0 && li + 1 < e.Args.Length)
            LanguageOverride = e.Args[li + 1].StartsWith("de", StringComparison.OrdinalIgnoreCase) ? Core.Util.UiLanguage.German : Core.Util.UiLanguage.English;
        var restarted = e.Args.Contains("--restart");

        DispatcherUnhandledException += OnUnhandled;
        SessionEnding += (_, _) =>
        {
            if (!_ranAsMain) return;
            SessionIsEnding = true;
            try { AppState.Instance.Save(); } catch { }
            if (AppState.Instance.Run.IsRunning) AppState.Instance.Run.CancelAndWait(TimeSpan.FromSeconds(6));
            if (AppState.Instance.Convert.IsRunning) AppState.Instance.Convert.CancelAndWait(TimeSpan.FromSeconds(4));
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            AppState.Instance.WriteLog("ABSTURZ " + args.ExceptionObject);

        if (ScreenshotFolder is null)
        {
            _instance = new SingleInstance(restarted ? TimeSpan.FromSeconds(20) : TimeSpan.Zero);
            if (!_instance.IsFirst)
            {
                _instance.SignalFirst();
                _instance.Dispose();
                _instance = null;
                Shutdown(0);
                return;
            }
            _instance.OnShowRequested(() => Dispatcher.BeginInvoke(ShowMainWindow));
        }

        _ranAsMain = true;
        var app = AppState.Instance;
        var s = app.Settings;
        if (ScreenshotFolder is null && Environment.ProcessPath is { } exe) Core.Util.AppUpdate.CleanupOld(exe);
        if (e.Args.Contains("--nach-update"))
            app.Run.UpdateNotice = L.T($"Gimbo wurde auf Version {SettingsViewModel_Version()} aktualisiert.", $"Gimbo was updated to version {SettingsViewModel_Version()}.");
        app.Store.CleanLogs(s.LogRetentionDays);
        if (ScreenshotFolder is null) Autostart.Sync(s.StartWithWindows);

        var window = new MainWindow();
        MainWindow = window;

        if (ScreenshotFolder is null)
        {
            Tray = new TrayIcon(app.Run, ShowMainWindow, ExitApp) { Visible = s.ShowTrayIcon };
            app.Run.Notify += (title, text, warn) =>
            {
                if (app.Settings.Notifications || warn) Tray?.Notify(title, text, warn);
            };
            app.Convert.Notify += (title, text, warn) =>
            {
                if (app.Settings.Notifications || warn) Tray?.Notify(title, text, warn);
            };
        }

        var hidden = (StartedByAutostart && s.AutostartMinimized) || s.StartMinimized;
        if (hidden && ScreenshotFolder is null)
        {
            window.StartHidden = s.ShowTrayIcon;
            window.WindowState = WindowState.Minimized;
        }
        window.Show();
    }

    public void ShowMainWindow()
    {
        if (MainWindow is not MainWindow w) return;
        w.ShowFromTray();
    }

    public void ExitApp()
    {
        ExitRequested = true;
        if (MainWindow is MainWindow w && !w.ClosedForReal)
        {
            w.Close();
            if (!w.ClosedForReal) { ExitRequested = false; return; }
        }
        Shutdown(0);
    }

    public void Restart() => Restart(null);

    public void Restart(string? extra)
    {
        ExitRequested = true;
        if (MainWindow is MainWindow w && !w.ClosedForReal)
        {
            w.Close();
            if (!w.ClosedForReal) { ExitRequested = false; return; }
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restart" + (extra is null ? "" : " " + extra)) { UseShellExecute = false });
        }
        catch
        {
        }
        Shutdown(0);
    }

    private static string SettingsViewModel_Version() => typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";

    private bool _ranAsMain;

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ranAsMain) { try { AppState.Instance.Save(); } catch { } }
        Tray?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppState.Instance.WriteLog("FEHLER " + e.Exception);
        if (ScreenshotFolder is not null)
        {
            try { File.WriteAllText(Path.Combine(ScreenshotFolder, "FEHLER.txt"), e.Exception.ToString()); } catch { }
            e.Handled = true;
            Shutdown(1);
            return;
        }
        MessageBox.Show(e.Exception.Message, Title + L.T(" – unerwarteter Fehler", " – unexpected error"), MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
