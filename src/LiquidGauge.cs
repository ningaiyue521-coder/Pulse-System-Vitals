using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DemoApp.Controls;

public enum LiquidGaugeShape
{
    Capsule,
    Orb
}

/// <summary>
/// A timer-free liquid gauge built from persistent DrawingVisual layers.
/// Value changes only move a clip and redraw the small water surface; the
/// glass vessel, liquid gradient and optical highlights stay cached.
/// </summary>
public sealed class LiquidGauge : FrameworkElement
{
    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(double), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(0d, OnValueRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(double), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(100d, OnValueRangeChanged));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(0d, OnValueRangeChanged));

    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(Brush), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(Brushes.DodgerBlue, OnAppearanceChanged));

    public static readonly DependencyProperty BackgroundProperty = DependencyProperty.Register(
        nameof(Background), typeof(Brush), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(null, OnAppearanceChanged));

    public static readonly DependencyProperty ShapeProperty = DependencyProperty.Register(
        nameof(Shape), typeof(LiquidGaugeShape), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(LiquidGaugeShape.Capsule, OnAppearanceChanged));

    public static readonly DependencyProperty ShowBubblesProperty = DependencyProperty.Register(
        nameof(ShowBubbles), typeof(bool), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(false, OnAppearanceChanged));

    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(
        nameof(Orientation), typeof(System.Windows.Controls.Orientation), typeof(LiquidGauge),
        new FrameworkPropertyMetadata(System.Windows.Controls.Orientation.Horizontal, OnAppearanceChanged));

    private static readonly Brush DefaultTrack = Frozen(Color.FromArgb(18, 60, 60, 67));
    private static readonly Brush GlassHighlight = CreateGlassHighlight();
    private static readonly Brush BubbleFill = Frozen(Color.FromArgb(56, 255, 255, 255));
    private static readonly Pen BubbleRim = FrozenPen(Color.FromArgb(82, 255, 255, 255), 0.60);
    private static readonly Pen OuterRim = FrozenPen(Color.FromArgb(138, 255, 255, 255), 0.90);
    private static readonly Pen InnerRim = FrozenPen(Color.FromArgb(42, 191, 230, 255), 0.60);
    private static readonly Pen CapsuleHighlight = FrozenPen(Color.FromArgb(102, 255, 255, 255), 0.72);
    private static readonly Pen OrbGlint = FrozenPen(Color.FromArgb(158, 255, 255, 255), 1.30);
    private static readonly Pen SurfaceLine = FrozenPen(Color.FromArgb(206, 255, 255, 255), 0.90);
    private static readonly Dictionary<(Color Accent, bool Horizontal), LinearGradientBrush> LiquidBrushCache = [];

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _underlay = new();
    private readonly DrawingVisual _liquid = new();
    private readonly DrawingVisual _surface = new();
    private readonly DrawingVisual _overlay = new();
    private readonly RectangleGeometry _liquidClip = new();
    private Rect _bounds;

    public LiquidGauge()
    {
        _visuals = new VisualCollection(this)
        {
            _underlay,
            _liquid,
            _surface,
            _overlay
        };
        _liquid.Clip = _liquidClip;

        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public double Minimum
    {
        get => (double)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public double Maximum
    {
        get => (double)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush? Foreground
    {
        get => (Brush?)GetValue(ForegroundProperty);
        set => SetValue(ForegroundProperty, value);
    }

    public Brush? Background
    {
        get => (Brush?)GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public LiquidGaugeShape Shape
    {
        get => (LiquidGaugeShape)GetValue(ShapeProperty);
        set => SetValue(ShapeProperty, value);
    }

    public bool ShowBubbles
    {
        get => (bool)GetValue(ShowBubblesProperty);
        set => SetValue(ShowBubblesProperty, value);
    }

    public System.Windows.Controls.Orientation Orientation
    {
        get => (System.Windows.Controls.Orientation)GetValue(OrientationProperty);
        set => SetValue(OrientationProperty, value);
    }

    protected override int VisualChildrenCount => _visuals.Count;

    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = Shape == LiquidGaugeShape.Orb
            ? new Size(174, 174)
            : Orientation == System.Windows.Controls.Orientation.Vertical
                ? new Size(8, 32)
                : new Size(120, 12);
        return new Size(
            double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? desired.Height : availableSize.Height);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        RebuildLayers();
    }

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LiquidGauge gauge) gauge.RebuildLayers();
    }

    private static void OnValueRangeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        if (sender is LiquidGauge gauge) gauge.UpdateLiquidLevel();
    }

    private void RebuildLayers()
    {
        if (ActualWidth < 2 || ActualHeight < 2) return;
        _bounds = new Rect(0.7, 0.7, Math.Max(0, ActualWidth - 1.4), Math.Max(0, ActualHeight - 1.4));
        DrawUnderlay();
        DrawFullLiquid();
        DrawOverlay();

        _surface.Clip = Shape == LiquidGaugeShape.Orb
            ? new EllipseGeometry(_bounds)
            : new RectangleGeometry(
                _bounds,
                Orientation == System.Windows.Controls.Orientation.Vertical ? _bounds.Width / 2 : _bounds.Height / 2,
                Orientation == System.Windows.Controls.Orientation.Vertical ? _bounds.Width / 2 : _bounds.Height / 2);
        UpdateLiquidLevel();
    }

    private void DrawUnderlay()
    {
        using var context = _underlay.RenderOpen();
        var track = Background ?? DefaultTrack;
        if (Shape == LiquidGaugeShape.Orb)
        {
            var center = Center(_bounds);
            context.DrawEllipse(track, null, center, _bounds.Width / 2, _bounds.Height / 2);
        }
        else
        {
            var radius = Orientation == System.Windows.Controls.Orientation.Vertical ? _bounds.Width / 2 : _bounds.Height / 2;
            context.DrawRoundedRectangle(track, null, _bounds, radius, radius);
        }
    }

    private void DrawFullLiquid()
    {
        using var context = _liquid.RenderOpen();
        var accent = ResolveColour(Foreground);
        var verticalGradient = Shape == LiquidGaugeShape.Orb || Orientation == System.Windows.Controls.Orientation.Vertical;
        var liquid = GetLiquidBrush(accent, horizontal: !verticalGradient);

        if (Shape == LiquidGaugeShape.Orb)
        {
            var center = Center(_bounds);
            context.DrawEllipse(liquid, null, center, _bounds.Width / 2, _bounds.Height / 2);
            if (ShowBubbles)
            {
                DrawBubble(context, _bounds.Left + _bounds.Width * 0.30, _bounds.Bottom - _bounds.Height * 0.30, Math.Clamp(_bounds.Width * 0.025, 2.0, 4.2));
                DrawBubble(context, _bounds.Left + _bounds.Width * 0.68, _bounds.Bottom - _bounds.Height * 0.54, Math.Clamp(_bounds.Width * 0.016, 1.4, 3.0));
                DrawBubble(context, _bounds.Left + _bounds.Width * 0.48, _bounds.Bottom - _bounds.Height * 0.76, Math.Clamp(_bounds.Width * 0.010, 1.0, 2.1));
            }
            return;
        }

        var radius = Orientation == System.Windows.Controls.Orientation.Vertical ? _bounds.Width / 2 : _bounds.Height / 2;
        context.DrawRoundedRectangle(liquid, null, _bounds, radius, radius);

        if (Orientation == System.Windows.Controls.Orientation.Horizontal)
        {
            context.DrawLine(
                CapsuleHighlight,
                new Point(_bounds.Left + radius * 0.65, _bounds.Top + _bounds.Height * 0.23),
                new Point(_bounds.Right - radius * 0.65, _bounds.Top + _bounds.Height * 0.23));
            if (ShowBubbles)
            {
                DrawBubble(context, _bounds.Left + _bounds.Width * 0.36, _bounds.Top + _bounds.Height * 0.63, Math.Clamp(_bounds.Height * 0.10, 0.8, 2.1));
                DrawBubble(context, _bounds.Left + _bounds.Width * 0.68, _bounds.Top + _bounds.Height * 0.42, Math.Clamp(_bounds.Height * 0.065, 0.65, 1.5));
            }
        }
        else if (ShowBubbles)
        {
            DrawBubble(context, _bounds.Left + _bounds.Width * 0.43, _bounds.Bottom - _bounds.Height * 0.42, Math.Clamp(_bounds.Width * 0.12, 0.65, 1.25));
        }
    }

    private void DrawOverlay()
    {
        using var context = _overlay.RenderOpen();
        if (Shape == LiquidGaugeShape.Orb)
        {
            var center = Center(_bounds);
            context.DrawEllipse(GlassHighlight, null, center, _bounds.Width / 2, _bounds.Height / 2);
            context.DrawEllipse(null, OuterRim, center, _bounds.Width / 2, _bounds.Height / 2);

            var orbInner = new Rect(
                _bounds.Left + 1.15,
                _bounds.Top + 1.15,
                Math.Max(0, _bounds.Width - 2.30),
                Math.Max(0, _bounds.Height - 2.30));
            context.DrawEllipse(null, InnerRim, Center(orbInner), orbInner.Width / 2, orbInner.Height / 2);

            var glint = new PathFigure { StartPoint = new Point(_bounds.Left + _bounds.Width * 0.18, _bounds.Top + _bounds.Height * 0.31) };
            glint.Segments.Add(new BezierSegment(
                new Point(_bounds.Left + _bounds.Width * 0.25, _bounds.Top + _bounds.Height * 0.13),
                new Point(_bounds.Left + _bounds.Width * 0.43, _bounds.Top + _bounds.Height * 0.07),
                new Point(_bounds.Left + _bounds.Width * 0.58, _bounds.Top + _bounds.Height * 0.11),
                true));
            context.DrawGeometry(null, OrbGlint, new PathGeometry([glint]));
            return;
        }

        var radius = Orientation == System.Windows.Controls.Orientation.Vertical ? _bounds.Width / 2 : _bounds.Height / 2;
        context.DrawRoundedRectangle(null, OuterRim, _bounds, radius, radius);
        var inner = new Rect(_bounds.Left + 0.35, _bounds.Top + 0.35, _bounds.Width - 0.7, _bounds.Height - 0.7);
        context.DrawRoundedRectangle(null, InnerRim, inner, Math.Max(0, radius - 0.35), Math.Max(0, radius - 0.35));
    }

    private void UpdateLiquidLevel()
    {
        if (_bounds.Width <= 0 || _bounds.Height <= 0) return;
        var ratio = GetRatio();
        if (ratio <= 0.001)
        {
            _liquidClip.Rect = new Rect(_bounds.Left, _bounds.Bottom, 0, 0);
            _surface.Opacity = 0;
            using var empty = _surface.RenderOpen();
            return;
        }

        _surface.Opacity = 1;
        var accent = ResolveColour(Foreground);
        if (Shape == LiquidGaugeShape.Capsule && Orientation == System.Windows.Controls.Orientation.Horizontal)
        {
            var fillEnd = _bounds.Left + _bounds.Width * ratio;
            _liquidClip.Rect = new Rect(_bounds.Left, _bounds.Top, Math.Max(0, fillEnd - _bounds.Left), _bounds.Height);
            DrawHorizontalMeniscus(fillEnd, accent, ratio);
            return;
        }

        var amplitude = Shape == LiquidGaugeShape.Orb
            ? Math.Clamp(_bounds.Width * 0.014, 0.70, 2.40)
            : Math.Clamp(_bounds.Width * 0.11, 0.45, 1.35);
        var rawSurface = _bounds.Bottom - _bounds.Height * ratio;
        var surface = Math.Clamp(rawSurface, _bounds.Top + amplitude * 0.40, _bounds.Bottom - amplitude * 0.40);
        _liquidClip.Rect = new Rect(_bounds.Left, surface, _bounds.Width, Math.Max(0, _bounds.Bottom - surface));
        DrawWaveSurface(surface, amplitude, accent);
    }

    private void DrawHorizontalMeniscus(double fillEnd, Color accent, double ratio)
    {
        using var context = _surface.RenderOpen();
        if (ratio >= 0.995) return;
        var light = Mix(accent, Colors.White, 0.42);
        var caustic = FrozenPen(
            Color.FromArgb(176, light.R, light.G, light.B),
            Math.Clamp(_bounds.Height * 0.12, 0.85, 1.45));
        var top = _bounds.Top + Math.Max(1.2, _bounds.Height * 0.18);
        var bottom = _bounds.Bottom - Math.Max(1.2, _bounds.Height * 0.18);
        context.DrawLine(caustic, new Point(fillEnd, top), new Point(fillEnd, bottom));
        context.DrawLine(SurfaceLine, new Point(fillEnd - 0.45, top), new Point(fillEnd - 0.45, bottom));
    }

    private void DrawWaveSurface(double surface, double amplitude, Color accent)
    {
        using var context = _surface.RenderOpen();
        var width = _bounds.Width;
        var figure = new PathFigure { StartPoint = new Point(_bounds.Left, surface), IsClosed = true };
        figure.Segments.Add(new BezierSegment(
            new Point(_bounds.Left + width * 0.18, surface - amplitude),
            new Point(_bounds.Left + width * 0.34, surface + amplitude),
            new Point(_bounds.Left + width * 0.50, surface),
            true));
        figure.Segments.Add(new BezierSegment(
            new Point(_bounds.Left + width * 0.66, surface - amplitude),
            new Point(_bounds.Left + width * 0.84, surface + amplitude),
            new Point(_bounds.Right, surface),
            true));
        figure.Segments.Add(new LineSegment(new Point(_bounds.Right, surface + amplitude * 2.1), true));
        figure.Segments.Add(new LineSegment(new Point(_bounds.Left, surface + amplitude * 2.1), true));
        var waterBand = new PathGeometry([figure]);
        context.DrawGeometry(Frozen(Color.FromArgb(202, accent.R, accent.G, accent.B)), null, waterBand);

        var line = new PathFigure { StartPoint = new Point(_bounds.Left, surface) };
        line.Segments.Add(new BezierSegment(
            new Point(_bounds.Left + width * 0.18, surface - amplitude),
            new Point(_bounds.Left + width * 0.34, surface + amplitude),
            new Point(_bounds.Left + width * 0.50, surface),
            true));
        line.Segments.Add(new BezierSegment(
            new Point(_bounds.Left + width * 0.66, surface - amplitude),
            new Point(_bounds.Left + width * 0.84, surface + amplitude),
            new Point(_bounds.Right, surface),
            true));
        context.DrawGeometry(null, SurfaceLine, new PathGeometry([line]));
    }

    private double GetRatio()
    {
        var range = Maximum - Minimum;
        return range <= 0 ? 0 : Math.Clamp((Value - Minimum) / range, 0, 1);
    }

    private static LinearGradientBrush GetLiquidBrush(Color accent, bool horizontal)
    {
        var key = (accent, horizontal);
        if (LiquidBrushCache.TryGetValue(key, out var cached)) return cached;

        var light = Mix(accent, Colors.White, 0.27);
        var deep = Mix(accent, Color.FromRgb(18, 55, 104), 0.12);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = horizontal ? new Point(1, 0) : new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(198, light.R, light.G, light.B), 0),
                new GradientStop(Color.FromArgb(218, accent.R, accent.G, accent.B), 0.48),
                new GradientStop(Color.FromArgb(228, deep.R, deep.G, deep.B), 1)
            }
        };
        brush.Freeze();
        LiquidBrushCache[key] = brush;
        return brush;
    }

    private static void DrawBubble(DrawingContext context, double x, double y, double radius) =>
        context.DrawEllipse(BubbleFill, BubbleRim, new Point(x, y), radius, radius);

    private static Color ResolveColour(Brush? brush) =>
        brush is SolidColorBrush solid ? solid.Color : Color.FromRgb(10, 132, 255);

    private static Color Mix(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
    }

    private static Point Center(Rect rect) => new(rect.Left + rect.Width / 2, rect.Top + rect.Height / 2);

    private static Brush CreateGlassHighlight()
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.24, 0.18),
            GradientOrigin = new Point(0.19, 0.12),
            RadiusX = 0.88,
            RadiusY = 0.94,
            GradientStops =
            {
                new GradientStop(Color.FromArgb(54, 255, 255, 255), 0),
                new GradientStop(Color.FromArgb(11, 255, 255, 255), 0.44),
                new GradientStop(Colors.Transparent, 0.76)
            }
        };
        brush.Freeze();
        return brush;
    }

    private static Brush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Pen FrozenPen(Color colour, double thickness)
    {
        var pen = new Pen(Frozen(colour), thickness);
        pen.Freeze();
        return pen;
    }
}
