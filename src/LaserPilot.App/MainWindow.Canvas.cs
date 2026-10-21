using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using LaserPilot.App.Models;
using LaserPilot.Core.Comm;
using LaserPilot.Core.Gcode;

namespace LaserPilot.App;

public partial class MainWindow
{
    // View state
    private double _zoom = 1.2;              // px per mm
    private double _fitZoom = 1.2;
    private double _panX = 40, _panY = 20;
    private bool _userMovedView; // auto-fit the bed until the user zooms or pans

    // Mouse interaction
    private enum DragMode { None, Pan, Move, Resize, RubberBand }
    private DragMode _drag;
    private MouseButton _dragButton;
    private Point _lastMouse, _mouseDownPos;
    private bool _dragMoved;
    private DesignSnapshot? _dragSnapshot;
    private int _resizeCorner;                                    // 0 = bottom-left, 1 = bottom-right, 2 = top-right, 3 = top-left
    private Point _resizeFixed;                                   // opposite corner (mm)
    private (double X, double Y, double W, double H, double Rot) _resizeOrig;
    private (double X, double Y, double W, double H) _resizeOrigBounds;
    private Rectangle? _rubberBand;

    // Overlays that move without a full redraw
    private Ellipse? _laserMarker;
    private Line? _laserCrossH, _laserCrossV;
    private Rectangle? _jobGhost;

    private readonly Dictionary<DesignItem, (int Steps, BitmapSource Bitmap)> _rotatedCache = new();
    private readonly Dictionary<DesignItem, (string Key, BitmapSource? Bitmap)> _engraveCache = new();

    private const double HandleSize = 9;

    private double MapX(double mmX) => _panX + mmX * _zoom;
    private double MapY(double mmY) => _panY + (_profile.HeightMm - mmY) * _zoom;
    private double UnmapX(double px) => (px - _panX) / _zoom;
    private double UnmapY(double py) => _profile.HeightMm - (py - _panY) / _zoom;

    // ==================== View ====================

    private void FitView()
    {
        if (WorkCanvas.ActualWidth < 50 || WorkCanvas.ActualHeight < 50) return;
        // Room for the zoom toolbar (top), the Y ruler (left) and the X ruler + hint (bottom).
        // The hint wraps onto a second line in narrow windows.
        const double left = 40, right = 16, top = 52;
        double bottom = 38 + (CanvasHint.IsVisible ? Math.Max(16, CanvasHint.ActualHeight) + 10 : 0);
        double availW = Math.Max(50, WorkCanvas.ActualWidth - left - right);
        double availH = Math.Max(50, WorkCanvas.ActualHeight - top - bottom);
        _zoom = _fitZoom = Math.Max(0.05, Math.Min(availW / _profile.WidthMm, availH / _profile.HeightMm));
        _panX = left + (availW - _profile.WidthMm * _zoom) / 2;
        _panY = top + (availH - _profile.HeightMm * _zoom) / 2;
        _userMovedView = false;
        UpdateZoomText();
    }

    private void UpdateZoomText() => ZoomText.Text = $"{_zoom / _fitZoom * 100:0}%";

