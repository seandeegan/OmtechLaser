using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace LaserPilot.App.Tutorial;

/// <summary>One stop on the guided tour.</summary>
public sealed class TutorialStep
{
    public required string Chapter { get; init; }
    public required string Title { get; init; }
    public required string Body { get; init; }
    /// <summary>The control to spotlight; null shows the card in the middle of the window.</summary>
    public Func<FrameworkElement?>? Target { get; init; }
    /// <summary>An optional "try it" instruction.</summary>
    public string? TryIt { get; init; }
    /// <summary>For interactive steps: becomes true once the user has done the thing.</summary>
    public Func<bool>? IsDone { get; init; }
    public string DoneText { get; init; } = "✓ Nicely done! Click Next to carry on.";
    public Action? OnEnter { get; init; }
    public Action? OnLeave { get; init; }
}

/// <summary>
/// Spotlight overlay for the tutorial: dims the window except for the
/// highlighted control and shows an explanation card next to it. It never
/// blocks the window — everything (including STOP) keeps working.
/// </summary>
public sealed class TutorialOverlay : Grid
{
    private const double CardWidth = 420;

    private readonly FrameworkElement _host;
    private readonly IReadOnlyList<TutorialStep> _steps;
    private readonly Canvas _layer = new() { IsHitTestVisible = true };
    private readonly Path _dim;
    private readonly Border _ring;
    private readonly Border _card;
    private readonly TextBlock _chapter, _title, _body, _tryIt, _done, _counter;
    private readonly ProgressBar _progress;
    private readonly Button _back, _next;
    private readonly ComboBox _chapters;
    private readonly DispatcherTimer _timer;
    private bool _suppressChapterEvent;
    private bool _closed;

    public int Index { get; private set; }

    /// <summary>A control that is never dimmed or covered by the card (the STOP button).</summary>
    public Func<FrameworkElement?>? KeepClear { get; init; }

    /// <summary>Raised once when the tour closes: true if the user reached the end.</summary>
    public event Action<bool>? Finished;

