using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using LaserPilot.App.Models;
using LaserPilot.Core.Gcode;
using LaserPilot.Core.Geometry;
using LaserPilot.Core.Materials;
using Microsoft.Win32;

namespace LaserPilot.App;

public partial class MainWindow
{
    private List<DesignItem> _items = new();
    /// <summary>The item shown in the property panels (last clicked).</summary>
    private DesignItem? _selected;
    private readonly HashSet<DesignItem> _selection = new();
    private readonly UndoStack _undo = new();
    private string? _projectPath;
    private bool _dirty;
    private bool _syncingList;
    private DateTime _lastNudgeUndo;

    private const string ImportFilter =
        "Everything LaserPilot can open|*.lpproj;*.svg;*.dxf;*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|" +
        "Drawings (SVG, DXF)|*.svg;*.dxf|Pictures|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.tif;*.tiff;*.webp|" + ProjectFile.Filter;

    private static readonly string[] ImportableExtensions =
        { ".svg", ".dxf", ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".tif", ".tiff", ".webp" };

    /// <summary>Selected items in list order.</summary>
    private List<DesignItem> SelectedItems => _items.Where(_selection.Contains).ToList();

    // ==================== Selection ====================

    private void SetSelection(IEnumerable<DesignItem> items, DesignItem? primary)
    {
        _selection.Clear();
        foreach (var i in items) _selection.Add(i);
        if (primary is not null) _selection.Add(primary);
        _selected = primary ?? _items.LastOrDefault(_selection.Contains);
        SyncItemsList();
        SyncPropPanel();
        RefreshPresetList();
        RedrawCanvas();
    }

    private void Select(DesignItem? item) =>
        SetSelection(item is null ? Array.Empty<DesignItem>() : new[] { item }, item);

    private void SelectAll_Click(object sender, RoutedEventArgs e) => SelectAll();
    private void SelectAll() => SetSelection(_items, _selected ?? _items.LastOrDefault());

    private void SyncItemsList()
    {
        _syncingList = true;
        try
        {
            ItemsList.Items.Clear();
            var subtle = (Brush)FindResource("SubtleTextBrush");
            foreach (var it in _items)
            {
                var item = it;
                var output = new CheckBox
                {
                    IsChecked = it.Enabled,
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(0, 0, 4, 0),
                    ToolTip = "Output: untick to keep this item on the bed but not burn it",
                };
                output.Click += (_, _) =>
                {
                    PushUndo();
                    item.Enabled = output.IsChecked == true;
                    MarkDirty();
                    SyncItemsList();
                    RedrawCanvas();
                };
                var text = new StackPanel { Opacity = it.Enabled ? 1 : 0.5 };
                text.Children.Add(new TextBlock { Text = $"{(it.IsRaster ? "🖼" : "✒")} {it.Name}", TextTrimming = TextTrimming.CharacterEllipsis });
                text.Children.Add(new TextBlock { Text = it.Summary, FontSize = 11, Foreground = subtle, TextTrimming = TextTrimming.CharacterEllipsis });
                var row = new DockPanel();
                DockPanel.SetDock(output, Dock.Left);
                row.Children.Add(output);
                row.Children.Add(text);
                var lbi = new ListBoxItem { Content = row, Tag = it, IsSelected = _selection.Contains(it) };
                ItemsList.Items.Add(lbi);
                if (it == _selected) ItemsList.ScrollIntoView(lbi);
            }
        }
        finally { _syncingList = false; }
    }

