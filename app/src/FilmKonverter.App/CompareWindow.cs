using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using FilmKonverter.Core.Media;
using FilmKonverter.Core.Util;

namespace FilmKonverter.App;

public sealed class CompareWindow : Window
{
    private readonly FfmpegTools _tools;
    private readonly string _source;
    private readonly string _clip;
    private readonly double _clipStart;
    private readonly double _clipLength;
    private readonly string _clipName;

    private readonly Image _under = new() { Stretch = Stretch.Uniform };
    private readonly Image _over = new() { Stretch = Stretch.Uniform };
    private readonly Grid _stage = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Background = Brushes.Transparent };
    private readonly Border _line = new() { Width = 2, Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, IsHitTestVisible = false, Opacity = 0.85 };
    private readonly ScrollViewer _scroll = new() { HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private readonly Slider _time = new() { Width = 260, Minimum = 0, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _status = new() { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly CheckBox _oneToOne = new() { Margin = new Thickness(16, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Content = L.T("Originalgröße 1:1", "Actual size 1:1") };
    private double _split = 0.5;
    private int _w, _h;
    private CancellationTokenSource? _cts;
    private readonly string _id = Guid.NewGuid().ToString("N")[..8];

    public CompareWindow(FfmpegTools tools, string source, string clip, double clipStart, double clipLength)
    {
        _tools = tools;
        _source = source;
        _clip = clip;
        _clipStart = clipStart;
        _clipLength = Math.Max(1, clipLength);
        _clipName = Path.GetFileNameWithoutExtension(clip);
        Title = L.T($"Bildvergleich · Original ↔ {_clipName}", $"Picture comparison · original ↔ {_clipName}");
        Width = 1280;
        Height = 820;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "ApplicationBackgroundBrush");
        SetResourceReference(ForegroundProperty, "TextFillColorPrimaryBrush");

        _time.Maximum = _clipLength;
        _time.Value = _clipLength / 2;
        _time.SmallChange = 1;
        _time.LargeChange = 5;

        var bar = new DockPanel { Margin = new Thickness(16, 12, 16, 8), LastChildFill = false };
        bar.Children.Add(new TextBlock { Text = L.T("Stelle im Ausschnitt", "Position in the clip"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) });
        bar.Children.Add(_time);
        var load = new Button { Content = L.T("Bild laden", "Load frame"), Margin = new Thickness(10, 0, 0, 0), Padding = new Thickness(12, 4, 12, 4) };
        load.Click += async (_, _) => await LoadAsync();
        bar.Children.Add(load);
        bar.Children.Add(_oneToOne);
        bar.Children.Add(_status);

        var hint = new TextBlock
        {
            Margin = new Thickness(16, 0, 16, 8),
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Text = L.T($"Links Original, rechts {_clipName}. Die Trennlinie folgt der Maus. Am besten dunkle Flächen, Gesichter und feine Strukturen ansehen.",
                       $"Left original, right {_clipName}. The dividing line follows the mouse. Best look at dark areas, faces and fine textures."),
        };

        _stage.Children.Add(_under);
        _stage.Children.Add(_over);
        _stage.Children.Add(_line);
        _stage.Children.Add(Label(L.T("Original", "Original"), HorizontalAlignment.Left));
        _stage.Children.Add(Label(_clipName, HorizontalAlignment.Right));
        _stage.MouseMove += (_, e) => { var x = e.GetPosition(_stage).X; if (_stage.ActualWidth > 0) { _split = Math.Clamp(x / _stage.ActualWidth, 0, 1); UpdateClip(); } };
        _stage.SizeChanged += (_, _) => UpdateClip();
        _scroll.Content = _stage;
        _oneToOne.Checked += (_, _) => ApplyZoom();
        _oneToOne.Unchecked += (_, _) => ApplyZoom();

        var root = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        root.Children.Add(bar);
        root.Children.Add(hint);
        root.Children.Add(new Border { Margin = new Thickness(16, 0, 16, 16), Background = Brushes.Black, CornerRadius = new CornerRadius(6), Child = _scroll });
        Content = root;

        Loaded += async (_, _) => await LoadAsync();
        Closed += (_, _) => _cts?.Cancel();
        PreviewKeyDown += async (_, e) =>
        {
            if (e.Key is not (Key.Right or Key.Left)) return;
            e.Handled = true;
            _time.Value = Math.Clamp(_time.Value + (e.Key == Key.Right ? 1 : -1), 0, _time.Maximum);
            await LoadAsync();
        };
        Closed += (_, _) =>
        {
            try { Directory.Delete(Path.Combine(Path.GetTempPath(), "GimboKonverter", "Vergleich", _id), true); } catch { }
        };
    }

    private static Border Label(string text, HorizontalAlignment side) => new()
    {
        HorizontalAlignment = side,
        VerticalAlignment = VerticalAlignment.Top,
        Margin = new Thickness(10),
        Padding = new Thickness(8, 3, 8, 3),
        CornerRadius = new CornerRadius(4),
        Background = new SolidColorBrush(Color.FromArgb(0xB0, 0, 0, 0)),
        IsHitTestVisible = false,
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontWeight = FontWeights.SemiBold },
    };

    private void ApplyZoom()
    {
        var oneToOne = _oneToOne.IsChecked == true;
        foreach (var img in new[] { _under, _over })
        {
            img.Stretch = oneToOne ? Stretch.None : Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(img, oneToOne ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        }
        _scroll.HorizontalScrollBarVisibility = oneToOne ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
        _scroll.VerticalScrollBarVisibility = oneToOne ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;
    }

    private void UpdateClip()
    {
        var w = _over.ActualWidth;
        var h = _over.ActualHeight;
        if (w <= 0 || h <= 0) return;
        var x = w * _split;
        _over.Clip = new RectangleGeometry(new Rect(x, 0, Math.Max(0, w - x), h));
        _line.Margin = new Thickness(Math.Max(0, x - 1), 0, 0, 0);
        _line.Height = h;
        _line.VerticalAlignment = VerticalAlignment.Top;
    }

    private async Task LoadAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _status.Text = L.T("Lade Bilder …", "Loading frames …");
        try
        {
            if (_w == 0)
            {
                var info = await _tools.ProbeAsync(_clip, ct);
                var v = info?.Video;
                _w = v is null ? 0 : v.DisplayWidth;
                _h = v is null ? 0 : v.DisplayHeight;
            }
            var dir = Path.Combine(Path.GetTempPath(), "GimboKonverter", "Vergleich", _id);
            Directory.CreateDirectory(dir);
            var t = _time.Value;
            var a = Path.Combine(dir, "original.png");
            var b = Path.Combine(dir, "clip.png");
            var okA = await QualityCheck.ExtractFrameAsync(_tools, _source, _clipStart + t, _w, _h, a, ct);
            var okB = await QualityCheck.ExtractFrameAsync(_tools, _clip, t, _w, _h, b, ct);
            if (!okA || !okB) { _status.Text = L.T("Bild konnte nicht gelesen werden.", "Frame could not be read."); return; }
            _under.Source = Load(a);
            _over.Source = Load(b);
            _status.Text = L.T($"{Fmt.Time(_clipStart + t)} in der Quelle · {_w}x{_h}", $"{Fmt.Time(_clipStart + t)} in the source · {_w}x{_h}");
            UpdateLayout();
            UpdateClip();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _status.Text = ex.Message;
        }
    }

    private static BitmapImage Load(string file)
    {
        var img = new BitmapImage();
        img.BeginInit();
        img.CacheOption = BitmapCacheOption.OnLoad;
        img.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        img.UriSource = new Uri(file);
        img.EndInit();
        img.Freeze();
        return img;
    }
}
