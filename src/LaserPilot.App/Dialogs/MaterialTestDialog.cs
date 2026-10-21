using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.App.Models;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Machines;

namespace LaserPilot.App;

/// <summary>Sets up a material test card: squares at different speeds (rows) and powers (columns).</summary>
public sealed class MaterialTestDialog : Window
{
    private readonly MachineProfile _profile;
    private readonly ComboBox _mode = new();
    private readonly TextBox _rows = Box("5"), _cols = Box("5");
    private readonly TextBox _speedMin = Box("1000"), _speedMax = Box("6000");
    private readonly TextBox _powerMin = Box("20"), _powerMax = Box("100");
    private readonly TextBox _size = Box("8"), _gap = Box("3"), _interval = Box("0.1"), _passes = Box("1");
    private readonly TextBox _x = Box("10"), _y = Box("10");
    private readonly CheckBox _labels = new() { Content = "Engrave labels (speed and power values)", IsChecked = true };
    private readonly CheckBox _replace = new() { Content = "Replace the current design", IsChecked = true };
    private readonly CheckBox _atLaser = new() { Content = "Run it wherever the laser head is (Start from: current laser position)", IsChecked = true };
    private readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };

    public MaterialTestOptions? Options { get; private set; }
    public bool ReplaceDesign => _replace.IsChecked == true;
    public bool RunAtLaserPosition => _atLaser.IsChecked == true;

    private static TextBox Box(string text) => new() { Text = text, Width = 70 };

    public MaterialTestDialog(MachineProfile profile)
    {
        _profile = profile;
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = "Material test";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];
        var sp = new StackPanel { Margin = new Thickness(18) };
        sp.Children.Add(new TextBlock { Text = "🧪 Material test", FontSize = 19, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock
        {
            Text = "Burns a grid of small squares on a scrap of your material — each row a different speed, each column a different power. " +
                   "Pick the square you like best and use its numbers for your real project.",
            TextWrapping = TextWrapping.Wrap, Foreground = subtle, Margin = new Thickness(0, 4, 0, 12),
        });

        _mode.Items.Add("Engraving test (filled squares)");
        _mode.Items.Add("Cutting / scoring test (outlines)");
        _mode.SelectedIndex = 0;
        _mode.SelectionChanged += (_, _) =>
        {
            bool cut = _mode.SelectedIndex == 1;
            _speedMin.Text = cut ? "100" : "1000";
            _speedMax.Text = cut ? "600" : "6000";
            _powerMin.Text = cut ? "60" : "20";
            _powerMax.Text = "100";
            _passes.Text = cut ? "1" : "1";
            _size.Text = cut ? "10" : "8";
            UpdateSummary();
        };
        sp.Children.Add(Row("Test type", _mode));

        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        for (int c = 0; c < 6; c++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = c % 2 == 0 ? new GridLength(118) : GridLength.Auto });
        for (int r = 0; r < 5; r++) grid.RowDefinitions.Add(new RowDefinition());
        void Cell(string label, UIElement box, int row, int col)
        {
            var l = new Label { Content = label };
            Grid.SetRow(l, row); Grid.SetColumn(l, col);
            Grid.SetRow(box, row); Grid.SetColumn(box, col + 1);
            grid.Children.Add(l);
            grid.Children.Add(box);
        }
        Cell("Rows (speeds)", _rows, 0, 0);
        Cell("Columns (powers)", _cols, 0, 2);
        Cell("Slowest mm/min", _speedMin, 1, 0);
        Cell("Fastest mm/min", _speedMax, 1, 2);
        Cell("Lowest power %", _powerMin, 2, 0);
        Cell("Highest power %", _powerMax, 2, 2);
        Cell("Square size mm", _size, 3, 0);
        Cell("Gap mm", _gap, 3, 2);
        Cell("Line gap mm", _interval, 4, 0);
        Cell("Passes", _passes, 4, 2);
        sp.Children.Add(grid);

        var pos = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        pos.Children.Add(new Label { Content = "Bottom-left corner at X", Width = 160 });
        pos.Children.Add(_x);
        pos.Children.Add(new Label { Content = "Y" });
        pos.Children.Add(_y);
        pos.Children.Add(new Label { Content = "mm" });
        sp.Children.Add(pos);

        sp.Children.Add(_labels);
        sp.Children.Add(_replace);
        sp.Children.Add(_atLaser);
        sp.Children.Add(_summary);

        foreach (var tb in new[] { _rows, _cols, _speedMin, _speedMax, _powerMin, _powerMax, _size, _gap, _interval, _passes, _x, _y })
            tb.TextChanged += (_, _) => UpdateSummary();
        _labels.Click += (_, _) => UpdateSummary();
        _atLaser.Click += (_, _) => UpdateSummary();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "Create test", Width = 110, Style = (Style)Application.Current.Resources["AccentButton"] };
        ok.Click += (_, _) => Create();
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
        sp.Children.Add(buttons);
        Content = sp;
        Loaded += (_, _) => UpdateSummary();
    }

    private static FrameworkElement Row(string label, FrameworkElement control)
    {
        var dp = new DockPanel();
        var l = new Label { Content = label, Width = 118 };
        DockPanel.SetDock(l, Dock.Left);
        dp.Children.Add(l);
        dp.Children.Add(control);
        return dp;
    }

    private static double Num(TextBox tb, double fallback) =>
        MainWindow.TryParseNumber(tb.Text, out var v) ? v : fallback;

    private MaterialTestOptions Read() => new()
    {
        Mode = _mode.SelectedIndex == 1 ? VectorMode.Line : VectorMode.Fill,
        Rows = (int)Math.Clamp(Num(_rows, 5), 1, 12),
        Columns = (int)Math.Clamp(Num(_cols, 5), 1, 12),
        SpeedMin = Math.Clamp(Num(_speedMin, 1000), 10, _profile.MaxFeedXy),
        SpeedMax = Math.Clamp(Num(_speedMax, 6000), 10, _profile.MaxFeedXy),
        PowerMin = Math.Clamp(Num(_powerMin, 20), 0, 100),
        PowerMax = Math.Clamp(Num(_powerMax, 100), 0, 100),
        BoxSizeMm = Math.Clamp(Num(_size, 8), 2, 50),
        GapMm = Math.Clamp(Num(_gap, 3), 0.5, 30),
        IntervalMm = Math.Clamp(Num(_interval, 0.1), 0.03, 1),
        Passes = (int)Math.Clamp(Num(_passes, 1), 1, 20),
        Labels = _labels.IsChecked == true,
        X = Num(_x, 10),
        Y = Num(_y, 10),
    };

    private void UpdateSummary()
    {
        var o = Read();
        var (w, h) = MaterialTest.Size(o);
        bool fits = o.X >= 0 && o.Y >= 0 && o.X + w <= _profile.WidthMm && o.Y + h <= _profile.HeightMm;
        _summary.Text = $"{o.Rows * o.Columns} squares · test card about {w:0} × {h:0} mm" +
                        (RunAtLaserPosition ? " · its bottom-left corner will start under the laser head." :
                            fits ? "" : $" · ⚠ doesn't fit on the {_profile.WidthMm:0}×{_profile.HeightMm:0} mm bed at this position");
        _summary.Foreground = (Brush)Application.Current.Resources[fits || RunAtLaserPosition ? "SubtleTextBrush" : "WarnBrush"];
    }

    private void Create()
    {
        var o = Read();
        if (o.SpeedMin > o.SpeedMax) (o.SpeedMin, o.SpeedMax) = (o.SpeedMax, o.SpeedMin);
        if (o.PowerMin > o.PowerMax) (o.PowerMin, o.PowerMax) = (o.PowerMax, o.PowerMin);
        Options = o;
        DialogResult = true;
    }
}