    private void ItemsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncingList) return;
        var picked = ItemsList.SelectedItems.Cast<ListBoxItem>().Select(l => (DesignItem)l.Tag).ToList();
        DesignItem? primary = e.AddedItems.Count > 0 ? (DesignItem)((ListBoxItem)e.AddedItems[e.AddedItems.Count - 1]!).Tag : null;
        _selection.Clear();
        foreach (var p in picked) _selection.Add(p);
        _selected = primary is not null && picked.Contains(primary) ? primary : picked.LastOrDefault();
        SyncPropPanel();
        RefreshPresetList();
        RedrawCanvas();
    }

    // ==================== Undo / dirty ====================

    private DesignSnapshot Capture() => new(
        _items.Select(i => i.Clone()).ToList(),
        _items.Select((it, idx) => (it, idx)).Where(t => _selection.Contains(t.it)).Select(t => t.idx).ToList(),
        _selected is null ? -1 : _items.IndexOf(_selected));

    private void PushUndo() => PushUndo(Capture());

    private void PushUndo(DesignSnapshot before)
    {
        _undo.Push(before);
        _lastNudgeUndo = DateTime.MinValue; // any other change ends a burst of arrow-key nudges
        UpdateUndoMenu();
    }

    private void Restore(DesignSnapshot s)
    {
        _lastNudgeUndo = DateTime.MinValue;
        _items = s.Items;
        _selection.Clear();
        foreach (var idx in s.Selected.Where(i => i >= 0 && i < _items.Count)) _selection.Add(_items[idx]);
        _selected = s.Primary >= 0 && s.Primary < _items.Count ? _items[s.Primary] : null;
        MarkDirty();
        SyncItemsList();
        SyncPropPanel();
        RefreshPresetList();
        RedrawCanvas();
        UpdateUndoMenu();
    }

    private void Undo()
    {
        if (_undo.Undo(Capture()) is { } s) Restore(s);
    }

    private void Redo()
    {
        if (_undo.Redo(Capture()) is { } s) Restore(s);
    }

    private void Undo_Click(object sender, RoutedEventArgs e) => Undo();
    private void Redo_Click(object sender, RoutedEventArgs e) => Redo();

    private void UpdateUndoMenu()
    {
        MenuUndo.IsEnabled = _undo.CanUndo;
        MenuRedo.IsEnabled = _undo.CanRedo;
    }

    private void MarkDirty()
    {
        _dirty = true;
        EstimateText.Text = "";
        UpdateTitle();
    }

    // ==================== Adding items ====================

    private void AddItems(IReadOnlyList<DesignItem> newItems, bool centerOnBed = true)
    {
        if (newItems.Count == 0) return;
        PushUndo();
        foreach (var item in newItems)
        {
            if (centerOnBed)
            {
                item.X = (_profile.WidthMm - item.W) / 2;
                item.Y = (_profile.HeightMm - item.H) / 2;
            }
            _items.Add(item);
        }
        MarkDirty();
        SetSelection(newItems, newItems[^1]);
    }

    private void Shape_Click(object sender, RoutedEventArgs e)
    {
        if (ShapeBtn.ContextMenu is null) return;
        ShapeBtn.ContextMenu.PlacementTarget = ShapeBtn;
        ShapeBtn.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
        ShapeBtn.ContextMenu.IsOpen = true;
    }

    private void AddRect_Click(object sender, RoutedEventArgs e) => AddItems(new[] { ShapeFactory.Rect() });
    private void AddEllipse_Click(object sender, RoutedEventArgs e) => AddItems(new[] { ShapeFactory.Ellipse() });
    private void AddLine_Click(object sender, RoutedEventArgs e) => AddItems(new[] { ShapeFactory.Line() });
    private void AddStar_Click(object sender, RoutedEventArgs e) => AddItems(new[] { ShapeFactory.Star() });

    private void AddText_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new TextDialog { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            AddItems(new[] { ShapeFactory.Text(dlg.TextValue, dlg.FontValue, dlg.Bold, dlg.Italic, dlg.HeightMm) });
        }
        catch (Exception ex)
        {
            Warn($"Could not create the text: {ex.Message}", "Text");
        }
    }

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = ImportFilter, Multiselect = true, Title = "Import a picture or drawing" };
        if (dlg.ShowDialog(this) != true) return;
        var projects = dlg.FileNames.Where(f => f.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (projects.Count > 0) { LoadProject(projects[0]); return; }
        ImportFiles(dlg.FileNames, null);
    }

    /// <param name="dropPoint">Where to put the first item (mm), or null for the default spot.</param>
    private void ImportFiles(IReadOnlyList<string> files, Point? dropPoint)
    {
        var added = new List<DesignItem>();
        foreach (var file in files)
        {
            try
            {
                var item = DesignItem.FromFile(file);
                // Fit inside the bed if it is bigger.
                if (item.W > _profile.WidthMm || item.H > _profile.HeightMm)
                {
                    var k = Math.Min(_profile.WidthMm / item.W, _profile.HeightMm / item.H) * 0.9;
                    item.W *= k;
                    item.H *= k;
                }
                double offset = added.Count * 5;
                if (dropPoint is { } p)
                {
                    item.X = p.X - item.W / 2 + offset;
                    item.Y = p.Y - item.H / 2 - offset;
                }
                else
                {
                    item.X = 10 + offset;
                    item.Y = Math.Max(0, _profile.HeightMm - item.H - 10 - offset);
                }
                item.X = Math.Clamp(item.X, 0, Math.Max(0, _profile.WidthMm - item.W));
                item.Y = Math.Clamp(item.Y, 0, Math.Max(0, _profile.HeightMm - item.H));
                if (item.IsRaster) item.Raster.LineIntervalMm = Math.Max(0.08, _profile.SpotSizeMm);
                added.Add(item);
            }
            catch (Exception ex)
            {
                Warn($"Could not import {Path.GetFileName(file)}:\n{ex.Message}", "Import failed");
            }
        }
        if (added.Count == 0) return;
        AddItems(added, centerOnBed: false);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) &&
                    e.Data.GetData(DataFormats.FileDrop) is string[] files &&
                    files.Any(f => IsOpenable(f))
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files) return;
        var project = files.FirstOrDefault(f => f.EndsWith(ProjectFile.Extension, StringComparison.OrdinalIgnoreCase));
        if (project is not null) { LoadProject(project); return; }
        var importable = files.Where(f => ImportableExtensions.Contains(Path.GetExtension(f).ToLowerInvariant())).ToList();
        if (importable.Count == 0) return;
        Point? at = null;
        var pos = e.GetPosition(WorkCanvas);
        if (pos.X >= 0 && pos.Y >= 0 && pos.X <= WorkCanvas.ActualWidth && pos.Y <= WorkCanvas.ActualHeight)
            at = new Point(UnmapX(pos.X), UnmapY(pos.Y));
        ImportFiles(importable, at);
        Activate();
    }

    private static bool IsOpenable(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext == ProjectFile.Extension || ImportableExtensions.Contains(ext);
    }

    private void Trace_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is null || !_selected.IsRaster || _selected.Gray is null)
        {
            Info("Select an imported picture first, then trace it into outlines.", "Trace");
            return;
        }
        var source = _selected;
        var (gray, w, h) = source.RotatedGray();
        var dlg = new TraceDialog(gray, w, h) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result is null || dlg.Result.Count == 0) return;
        var traced = DesignItem.FromVector($"Traced {source.Name}", dlg.Result);
        // The outlines only cover the dark parts of the picture: size and place them by
        // their own pixel bounds so they sit exactly on top of the source image.
        double kx = source.W / dlg.TracedWidth, ky = source.H / dlg.TracedHeight; // mm per pixel
        var (min, max) = Polyline.BoundsOf(dlg.Result);                          // pixels, Y down
        traced.W = Math.Max(0.01, (max.X - min.X) * kx);
        traced.H = Math.Max(0.01, (max.Y - min.Y) * ky);
        traced.X = source.X + min.X * kx;
        traced.Y = source.Y + (dlg.TracedHeight - max.Y) * ky;
        traced.Mode = VectorMode.Fill;
        traced.Cut.SpeedMmMin = 3000;
        traced.Cut.PowerPercent = 40;
        AddItems(new[] { traced }, centerOnBed: false);
    }

    // ==================== Editing ====================

    private void Duplicate_Click(object sender, RoutedEventArgs e) => Duplicate();

    private void Duplicate()
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        var copies = sel.Select(i =>
        {
            var c = i.Clone();
            c.X = Math.Min(c.X + 5, Math.Max(0, _profile.WidthMm - c.W));
            c.Y = Math.Max(c.Y - 5, 0);
            return c;
        }).ToList();
        AddItems(copies, centerOnBed: false);
    }

    private void DeleteItem_Click(object sender, RoutedEventArgs e) => DeleteSelection();

    private void DeleteSelection()
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        PushUndo();
        foreach (var it in sel) _items.Remove(it);
        MarkDirty();
        Select(_items.LastOrDefault());
    }

    private void Rotate90_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        PushUndo();
        foreach (var it in sel)
        {
            var (cx, cy) = (it.X + it.W / 2, it.Y + it.H / 2);
            it.RotationDeg = (it.RotationDeg + 90) % 360;
            if (it.IsRaster)
            {
                // Raster sizes are stored as seen on the bed: swap and keep the center.
                (it.W, it.H) = (it.H, it.W);
                it.X = cx - it.W / 2;
                it.Y = cy - it.H / 2;
            }
        }
        MarkDirty();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void CenterItem_Click(object sender, RoutedEventArgs e) =>
        AlignSelection(b => (_profile.WidthMm - b.W) / 2, b => (_profile.HeightMm - b.H) / 2);

    private void Nudge(double dx, double dy)
    {
        var sel = SelectedItems;
        if (sel.Count == 0) return;
        // A burst of key presses is one undo step (PushUndo ends the burst for anything else).
        if (DateTime.UtcNow - _lastNudgeUndo > TimeSpan.FromSeconds(1.5)) PushUndo();
        _lastNudgeUndo = DateTime.UtcNow;
        foreach (var it in sel) { it.X += dx; it.Y += dy; }
        MarkDirty();
        SyncPropPanel();
        RedrawCanvas();
    }

    /// <summary>Moves the selection (as a group) so its bounding box lands where the functions say.</summary>
    private void AlignSelection(Func<(double X, double Y, double W, double H), double>? x,
                                Func<(double X, double Y, double W, double H), double>? y)
    {
        var sel = SelectedItems;
        if (sel.Count == 0 || JobCompiler.DesignBounds(sel) is not { } b) return;
        double dx = x is null ? 0 : x(b) - b.X;
        double dy = y is null ? 0 : y(b) - b.Y;
        if (Math.Abs(dx) < 1e-9 && Math.Abs(dy) < 1e-9) return;
        PushUndo();
        foreach (var it in sel) { it.X += dx; it.Y += dy; }
        MarkDirty();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void AlignL_Click(object sender, RoutedEventArgs e) => AlignSelection(_ => 0, null);
    private void AlignCX_Click(object sender, RoutedEventArgs e) => AlignSelection(b => (_profile.WidthMm - b.W) / 2, null);
    private void AlignR_Click(object sender, RoutedEventArgs e) => AlignSelection(b => _profile.WidthMm - b.W, null);
    private void AlignB_Click(object sender, RoutedEventArgs e) => AlignSelection(null, _ => 0);
    private void AlignCY_Click(object sender, RoutedEventArgs e) => AlignSelection(null, b => (_profile.HeightMm - b.H) / 2);
    private void AlignT_Click(object sender, RoutedEventArgs e) => AlignSelection(null, b => _profile.HeightMm - b.H);

    // ==================== Property panels ====================

    private static string Fmt(double v, string format = "0.##") => v.ToString(format, Inv);

    private void SyncPropPanel()
    {
        _suppressPropEvents = true;
        try
        {
            var it = _selected;
            PositionGroup.IsEnabled = LaserGroup.IsEnabled = it is not null;
            SelectionInfo.Visibility = _selection.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
            SelectionInfo.Text = $"{_selection.Count} items selected. These boxes show '{_selected?.Name}'. " +
                                 "Laser settings you change apply to every selected item of the same kind; dragging, arrow keys, align and delete act on all of them.";
            if (it is null)
            {
                ItemX.Text = ItemY.Text = ItemW.Text = ItemH.Text = ItemRot.Text = "";
                OpSpeed.Text = OpPower.Text = OpPasses.Text = OpInterval.Text = "";
                ModeRow.Visibility = Visibility.Visible;
                DitherRow.Visibility = OpInvert.Visibility = ImageAdjustExpander.Visibility = Visibility.Collapsed;
                return;
            }
            ItemX.Text = Fmt(it.X);
            ItemY.Text = Fmt(it.Y);
            ItemW.Text = Fmt(it.W);
            ItemH.Text = Fmt(it.H);
            ItemRot.Text = Fmt(it.RotationDeg, "0.#");

            bool raster = it.IsRaster;
            ModeRow.Visibility = raster ? Visibility.Collapsed : Visibility.Visible;
            DitherRow.Visibility = OpInvert.Visibility = ImageAdjustExpander.Visibility = raster ? Visibility.Visible : Visibility.Collapsed;
            OpInterval.IsEnabled = true;
            if (raster)
            {
                var r = it.Raster;
                OpSpeed.Text = Fmt(r.SpeedMmMin, "0");
                OpPower.Text = Fmt(r.MaxPowerPercent, "0.#");
                OpPasses.Text = r.Passes.ToString(Inv);
                OpInterval.Text = Fmt(r.LineIntervalMm, "0.###");
                OpDither.SelectedIndex = IndexFromDither(r.Dither);
                OpInvert.IsChecked = r.Invert;
                OpBrightness.Text = Fmt(r.Brightness, "0");
                OpContrast.Text = Fmt(r.Contrast, "0");
                OpGamma.Text = Fmt(r.Gamma, "0.##");
                OpMinPower.Text = Fmt(r.MinPowerPercent, "0.#");
                OpMinPower.IsEnabled = r.Dither == DitherMode.Grayscale;
            }
            else
            {
                var c = it.Cut;
                ModeCombo.SelectedIndex = (int)it.Mode;
                OpSpeed.Text = Fmt(c.SpeedMmMin, "0");
                OpPower.Text = Fmt(c.PowerPercent, "0.#");
                OpPasses.Text = c.Passes.ToString(Inv);
                OpInterval.Text = Fmt(c.FillIntervalMm, "0.###");
                OpInterval.IsEnabled = it.Mode != VectorMode.Line;
            }
        }
        finally { _suppressPropEvents = false; }
    }

    private static bool Same(double a, double b) => Math.Abs(a - b) < 1e-6;

    private void ItemProp_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPropEvents || _selected is null) return;
        var it = _selected;
        double x = ParseOr(ItemX.Text, it.X);
        double y = ParseOr(ItemY.Text, it.Y);
        double w = Math.Max(0.1, ParseOr(ItemW.Text, it.W));
        double h = Math.Max(0.1, ParseOr(ItemH.Text, it.H));
        double rot = ParseOr(ItemRot.Text, it.RotationDeg);
        if (KeepAspect.IsChecked == true)
        {
            if (!Same(w, it.W)) h = it.H * (w / it.W);
            else if (!Same(h, it.H)) w = it.W * (h / it.H);
        }
        if (it.IsRaster) rot = Math.Round(rot / 90) * 90 % 360;
        if (Same(x, it.X) && Same(y, it.Y) && Same(w, it.W) && Same(h, it.H) && Same(rot, it.RotationDeg))
        {
            SyncPropPanel(); // re-show the canonical values (e.g. after invalid input)
            return;
        }
        PushUndo();
        // Turning an image by an odd number of quarter turns swaps its width and height.
        if (it.IsRaster && ((int)Math.Round(rot / 90) - it.RotationSteps) % 2 != 0) (w, h) = (h, w);
        it.X = x;
        it.Y = y;
        it.W = w;
        it.H = h;
        it.RotationDeg = rot;
        MarkDirty();
        SyncPropPanel();
        RedrawCanvas();
    }

    private void OpProp_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressPropEvents || _selected is null) return;
        var it = _selected;
        var targets = SelectedItems.Where(i => i.IsRaster == it.IsRaster).ToList();
        if (!targets.Contains(it)) targets.Add(it);
        var changes = new List<Action<DesignItem>>();
        double maxFeed = _profile.MaxFeedXy;

        if (it.IsRaster)
        {
            var r = it.Raster;
            double speed = Math.Clamp(ParseOr(OpSpeed.Text, r.SpeedMmMin), 1, maxFeed);
            double power = Math.Clamp(ParseOr(OpPower.Text, r.MaxPowerPercent), 0, 100);
            int passes = (int)Math.Clamp(ParseOr(OpPasses.Text, r.Passes), 1, 50);
            double interval = Math.Clamp(ParseOr(OpInterval.Text, r.LineIntervalMm), 0.02, 5);
            var dither = OpDither.SelectedIndex >= 0 ? DitherFromIndex(OpDither.SelectedIndex) : r.Dither;
            bool invert = OpInvert.IsChecked == true;
            double brightness = Math.Clamp(ParseOr(OpBrightness.Text, r.Brightness), -100, 100);
            double contrast = Math.Clamp(ParseOr(OpContrast.Text, r.Contrast), -100, 100);
            double gamma = Math.Clamp(ParseOr(OpGamma.Text, r.Gamma), 0.1, 10);
            double minPower = Math.Clamp(ParseOr(OpMinPower.Text, r.MinPowerPercent), 0, 100);

            if (!Same(speed, r.SpeedMmMin)) changes.Add(i => i.Raster.SpeedMmMin = speed);
            if (!Same(power, r.MaxPowerPercent)) changes.Add(i => i.Raster.MaxPowerPercent = power);
            if (passes != r.Passes) changes.Add(i => i.Raster.Passes = passes);
            if (!Same(interval, r.LineIntervalMm)) changes.Add(i => i.Raster.LineIntervalMm = interval);
            if (dither != r.Dither) changes.Add(i => i.Raster.Dither = dither);
            if (invert != r.Invert) changes.Add(i => i.Raster.Invert = invert);
            if (!Same(brightness, r.Brightness)) changes.Add(i => i.Raster.Brightness = brightness);
            if (!Same(contrast, r.Contrast)) changes.Add(i => i.Raster.Contrast = contrast);
            if (!Same(gamma, r.Gamma)) changes.Add(i => i.Raster.Gamma = gamma);
            if (!Same(minPower, r.MinPowerPercent)) changes.Add(i => i.Raster.MinPowerPercent = minPower);
        }
        else
        {
            var c = it.Cut;
            var mode = ModeCombo.SelectedIndex >= 0 ? (VectorMode)ModeCombo.SelectedIndex : it.Mode;
            if (mode != VectorMode.Line && !targets.Any(t => t.HasClosedShapes))
            {
                Info("Fill engraves the inside of closed shapes, but this design only has open lines. It will stay in Line mode.", "Fill");
                mode = VectorMode.Line;
            }
            double speed = Math.Clamp(ParseOr(OpSpeed.Text, c.SpeedMmMin), 1, maxFeed);
            double power = Math.Clamp(ParseOr(OpPower.Text, c.PowerPercent), 0, 100);
            int passes = (int)Math.Clamp(ParseOr(OpPasses.Text, c.Passes), 1, 50);
            double interval = Math.Clamp(ParseOr(OpInterval.Text, c.FillIntervalMm), 0.02, 5);

            if (mode != it.Mode) changes.Add(i => i.Mode = mode == VectorMode.Line || i.HasClosedShapes ? mode : VectorMode.Line);
            if (!Same(speed, c.SpeedMmMin)) changes.Add(i => i.Cut.SpeedMmMin = speed);
            if (!Same(power, c.PowerPercent)) changes.Add(i => i.Cut.PowerPercent = power);
            if (passes != c.Passes) changes.Add(i => i.Cut.Passes = passes);
            if (!Same(interval, c.FillIntervalMm)) changes.Add(i => i.Cut.FillIntervalMm = interval);
        }

        if (changes.Count == 0)
        {
            SyncPropPanel();
            return;
        }
        PushUndo();
        foreach (var t in targets)
        {
            foreach (var change in changes) change(t);
            t.PresetName = null; // hand-tuned now
        }
        MarkDirty();
        SyncItemsList();
        SyncPropPanel();
        RefreshPresetList();
        RedrawCanvas();
    }

    // ==================== Material presets ====================

    private sealed record PresetPlaceholder(string Text)
    {
        public override string ToString() => Text;
    }

    /// <summary>Lists the presets that suit the selected item (engraving presets for images and fills).</summary>
    private void RefreshPresetList()
    {
        _suppressPropEvents = true;
        try
        {
            PresetCombo.Items.Clear();
            PresetCombo.Items.Add(new PresetPlaceholder("Choose material…"));
            var all = MaterialPresets.ForDiode10W.Concat(_settings.CustomPresets);
            IEnumerable<MaterialPreset> suitable = _selected switch
            {
                null => all,
                { IsRaster: true } => all.Where(p => p.Operation == OperationKind.Engrave),
                { Mode: VectorMode.Line } => all.Where(p => p.Operation != OperationKind.Engrave),
                _ => all.Where(p => p.Operation == OperationKind.Engrave),
            };
            int selectIndex = 0;
            foreach (var p in suitable)
            {
                PresetCombo.Items.Add(p);
                if (_selected?.PresetName == p.Material) selectIndex = PresetCombo.Items.Count - 1;
            }
            PresetCombo.SelectedIndex = selectIndex;
            PresetCombo.ToolTip = PresetCombo.SelectedItem is MaterialPreset mp && mp.Notes.Length > 0
                ? mp.Notes
                : "Material presets for a 10W diode laser — starting points. Always test on a scrap piece first (Tools → Material test).";
        }
        finally { _suppressPropEvents = false; }
    }

    private void Preset_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPropEvents || PresetCombo.SelectedItem is not MaterialPreset preset || _selected is null) return;
        var targets = SelectedItems.Where(i => preset.Operation == OperationKind.Engrave || !i.IsRaster).ToList();
        if (targets.Count == 0) return;
        PushUndo();
        foreach (var it in targets)
        {
            if (it.IsRaster)
            {
                it.Raster.SpeedMmMin = preset.SpeedMmMin;
                it.Raster.MaxPowerPercent = preset.PowerPercent;
                it.Raster.LineIntervalMm = preset.IntervalMm;
                it.Raster.Passes = Math.Max(1, preset.Passes);
            }
            else
            {
                var mode = it.Mode;
                it.Cut = preset.ToCutSettings();
                // Engraving presets fill shapes; cutting and scoring follow the outline.
                it.Mode = preset.Operation == OperationKind.Engrave
                    ? (it.HasClosedShapes ? (mode == VectorMode.FillAndLine ? mode : VectorMode.Fill) : VectorMode.Line)
                    : VectorMode.Line;
            }
            it.PresetName = preset.Material;
        }
        MarkDirty();
        SyncItemsList();
        SyncPropPanel();
        RefreshPresetList();
        RedrawCanvas();
        if (preset.Notes.Length > 0)
            AppendConsole(new LaserPilot.Core.Comm.LogEntry { Direction = LaserPilot.Core.Comm.LogDirection.Info, Text = $"{preset.Material}: {preset.Notes}" });
    }

    private void SavePreset_Click(object sender, RoutedEventArgs e)
    {
        if (_selected is not { } it) { Info("Select an item whose settings you want to save as a preset."); return; }
        var name = InputDialog.Ask(this, "Save material preset",
            "Name this preset (for example \"Walnut 3mm engrave\"). It will appear in the Material list on this computer.",
            it.PresetName ?? "");
        if (string.IsNullOrWhiteSpace(name)) return;
        name = name.Trim();
        MaterialPreset preset = it.IsRaster
            ? new MaterialPreset(name, OperationKind.Engrave, it.Raster.SpeedMmMin, it.Raster.MaxPowerPercent, it.Raster.Passes,
                "Your own preset.", it.Raster.LineIntervalMm) { BuiltIn = false }
            : new MaterialPreset(name, it.Mode == VectorMode.Line ? OperationKind.Cut : OperationKind.Engrave,
                it.Cut.SpeedMmMin, it.Cut.PowerPercent, it.Cut.Passes, "Your own preset.", it.Cut.FillIntervalMm) { BuiltIn = false };
        _settings.CustomPresets.RemoveAll(p => p.Material.Equals(name, StringComparison.OrdinalIgnoreCase));
        _settings.CustomPresets.Add(preset);
        _settings.Save();
        it.PresetName = name;
        RefreshPresetList();
        Info($"Saved \"{name}\". It's marked with ★ in the Material list.", "Preset saved");
    }

    // ==================== Projects ====================

    private void NewProject_Click(object sender, RoutedEventArgs e) => NewProject();
    private void OpenProject_Click(object sender, RoutedEventArgs e) => OpenProject();
    private void SaveProject_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: false);
    private void SaveProjectAs_Click(object sender, RoutedEventArgs e) => SaveProject(saveAs: true);

    private bool ConfirmDiscardChanges()
    {
        if (!_dirty || _items.Count == 0) return true;
        var r = MessageBox.Show(this, "Save the changes to your design first?", "LaserPilot",
            MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
        if (r == MessageBoxResult.Cancel) return false;
        return r != MessageBoxResult.Yes || SaveProject(saveAs: false);
    }

    private void NewProject()
    {
        if (!ConfirmDiscardChanges()) return;
        _items = new List<DesignItem>();
        _projectPath = null;
        _undo.Clear();
        _dirty = false;
        Select(null);
        UpdateTitle();
        UpdateUndoMenu();
    }

    private void OpenProject()
    {
        var dlg = new OpenFileDialog { Filter = ImportFilter, Title = "Open a project (or import a design)" };
        if (dlg.ShowDialog(this) == true) OpenAnyFile(dlg.FileName);
    }

    private void OpenAnyFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ProjectFile.Extension) LoadProject(path);
        else if (ext is ".gcode" or ".nc" or ".gc" or ".ngc") RunGcodeFile(path);
        else if (ImportableExtensions.Contains(ext)) ImportFiles(new[] { path }, null);
        else Warn($"LaserPilot can't open {Path.GetFileName(path)}.");
    }

    private void LoadProject(string path)
    {
        if (!ConfirmDiscardChanges()) return;
        try
        {
            var doc = ProjectFile.Load(path, out var items);
            if (doc.ProfileId is { } pid && pid != _profile.Id)
            {
                var idx = MachineProfile_IndexOf(pid);
                if (idx >= 0) ProfileCombo.SelectedIndex = idx;
            }
            ApplyJobOptions(doc.Job);
            _items = items;
            _projectPath = path;
            _undo.Clear();
            _dirty = false;
            _settings.AddRecentFile(path);
            _settings.Save();
            BuildRecentMenu();
            Select(null);
            UpdateTitle();
            UpdateUndoMenu();
        }
        catch (Exception ex)
        {
            Warn($"Could not open {Path.GetFileName(path)}:\n{ex.Message}", "Open project");
        }
    }

    private static int MachineProfile_IndexOf(string id) =>
        LaserPilot.Core.Machines.MachineProfile.BuiltIn.ToList().FindIndex(p => p.Id == id);

    private bool SaveProject(bool saveAs)
    {
        var path = _projectPath;
        if (path is null || saveAs)
        {
            var dlg = new SaveFileDialog
            {
                Filter = ProjectFile.Filter,
                FileName = path is null ? "My design" : Path.GetFileNameWithoutExtension(path),
                Title = "Save project",
            };
            if (dlg.ShowDialog(this) != true) return false;
            path = dlg.FileName;
        }
        try
        {
            ProjectFile.Save(path, _items, _profile.Id, CurrentJobOptions());
            _projectPath = path;
            _dirty = false;
            _settings.AddRecentFile(path);
            _settings.Save();
            BuildRecentMenu();
            UpdateTitle();
            return true;
        }
        catch (Exception ex)
        {
            Warn($"Could not save the project:\n{ex.Message}", "Save project");
            return false;
        }
    }

    private void BuildRecentMenu()
    {
        MenuRecent.Items.Clear();
        foreach (var path in _settings.RecentFiles.Where(File.Exists).Take(10))
        {
            var mi = new MenuItem { Header = Path.GetFileName(path).Replace("_", "__"), ToolTip = path };
            mi.Click += (_, _) => OpenAnyFile(path);
            MenuRecent.Items.Add(mi);
        }
        if (MenuRecent.Items.Count == 0) MenuRecent.Items.Add(new MenuItem { Header = "(nothing yet)", IsEnabled = false });
    }
}
