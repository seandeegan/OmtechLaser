using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.App.Services;
using LaserPilot.Core.Updates;

namespace LaserPilot.App;

/// <summary>"A new version is available" — shows what's new, then downloads and installs it.</summary>
public sealed class UpdateDialog : Window
{
    public enum Result { Later, Skip, Installed }

    public Result Outcome { get; private set; } = Result.Later;

    private readonly ReleaseInfo _release;
    private readonly UpdateService _updates;
    private readonly Func<string?> _canInstall;
    private readonly ProgressBar _progress = new() { Height = 8, Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Button _install, _later, _skip;
    private CancellationTokenSource? _cts;
    private bool _busy;

    /// <param name="canInstall">Returns null if installing now is fine, otherwise a reason ("" = user cancelled).</param>
    public UpdateDialog(ReleaseInfo release, UpdateService updates, Func<string?> canInstall)
    {
        _release = release;
        _updates = updates;
        _canInstall = canInstall;

        Style = (Style)Application.Current.Resources["DarkWindow"];
        Title = "Update available";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        var version = $"{release.Version.Major}.{release.Version.Minor}.{Math.Max(0, release.Version.Build)}";
        var sp = new StackPanel { Margin = new Thickness(22, 18, 22, 18) };
        sp.Children.Add(new TextBlock
        {
            Text = $"🎉 LaserPilot {version} is ready",
            FontSize = 21, FontWeight = FontWeights.Bold,
            Foreground = (Brush)Application.Current.Resources["AccentBrush"],
        });
        sp.Children.Add(new TextBlock
        {
            Text = $"You have version {UpdateService.CurrentVersionText}." +
                   (release.Published is { } p ? $" Released {p.LocalDateTime:d MMM yyyy, HH:mm}." : ""),
            Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"],
            Margin = new Thickness(0, 4, 0, 12),
        });
        sp.Children.Add(new TextBlock { Text = "What's new:", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 4) });
        sp.Children.Add(new TextBox
        {
            Style = (Style)Application.Current.Resources["TextArea"],
            Text = string.IsNullOrWhiteSpace(release.Notes) ? "No notes were written for this version." : release.Notes.Trim(),
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 200,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        sp.Children.Add(new TextBlock
        {
            Text = "Updating takes a few seconds: LaserPilot downloads the new version, closes, and starts again. Your designs and settings are kept.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["SubtleTextBrush"],
            Margin = new Thickness(0, 10, 0, 0),
        });
        sp.Children.Add(_progress);
        sp.Children.Add(_status);

        var buttons = new DockPanel { Margin = new Thickness(0, 16, 0, 0) };
        _install = new Button { Content = "⬇ Update now", Style = (Style)Application.Current.Resources["AccentButton"], Padding = new Thickness(16, 7, 16, 7) };
        _install.Click += async (_, _) => await InstallAsync();
        _later = new Button { Content = "Remind me later" };
        _later.Click += (_, _) => Close();
        _skip = new Button { Content = "Skip this version", Style = (Style)Application.Current.Resources["LinkButton"] };
        _skip.Click += (_, _) => { Outcome = Result.Skip; Close(); };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_later);
        right.Children.Add(_install);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_skip);
        sp.Children.Add(buttons);
        Content = sp;

        Closing += (_, e) =>
        {
            if (_busy && Outcome != Result.Installed)
            {
                if (MessageBox.Show(this, "Cancel the update?", "Update", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                    e.Cancel = true;
                else _cts?.Cancel();
            }
        };
    }

    private async Task InstallAsync()
    {
        var reason = _canInstall();
        if (reason is not null)
        {
            if (reason.Length > 0)
            {
                var open = !string.IsNullOrEmpty(_release.PageUrl) &&
                           MessageBox.Show(this, reason + "\n\nOpen the download page in your web browser instead?", "Update",
                               MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes;
                if (open) MainWindow.OpenUrl(_release.PageUrl);
            }
            return;
        }

        _busy = true;
        _install.IsEnabled = _later.IsEnabled = _skip.IsEnabled = false;
        _progress.Visibility = Visibility.Visible;
        _status.Text = "Downloading…";
        _cts = new CancellationTokenSource();
        try
        {
            var progress = new Progress<double>(p =>
            {
                _progress.Value = p;
                _status.Text = $"Downloading… {p:P0}";
            });
            var staged = await _updates.DownloadAsync(_release, progress, _cts.Token);
            // The user may have cancelled (closed this window) just as the download finished.
            _cts.Token.ThrowIfCancellationRequested();
            _status.Text = "Installing…";
            UpdateService.SwapInNewExe(staged);
            Outcome = Result.Installed;
            _status.Text = "Done — restarting LaserPilot…";
            await Task.Delay(400);
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            _status.Text = "Update cancelled.";
        }
        catch (Exception ex)
        {
            _status.Text = "The update didn't work: " + ex.Message;
            _status.Foreground = (Brush)Application.Current.Resources["WarnBrush"];
        }
        finally
        {
            _busy = false;
            if (Outcome != Result.Installed)
            {
                _install.IsEnabled = _later.IsEnabled = _skip.IsEnabled = true;
                _progress.Visibility = Visibility.Collapsed;
            }
        }
    }
}
