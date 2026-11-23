using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;

namespace FilmKonverter.App.Behaviors;

public static class Wheel
{
    public static readonly DependencyProperty ScrollPageProperty = DependencyProperty.RegisterAttached(
        "ScrollPage", typeof(bool), typeof(Wheel), new PropertyMetadata(false, OnChanged));

    public static bool GetScrollPage(DependencyObject d) => (bool)d.GetValue(ScrollPageProperty);
    public static void SetScrollPage(DependencyObject d, bool value) => d.SetValue(ScrollPageProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not ScrollViewer sv) return;
        if ((bool)e.NewValue) sv.PreviewMouseWheel += OnPreviewMouseWheel;
        else sv.PreviewMouseWheel -= OnPreviewMouseWheel;
    }

    private static void OnPreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer sv || sv.ScrollableHeight <= 0) return;
        var up = e.Delta > 0;

        for (var d = e.OriginalSource as DependencyObject; d is not null && !ReferenceEquals(d, sv); d = Parent(d))
        {
            switch (d)
            {
                case Popup:
                case ComboBox { IsDropDownOpen: true }:
                    return;
                case ScrollViewer inner when inner.ScrollableHeight > 0:
                    if (up ? inner.VerticalOffset > 0 : inner.VerticalOffset < inner.ScrollableHeight) return;
                    break;
            }
        }

        if (e.OriginalSource is Visual v && PresentationSource.FromVisual(v) != PresentationSource.FromVisual(sv)) return;

        sv.ScrollToVerticalOffset(sv.VerticalOffset - e.Delta * 0.6);
        e.Handled = true;
    }

    private static DependencyObject? Parent(DependencyObject d) => d switch
    {
        Visual or System.Windows.Media.Media3D.Visual3D => VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d),
        FrameworkContentElement fce => fce.Parent,
        ContentElement ce => ContentOperations.GetParent(ce) ?? LogicalTreeHelper.GetParent(ce),
        _ => LogicalTreeHelper.GetParent(d),
    };
}
