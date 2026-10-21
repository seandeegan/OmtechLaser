using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using LaserPilot.App.Services;

namespace LaserPilot.App;

/// <summary>
/// One-time setup for automatic updates from a private GitHub repository:
/// a read-only access token, stored encrypted for this Windows account.
/// </summary>
public sealed class UpdateSettingsDialog : Window
{
    private const string TokenPage = "https://github.com/settings/personal-access-tokens/new";

    private readonly AppSettings _settings;
    private readonly UpdateService _updates;
    private readonly PasswordBox _token = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly CheckBox _auto = new() { Content = "Check for updates automatically (recommended)" };
    private readonly CheckBox _pre = new() { Content = "Also offer test versions (pre-releases)" };
    private bool _removeSaved;

    public UpdateSettingsDialog(AppSettings settings, UpdateService updates)
    {
        _settings = settings;
        _updates = updates;
        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = "Update settings";
        Width = 600;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];
        var sp = new StackPanel { Margin = new Thickness(20, 16, 20, 16) };
        sp.Children.Add(new TextBlock { Text = "Automatic updates", FontSize = 19, FontWeight = FontWeights.Bold });
        sp.Children.Add(new TextBlock
        {
            Text = $"LaserPilot {UpdateService.CurrentVersionText} gets new versions from the GitHub repository {UpdateService.Repository ?? "(not set)"}. " +
                   "Because that repository is private, GitHub needs a read-only access key (a \"token\") before it will share the downloads.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 10),
        });

        var steps = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = subtle, LineHeight = 20 };
        steps.Inlines.Add(new Run("How to make a token (do this once, signed in to GitHub as the repository owner):\n"));
        steps.Inlines.Add(new Run("  1. Click \"Open GitHub token page\" below.\n"));
        steps.Inlines.Add(new Run("  2. Token name: LaserPilot updates. Expiration: the longest option offered.\n"));
        steps.Inlines.Add(new Run("  3. Repository access: \"Only select repositories\" → choose the LaserPilot repository.\n"));
        steps.Inlines.Add(new Run("  4. Permissions → Repository permissions → Contents: \"Read-only\". Nothing else.\n"));
        steps.Inlines.Add(new Run("  5. Click Generate token, copy it, paste it below, then click Test and Save."));
        sp.Children.Add(steps);
        var open = new Button { Content = "🔑 Open GitHub token page", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 8) };
        open.Click += (_, _) => MainWindow.OpenUrl(TokenPage);
        sp.Children.Add(open);

        sp.Children.Add(new TextBlock { Text = "Access token:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 6, 0, 2) });
        var tokenRow = new DockPanel();
        var test = new Button { Content = "Test", Width = 70 };
        test.Click += async (_, _) => await TestAsync();
        DockPanel.SetDock(test, Dock.Right);
        tokenRow.Children.Add(test);
        tokenRow.Children.Add(_token);
        sp.Children.Add(tokenRow);

        bool hasSaved = _settings.UpdateToken is not null;
        bool hasBuiltIn = UpdateService.EmbeddedToken is not null;
        var saved = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
        var savedText = new TextBlock
        {
            Foreground = subtle, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center,
            Text = hasSaved ? "✓ A token is saved for your Windows account (stored encrypted). Paste a new one to replace it."
                 : hasBuiltIn ? "✓ This copy has a built-in token, so you don't need to enter one — unless it stops working."
                 : "No token yet.",
        };
        if (hasSaved)
        {
            var remove = new Button { Content = "Remove saved token", Style = (Style)Application.Current.Resources["LinkButton"] };
            remove.Click += (_, _) =>
            {
                _removeSaved = true;
                savedText.Text = "The saved token will be removed when you click Save.";
            };
            DockPanel.SetDock(remove, Dock.Right);
            saved.Children.Add(remove);
        }
        saved.Children.Add(savedText);
        sp.Children.Add(saved);
        sp.Children.Add(_status);

        _auto.IsChecked = _settings.CheckForUpdates;
        _pre.IsChecked = _settings.IncludePrereleases;
        _auto.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(_auto);
        sp.Children.Add(_pre);
        sp.Children.Add(new TextBlock
        {
            Text = "The token is only ever sent to GitHub. It can only read this one repository, and you can delete it on GitHub at any time.",
            Foreground = subtle, FontSize = 12, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = "Save", Width = 90, Style = (Style)Application.Current.Resources["AccentButton"] };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save);
        buttons.Children.Add(new Button { Content = "Cancel", Width = 80, IsCancel = true });
        sp.Children.Add(buttons);
        Content = sp;
    }

    private async Task TestAsync()
    {
        var token = _token.Password.Trim();
        if (token.Length == 0) token = _updates.Token ?? "";
        if (token.Length == 0)
        {
            _status.Text = "Paste a token first.";
            return;
        }
        _status.Foreground = (Brush)Application.Current.Resources["TextBrush"];
        _status.Text = "Testing…";
        var problem = await _updates.TestTokenAsync(token);
        _status.Foreground = (Brush)Application.Current.Resources[problem is null ? "OkBrush" : "WarnBrush"];
        _status.Text = problem is null ? "✓ The token works — LaserPilot can see the releases." : "✗ " + problem;
    }

    private void Save()
    {
        var token = _token.Password.Trim();
        if (token.Length > 0) _settings.UpdateToken = token;
        else if (_removeSaved) _settings.UpdateToken = null;
        _settings.CheckForUpdates = _auto.IsChecked == true;
        _settings.IncludePrereleases = _pre.IsChecked == true;
        _settings.Save();
        DialogResult = true;
    }
}
