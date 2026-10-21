using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LaserPilot.App;

/// <summary>First-run welcome: offers the guided tour, the Setup Wizard, or neither.</summary>
public sealed class WelcomeDialog : Window
{
    public enum Choice { Tutorial, Wizard, Skip }

    public Choice Result { get; private set; } = Choice.Skip;

    public static Choice Ask(Window owner, bool setupDone)
    {
        var dlg = new WelcomeDialog(setupDone) { Owner = owner };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private WelcomeDialog(bool setupDone)
    {
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = "Welcome to LaserPilot";
        Width = 580;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];
        var sp = new StackPanel { Margin = new Thickness(26, 22, 26, 22) };
        sp.Children.Add(new TextBlock
        {
            Text = "Welcome to LaserPilot 👋",
            FontSize = 26, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Safe, simple laser control made for your OMTech B10. Where would you like to start?",
            FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 18),
        });

        sp.Children.Add(BigButton("🎓  Show me around",
            "A friendly guided tour of every part of the screen, step by step. Recommended if you're new to LaserPilot or to laser engraving.",
            accent: true, () => Choose(Choice.Tutorial)));
        sp.Children.Add(BigButton("🧙  Set up my laser first",
            setupDone
                ? "Re-run the Setup Wizard: it safely re-checks the homing direction and limit switches."
                : "Recommended before your first job: the Setup Wizard safely checks the homing direction and limit switches before anything moves.",
            accent: false, () => Choose(Choice.Wizard)));
        sp.Children.Add(BigButton("I know my way around",
            "Skip for now and go straight to the main screen.",
            accent: false, () => Choose(Choice.Skip)));

        sp.Children.Add(new TextBlock
        {
            Text = "You can start the tour any time: press F1, or click ❓ Tutorial at the top of the window.",
            Foreground = subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0),
        });
        Content = sp;
    }

    private void Choose(Choice c)
    {
        Result = c;
        DialogResult = true;
    }

    private static Button BigButton(string title, string description, bool accent, Action onClick)
    {
        var content = new StackPanel { Margin = new Thickness(4, 2, 4, 2) };
        content.Children.Add(new TextBlock { Text = title, FontSize = 16, FontWeight = FontWeights.SemiBold });
        content.Children.Add(new TextBlock { Text = description, FontSize = 12.5, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0), Opacity = 0.9 });
        var b = new Button
        {
            Content = content,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Padding = new Thickness(14, 10, 14, 10),
            Margin = new Thickness(0, 0, 0, 10),
        };
        if (accent) b.Style = (Style)Application.Current.Resources["AccentButton"];
        b.Click += (_, _) => onClick();
        return b;
    }
}
