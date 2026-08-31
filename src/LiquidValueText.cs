using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DemoApp.Controls;

/// <summary>
/// A timer-free numeric readout whose glyphs act as the liquid vessel.
/// The value only invalidates this small visual; there is no animation loop.
/// </summary>
public sealed class LiquidValueText : FrameworkElement
{
    private static readonly Brush DefaultBaseBrush = Frozen(Color.FromArgb(224, 255, 255, 255));
    private static readonly Brush DefaultAccentBrush = Frozen(Color.FromRgb(66, 169, 255));
    private static readonly Pen GlyphRim = FrozenPen(Color.FromArgb(42, 255, 255, 255), 0.36);
    private static readonly Dictionary<Color, LinearGradientBrush> LiquidBrushCache = [];

    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text), typeof(string), typeof(LiquidValueText),
        new FrameworkPropertyMetadata("--%", FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, null, CoerceValue));

    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush), typeof(Brush), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(DefaultBaseBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty AccentBrushProperty = DependencyProperty.Register(
        nameof(AccentBrush), typeof(Brush), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(DefaultAccentBrush, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontFamilyProperty = DependencyProperty.Register(
        nameof(FontFamily), typeof(FontFamily), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(
            new FontFamily("Segoe UI Variable Display, Segoe UI"),
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontSizeProperty = DependencyProperty.Register(
        nameof(FontSize), typeof(double), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(21d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FontWeightProperty = DependencyProperty.Register(
        nameof(FontWeight), typeof(FontWeight), typeof(LiquidValueText),
        new FrameworkPropertyMetadata(FontWeights.SemiBold, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    public LiquidValueText()
    {
        Focusable = false;
        IsHitTestVisible = false;
        SnapsToDevicePixels = true;
        UseLayoutRounding = true;
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public Brush BaseBrush
    {
        get => (Brush)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public Brush AccentBrush
    {
        get => (Brush)GetValue(AccentBrushProperty);
        set => SetValue(AccentBrushProperty, value);
    }

    public FontFamily FontFamily
    {
        get => (FontFamily)GetValue(FontFamilyProperty);
        set => SetValue(FontFamilyProperty, value);
    }

    public double FontSize
    {
        get => (double)GetValue(FontSizeProperty);
        set => SetValue(FontSizeProperty, value);
    }

    public FontWeight FontWeight
    {
        get => (FontWeight)GetValue(FontWeightProperty);
        set => SetValue(FontWeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var desired = new Size(FontSize * 3.15, FontSize * 1.28);
        return new Size(
            double.IsInfinity(availableSize.Width) ? desired.Width : availableSize.Width,
            double.IsInfinity(availableSize.Height) ? desired.Height : availableSize.Height);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (RenderSize.Width < 2 || RenderSize.Height < 2 || string.IsNullOrWhiteSpace(Text)) return;

        var typeface = new Typeface(FontFamily, FontStyles.Normal, FontWeight, FontStretches.Normal);
        var formatted = new FormattedText(
            Text,
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            FontSize,
            BaseBrush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
        var origin = new Point(
            Math.Max(0, (RenderSize.Width - formatted.WidthIncludingTrailingWhitespace) / 2),
            Math.Max(0, (RenderSize.Height - formatted.Height) / 2 - 0.35));
        var glyphs = formatted.BuildGeometry(origin);
        var bounds = glyphs.Bounds;
        if (bounds.IsEmpty || bounds.Width <= 0 || bounds.Height <= 0) return;

        drawingContext.DrawGeometry(BaseBrush, null, glyphs);

        var ratio = Math.Clamp(Value / 100d, 0, 1);
        if (ratio > 0.001)
        {
            var amplitude = Math.Clamp(FontSize * 0.035, 0.58, 0.92);
            var surface = ratio >= 0.995
                ? bounds.Top - amplitude * 1.8
                : bounds.Bottom - bounds.Height * ratio;
            var liquidFigure = CreateWave(bounds, surface, amplitude, closeAtBottom: true);
            var liquidGeometry = new PathGeometry([liquidFigure]);

            drawingContext.PushClip(glyphs);
            drawingContext.DrawGeometry(GetLiquidBrush(ResolveColour(AccentBrush)), null, liquidGeometry);
            if (ratio < 0.995)
            {
                var surfaceFigure = CreateWave(bounds, surface, amplitude, closeAtBottom: false);
                drawingContext.DrawGeometry(null, CreateCausticPen(ResolveColour(AccentBrush)), new PathGeometry([surfaceFigure]));
            }
            drawingContext.Pop();
        }

        drawingContext.DrawGeometry(null, GlyphRim, glyphs);
    }

    private static PathFigure CreateWave(Rect bounds, double surface, double amplitude, bool closeAtBottom)
    {
        var left = bounds.Left - 1;
        var right = bounds.Right + 1;
        var width = right - left;
        var figure = new PathFigure
        {
            StartPoint = new Point(left, surface),
            IsClosed = closeAtBottom,
            IsFilled = closeAtBottom
        };
        figure.Segments.Add(new BezierSegment(
            new Point(left + width * 0.16, surface - amplitude * 0.92),
            new Point(left + width * 0.39, surface + amplitude * 0.82),
            new Point(left + width * 0.55, surface + amplitude * 0.06),
            true));
        figure.Segments.Add(new BezierSegment(
            new Point(left + width * 0.72, surface - amplitude * 0.78),
            new Point(left + width * 0.88, surface + amplitude * 0.70),
            new Point(right, surface - amplitude * 0.10),
            true));
        if (closeAtBottom)
        {
            figure.Segments.Add(new LineSegment(new Point(right, bounds.Bottom + 2), true));
            figure.Segments.Add(new LineSegment(new Point(left, bounds.Bottom + 2), true));
        }
        return figure;
    }

    private static object CoerceValue(DependencyObject sender, object value) =>
        value is double number && double.IsFinite(number) ? Math.Clamp(number, 0, 100) : 0d;

    private static LinearGradientBrush GetLiquidBrush(Color accent)
    {
        if (LiquidBrushCache.TryGetValue(accent, out var cached)) return cached;

        var light = Mix(accent, Colors.White, 0.35);
        var deep = Mix(accent, Color.FromRgb(9, 43, 84), 0.18);
        var brush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop(Color.FromArgb(242, light.R, light.G, light.B), 0),
                new GradientStop(Color.FromArgb(236, accent.R, accent.G, accent.B), 0.34),
                new GradientStop(Color.FromArgb(244, deep.R, deep.G, deep.B), 1)
            }
        };
        brush.Freeze();
        LiquidBrushCache[accent] = brush;
        return brush;
    }

    private static Pen CreateCausticPen(Color accent)
    {
        var light = Mix(accent, Colors.White, 0.72);
        return FrozenPen(Color.FromArgb(218, light.R, light.G, light.B), 0.82);
    }

    private static Color ResolveColour(Brush brush) =>
        brush is SolidColorBrush solid ? solid.Color : Color.FromRgb(66, 169, 255);

    private static Color Mix(Color first, Color second, double amount)
    {
        amount = Math.Clamp(amount, 0, 1);
        return Color.FromRgb(
            (byte)Math.Round(first.R + (second.R - first.R) * amount),
            (byte)Math.Round(first.G + (second.G - first.G) * amount),
            (byte)Math.Round(first.B + (second.B - first.B) * amount));
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