    private void Canvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_userMovedView) FitView();
        RedrawCanvas();
    }

    private void ZoomAt(Point screen, double factor)
    {
        _userMovedView = true;
        var mmX = UnmapX(screen.X);
        var mmY = UnmapY(screen.Y);
        _zoom = Math.Clamp(_zoom * factor, _fitZoom * 0.25, 60);
        // keep the point under the cursor fixed
        _panX = screen.X - mmX * _zoom;
        _panY = screen.Y - (_profile.HeightMm - mmY) * _zoom;
        UpdateZoomText();
        RedrawCanvas();
    }

    /// <summary>Zoom buttons zoom toward the selection when there is one, else the middle of the view.</summary>
    private Point ZoomCenter =>
        _selection.Count > 0 && JobCompiler.DesignBounds(_selection) is { } b
            ? new Point(MapX(b.X + b.W / 2), MapY(b.Y + b.H / 2))
            : new Point(WorkCanvas.ActualWidth / 2, WorkCanvas.ActualHeight / 2);

    private void ZoomFit_Click(object sender, RoutedEventArgs e) { FitView(); RedrawCanvas(); }

    private void ZoomIn_Click(object sender, RoutedEventArgs e)
    {
        var target = ZoomCenter;
        ZoomAt(target, 1.25);
        // Bring the zoom target toward the middle of the view.
        _panX += (WorkCanvas.ActualWidth / 2 - target.X) * 0.5;
        _panY += (WorkCanvas.ActualHeight / 2 - target.Y) * 0.5;
        RedrawCanvas();
    }

    private void ZoomOut_Click(object sender, RoutedEventArgs e) => ZoomAt(ZoomCenter, 1 / 1.25);

    private void Canvas_MouseWheel(object sender, MouseWheelEventArgs e) =>
        ZoomAt(e.GetPosition(WorkCanvas), e.Delta > 0 ? 1.15 : 1 / 1.15);

    private void PreviewToggle_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowEngravePreview = PreviewToggle.IsChecked == true;
        _settings.Save();
        RedrawCanvas();
    }

    // ==================== Drawing ====================

    private void RedrawCanvas()
    {
        WorkCanvas.Children.Clear();
        _laserMarker = null;
        _laserCrossH = _laserCrossV = null;
        _jobGhost = null;
        _rubberBand = null;

        DrawBed();

        // Forget cached previews of deleted items.
        foreach (var gone in _rotatedCache.Keys.Where(k => !_items.Contains(k)).ToList()) _rotatedCache.Remove(gone);
        foreach (var gone in _engraveCache.Keys.Where(k => !_items.Contains(k)).ToList()) _engraveCache.Remove(gone);

        var accent = Res("AccentBrush");
        foreach (var item in _items)
        {
            bool selected = _selection.Contains(item);
            if (item.IsRaster && item.Preview is not null)
            {
                var img = new Image
                {
                    Source = RasterPreview(item),
                    Width = Math.Max(1, item.W * _zoom),
                    Height = Math.Max(1, item.H * _zoom),
                    Stretch = Stretch.Fill,
                    Opacity = item.Enabled ? 0.95 : 0.3,
                };
                // Crisp pixels for the dot preview; smooth scaling for the plain picture.
                RenderOptions.SetBitmapScalingMode(img, PreviewToggle.IsChecked == true ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
                Canvas.SetLeft(img, MapX(item.X));
                Canvas.SetTop(img, MapY(item.Y + item.H));
                WorkCanvas.Children.Add(img);
            }
            else
            {
                bool filled = item.Mode != VectorMode.Line && item.HasClosedShapes;
                var geo = new StreamGeometry { FillRule = FillRule.EvenOdd };
                using (var ctx = geo.Open())
                {
                    foreach (var poly in item.TransformedShapes())
                    {
                        if (poly.Points.Count < 2) continue;
                        ctx.BeginFigure(new Point(MapX(poly.Points[0].X), MapY(poly.Points[0].Y)), filled && FillGenerator.IsFillable(poly), poly.Closed);
                        for (int i = 1; i < poly.Points.Count; i++)
                            ctx.LineTo(new Point(MapX(poly.Points[i].X), MapY(poly.Points[i].Y)), true, false);
                    }
                }
                geo.Freeze();
                var stroke = selected ? accent : item.Enabled ? Brushes.Gainsboro : Brushes.DimGray;
                var path = new Path
                {
                    Data = geo,
                    Stroke = stroke,
                    StrokeThickness = selected ? 1.6 : 1.3,
                    Fill = filled ? new SolidColorBrush(Color.FromArgb(item.Enabled ? (byte)0x70 : (byte)0x30, 0xD8, 0xC8, 0xA8)) : null,
                };
                if (!item.Enabled) path.StrokeDashArray = new DoubleCollection { 3, 3 };
                WorkCanvas.Children.Add(path);
            }

            if (selected)
            {
                var (bx, by, bw, bh) = item.WorkBounds();
                var sel = new Rectangle
                {
                    Width = Math.Max(2, bw * _zoom),
                    Height = Math.Max(2, bh * _zoom),
                    Stroke = accent,
                    StrokeThickness = 1,
                    StrokeDashArray = new DoubleCollection { 4, 3 },
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(sel, MapX(bx));
                Canvas.SetTop(sel, MapY(by + bh));
                WorkCanvas.Children.Add(sel);
            }
        }

        // Resize handles on the primary selection (single selection only, to keep it predictable).
        if (_selected is not null && _selection.Count == 1)
            foreach (var (corner, pt) in HandlePoints(_selected))
                WorkCanvas.Children.Add(MakeHandle(pt, corner));

        RedrawLaserMarker();
    }

    private void DrawBed()
    {
        var bed = new Rectangle
        {
            Width = _profile.WidthMm * _zoom,
            Height = _profile.HeightMm * _zoom,
            Stroke = Res("PanelBorderBrush"),
            StrokeThickness = 1.5,
            Fill = new SolidColorBrush(Color.FromRgb(0x1B, 0x1C, 0x21)),
        };
        Canvas.SetLeft(bed, MapX(0));
        Canvas.SetTop(bed, MapY(_profile.HeightMm));
        WorkCanvas.Children.Add(bed);

        var minor = new SolidColorBrush(Color.FromRgb(0x22, 0x23, 0x29));
        var major = new SolidColorBrush(Color.FromRgb(0x2E, 0x2F, 0x38));
        var labelBrush = Res("SubtleTextBrush");
        bool showMinor = _zoom * 10 >= 9;
        for (double g = 10; g < _profile.WidthMm; g += 10)
        {
            bool isMajor = g % 50 == 0;
            if (!isMajor && !showMinor) continue;
            WorkCanvas.Children.Add(new Line { X1 = MapX(g), Y1 = MapY(0), X2 = MapX(g), Y2 = MapY(_profile.HeightMm), Stroke = isMajor ? major : minor, StrokeThickness = 1 });
        }
        for (double g = 10; g < _profile.HeightMm; g += 10)
        {
            bool isMajor = g % 50 == 0;
            if (!isMajor && !showMinor) continue;
            WorkCanvas.Children.Add(new Line { X1 = MapX(0), Y1 = MapY(g), X2 = MapX(_profile.WidthMm), Y2 = MapY(g), Stroke = isMajor ? major : minor, StrokeThickness = 1 });
        }

        // Rulers (every 50 mm) along the front and left edges.
        double labelStep = _zoom * 50 >= 30 ? 50 : 100;
        for (double g = 0; g <= _profile.WidthMm + 0.01; g += labelStep)
            AddLabel($"{g:0}", MapX(g) - 8, MapY(0) + 4, labelBrush);
        for (double g = labelStep; g <= _profile.HeightMm + 0.01; g += labelStep)
            AddLabel($"{g:0}", MapX(0) - 28, MapY(g) - 7, labelBrush);

        var origin = new Ellipse { Width = 10, Height = 10, Fill = Res("OkBrush"), IsHitTestVisible = false };
        Canvas.SetLeft(origin, MapX(0) - 5);
        Canvas.SetTop(origin, MapY(0) - 5);
        WorkCanvas.Children.Add(origin);
        AddLabel("0,0 front-left", MapX(0) + 10, MapY(0) + 18, labelBrush);
        AddLabel("FRONT (you)", MapX(_profile.WidthMm / 2) - 30, MapY(0) + 18, labelBrush);
    }

    private void AddLabel(string text, double left, double top, Brush brush)
    {
        var tb = new TextBlock { Text = text, FontSize = 10, Foreground = brush, IsHitTestVisible = false };
        Canvas.SetLeft(tb, left);
        Canvas.SetTop(tb, top);
        WorkCanvas.Children.Add(tb);
    }

    /// <summary>Image as shown on the bed: plain gray, or the dithered dots the laser will burn.</summary>
    private BitmapSource RasterPreview(DesignItem item)
    {
        if (PreviewToggle.IsChecked == true && _drag != DragMode.Resize && EngravingPreview(item) is { } burned)
            return burned;
        int steps = item.RotationSteps;
        if (steps == 0) return item.Preview!;
        if (_rotatedCache.TryGetValue(item, out var c) && c.Steps == steps) return c.Bitmap;
        // Workspace-CCW rotation == screen-CCW; WPF's rotation is clockwise, so invert.
        var tb = new TransformedBitmap(item.Preview, new RotateTransform(steps switch { 1 => 270, 2 => 180, _ => 90 }));
        tb.Freeze();
        _rotatedCache[item] = (steps, tb);
        return tb;
    }

    private BitmapSource? EngravingPreview(DesignItem item)
    {
        var r = item.Raster;
        var key = string.Join("|", r.Dither, r.Invert, r.Brightness, r.Contrast, r.Gamma, r.LineIntervalMm,
            Math.Round(item.W, 1), Math.Round(item.H, 1), item.RotationSteps);
        if (_engraveCache.TryGetValue(item, out var c) && c.Key == key) return c.Bitmap;
        BitmapSource? bmp = null;
        try
        {
            var (gray, w, h) = item.RotatedGray();
            var (outGray, ow, oh) = RasterProcessor.Prepare(gray, w, h, item.W, item.H, r);
            if ((long)ow * oh <= 16_000_000)
            {
                bmp = BitmapSource.Create(ow, oh, 96, 96, PixelFormats.Gray8, null, outGray, ow);
                bmp.Freeze();
            }
        }
        catch
        {
            bmp = null; // too large at this resolution: fall back to the plain picture
        }
        _engraveCache[item] = (key, bmp);
        return bmp;
    }

    private IEnumerable<(int Corner, Point Px)> HandlePoints(DesignItem item)
    {
        var (bx, by, bw, bh) = item.WorkBounds();
        yield return (0, new Point(MapX(bx), MapY(by)));
        yield return (1, new Point(MapX(bx + bw), MapY(by)));
        yield return (2, new Point(MapX(bx + bw), MapY(by + bh)));
        yield return (3, new Point(MapX(bx), MapY(by + bh)));
    }

    private Rectangle MakeHandle(Point center, int corner)
    {
        var r = new Rectangle
        {
            Width = HandleSize,
            Height = HandleSize,
            Fill = Brushes.White,
            Stroke = Res("AccentBrush"),
            StrokeThickness = 1.5,
            Cursor = corner is 0 or 2 ? Cursors.SizeNESW : Cursors.SizeNWSE,
            ToolTip = "Drag to resize (hold Shift to ignore 'Lock aspect')",
        };
        Canvas.SetLeft(r, center.X - HandleSize / 2);
        Canvas.SetTop(r, center.Y - HandleSize / 2);
        return r;
    }

    private void RedrawLaserMarker()
    {
        var s = _grbl.Status;
        bool show = _grbl.IsConnected && s.State is not (MachineState.Disconnected or MachineState.Unknown);
        if (!show)
        {
            if (_laserMarker is not null) WorkCanvas.Children.Remove(_laserMarker);
            if (_laserCrossH is not null) WorkCanvas.Children.Remove(_laserCrossH);
            if (_laserCrossV is not null) WorkCanvas.Children.Remove(_laserCrossV);
            if (_jobGhost is not null) WorkCanvas.Children.Remove(_jobGhost);
            _laserMarker = null;
            _laserCrossH = _laserCrossV = null;
            _jobGhost = null;
            return;
        }
        var danger = Res("DangerBrush");
        if (_laserMarker is null)
        {
            _laserMarker = new Ellipse { Width = 13, Height = 13, Stroke = danger, StrokeThickness = 2, IsHitTestVisible = false, ToolTip = "Laser head" };
            _laserCrossH = new Line { Stroke = danger, StrokeThickness = 1, IsHitTestVisible = false };
            _laserCrossV = new Line { Stroke = danger, StrokeThickness = 1, IsHitTestVisible = false };
            WorkCanvas.Children.Add(_laserMarker);
            WorkCanvas.Children.Add(_laserCrossH);
            WorkCanvas.Children.Add(_laserCrossV);
        }
        double x = MapX(s.WX), y = MapY(s.WY);
        Canvas.SetLeft(_laserMarker, x - 6.5);
        Canvas.SetTop(_laserMarker, y - 6.5);
        (_laserCrossH!.X1, _laserCrossH.Y1, _laserCrossH.X2, _laserCrossH.Y2) = (x - 10, y, x + 10, y);
        (_laserCrossV!.X1, _laserCrossV.Y1, _laserCrossV.X2, _laserCrossV.Y2) = (x, y - 10, x, y + 10);

        // Where a "start from current laser position" job will land.
        var opts = CurrentJobOptions();
        var enabled = _items.Where(i => i.Enabled).ToList();
        if (opts.Origin == JobOrigin.CurrentPosition && enabled.Count > 0 && JobCompiler.DesignBounds(enabled) is { } b)
        {
            var offset = new LaserPilot.Core.Geometry.Vec2(s.WX, s.WY) - JobCompiler.AnchorPoint(b, opts.Anchor);
            if (_jobGhost is null)
            {
                _jobGhost = new Rectangle
                {
                    Stroke = Res("WarnBrush"),
                    StrokeThickness = 1.5,
                    StrokeDashArray = new DoubleCollection { 6, 4 },
                    IsHitTestVisible = false,
                    ToolTip = "Where the job will run (starting from the laser's current position)",
                };
                WorkCanvas.Children.Add(_jobGhost);
            }
            _jobGhost.Width = Math.Max(2, b.W * _zoom);
            _jobGhost.Height = Math.Max(2, b.H * _zoom);
            Canvas.SetLeft(_jobGhost, MapX(b.X + offset.X));
            Canvas.SetTop(_jobGhost, MapY(b.Y + offset.Y + b.H));
        }
        else if (_jobGhost is not null)
        {
            WorkCanvas.Children.Remove(_jobGhost);
            _jobGhost = null;
        }
    }

    // ==================== Mouse ====================

    private DesignItem? HitTest(double mmX, double mmY)
    {
        for (int i = _items.Count - 1; i >= 0; i--) // topmost = last
        {
            var (bx, by, bw, bh) = _items[i].WorkBounds();
            double pad = 3 / _zoom; // a few pixels of slack for thin lines
            if (mmX >= bx - pad && mmX <= bx + bw + pad && mmY >= by - pad && mmY <= by + bh + pad) return _items[i];
        }
        return null;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        WorkCanvas.Focus();
        var pos = e.GetPosition(WorkCanvas);
        _lastMouse = _mouseDownPos = pos;
        _dragMoved = false;
        _dragButton = e.ChangedButton;

        if (e.ChangedButton is MouseButton.Middle or MouseButton.Right)
        {
            _drag = DragMode.Pan;
            WorkCanvas.CaptureMouse();
            return;
        }
        if (e.ChangedButton != MouseButton.Left) return;

        // Resize handle?
        if (_selected is not null && _selection.Count == 1)
        {
            foreach (var (corner, pt) in HandlePoints(_selected))
            {
                if (Math.Abs(pos.X - pt.X) <= HandleSize && Math.Abs(pos.Y - pt.Y) <= HandleSize)
                {
                    var b = _selected.WorkBounds();
                    _resizeCorner = corner;
                    _resizeOrigBounds = b;
                    _resizeOrig = (_selected.X, _selected.Y, _selected.W, _selected.H, _selected.RotationDeg);
                    _resizeFixed = corner switch
                    {
                        0 => new Point(b.x + b.w, b.y + b.h),
                        1 => new Point(b.x, b.y + b.h),
                        2 => new Point(b.x, b.y),
                        _ => new Point(b.x + b.w, b.y),
                    };
                    _drag = DragMode.Resize;
                    _dragSnapshot = Capture();
                    WorkCanvas.CaptureMouse();
                    return;
                }
            }
        }

        var mmX = UnmapX(pos.X);
        var mmY = UnmapY(pos.Y);
        var hit = HitTest(mmX, mmY);
        bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
        if (hit is not null)
        {
            if (additive)
            {
                var next = _selection.ToList();
                if (!next.Remove(hit)) next.Add(hit);
                SetSelection(next, next.Contains(hit) ? hit : next.LastOrDefault());
                return;
            }
            if (!_selection.Contains(hit)) Select(hit);
            else if (_selected != hit)
            {
                _selected = hit;
                SyncPropPanel();
                RefreshPresetList();
                RedrawCanvas();
            }
            _drag = DragMode.Move;
            _dragSnapshot = Capture();
            WorkCanvas.CaptureMouse();
        }
        else
        {
            if (!additive && _selection.Count > 0) Select(null);
            _drag = DragMode.RubberBand;
            _rubberBand = new Rectangle
            {
                Stroke = Res("AccentBrush"),
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 3, 2 },
                Fill = new SolidColorBrush(Color.FromArgb(0x22, 0x4F, 0x9C, 0xF7)),
                IsHitTestVisible = false,
            };
            WorkCanvas.Children.Add(_rubberBand);
            WorkCanvas.CaptureMouse();
        }
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        var pos = e.GetPosition(WorkCanvas);
        double mmX = UnmapX(pos.X), mmY = UnmapY(pos.Y);
        MouseText.Text = mmX >= 0 && mmY >= 0 && mmX <= _profile.WidthMm && mmY <= _profile.HeightMm
            ? $"Mouse X {mmX,6:0.0}  Y {mmY,6:0.0}"
            : "";

        // The button was released somewhere we didn't hear about: stop dragging.
        bool held = _drag == DragMode.Pan
            ? e.MiddleButton == MouseButtonState.Pressed || e.RightButton == MouseButtonState.Pressed
            : e.LeftButton == MouseButtonState.Pressed;
        if (_drag != DragMode.None && !held)
        {
            FinishDrag(pos, released: false);
            return;
        }

        if (!_dragMoved && (pos - _mouseDownPos).Length >= 3) _dragMoved = true;
        var dx = pos.X - _lastMouse.X;
        var dy = pos.Y - _lastMouse.Y;

        switch (_drag)
        {
            case DragMode.Pan:
                if (_dragMoved) _userMovedView = true;
                _panX += dx;
                _panY += dy;
                _lastMouse = pos;
                RedrawCanvas();
                break;

            case DragMode.Move when _dragMoved:
                foreach (var it in _selection)
                {
                    it.X += dx / _zoom;
                    it.Y -= dy / _zoom;
                }
                _lastMouse = pos;
                SyncPropPanel();
                RedrawCanvas();
                break;

            case DragMode.Resize when _dragMoved && _selected is not null:
                ResizeTo(_selected, new Point(mmX, mmY), (Keyboard.Modifiers & ModifierKeys.Shift) != 0);
                _lastMouse = pos;
                SyncPropPanel();
                RedrawCanvas();
                break;

            case DragMode.RubberBand when _rubberBand is not null:
                Canvas.SetLeft(_rubberBand, Math.Min(pos.X, _mouseDownPos.X));
                Canvas.SetTop(_rubberBand, Math.Min(pos.Y, _mouseDownPos.Y));
                _rubberBand.Width = Math.Abs(pos.X - _mouseDownPos.X);
                _rubberBand.Height = Math.Abs(pos.Y - _mouseDownPos.Y);
                break;
        }
    }

    private void ResizeTo(DesignItem it, Point mouseMm, bool freeAspect)
    {
        var o = _resizeOrigBounds;
        double newW = Math.Max(0.5, Math.Abs(mouseMm.X - _resizeFixed.X));
        double newH = Math.Max(0.5, Math.Abs(mouseMm.Y - _resizeFixed.Y));
        double sx = newW / Math.Max(1e-6, o.W);
        double sy = newH / Math.Max(1e-6, o.H);
        bool lockAspect = (KeepAspect.IsChecked == true) != freeAspect;
        double quarter = ((_resizeOrig.Rot % 180) + 180) % 180;
        bool axisAligned = it.IsRaster || quarter < 1e-6 || Math.Abs(quarter - 90) < 1e-6;
        if (lockAspect || !axisAligned)
        {
            double s = Math.Max(sx, sy);
            sx = sy = s;
        }
        // Unrotated W/H: a quarter-turned vector swaps which screen axis they follow.
        bool swapped = !it.IsRaster && Math.Abs(quarter - 90) < 1e-6;
        it.W = _resizeOrig.W * (swapped ? sy : sx);
        it.H = _resizeOrig.H * (swapped ? sx : sy);
        it.X = _resizeOrig.X;
        it.Y = _resizeOrig.Y;
        // Keep the opposite corner where it was.
        var (bx, by, bw, bh) = it.WorkBounds();
        var anchor = _resizeCorner switch
        {
            0 => new Point(bx + bw, by + bh),
            1 => new Point(bx, by + bh),
            2 => new Point(bx, by),
            _ => new Point(bx + bw, by),
        };
        it.X += _resizeFixed.X - anchor.X;
        it.Y += _resizeFixed.Y - anchor.Y;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e) => FinishDrag(e.GetPosition(WorkCanvas), released: true);

    // Alt+Tab or a message box mid-drag takes the mouse away: end the drag as if released.
    private void Canvas_LostMouseCapture(object sender, MouseEventArgs e)
    {
        if (_drag != DragMode.None) FinishDrag(e.GetPosition(WorkCanvas), released: false);
    }

    private void FinishDrag(Point pos, bool released)
    {
        var mode = _drag;
        _drag = DragMode.None; // before releasing capture: LostMouseCapture must see "no drag"
        if (WorkCanvas.IsMouseCaptured) WorkCanvas.ReleaseMouseCapture();
        if (!released && mode == DragMode.Pan) _dragMoved = true; // never open the menu after an interruption

        switch (mode)
        {
            case DragMode.Move or DragMode.Resize when _dragMoved && _dragSnapshot is not null:
                PushUndo(_dragSnapshot);
                MarkDirty();
                RedrawCanvas();
                break;

            case DragMode.RubberBand:
                if (_rubberBand is not null) WorkCanvas.Children.Remove(_rubberBand);
                _rubberBand = null;
                if (_dragMoved)
                {
                    double x1 = UnmapX(Math.Min(pos.X, _mouseDownPos.X)), x2 = UnmapX(Math.Max(pos.X, _mouseDownPos.X));
                    double y1 = UnmapY(Math.Max(pos.Y, _mouseDownPos.Y)), y2 = UnmapY(Math.Min(pos.Y, _mouseDownPos.Y));
                    var inside = _items.Where(it =>
                    {
                        var (bx, by, bw, bh) = it.WorkBounds();
                        return bx < x2 && bx + bw > x1 && by < y2 && by + bh > y1;
                    }).ToList();
                    bool additive = (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0;
                    var next = additive ? _selection.Union(inside).ToList() : inside;
                    SetSelection(next, inside.LastOrDefault() ?? _selected);
                }
                break;

            case DragMode.Pan when _dragButton == MouseButton.Right && !_dragMoved:
                ShowCanvasMenu(new Point(UnmapX(pos.X), UnmapY(pos.Y)));
                break;
        }
        _dragSnapshot = null;
    }

    private void Canvas_MouseLeave(object sender, MouseEventArgs e) => MouseText.Text = "";

    private void ShowCanvasMenu(Point mm)
    {
        var menu = new ContextMenu();
        var hit = HitTest(mm.X, mm.Y);
        if (hit is not null && !_selection.Contains(hit)) Select(hit);

        bool onBed = mm.X >= 0 && mm.Y >= 0 && mm.X <= _profile.WidthMm && mm.Y <= _profile.HeightMm;
        var move = new MenuItem
        {
            Header = $"➤ Move the laser here  (X {mm.X:0.#}, Y {mm.Y:0.#})",
            IsEnabled = onBed && _grbl.IsConnected && !_grbl.IsJobRunning && _grbl.Status.State is MachineState.Idle,
            ToolTip = "Moves the head to this point with the laser off",
        };
        move.Click += async (_, _) =>
        {
            var r = await _grbl.JogAbsoluteAsync(mm.X, mm.Y, JogFeed());
            if (!r.Ok) Warn($"The move was refused: {r.ErrorText}");
        };
        menu.Items.Add(move);

        if (_selection.Count > 0)
        {
            menu.Items.Add(new Separator());
            AddMenu(menu, "⧉ Duplicate", Duplicate);
            AddMenu(menu, "⟳ Rotate 90°", () => Rotate90_Click(this, new RoutedEventArgs()));
            AddMenu(menu, "⌖ Center on the bed", () => CenterItem_Click(this, new RoutedEventArgs()));
            var allOn = SelectedItems.All(i => i.Enabled);
            AddMenu(menu, allOn ? "🚫 Don't burn (output off)" : "✔ Burn (output on)", () =>
            {
                PushUndo();
                foreach (var i in SelectedItems) i.Enabled = !allOn;
                MarkDirty();
                SyncItemsList();
                RedrawCanvas();
            });
            AddMenu(menu, "🗑 Delete", DeleteSelection);
        }
        menu.Items.Add(new Separator());
        AddMenu(menu, "Select all", SelectAll);
        AddMenu(menu, "⤢ Zoom to fit", () => { FitView(); RedrawCanvas(); });
        menu.PlacementTarget = WorkCanvas;
        menu.IsOpen = true;
    }

    private static void AddMenu(ContextMenu menu, string header, Action action)
    {
        var mi = new MenuItem { Header = header };
        mi.Click += (_, _) => action();
        menu.Items.Add(mi);
    }
}
