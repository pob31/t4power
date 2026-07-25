using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using T4Power.Core.Model;

// WinForms is enabled project-wide for the tray NotifyIcon, so System.Drawing and
// System.Windows.Forms are in scope here and collide with their WPF namesakes. Alias the WPF ones
// explicitly rather than turning off implicit usings for the whole project.
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using FlowDirection = System.Windows.FlowDirection;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Pen = System.Windows.Media.Pen;
using Point = System.Windows.Point;

namespace T4Power.Ui;

/// <summary>
/// A draggable fan curve: temperature across, duty up, with a live marker for where the GPU
/// currently is.
///
/// Drawn with <see cref="OnRender"/> rather than built from child elements. The whole control is
/// a grid, a polyline, a handful of handles and one marker, and drawing them directly is both
/// less code and easier to hit-test by hand than keeping a Canvas of Ellipses in sync with a
/// list of points.
///
/// The important rule it obeys is the repo's drag-commit discipline. This app has already been
/// bitten once by committing from bound property setters (see the comment block on
/// <see cref="GpuViewModel"/>'s sliders, and MainWindow.xaml.cs), and a curve editor is far more
/// exposed to it than a slider: the service is polled once a second, and a poll landing mid-drag
/// would both yank the point out from under the cursor and push a half-dragged curve to the fan.
/// So:
///
///   - <see cref="PointsChanged"/> fires continuously during a drag, but only updates the view
///     model's local copy.
///   - <see cref="CurveCommitted"/> fires only on mouse-up and after an add or remove, and that
///     is the only thing that reaches the service.
///   - <see cref="IsUserAdjusting"/> is raised for the whole gesture, which is what tells the
///     view model to stop syncing from the poll.
/// </summary>
internal sealed class FanCurveEditor : FrameworkElement
{
    const double HandleRadius = 5;
    const double GrabRadius = 10;

    static readonly Thickness Padding = new(38, 12, 12, 24);

