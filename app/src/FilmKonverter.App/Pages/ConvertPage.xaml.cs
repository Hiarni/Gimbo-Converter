using System.Windows;

namespace FilmKonverter.App.Pages;

public partial class ConvertPage
{
    public ConvertPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Convert;
        if (App.ScreenshotFolder is null) _ = AppState.Instance.Convert.EnsureCapsAsync();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            _ = AppState.Instance.Convert.AddSourcesAsync(paths);
    }
}
