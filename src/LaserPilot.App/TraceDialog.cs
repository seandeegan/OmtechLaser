using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Importers;

namespace LaserPilot.App;

/// <summary>
/// Dialog for tracing an image into vector outlines. Adjust the darkness
/// threshold until the contour count/preview looks right, then Add.
/// </summary>
public sealed class TraceDialog : Window
{
    private readonly byte[] _gray;
    private readonly int _w, _h;
    private readonly Slider _threshold = new() { Minimum = 1, Maximum = 254, Value = 128, TickFrequency = 1, IsSnapToTickEnabled = true };
    private readonly CheckBox _invert = new() { Content = "Trace light areas instead of dark" };
    private readonly TextBlock _info = new() { Margin = new Thickness(0, 8, 0, 0) };
    private readonly Canvas _preview = new() { Width = 360, Height = 260, Background = new SolidColorBrush(Color.FromRgb(0x14, 0x15, 0x19)) };

    public List<Polyline>? Result { get; private set; }

    public TraceDialog(byte[] gray, int pxW, int pxH)
    {
        Title = "Trace image to vectors";
        Width = 420; Height = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = (Brush)Application.Current.Resources["BgBrush"];
        ResizeMode = ResizeMode.NoResize;

        // Downsample large images for speed and manageable geometry.
        (_gray, _w, _h) = Downsample(gray, pxW, pxH, 640);

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new Label { Content = "Darkness threshold" });
        sp.Children.Add(_threshold);
        sp.Children.Add(_invert);
        sp.Children.Add(_preview);
        sp.Children.Add(_info);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var ok = new Button { Content = "Add vectors", Width = 110, IsDefault = true, Style = (Style)Application.Current.Resources["AccentButton"] };
        ok.Click += (_, _) =>
        {
            if (Result is null || Result.Count == 0)
            {
                MessageBox.Show(this, "No contours found — adjust the threshold.", "Trace", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        sp.Children.Add(buttons);
        Content = sp;

        _threshold.ValueChanged += (_, _) => Retrace();
        _invert.Checked += (_, _) => Retrace();
        _invert.Unchecked += (_, _) => Retrace();
        Loaded += (_, _) => Retrace();
    }

    private void Retrace()
    {
        Result = ImageTracer.Trace(_gray, _w, _h, (int)_threshold.Value, _invert.IsChecked == true);
        var pts = Result.Sum(p => p.Points.Count);
        _info.Text = $"{Result.Count} contour(s), {pts} points";
        DrawPreview();
    }

    private void DrawPreview()
    {
        _preview.Children.Clear();
        if (Result is null || Result.Count == 0) return;
        var k = Math.Min(_preview.Width / _w, _preview.Height / _h);
        var ox = (_preview.Width - _w * k) / 2;
        var oy = (_preview.Height - _h * k) / 2;
        var geo = new StreamGeometry();
        using (var ctx = geo.Open())
        {
            foreach (var poly in Result)
            {
                if (poly.Points.Count < 2) continue;
                ctx.BeginFigure(new Point(ox + poly.Points[0].X * k, oy + poly.Points[0].Y * k), false, poly.Closed);
                for (int i = 1; i < poly.Points.Count; i++)
                    ctx.LineTo(new Point(ox + poly.Points[i].X * k, oy + poly.Points[i].Y * k), true, false);
            }
        }
        geo.Freeze();
        _preview.Children.Add(new System.Windows.Shapes.Path
        {
            Data = geo,
            Stroke = (Brush)Application.Current.Resources["AccentBrush"],
            StrokeThickness = 1,
        });
    }

    private static (byte[] gray, int w, int h) Downsample(byte[] src, int w, int h, int maxDim)
    {
        if (Math.Max(w, h) <= maxDim) return (src, w, h);
        var k = (double)maxDim / Math.Max(w, h);
        int nw = Math.Max(1, (int)(w * k)), nh = Math.Max(1, (int)(h * k));
        var dst = new byte[nw * nh];
        for (int y = 0; y < nh; y++)
            for (int x = 0; x < nw; x++)
                dst[y * nw + x] = src[(int)(y / k) * w + (int)(x / k)];
        return (dst, nw, nh);
    }
}