    // Matches the window's palette: accent green for the curve, dim grey for the furniture.
    static readonly Brush CurveBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x76, 0xB9, 0x00)));
    static readonly Brush HandleBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEA, 0xED)));
    static readonly Brush MarkerBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xFF, 0xB1, 0x3B)));
    static readonly Brush LabelBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x8A, 0x8F, 0x98)));
    static readonly Pen GridPen = Freeze(new Pen(new SolidColorBrush(Color.FromRgb(0x3A, 0x3F, 0x48)), 1));
    static readonly Pen CurvePen = Freeze(new Pen(CurveBrush, 2));
    static readonly Pen MarkerPen = Freeze(new Pen(MarkerBrush, 1) { DashStyle = DashStyles.Dash });

    static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }

    int _dragging = -1;

    public FanCurveEditor()
    {
        Focusable = true;
        // Without a background the control is transparent to hit-testing and never sees a click.
        // A near-invisible fill is the usual WPF answer.
        MinHeight = 150;
    }

    // ---- dependency properties -------------------------------------------------------

    public static readonly DependencyProperty PointsProperty = DependencyProperty.Register(
        nameof(Points), typeof(IReadOnlyList<FanCurvePoint>), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MinTemperatureCProperty = DependencyProperty.Register(
        nameof(MinTemperatureC), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(20d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty MaxTemperatureCProperty = DependencyProperty.Register(
        nameof(MaxTemperatureC), typeof(double), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(85d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentTemperatureCProperty = DependencyProperty.Register(
        nameof(CurrentTemperatureC), typeof(double?), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CurrentPercentProperty = DependencyProperty.Register(
        nameof(CurrentPercent), typeof(double?), typeof(FanCurveEditor),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<FanCurvePoint>? Points
    {
        get => (IReadOnlyList<FanCurvePoint>?)GetValue(PointsProperty);
        set => SetValue(PointsProperty, value);
    }

    public double MinTemperatureC
    {
        get => (double)GetValue(MinTemperatureCProperty);
        set => SetValue(MinTemperatureCProperty, value);
    }

    public double MaxTemperatureC
    {
        get => (double)GetValue(MaxTemperatureCProperty);
        set => SetValue(MaxTemperatureCProperty, value);
    }

    public double? CurrentTemperatureC
    {
        get => (double?)GetValue(CurrentTemperatureCProperty);
        set => SetValue(CurrentTemperatureCProperty, value);
    }

    public double? CurrentPercent
    {
        get => (double?)GetValue(CurrentPercentProperty);
        set => SetValue(CurrentPercentProperty, value);
    }

    /// <summary>Raised continuously while editing. The handler updates local state only.</summary>
    public event Action<IReadOnlyList<FanCurvePoint>>? PointsChanged;

    /// <summary>Raised on gesture boundaries only. The handler sends to the service.</summary>
    public event Action? CurveCommitted;

    /// <summary>Raised true when a gesture starts and false when it ends, so the owner can
    /// suspend syncing from the poll loop for its duration.</summary>
    public event Action<bool>? IsUserAdjusting;

    // ---- geometry --------------------------------------------------------------------

    Rect Plot => new(
        Padding.Left,
        Padding.Top,
        Math.Max(1, ActualWidth - Padding.Left - Padding.Right),
        Math.Max(1, ActualHeight - Padding.Top - Padding.Bottom));

    Point ToScreen(double temperatureC, double percent)
    {
        var plot = Plot;
        var span = Math.Max(1, MaxTemperatureC - MinTemperatureC);
        var x = plot.Left + plot.Width * ((temperatureC - MinTemperatureC) / span);
        var y = plot.Bottom - plot.Height * (percent / 100);
        return new Point(x, y);
    }

    (double TemperatureC, double Percent) ToData(Point screen)
    {
        var plot = Plot;
        var span = MaxTemperatureC - MinTemperatureC;
        var temperature = MinTemperatureC + span * ((screen.X - plot.Left) / plot.Width);
        var percent = (plot.Bottom - screen.Y) / plot.Height * 100;

        return (Math.Clamp(temperature, MinTemperatureC, MaxTemperatureC), Math.Clamp(percent, 0, 100));
    }

    // ---- rendering -------------------------------------------------------------------

    protected override void OnRender(DrawingContext dc)
    {
        var plot = Plot;

        // A transparent fill over the whole control so clicks land here rather than passing
        // through to whatever is behind.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));

        DrawGrid(dc, plot);

        var points = (Points ?? []).OrderBy(p => p.TemperatureC).ToList();
        if (points.Count > 0)
        {
            DrawCurve(dc, plot, points);
            foreach (var point in points)
            {
                var at = ToScreen(point.TemperatureC, point.Percent);
                dc.DrawEllipse(HandleBrush, null, at, HandleRadius, HandleRadius);
            }
        }

        DrawLiveMarker(dc, plot);
    }

    void DrawGrid(DrawingContext dc, Rect plot)
    {
        for (var percent = 0; percent <= 100; percent += 25)
        {
            var y = ToScreen(MinTemperatureC, percent).Y;
            dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            dc.DrawText(Label($"{percent}%"), new Point(4, y - 8));
        }

        // Round to a sensible tick rather than dividing the range, so the labels are readable
        // numbers whatever the axis bounds happen to be.
        var step = Math.Max(5, Math.Round((MaxTemperatureC - MinTemperatureC) / 5 / 5) * 5);
        for (var temperature = MinTemperatureC; temperature <= MaxTemperatureC + 0.001; temperature += step)
        {
            var x = ToScreen(temperature, 0).X;
            dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            dc.DrawText(Label($"{temperature:0}°"), new Point(x - 10, plot.Bottom + 4));
        }
    }

    void DrawCurve(DrawingContext dc, Rect plot, List<FanCurvePoint> points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            // Flat extensions beyond the first and last points, matching what FanCurve.Evaluate
            // actually does. Drawing only between the handles would show a curve the fan does not
            // follow at the ends.
            var first = ToScreen(points[0].TemperatureC, points[0].Percent);
            ctx.BeginFigure(new Point(plot.Left, first.Y), false, false);
            ctx.LineTo(first, true, false);

            foreach (var point in points.Skip(1))
                ctx.LineTo(ToScreen(point.TemperatureC, point.Percent), true, false);

            var last = ToScreen(points[^1].TemperatureC, points[^1].Percent);
            ctx.LineTo(new Point(plot.Right, last.Y), true, false);
        }

        geometry.Freeze();
        dc.DrawGeometry(null, CurvePen, geometry);
    }

    void DrawLiveMarker(DrawingContext dc, Rect plot)
    {
        if (CurrentTemperatureC is not { } temperature) return;

        var x = ToScreen(Math.Clamp(temperature, MinTemperatureC, MaxTemperatureC), 0).X;
        dc.DrawLine(MarkerPen, new Point(x, plot.Top), new Point(x, plot.Bottom));

        if (CurrentPercent is { } percent)
        {
            var at = ToScreen(Math.Clamp(temperature, MinTemperatureC, MaxTemperatureC), percent);
            dc.DrawEllipse(MarkerBrush, null, at, 4, 4);
        }

        dc.DrawText(Label($"{temperature:0}°C", MarkerBrush), new Point(x + 4, plot.Top));
    }

    FormattedText Label(string text, Brush? brush = null) => new(
        text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
        new Typeface("Segoe UI"), 10, brush ?? LabelBrush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // ---- gestures --------------------------------------------------------------------

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();

        var points = Sorted();
        var hit = HitTest(e.GetPosition(this), points);

        if (e.ClickCount == 2)
        {
            // Double-click: add a point where the pointer is, or remove the one under it.
            if (hit >= 0) RemoveAt(hit, points);
            else AddAt(e.GetPosition(this), points);
            return;
        }

        if (hit < 0) return;

        _dragging = hit;
        IsUserAdjusting?.Invoke(true);
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_dragging < 0) return;

        var points = Sorted();
        if (_dragging >= points.Count) return;

        var (temperature, percent) = ToData(e.GetPosition(this));

        // Keep the point between its neighbours so the curve stays a function of temperature.
        // Without this a dragged handle can cross another and the curve folds back on itself.
        var lower = _dragging > 0 ? points[_dragging - 1].TemperatureC + 0.5 : MinTemperatureC;
        var upper = _dragging < points.Count - 1 ? points[_dragging + 1].TemperatureC - 0.5 : MaxTemperatureC;

        points[_dragging] = new FanCurvePoint
        {
            TemperatureC = Math.Round(Math.Clamp(temperature, lower, Math.Max(lower, upper)), 1),
            Percent = Math.Round(percent, 1),
        };

        // Local only. The service does not hear about any of this until the button comes up.
        PointsChanged?.Invoke(points);
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_dragging < 0) return;

        _dragging = -1;
        ReleaseMouseCapture();
        IsUserAdjusting?.Invoke(false);

        // The one place a drag reaches the service.
        CurveCommitted?.Invoke();
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);

        var points = Sorted();
        var hit = HitTest(e.GetPosition(this), points);
        if (hit >= 0) RemoveAt(hit, points);

        e.Handled = true;
    }

    void AddAt(Point screen, List<FanCurvePoint> points)
    {
        var (temperature, percent) = ToData(screen);

        points.Add(new FanCurvePoint
        {
            TemperatureC = Math.Round(temperature, 1),
            Percent = Math.Round(percent, 1),
        });

        Commit(points.OrderBy(p => p.TemperatureC).ToList());
    }

    void RemoveAt(int index, List<FanCurvePoint> points)
    {
        // Two points is the floor: one leaves nothing to interpolate between, and zero would mean
        // the fan silently falls back to the curve's floor duty.
        if (points.Count <= 2) return;

        points.RemoveAt(index);
        Commit(points);
    }

    void Commit(List<FanCurvePoint> points)
    {
        PointsChanged?.Invoke(points);
        CurveCommitted?.Invoke();
    }

    int HitTest(Point screen, List<FanCurvePoint> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            var at = ToScreen(points[i].TemperatureC, points[i].Percent);
            if ((at - screen).Length <= GrabRadius) return i;
        }

        return -1;
    }

    List<FanCurvePoint> Sorted() => (Points ?? []).OrderBy(p => p.TemperatureC).ToList();
}
