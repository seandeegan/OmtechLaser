using System.Windows;
using System.Windows.Input;
using LaserPilot.Core.Comm;

namespace LaserPilot.App;

public partial class MainWindow
{
    private const int ConsoleLimit = 2000;
    private readonly List<string> _commandHistory = new();
    private int _historyIndex;

    private void AppendConsole(LogEntry e)
    {
        var prefix = e.Direction switch
        {
            LogDirection.Sent => "→ ",
            LogDirection.Received => "← ",
            LogDirection.Error => "✖ ",
            _ => "ℹ ",
        };
        ConsoleList.Items.Add($"{e.Time:HH:mm:ss} {prefix}{e.Text}");
        if (ConsoleList.Items.Count > ConsoleLimit)
            for (int i = ConsoleList.Items.Count - ConsoleLimit; i > 0; i--) ConsoleList.Items.RemoveAt(0);
        if (ConsoleExpander.IsExpanded) ConsoleList.ScrollIntoView(ConsoleList.Items[^1]);
    }

    private async void ConsoleSend_Click(object sender, RoutedEventArgs e)
    {
        var cmd = ConsoleInput.Text.Trim();
        if (cmd.Length == 0 || !EnsureConnected()) return;
        ConsoleInput.Clear();
        _commandHistory.Remove(cmd);
        _commandHistory.Add(cmd);
        if (_commandHistory.Count > 100) _commandHistory.RemoveAt(0);
        _historyIndex = _commandHistory.Count;

        // Realtime commands are single characters that must bypass the line buffer.
        switch (cmd)
        {
            case "?": _grbl.QueryStatus(); return;
            case "!": _grbl.FeedHold(); return;
            case "~": _grbl.CycleResume(); return;
        }
        var r = await _grbl.SendAsync(cmd);
        if (!r.Ok && r.ErrorCode == -1)
            AppendConsole(new LogEntry { Direction = LogDirection.Error, Text = r.ErrorText });
    }

    private void ConsoleInput_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                ConsoleSend_Click(sender, e);
                e.Handled = true;
                break;
            case Key.Up when _commandHistory.Count > 0:
                _historyIndex = Math.Max(0, _historyIndex - 1);
                ConsoleInput.Text = _commandHistory[_historyIndex];
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
                e.Handled = true;
                break;
            case Key.Down when _commandHistory.Count > 0:
                _historyIndex = Math.Min(_commandHistory.Count, _historyIndex + 1);
                ConsoleInput.Text = _historyIndex < _commandHistory.Count ? _commandHistory[_historyIndex] : "";
                ConsoleInput.CaretIndex = ConsoleInput.Text.Length;
                e.Handled = true;
                break;
        }
    }

    private void ConsoleClear_Click(object sender, RoutedEventArgs e) => ConsoleList.Items.Clear();

    private void ConsoleCopy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(string.Join(Environment.NewLine, ConsoleList.Items.Cast<string>())); }
        catch { /* clipboard busy */ }
    }

    private void ShowConsole_Click(object sender, RoutedEventArgs e)
    {
        ConsoleExpander.IsExpanded = true;
        ConsoleInput.Focus();
    }
}
