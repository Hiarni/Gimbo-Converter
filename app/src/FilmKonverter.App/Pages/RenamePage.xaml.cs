using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App.Pages;

public partial class RenamePage
{
    public RenamePage()
    {
        InitializeComponent();
        DataContext = AppState.Instance.Rename;
        if (AppState.Instance.Rename.Rows.Count == 0) AppState.Instance.Rename.LoadJournals();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths && paths.Length > 0)
            AppState.Instance.Rename.AddSources(paths, add: (Keyboard.Modifiers & ModifierKeys.Control) != 0);
    }

    private void OnTokenClick(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string token) return;
        var box = SchemaBox;
        var pos = Math.Clamp(box.CaretIndex, 0, box.Text.Length);
        box.Text = box.Text.Insert(pos, token);
        box.CaretIndex = pos + token.Length;
        box.Focus();
    }

    private void OnRecentClick(object sender, RoutedEventArgs e)
    {
        var vm = AppState.Instance.Rename;
        var menu = new ContextMenu { PlacementTarget = sender as UIElement, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var path in vm.Recent)
        {
            var item = new MenuItem { Header = path, Command = vm.UseRecentCommand, CommandParameter = path };
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        menu.Items.Add(new MenuItem { Header = L.T("Liste leeren", "Clear list"), Command = vm.ClearRecentCommand });
        menu.IsOpen = true;
    }
}