    public TutorialOverlay(FrameworkElement host, IReadOnlyList<TutorialStep> steps, int startIndex)
    {
        _host = host;
        _steps = steps;
        Panel.SetZIndex(this, 1000);
        Children.Add(_layer);

        _dim = new Path { Fill = new SolidColorBrush(Color.FromArgb(0x9C, 0, 0, 0)), IsHitTestVisible = false };
        _ring = new Border
        {
            BorderBrush = (Brush)Application.Current.Resources["AccentHoverBrush"],
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(8),
            IsHitTestVisible = false,
        };
        _ring.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.35, TimeSpan.FromSeconds(0.8))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });

        var text = (Brush)Application.Current.Resources["TextBrush"];
        var subtle = (Brush)Application.Current.Resources["SubtleTextBrush"];
        _chapter = new TextBlock { Foreground = subtle, FontSize = 11.5, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        _title = new TextBlock { Foreground = text, FontSize = 19, FontWeight = FontWeights.Bold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 6, 0, 6) };
        _body = new TextBlock { Foreground = text, FontSize = 14, TextWrapping = TextWrapping.Wrap, LineHeight = 21 };
        _tryIt = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["AccentHoverBrush"],
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 0),
        };
        _done = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["OkBrush"],
            FontSize = 14, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
            Visibility = Visibility.Collapsed,
        };
        _counter = new TextBlock { Foreground = subtle, FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
        _progress = new ProgressBar { Height = 6, Maximum = Math.Max(1, steps.Count - 1), VerticalAlignment = VerticalAlignment.Center };

        var close = new Button { Content = "✕", Style = (Style)Application.Current.Resources["LinkButton"], ToolTip = "Close the tutorial (Esc)", Padding = new Thickness(6, 2, 6, 2) };
        close.Click += (_, _) => Close(false);

        _chapters = new ComboBox { Width = 190, ToolTip = "Jump to a chapter" };
        var chapterNames = steps.Select(s => s.Chapter).Distinct().ToList();
        foreach (var c in chapterNames) _chapters.Items.Add(c);
        _chapters.SelectionChanged += (_, _) =>
        {
            if (_suppressChapterEvent || _chapters.SelectedItem is not string c) return;
            int first = _steps.ToList().FindIndex(s => s.Chapter == c);
            if (first >= 0 && first != Index && _steps[Index].Chapter != c) Show(first);
        };

        _back = new Button { Content = "◀ Back", MinWidth = 84 };
        _back.Click += (_, _) => { if (Index > 0) Show(Index - 1); };
        _next = new Button { Content = "Next ▶", MinWidth = 104, Style = (Style)Application.Current.Resources["AccentButton"] };
        _next.Click += (_, _) => { if (Index < _steps.Count - 1) Show(Index + 1); else Close(true); };
        var skip = new Button { Content = "Skip tutorial", Style = (Style)Application.Current.Resources["LinkButton"], ToolTip = "Leave the tour. Restart it any time with F1 or the ❓ Tutorial button." };
        skip.Click += (_, _) => Close(false);

        var header = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        header.Children.Add(close);
        header.Children.Add(_chapter);

        var progressRow = new DockPanel { Margin = new Thickness(0, 14, 0, 0) };
        DockPanel.SetDock(_counter, Dock.Right);
        progressRow.Children.Add(_counter);
        progressRow.Children.Add(_progress);

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_back);
        right.Children.Add(_next);
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(skip);

        var jump = new DockPanel { Margin = new Thickness(0, 6, 0, 0) };
        jump.Children.Add(new TextBlock { Text = "Jump to:", Foreground = subtle, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) });
        jump.Children.Add(_chapters);

        var stack = new StackPanel();
        stack.Children.Add(header);
        stack.Children.Add(_title);
        stack.Children.Add(_body);
        stack.Children.Add(_tryIt);
        stack.Children.Add(_done);
        stack.Children.Add(progressRow);
        stack.Children.Add(buttons);
        stack.Children.Add(jump);

        _card = new Border
        {
            Width = CardWidth,
            Background = (Brush)Application.Current.Resources["PanelBrush"],
            BorderBrush = (Brush)Application.Current.Resources["AccentBrush"],
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(18, 12, 18, 14),
            Child = stack,
            Effect = new DropShadowEffect { BlurRadius = 24, ShadowDepth = 4, Opacity = 0.6 },
        };
        TextElement.SetForeground(_card, text);

        _layer.Children.Add(_dim);
        _layer.Children.Add(_ring);
        _layer.Children.Add(_card);

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        Loaded += (_, _) =>
        {
            Show(Math.Clamp(startIndex, 0, _steps.Count - 1));
            _timer.Start();
        };
        SizeChanged += (_, _) => Layout();
    }

    public void Close(bool completed)
    {
        if (_closed) return;
        _closed = true;
        _timer.Stop();
        try { _steps[Index].OnLeave?.Invoke(); } catch { }
        (Parent as Panel)?.Children.Remove(this);
        Finished?.Invoke(completed);
    }

    private void Show(int index)
    {
        if (index != Index) { try { _steps[Index].OnLeave?.Invoke(); } catch { } }
        Index = index;
        var step = _steps[index];
        try { step.OnEnter?.Invoke(); } catch { }

        _chapter.Text = step.Chapter.ToUpperInvariant();
        _title.Text = step.Title;
        _body.Text = step.Body;
        _tryIt.Text = step.TryIt is null ? "" : "👉 " + step.TryIt;
        _tryIt.Visibility = step.TryIt is null ? Visibility.Collapsed : Visibility.Visible;
        _done.Text = step.DoneText;
        _done.Visibility = Visibility.Collapsed;
        _counter.Text = $"Step {index + 1} of {_steps.Count}";
        _progress.Value = index;
        _back.IsEnabled = index > 0;
        _next.Content = index == _steps.Count - 1 ? "Finish 🎉" : "Next ▶";
        _suppressChapterEvent = true;
        _chapters.SelectedItem = step.Chapter;
        _suppressChapterEvent = false;

        if (Target(step) is { } t)
        {
            try { t.BringIntoView(); } catch { }
        }
        // Layout after the BringIntoView scroll has happened.
        Dispatcher.BeginInvoke(Layout, DispatcherPriority.Loaded);
        Tick();
        _next.Focus();
    }

    private static FrameworkElement? Target(TutorialStep step)
    {
        try { return step.Target?.Invoke(); }
        catch { return null; }
    }

    private void Tick()
    {
        if (_closed) return;
        Layout();
        var step = _steps[Index];
        if (step.IsDone is not null)
        {
            bool done;
            try { done = step.IsDone(); } catch { done = false; }
            _done.Visibility = done ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    /// <summary>Rectangle of a control in overlay coordinates (with a margin), if it is on screen.</summary>
    private Rect? RectOf(FrameworkElement? t)
    {
        if (t is null || !t.IsVisible || t.ActualWidth < 1 || t.ActualHeight < 1) return null;
        try
        {
            var r = t.TransformToVisual(_host).TransformBounds(new Rect(0, 0, t.ActualWidth, t.ActualHeight));
            r.Inflate(6, 6);
            r.Intersect(new Rect(0, 0, ActualWidth, ActualHeight));
            return r.IsEmpty || r.Width < 2 || r.Height < 2 ? null : r;
        }
        catch
        {
            return null; // not in this window's visual tree
        }
    }

    private void Layout()
    {
        if (_closed || ActualWidth < 10 || ActualHeight < 10) return;
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var target = RectOf(Target(_steps[Index]));
        Rect? keep = null;
        try { keep = RectOf(KeepClear?.Invoke()); } catch { }

        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        if (target is { } t) holes.Children.Add(new RectangleGeometry(t, 8, 8));
        if (keep is { } k) holes.Children.Add(new RectangleGeometry(k, 6, 6));
        _dim.Data = holes.Children.Count == 0 ? full : new CombinedGeometry(GeometryCombineMode.Exclude, full, holes);

        _card.Measure(new Size(CardWidth, double.PositiveInfinity));
        double cw = CardWidth, ch = _card.DesiredSize.Height;
        double left, top;
        const double gap = 18;

        if (target is { } r)
        {
            _ring.Visibility = Visibility.Visible;
            _ring.Width = r.Width;
            _ring.Height = r.Height;
            Canvas.SetLeft(_ring, r.Left);
            Canvas.SetTop(_ring, r.Top);

            // Prefer beside the target, then below, then above.
            if (ActualWidth - r.Right >= cw + gap * 2) { left = r.Right + gap; top = r.Top; }
            else if (r.Left >= cw + gap * 2) { left = r.Left - gap - cw; top = r.Top; }
            else if (ActualHeight - r.Bottom >= ch + gap * 2) { left = r.Left; top = r.Bottom + gap; }
            else if (r.Top >= ch + gap * 2) { left = r.Left; top = r.Top - gap - ch; }
            else { left = (ActualWidth - cw) / 2; top = (ActualHeight - ch) / 2; }
        }
        else
        {
            _ring.Visibility = Visibility.Collapsed;
            left = (ActualWidth - cw) / 2;
            top = (ActualHeight - ch) / 2;
        }
        left = Math.Clamp(left, 8, Math.Max(8, ActualWidth - cw - 8));
        top = Math.Clamp(top, 8, Math.Max(8, ActualHeight - ch - 8));
        if (keep is { } kr && new Rect(left, top, cw, ch).IntersectsWith(kr))
        {
            // Never cover STOP: slide the card below it, or else to its left.
            if (kr.Bottom + 8 + ch <= ActualHeight - 8) top = kr.Bottom + 8;
            else left = Math.Max(8, kr.Left - 8 - cw);
        }
        Canvas.SetLeft(_card, left);
        Canvas.SetTop(_card, top);
    }
}
