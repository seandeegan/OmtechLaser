using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Machines;

namespace LaserPilot.App;

public partial class SettingsWindow : Window
{
    public sealed class Row
    {
        public int Number { get; init; }
        public string Name { get; init; } = "";
        public string Value { get; set; } = "";
        public string Unit { get; init; } = "";
        public string Description { get; init; } = "";
        public string Risk { get; init; } = "";
    }

    private readonly GrblController _grbl;
    private readonly ObservableCollection<Row> _rows = new();

    public SettingsWindow(GrblController grbl)
    {
        InitializeComponent();
        _grbl = grbl;
        Grid.ItemsSource = _rows;
        _ = LoadAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e) => await LoadAsync();

    private async Task LoadAsync()
    {
        _rows.Clear();
        if (!_grbl.IsConnected)
        {
            HintText.Text = "Not connected — connect to the machine first.";
            return;
        }
        await _grbl.RefreshSettingsAsync();
        foreach (var (n, v) in _grbl.Settings)
        {
            var info = GrblSettingsCatalog.Get(n);
            _rows.Add(new Row
            {
                Number = n,
                Name = info.Name,
                Value = v.ToString("0.###", CultureInfo.InvariantCulture),
                Unit = info.Unit,
                Description = info.Description,
                Risk = info.Risk.ToString(),
            });
        }
    }

    private async void Grid_CellEditEnding(object? sender, DataGridCellEditEndingEventArgs e)
    {
        if (e.EditAction != DataGridEditAction.Commit || e.Row.Item is not Row row) return;
        if ((e.EditingElement as TextBox)?.Text is not string text) return;
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var newVal))
        {
            MessageBox.Show(this, "Not a valid number.", "GRBL Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            e.Cancel = true;
            return;
        }
        var oldVal = _grbl.GetSetting(row.Number);
        if (oldVal.HasValue && Math.Abs(oldVal.Value - newVal) < 1e-9) return;

        var info = GrblSettingsCatalog.Get(row.Number);
        if (info.Risk == SettingRisk.Dangerous)
        {
            var confirm = MessageBox.Show(this,
                $"${row.Number} ({info.Name}) directly affects machine motion or homing.\n\n{info.Description}\n\n" +
                $"Change {oldVal?.ToString("0.###", CultureInfo.InvariantCulture) ?? "?"} → {newVal.ToString("0.###", CultureInfo.InvariantCulture)}?",
                "Risky setting — confirm", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) { e.Cancel = true; await LoadAsync(); return; }
        }

        var r = await _grbl.WriteSettingAsync(row.Number, newVal);
        if (!r.Ok)
        {
            MessageBox.Show(this, $"Controller rejected the change: {r.ErrorText}", "GRBL Settings", MessageBoxButton.OK, MessageBoxImage.Warning);
            await LoadAsync();
        }
        else
        {
            row.Value = newVal.ToString("0.###", CultureInfo.InvariantCulture);
        }
    }
}
