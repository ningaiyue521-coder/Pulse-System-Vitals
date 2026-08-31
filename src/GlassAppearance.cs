using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;

namespace DemoApp;

internal sealed class GlassPreferences
{
    public const double DefaultTransparency = 32;
    public const double DefaultRefraction = 58;

    public double Transparency { get; set; } = DefaultTransparency;
    public double Refraction { get; set; } = DefaultRefraction;

    public static GlassPreferences Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var preferences = JsonSerializer.Deserialize<GlassPreferences>(File.ReadAllText(SettingsPath));
                if (preferences is not null)
                {
                    preferences.Normalize();
                    return preferences;
                }
            }
        }
        catch
        {
        }

        return new GlassPreferences();
    }

    public void Save()
    {
        try
        {
            Normalize();
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch
        {
            // A read-only launch directory must not prevent Pulse from closing.
        }
    }

    private void Normalize()
    {
        Transparency = Math.Clamp(Transparency, 10, 75);
        Refraction = Math.Clamp(Refraction, 0, 100);
    }

    private static string SettingsPath => Path.Combine(AppContext.BaseDirectory, "Pulse.settings.json");
}

internal static class GlassPalette
{
    public static void ApplyMain(
        ResourceDictionary resources,
        GlassPreferences preferences,
        double lightX = 0.22,
        double lightY = 0.10)
    {
        var transparency = Math.Clamp(preferences.Transparency / 100.0, 0.1, 0.75);
        var refraction = Math.Clamp(preferences.Refraction / 100.0, 0, 1);
        var light = new Point(Math.Clamp(lightX, 0, 1), Math.Clamp(lightY, 0, 1));
        var opposite = new Point(1 - light.X, 1 - light.Y);

        // The native DWM blur remains visible through the shell. Content is a
        // stable reading plane and deliberately does not fade with the shell.
        resources["WindowGlassOverlay"] = Solid(Alpha(28, 0, transparency), 246, 250, 255);
        resources["WindowChromeFill"] = Solid(Alpha(22, 0, transparency), 255, 255, 255);
        resources["ContentSurfaceFill"] = Solid(Alpha(252, 234, transparency), 248, 249, 251);
        resources["ContentSurfaceEdge"] = Solid(Alpha(94, 48, transparency), 255, 255, 255);
        resources["ContentLine"] = Solid(18, 30, 38, 48);
        resources["FunctionalGlassFill"] = Solid(Alpha(208, 105, transparency), 255, 255, 255);
        resources["SettingsGlassFill"] = Solid(Alpha(250, 162, transparency), 252, 253, 255);
        resources["HeaderGlassFill"] = Solid(Alpha(214, 116, transparency), 255, 255, 255);
        resources["GlassButtonFill"] = Solid(Alpha(164, 72, transparency), 255, 255, 255);
        resources["StatusChipFill"] = Solid(25, 10, 132, 255);

        resources["HeroAmbientFill"] = DirectionalGradient(
            light,
            opposite,
            (54, 255, 255, 255, 0.00),
            (30, 237, 245, 255, 0.58),
            (17, 10, 132, 255, 1.00));

        // Refraction is now rendered as a safe inner optical rim. Every colour
        // is light or cool-tinted: the slider can strengthen the edge without
        // ever introducing a dark overlay on the outermost window pixels.
        var functionalEdge = DirectionalGradient(
            light,
            opposite,
            (RefractionAlpha(72, 164, refraction), 255, 255, 255, 0.00),
            (RefractionAlpha(28, 76, refraction), 240, 250, 255, 0.52),
            (RefractionAlpha(14, 42, refraction), 172, 220, 248, 1.00));
        resources["FunctionalGlassEdge"] = functionalEdge;
        resources["GlassCardEdge"] = functionalEdge;

        resources["WindowFrameEdgeBrush"] = DirectionalGradient(
            light,
            opposite,
            (RefractionAlpha(64, 150, refraction), 255, 255, 255, 0.00),
            (RefractionAlpha(22, 66, refraction), 240, 250, 255, 0.46),
            (RefractionAlpha(10, 34, refraction), 175, 220, 248, 1.00));
        resources["WindowOpticalEdgeBrush"] = DirectionalGradient(
            light,
            opposite,
            (RefractionAlpha(30, 172, refraction), 255, 255, 255, 0.00),
            (RefractionAlpha(10, 82, refraction), 210, 241, 255, 0.54),
            (RefractionAlpha(6, 48, refraction), 178, 202, 255, 1.00));
        resources["WindowInnerCausticBrush"] = PrismaticEdge(refraction, light, opposite);
        resources["WindowSpecularBrush"] = SpotSpecular(refraction, light.X, 210, 92);
        resources["WindowOpticalEdgeThickness"] = refraction <= 0.005
            ? new Thickness(0)
            : new Thickness(0.75 + refraction * 1.25);
        resources["WindowInnerCausticThickness"] = refraction <= 0.005
            ? new Thickness(0)
            : new Thickness(0.55 + refraction * 0.85);
        resources["FunctionalSheen"] = InteractiveSheen(refraction, light);
    }

