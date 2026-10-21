using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using LaserPilot.App.Services;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;
using Microsoft.Win32;

namespace LaserPilot.App;

public partial class SettingsWindow : Window
{
    public sealed class Row : INotifyPropertyChanged
    {
        private string _value = "";
        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string Value
        {
            get => _value;
            set { _value = value; OnChanged(); }
        }
        public string Unit { get; init; } = "";
        public string Description { get; init; } = "";
        public string Risk { get; init; } = "";

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly GrblController _grbl;
    private readonly ObservableCollection<Row> _rows = new();

    public SettingsWindow(GrblController grbl)
    {
        InitializeComponent();
        _grbl = grbl;
        Grid.ItemsSource = _rows;
        Loaded += async (_, _) => await LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        // Never rebuild the rows in the middle of an edit (WPF throws).
        Grid.CancelEdit(DataGridEditingUnit.Row);
        _rows.Clear();
        if (!_grbl.IsConnected)
        {
            HintText.Text = "Not connected — connect to the laser first.";
            return;
        }
        if (!await _grbl.RefreshSettingsAsync())
        {
            HintText.Text = _grbl.IsJobRunning ? "Settings can't be read while a job is running." : "The controller didn't answer — try again.";
            return;
        }
        foreach (var (n, v) in _grbl.Settings)
        {
            var info = GrblSettingsCatalog.Get(n);
            _rows.Add(new Row
            {
                Number = n,
                Name = info.Name,
                Value = v.ToString("0.###", Inv),
                Unit = info.Unit,
                Description = info.Description,
                Risk = info.Risk.ToString(),
            });
        }
    }

    private async void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Row row) return;
        if (e.EditingElement is not TextBox box) return;
        var oldVal = _grbl.GetSetting(row.Number);
        var oldText = oldVal?.ToString("0.###", Inv) ?? row.Value;

        if (!MainWindow.TryParseNumber(box.Text, out var newVal))
        {
            MessageBox.Show(this, "That isn't a number.", "GRBL Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            box.Text = oldText; // commit the old value rather than leaving the grid stuck in edit mode
            return;
        }
        if (oldVal.HasValue && Math.Abs(oldVal.Value - newVal) < 1e-9) return;

        var info = GrblSettingsCatalog.Get(row.Number);
        if (info.Risk == SettingRisk.Dangerous &&
            MessageBox.Show(this,
                $"${row.Number} ({info.Name}) directly affects how the machine moves or homes.\n\n{info.Description}\n\n" +
                $"Change {oldText} → {newVal.ToString("0.###", Inv)}?",
                "Risky setting — please confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            box.Text = oldText;
            return;
        }

        // The edit commits when this handler returns; the controller is written afterwards.
        var r = await _grbl.WriteSettingAsync(row.Number, newVal);
        if (!r.Ok)
        {
            MessageBox.Show(this, $"The controller refused the change: {r.ErrorText}", "GRBL Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            row.Value = oldText;
        }
        else
        {
            row.Value = newVal.ToString("0.###", Inv);
        }
    }

    private void Backup_Click(object sender, RoutedEventArgs e)
    {
        var settings = _grbl.Settings;
        if (settings.Count == 0)
        {
            MessageBox.Show(this, "Read the settings from the machine first.", "Back up", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Directory.CreateDirectory(GrblBackup.Folder);
        var dlg = new SaveFileDialog
        {
            Filter = "Text file|*.txt",
            FileName = $"grbl-settings-{DateTime.Now:yyyy-MM-dd}.txt",
            InitialDirectory = GrblBackup.Folder,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dlg.FileName, GrblBackup.Format(settings, _grbl.FirmwareVersion));
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't save the backup: {ex.Message}", "Back up", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        if (!_grbl.IsConnected || _grbl.IsJobRunning)
        {
            MessageBox.Show(this, "Connect to the laser (and make sure no job is running) first.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Directory.CreateDirectory(GrblBackup.Folder);
        var dlg = new OpenFileDialog { Filter = "Text file|*.txt|All files|*.*", InitialDirectory = GrblBackup.Folder };
        if (dlg.ShowDialog(this) != true) return;

        Dictionary<int, double> wanted;
        try { wanted = GrblBackup.Parse(File.ReadAllText(dlg.FileName)); }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Couldn't read the file: {ex.Message}", "Restore", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var current = _grbl.Settings;
        var changes = wanted.Where(kv => !current.TryGetValue(kv.Key, out var v) || Math.Abs(v - kv.Value) > 1e-6)
            .OrderBy(kv => kv.Key).ToList();
        if (changes.Count == 0)
        {
            MessageBox.Show(this, "The machine already has exactly these settings.", "Restore", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var lines = changes.Select(kv =>
        {
            var info = GrblSettingsCatalog.Get(kv.Key);
            var have = current.TryGetValue(kv.Key, out var v) ? v.ToString("0.###", Inv) : "?";
            return $"{(info.Risk == SettingRisk.Dangerous ? "⚠ " : "   ")}${kv.Key} {info.Name}: {have} → {kv.Value.ToString("0.###", Inv)}";
        });
        if (MessageBox.Show(this, $"These {changes.Count} setting(s) will change:\n\n{string.Join("\n", lines)}\n\nWrite them to the controller?",
                "Restore settings", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;

        var failed = new List<string>();
        foreach (var (n, v) in changes)
        {
            var r = await _grbl.WriteSettingAsync(n, v);
            if (!r.Ok) failed.Add($"${n}: {r.ErrorText}");
        }
        await LoadAsync();
        MessageBox.Show(this,
            failed.Count == 0 ? "Settings restored." : "Some settings were refused:\n" + string.Join("\n", failed),
            "Restore settings", MessageBoxButton.OK, failed.Count == 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
    }

    private void OpenBackups_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(GrblBackup.Folder);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{GrblBackup.Folder}\"") { UseShellExecute = true });
        }
        catch { /* nothing useful to do */ }
    }
}
