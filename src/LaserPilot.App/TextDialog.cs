using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LaserPilot.App;

/// <summary>Small dialog for adding vector text: content, font, size, style.</summary>
public sealed class TextDialog : Window
{
    private readonly TextBox _text = new() { MinHeight = 70, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true };
    private readonly ComboBox _font = new();
    private readonly TextBox _height = new() { Text = "20" };
    private readonly CheckBox _bold = new() { Content = "Bold" };
    private readonly CheckBox _italic = new() { Content = "Italic" };

    public string TextValue => _text.Text;
    public string FontValue => _font.SelectedItem?.ToString() ?? "Arial";
    public double HeightMm => MainWindow.TryParseNumber(_height.Text, out var v) && v > 0 ? Math.Min(v, 400) : 20;
    public bool Bold => _bold.IsChecked == true;
    public bool Italic => _italic.IsChecked == true;

    public TextDialog()
    {
        Style = (Style)Application.Current.Resources["DarkWindow"];
        _text.Style = (Style)Application.Current.Resources["TextArea"];
        Title = "Add text";
        Width = 440;
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ResizeMode = ResizeMode.NoResize;

        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).OrderBy(s => s))
            _font.Items.Add(f);
        _font.SelectedItem = _font.Items.Contains("Arial") ? "Arial" : _font.Items[0];

        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new Label { Content = "Text" });
        sp.Children.Add(_text);
        sp.Children.Add(new Label { Content = "Font" });
        sp.Children.Add(_font);

        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        row.Children.Add(new Label { Content = "Letter height (mm)", ToolTip = "Height of a capital letter. 20 mm ≈ the height of a fingertip." });
        _height.Width = 60;
        row.Children.Add(_height);
        row.Children.Add(_bold);
        row.Children.Add(_italic);
        sp.Children.Add(row);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "Add", Width = 90, IsDefault = true, Style = (Style)Application.Current.Resources["AccentButton"] };
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_text.Text))
            {
                MessageBox.Show(this, "Enter some text first.", "Add text", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            DialogResult = true;
        };
        var cancel = new Button { Content = "Cancel", Width = 80, IsCancel = true };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        sp.Children.Add(buttons);
        Content = sp;
        Loaded += (_, _) => _text.Focus();
    }
}
