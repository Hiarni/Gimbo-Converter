using System.Windows;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.Pages;

public partial class DashboardPage
{
    public DashboardPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Run;
    }

    private static string[] DroppedPaths(DragEventArgs e) =>
        e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] paths ? paths : [];

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var ok = DroppedPaths(e).Length > 0;
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        DropHint.Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
        e.Handled = true;
    }

    private void OnDragLeave(object sender, DragEventArgs e) => DropHint.Visibility = Visibility.Collapsed;

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropHint.Visibility = Visibility.Collapsed;
        var paths = DroppedPaths(e);
        if (paths.Length > 0) await AppState.Instance.Run.StartSelectionAsync(paths);
    }

    private async void OnPickSelection(object sender, RoutedEventArgs e)
    {
        var run = AppState.Instance.Run;
        var p = AppState.Instance.Profiles.Selected;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = L.T("Serien- oder Staffelordner zum Kodieren wählen (mehrere mit Strg)", "Choose series or season folders to encode (several with Ctrl)"),
            Multiselect = true,
        };
        if (p is not null && Io.DirExistsQuick(p.InputDir)) dlg.InitialDirectory = p.InputDir;
        if (dlg.ShowDialog() == true && dlg.FolderNames.Length > 0) await run.StartSelectionAsync(dlg.FolderNames);
    }
}
