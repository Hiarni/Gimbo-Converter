using System.ComponentModel;
using FilmKonverter.App.ViewModels;
using Forms = System.Windows.Forms;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.Services;

public sealed class TrayIcon : IDisposable
{
    private readonly RunViewModel _run;
    private readonly Action _show;
    private readonly Action _exit;
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _start;
    private readonly Forms.ToolStripMenuItem _pause;
    private readonly Forms.ToolStripMenuItem _stop;
    private readonly Forms.ToolStripMenuItem _cancel;
    private readonly Forms.ToolStripMenuItem _status;

    public TrayIcon(RunViewModel run, Action show, Action exit)
    {
        _run = run;
        _show = show;
        _exit = exit;

        var menu = new Forms.ContextMenuStrip();
        var open = new Forms.ToolStripMenuItem(L.T("Gimbo Konverter öffnen", "Open Gimbo Converter"), null, (_, _) => _show());
        open.Font = new System.Drawing.Font(open.Font, System.Drawing.FontStyle.Bold);
        _status = new Forms.ToolStripMenuItem(L.T("Bereit", "Ready")) { Enabled = false };
        _start = new Forms.ToolStripMenuItem("Start", null, (_, _) => Exec(_run.StartCommand));
        _pause = new Forms.ToolStripMenuItem("Pause", null, (_, _) => Exec(_run.PauseCommand));
        _stop = new Forms.ToolStripMenuItem(L.T("Nach laufenden Dateien stoppen", "Stop after current files"), null, (_, _) => Exec(_run.StopAfterCurrentCommand));
        _cancel = new Forms.ToolStripMenuItem(L.T("Abbrechen", "Cancel"), null, (_, _) => Exec(_run.CancelCommand));
        menu.Items.AddRange(new Forms.ToolStripItem[]
        {
            open, _status, new Forms.ToolStripSeparator(),
            _start, _pause, _stop, _cancel, new Forms.ToolStripSeparator(),
            new Forms.ToolStripMenuItem(L.T("Beenden", "Exit"), null, (_, _) => _exit()),
        });
        menu.Opening += (_, _) => UpdateMenu();

        _icon = new Forms.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = App.Title,
            ContextMenuStrip = menu,
            Visible = false,
        };
        _icon.MouseClick += (_, e) => { if (e.Button == Forms.MouseButtons.Left) _show(); };
        _icon.BalloonTipClicked += (_, _) => _show();
        _run.PropertyChanged += OnRunChanged;
    }

    public bool Visible
    {
        get => _icon.Visible;
        set => _icon.Visible = value;
    }

    private static System.Drawing.Icon LoadIcon()
    {
        var info = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Assets/GimboKonverter.ico"));
        using var s = info!.Stream;
        return new System.Drawing.Icon(s, Forms.SystemInformation.SmallIconSize);
    }

    private static void Exec(System.Windows.Input.ICommand cmd)
    {
        if (cmd.CanExecute(null)) cmd.Execute(null);
    }

    private void UpdateMenu()
    {
        _status.Text = Shorten(_run.IsRunning ? $"{_run.RunTitle} · {_run.CurrentPosition}" : _run.StatusText, 60);
        _start.Enabled = _run.StartCommand.CanExecute(null);
        _pause.Enabled = _run.IsRunning;
        _pause.Text = _run.IsPaused ? L.T("Fortsetzen", "Resume") : "Pause";
        _stop.Enabled = _run.IsRunning && !_run.StopRequested;
        _cancel.Enabled = _run.IsRunning;
    }

    private void OnRunChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(RunViewModel.IsRunning) or nameof(RunViewModel.PercentText)
            or nameof(RunViewModel.CurrentPosition) or nameof(RunViewModel.IsPaused) or nameof(RunViewModel.StatusText))) return;
        string text;
        if (!_run.IsRunning) text = App.Title + " – " + _run.StatusText;
        else
        {
            text = App.Title + " – " + (_run.IsPaused ? L.T("pausiert", "paused") : _run.StageName.Length > 0 ? $"{_run.StageName} {_run.PercentText}" : L.T("läuft", "running"));
            if (_run.CurrentPosition.Length > 0) text += "\n" + _run.CurrentPosition;
            if (_run.CurrentName.Length > 0) text += "\n" + Path.GetFileName(_run.CurrentName);
        }
        _icon.Text = Shorten(text, 127);
    }

    public void Notify(string title, string text, bool warning = false)
    {
        if (!_icon.Visible) return;
        _icon.ShowBalloonTip(5000, title, Shorten(text, 250), warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    public void Dispose()
    {
        _run.PropertyChanged -= OnRunChanged;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