    public static void ApplyMini(ResourceDictionary resources, GlassPreferences preferences)
    {
        var transparency = Math.Clamp(preferences.Transparency / 100.0, 0.1, 0.75);
        var refraction = Math.Clamp(preferences.Refraction / 100.0, 0, 1);

        // iOS 27 treats clear glass as a floating functional layer, not a
        // milky content card. A restrained dark luminosity layer protects
        // white telemetry over bright games while the real backdrop remains
        // visible through the native compositor.
        resources["OsdSurfaceFill"] = DirectionalGradient(
            new Point(0.08, 0),
            new Point(0.92, 1),
            (Alpha(158, 58, transparency), 27, 31, 39, 0.00),
            (Alpha(132, 42, transparency), 18, 23, 31, 0.52),
            (Alpha(176, 66, transparency), 12, 17, 25, 1.00));
        resources["OsdOuterEdge"] = DirectionalGradient(
            new Point(0.10, 0),
            new Point(0.90, 1),
            (RefractionAlpha(118, 210, refraction), 255, 255, 255, 0.00),
            (RefractionAlpha(34, 82, refraction), 231, 247, 255, 0.48),
            (RefractionAlpha(14, 46, refraction), 135, 197, 255, 1.00));
        resources["OsdInnerEdge"] = DirectionalGradient(
            new Point(0.84, 0),
            new Point(0.16, 1),
            (RefractionAlpha(22, 74, refraction), 146, 211, 255, 0.00),
            (RefractionAlpha(12, 42, refraction), 255, 255, 255, 0.56),
            (RefractionAlpha(8, 30, refraction), 190, 165, 255, 1.00));
        resources["OsdDivider"] = Solid(42, 255, 255, 255);
        resources["OsdSpecularBrush"] = SpotSpecular(refraction, 0.31, 224, 146);
    }

    private static SolidColorBrush Solid(byte alpha, byte red, byte green, byte blue)
    {
        var brush = new SolidColorBrush(Color.FromArgb(alpha, red, green, blue));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush DirectionalGradient(
        Point start,
        Point end,
        params (byte A, byte R, byte G, byte B, double Offset)[] stops)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        foreach (var stop in stops)
        {
            brush.GradientStops.Add(new GradientStop(Color.FromArgb(stop.A, stop.R, stop.G, stop.B), stop.Offset));
        }
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush SpotSpecular(double refraction, double center, byte strong, byte soft)
    {
        center = Math.Clamp(center, 0.16, 0.84);
        var inner = 0.11;
        var outer = 0.25;
        var brush = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, Math.Max(0, center - outer)));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(RefractionAlpha(10, soft, refraction), 255, 255, 255), Math.Max(0, center - inner)));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(RefractionAlpha(30, strong, refraction), 255, 255, 255), center));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(RefractionAlpha(10, soft, refraction), 255, 255, 255), Math.Min(1, center + inner)));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, Math.Min(1, center + outer)));
        brush.Freeze();
        return brush;
    }

    private static LinearGradientBrush PrismaticEdge(double refraction, Point start, Point end)
    {
        var brush = new LinearGradientBrush { StartPoint = start, EndPoint = end };
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(RefractionAlpha(10, 82, refraction), 130, 218, 255), 0));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(RefractionAlpha(7, 40, refraction), 255, 255, 255), 0.28));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.53));
        brush.GradientStops.Add(new GradientStop(
            Color.FromArgb(RefractionAlpha(5, 34, refraction), 177, 162, 255), 0.78));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
        brush.Freeze();
        return brush;
    }

    private static RadialGradientBrush InteractiveSheen(double refraction, Point light)
    {
        var brush = new RadialGradientBrush
        {
            Center = light,
            GradientOrigin = light,
            RadiusX = 0.82,
            RadiusY = 1.12
        };
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(RefractionAlpha(30, 148, refraction), 255, 255, 255), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(RefractionAlpha(8, 44, refraction), 255, 255, 255), 0.38));
        brush.GradientStops.Add(new GradientStop(Colors.Transparent, 0.80));
        brush.Freeze();
        return brush;
    }

    private static byte Alpha(int opaque, int clear, double transparency) =>
        (byte)Math.Clamp((int)Math.Round(opaque + (clear - opaque) * transparency), 0, 255);

    private static byte RefractionAlpha(int minimum, int maximum, double refraction) =>
        (byte)Math.Clamp((int)Math.Round(minimum + (maximum - minimum) * refraction), 0, 255);
}
