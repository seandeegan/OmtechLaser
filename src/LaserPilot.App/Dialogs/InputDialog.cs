using System.Windows;
using System.Windows.Controls;

namespace LaserPilot.App;

/// <summary>Asks for a single line of text.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _box = new();

    public static string? Ask(Window owner, string title, string prompt, string initial = "")
    {
        var dlg = new InputDialog(title, prompt, initial) { Owner = owner };
        return dlg.ShowDialog() == true ? dlg._box.Text : null;
    }

    private InputDialog(string title, string prompt, string initial)
    {
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        _box.Text = initial;
        sp.Children.Add(_box);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var ok = new Button { Content = "OK", Width = 90, IsDefault = true, Style = (Style)Application.Current.Resources["AccentButton"] };
        ok.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(ok);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
        sp.Children.Add(buttons);
        Content = sp;
        Loaded += (_, _) => { _box.Focus(); _box.SelectAll(); };
    }
}
