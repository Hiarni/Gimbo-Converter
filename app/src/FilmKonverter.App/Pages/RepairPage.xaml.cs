using System.Windows;

namespace FilmKonverter.App.Pages;

public partial class RepairPage
{
    public RepairPage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Repair;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            _ = AppState.Instance.Repair.AddSourcesAsync(paths);
    }
}
