using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Machines;
using Microsoft.Win32;

namespace LaserPilot.App;

/// <summary>
/// Shows what a job will do: a simulated burn on a wood-coloured bed, the
/// estimated time, and the raw G-code. Can save the G-code or start the job.
/// </summary>
public sealed class PreviewWindow : Window
{
    private const int MaxTextLines = 20000;

    private readonly IReadOnlyList<string> _gcode;
    private readonly MachineProfile _profile;
    private readonly double _maxS;
    private readonly Image _image = new() { Stretch = Stretch.Uniform };
    private readonly CheckBox _travel = new() { Content = "Show travel moves (laser off)", VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _rendering = new() { Text = "Drawing preview…", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private ToolpathParser.Result? _parsed;

    public PreviewWindow(IReadOnlyList<string> gcode, JobEstimate estimate, MachineProfile profile, double maxPowerS,
        string title, Action? onStart)
    {
        _gcode = gcode;
        _profile = profile;
        _maxS = maxPowerS;
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = $"{title} — preview";
        Width = 1000;
        Height = 760;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];

        // Stats
        var e = estimate.BurnExtents;
        var stats = new StackPanel { Margin = new Thickness(4, 0, 0, 0) };
        void Stat(string label, string value)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 10) };
            row.Children.Add(new TextBlock { Text = label, Foreground = subtle, FontSize = 12 });
            row.Children.Add(new TextBlock { Text = value, FontSize = 17, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
            stats.Children.Add(row);
        }
        Stat("Estimated time", FormatDuration(estimate.Total));
        Stat("Burning distance", $"{estimate.BurnDistanceMm / 1000:0.00} m");
        Stat("Travel (laser off)", $"{estimate.TravelDistanceMm / 1000:0.00} m");
        Stat("G-code lines", $"{estimate.LineCount:N0}");
        if (!double.IsNaN(e.MinX))
            Stat("Burn area", $"X {e.MinX:0.#} – {e.MaxX:0.#} mm\nY {e.MinY:0.#} – {e.MaxY:0.#} mm\n({e.MaxX - e.MinX:0.#} × {e.MaxY - e.MinY:0.#} mm)");
        stats.Children.Add(new TextBlock
        {
            Text = "Times are estimates from the machine's speed and acceleration settings; real jobs can take a little longer.",
            Foreground = subtle, FontSize = 11.5, TextWrapping = TextWrapping.Wrap,
        });

        var save = new Button { Content = "💾 Save G-code…", Margin = new Thickness(2, 14, 2, 2), ToolTip = "Save as a .gcode file — e.g. to copy onto the B10's MicroSD card" };
        save.Click += (_, _) => SaveGcode();
        stats.Children.Add(save);
        if (onStart is not null)
        {
            var start = new Button { Content = "▶ Start this job", Style = (Style)Application.Current.Resources["AccentButton"], Padding = new Thickness(10, 8, 10, 8) };
            start.Click += (_, _) => { Close(); onStart(); };
            stats.Children.Add(start);
        }
        var close = new Button { Content = "Close", IsCancel = true };
        close.Click += (_, _) => Close();
        stats.Children.Add(close);

        // Preview tab
        var previewArea = new Grid { Background = (Brush)Application.Current.Resources["CanvasBgBrush"] };
        previewArea.Children.Add(_rendering);
        previewArea.Children.Add(_image);
        var previewTop = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        previewTop.Children.Add(_travel);
        var preview = new DockPanel();
        DockPanel.SetDock(previewTop, Dock.Top);
        preview.Children.Add(previewTop);
        preview.Children.Add(previewArea);
        _travel.Click += (_, _) => Render();

        // G-code tab
        var text = string.Join(Environment.NewLine, gcode.Take(MaxTextLines));
        if (gcode.Count > MaxTextLines) text += $"{Environment.NewLine}… {gcode.Count - MaxTextLines:N0} more lines (use Save G-code to see everything)";
        var code = new TextBox
        {
            Style = (Style)Application.Current.Resources["TextArea"],
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Consolas"),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        };

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Preview", Content = preview });
        tabs.Items.Add(new TabItem { Header = "G-code", Content = code });

        var root = new DockPanel { Margin = new Thickness(10) };
        var side = new ScrollViewer { Content = stats, Width = 230, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(10, 0, 0, 0) };
        DockPanel.SetDock(side, Dock.Right);
        root.Children.Add(side);
        root.Children.Add(tabs);
        Content = root;

        Loaded += (_, _) => Render();
    }

    private static string FormatDuration(TimeSpan t) =>
        t.TotalHours >= 1 ? $"{(int)t.TotalHours} h {t.Minutes:00} min"
        : t.TotalMinutes >= 1 ? $"{(int)t.TotalMinutes} min {t.Seconds:00} s"
        : $"{Math.Max(1, (int)Math.Round(t.TotalSeconds))} s";

    private async void Render()
    {
        _rendering.Visibility = Visibility.Visible;
        bool travel = _travel.IsChecked == true;
        var gcode = _gcode;
        var parsed = _parsed;
        double w = _profile.WidthMm, h = _profile.HeightMm, maxS = _maxS;
        try
        {
            var (pixels, pw, ph, parsedNow) = await Task.Run(() =>
            {
                var p = parsed ?? ToolpathParser.Parse(gcode);
                var (px, iw, ih) = ToolpathRenderer.Render(p, w, h, maxS, travel, 1600);
                return (px, iw, ih, p);
            });
            _parsed = parsedNow;
            var bmp = BitmapSource.Create(pw, ph, 96, 96, PixelFormats.Bgra32, null, pixels, pw * 4);
            bmp.Freeze();
            _image.Source = bmp;
            _rendering.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            _rendering.Text = "Couldn't draw the preview: " + ex.Message;
        }
    }

    private void SaveGcode()
    {
        var dlg = new SaveFileDialog { Filter = "G-code (*.gcode)|*.gcode|G-code (*.nc)|*.nc|All files|*.*", FileName = "job.gcode" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllLines(dlg.FileName, _gcode);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Could not save:\n{ex.Message}", "Save G-code", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}

/// <summary>Draws parsed G-code moves into a BGRA pixel buffer: burns darken a wood-coloured bed.</summary>
public static class ToolpathRenderer
{
    public static (byte[] Pixels, int W, int H) Render(ToolpathParser.Result parsed, double bedW, double bedH,
        double maxPowerS, bool showTravel, int maxPixels)
    {
        // Area: the bed plus anything outside it.
        double minX = 0, minY = 0, maxX = bedW, maxY = bedH;
        foreach (var m in parsed.Moves)
        {
            minX = Math.Min(minX, Math.Min(m.X0, m.X1)); maxX = Math.Max(maxX, Math.Max(m.X0, m.X1));
            minY = Math.Min(minY, Math.Min(m.Y0, m.Y1)); maxY = Math.Max(maxY, Math.Max(m.Y0, m.Y1));
        }
        const double margin = 4;
        minX -= margin; minY -= margin; maxX += margin; maxY += margin;
        double scale = Math.Max(0.5, Math.Min(maxPixels / (maxX - minX), maxPixels / (maxY - minY))); // px per mm
        int w = Math.Max(1, (int)Math.Ceiling((maxX - minX) * scale));
        int h = Math.Max(1, (int)Math.Ceiling((maxY - minY) * scale));
        var dark = new float[w * h];
        var travel = showTravel ? new bool[w * h] : null;
        double pxMm = 1 / scale;
        const double spot = 0.08; // mm — B10-class spot
        double sMax = maxPowerS > 0 ? maxPowerS : 1000;

        foreach (var m in parsed.Moves)
        {
            double x0 = (m.X0 - minX) * scale, y0 = (maxY - m.Y0) * scale;
            double x1 = (m.X1 - minX) * scale, y1 = (maxY - m.Y1) * scale;
            double lenPx = Math.Sqrt((x1 - x0) * (x1 - x0) + (y1 - y0) * (y1 - y0));
            int steps = Math.Max(1, (int)Math.Ceiling(lenPx * 2));
            bool burn = !m.Rapid && m.Power > 0;
            if (!burn && travel is null) continue;
            float p = (float)Math.Clamp(m.Power / sMax, 0, 1);
            // Each sample covers (length/steps) of line, one spot wide.
            float coverage = (float)((lenPx / steps) * pxMm * spot / (pxMm * pxMm)) * p;
            float floor = 0.55f * p; // keep single lines visible
            for (int k = 0; k <= steps; k++)
            {
                double t = (double)k / steps;
                int px = (int)(x0 + (x1 - x0) * t), py = (int)(y0 + (y1 - y0) * t);
                if (px < 0 || py < 0 || px >= w || py >= h) continue;
                int i = py * w + px;
                if (burn) dark[i] = Math.Max(dark[i] + coverage, floor);
                else travel![i] = true;
            }
        }

        var pixels = new byte[w * h * 4];
        // Bed: light plywood; outside the bed: dark.
        int bedL = (int)((0 - minX) * scale), bedR = (int)((bedW - minX) * scale);
        int bedT = (int)((maxY - bedH) * scale), bedB = (int)((maxY - 0) * scale);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int i = y * w + x, o = i * 4;
                bool onBed = x >= bedL && x <= bedR && y >= bedT && y <= bedB;
                bool edge = onBed && (x == bedL || x == bedR || y == bedT || y == bedB);
                double r, g, b;
                if (onBed) { r = 0xE9; g = 0xDC; b = 0xC0; }
                else { r = 0x17; g = 0x18; b = 0x1C; }
                if (edge) { r = 0x8A; g = 0x7A; b = 0x60; }
                double d = Math.Clamp(dark[i], 0, 1);
                if (d > 0)
                {
                    r += (0x2A - r) * d;
                    g += (0x1C - g) * d;
                    b += (0x10 - b) * d;
                }
                if (travel is not null && travel[i] && d < 0.3)
                {
                    r = r * 0.4 + 0x3F * 0.6;
                    g = g * 0.4 + 0xA7 * 0.6;
                    b = b * 0.4 + 0xFF * 0.6;
                }
                pixels[o] = (byte)b;
                pixels[o + 1] = (byte)g;
                pixels[o + 2] = (byte)r;
                pixels[o + 3] = 255;
            }
        }
        return (pixels, w, h);
    }
}
