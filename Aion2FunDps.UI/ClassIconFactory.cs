using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Aion2FunDps.Core.Sessions;

namespace Aion2FunDps.UI;

public static class ClassIconFactory
{
    private static readonly IReadOnlyDictionary<JobClass, string> AssetFileNames = new Dictionary<JobClass, string>
    {
        [JobClass.Gladiator] = "gladiator.png",
        [JobClass.Guardian] = "guardian.png",
        [JobClass.Assassin] = "assassin.png",
        [JobClass.Archer] = "archer.png",
        [JobClass.Sorcerer] = "sorcerer.png",
        [JobClass.Spiritmaster] = "spiritmaster.png",
        [JobClass.Cleric] = "cleric.png",
        [JobClass.Songweaver] = "songweaver.png",
    };

    /// <summary>
    /// Per-class brightness multiplier applied to the source PNG at load
    /// time. Default 1.0 (untouched); a value > 1.0 lifts the RGB channels
    /// (alpha untouched) to compensate for source art that reads too dark
    /// against the meter's deep BG. Archer's source is a muted forest
    /// green that washes out next to the brighter Sorcerer/Cleric icons,
    /// so it gets a meaningful boost.
    /// </summary>
    private static readonly IReadOnlyDictionary<JobClass, double> BrightnessFactors = new Dictionary<JobClass, double>
    {
        [JobClass.Archer] = 1.45,
    };

    private static readonly IReadOnlyDictionary<JobClass, ImageSource> Cache = Enum
        .GetValues<JobClass>()
        .ToDictionary(jobClass => jobClass, CreateIcon);

    public static ImageSource GetIcon(JobClass jobClass) =>
        Cache.TryGetValue(jobClass, out var icon) ? icon : Cache[JobClass.Unknown];

    private static ImageSource CreateIcon(JobClass jobClass)
    {
        if (TryLoadAsset(jobClass, out var asset))
            return asset;

        var palette = GetPalette(jobClass);
        var group = new DrawingGroup();

        group.Children.Add(new GeometryDrawing(
            CreateBackgroundBrush(palette),
            null,
            CreateDiamond(2.5, 16)));

        group.Children.Add(new GeometryDrawing(
            null,
            CreatePen(Color.FromArgb(92, 255, 255, 255), 1.2),
            CreateDiamond(5.5, 16)));

        group.Children.Add(new GeometryDrawing(
            null,
            CreatePen(Color.FromArgb(118, palette.Glow.R, palette.Glow.G, palette.Glow.B), 1.4),
            CreateDiamond(8.25, 16)));

        foreach (var glyph in CreateGlyphs(jobClass, palette))
        {
            group.Children.Add(glyph);
        }

        group.Freeze();

        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private static bool TryLoadAsset(JobClass jobClass, out ImageSource? image)
    {
        image = null;

        if (!AssetFileNames.TryGetValue(jobClass, out var fileName))
            return false;

        var fullPath = Path.Combine(AppContext.BaseDirectory, "Assets", "ClassIcons", fileName);
        if (!File.Exists(fullPath))
            return false;

        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.UriSource = new Uri(fullPath, UriKind.Absolute);
        bitmap.EndInit();
        bitmap.Freeze();

        if (BrightnessFactors.TryGetValue(jobClass, out var factor) && factor > 1.0)
        {
            image = AdjustBrightness(bitmap, factor);
            return true;
        }

        image = bitmap;
        return true;
    }

    /// <summary>
    /// Linearly scales each RGB channel by <paramref name="factor"/> while
    /// leaving alpha alone, then returns a frozen WriteableBitmap. Suitable
    /// for one-shot use at startup since we run it inside a static cache
    /// initializer — no per-frame cost.
    /// </summary>
    private static ImageSource AdjustBrightness(BitmapSource source, double factor)
    {
        var converted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        int width = converted.PixelWidth;
        int height = converted.PixelHeight;
        int stride = width * 4;
        byte[] pixels = new byte[height * stride];
        converted.CopyPixels(pixels, stride, 0);

        for (int i = 0; i < pixels.Length; i += 4)
        {
            // BGRA — leave A alone, lift BGR.
            pixels[i + 0] = (byte)Math.Min(255, pixels[i + 0] * factor);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * factor);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * factor);
        }

        var result = new WriteableBitmap(width, height, converted.DpiX, converted.DpiY, PixelFormats.Bgra32, null);
        result.WritePixels(new Int32Rect(0, 0, width, height), pixels, stride, 0);
        result.Freeze();
        return result;
    }

    private static Brush CreateBackgroundBrush(IconPalette palette)
    {
        var brush = new RadialGradientBrush
        {
            Center = new Point(0.5, 0.38),
            GradientOrigin = new Point(0.5, 0.35),
            RadiusX = 0.72,
            RadiusY = 0.72
        };

        brush.GradientStops.Add(new GradientStop(Color.FromArgb(234, palette.Primary.R, palette.Primary.G, palette.Primary.B), 0));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, palette.Secondary.R, palette.Secondary.G, palette.Secondary.B), 0.72));
        brush.GradientStops.Add(new GradientStop(Color.FromArgb(255, 18, 20, 26), 1));
        brush.Freeze();
        return brush;
    }

    private static IEnumerable<Drawing> CreateGlyphs(JobClass jobClass, IconPalette palette)
    {
        var glowPen = CreatePen(Color.FromArgb(95, palette.Glow.R, palette.Glow.G, palette.Glow.B), 3.2);
        var strokePen = CreatePen(palette.Stroke, 1.7);
        var detailsPen = CreatePen(Color.FromArgb(220, 255, 247, 224), 1.15);
        var fill = new SolidColorBrush(Color.FromArgb(74, palette.Glow.R, palette.Glow.G, palette.Glow.B));
        fill.Freeze();

        foreach (var geometry in GetCoreGeometries(jobClass))
        {
            yield return new GeometryDrawing(null, glowPen, geometry);
            yield return new GeometryDrawing(null, strokePen, geometry);
        }

        foreach (var geometry in GetDetailGeometries(jobClass))
        {
            yield return new GeometryDrawing(fill, detailsPen, geometry);
        }
    }

    private static IEnumerable<Geometry> GetCoreGeometries(JobClass jobClass) =>
        jobClass switch
        {
            JobClass.Gladiator => new[]
            {
                CreatePolyline((10, 10), (16, 16), (22, 10)),
                CreatePolyline((11.5, 22), (16, 16), (20.5, 22)),
                CreatePolyline((13, 7.5), (16, 10.5), (19, 7.5)),
            },
            JobClass.Guardian => new[]
            {
                CreatePolyline((16, 8), (16, 24)),
                CreatePolyline((11.5, 11), (9.5, 16), (11.5, 21)),
                CreatePolyline((20.5, 11), (22.5, 16), (20.5, 21)),
                CreatePolyline((12, 24), (16, 20), (20, 24)),
            },
            JobClass.Assassin => new[]
            {
                CreatePolyline((16, 7.5), (19.5, 12.5), (16, 16), (12.5, 12.5), (16, 7.5)),
                CreatePolyline((8.5, 16), (12.5, 16)),
                CreatePolyline((19.5, 16), (23.5, 16)),
                CreatePolyline((16, 19.5), (16, 23.5)),
            },
            JobClass.Archer => new[]
            {
                CreatePolyline((16, 8), (16, 22.5)),
                CreatePolyline((11.5, 13.5), (16, 8), (20.5, 13.5)),
                CreatePolyline((12.5, 21), (16, 24), (19.5, 21)),
            },
            JobClass.Sorcerer => new Geometry[]
            {
                new EllipseGeometry(new Point(16, 16), 5.8, 5.8),
                CreatePolyline((16, 8.5), (16, 12.2)),
                CreatePolyline((16, 19.8), (16, 23.5)),
                CreatePolyline((8.5, 16), (12.2, 16)),
                CreatePolyline((19.8, 16), (23.5, 16)),
            },
            JobClass.Spiritmaster => new[]
            {
                CreatePolyline((16, 9), (16, 22.5)),
                CreatePolyline((16, 12), (11.5, 15.5)),
                CreatePolyline((16, 12), (20.5, 15.5)),
                CreatePolyline((12.5, 22.5), (16, 19.5), (19.5, 22.5)),
            },
            JobClass.Cleric => new[]
            {
                CreatePolyline((16, 8.5), (16, 23.5)),
                CreatePolyline((10.5, 13.5), (21.5, 13.5)),
                CreatePolyline((12.5, 20), (16, 23.5), (19.5, 20)),
            },
            JobClass.Songweaver => new[]
            {
                CreatePolyline((11, 10), (14.5, 22)),
                CreatePolyline((21, 10), (17.5, 22)),
                CreatePolyline((12.5, 15), (19.5, 15)),
                CreatePolyline((13.5, 19), (18.5, 19)),
            },
            _ => new[]
            {
                CreatePolyline((10.5, 11), (16, 8.5), (21.5, 11)),
                CreatePolyline((16, 13), (16, 18)),
                CreatePolyline((16, 21.5), (16, 23)),
            },
        };

    private static IEnumerable<Geometry> GetDetailGeometries(JobClass jobClass) =>
        jobClass switch
        {
            JobClass.Gladiator => new[]
            {
                CreatePolygon((13.5, 13.5), (16, 11.5), (18.5, 13.5), (16, 18)),
            },
            JobClass.Guardian => new[]
            {
                CreatePolygon((13.5, 14.5), (16, 12), (18.5, 14.5), (16, 18.5)),
            },
            JobClass.Assassin => new[]
            {
                CreatePolygon((16, 11.5), (18.5, 16), (16, 20.5), (13.5, 16)),
            },
            JobClass.Archer => new[]
            {
                CreatePolygon((16, 9.5), (18, 13), (16, 16.5), (14, 13)),
            },
            JobClass.Sorcerer => new[]
            {
                new EllipseGeometry(new Point(16, 16), 2.1, 2.1),
            },
            JobClass.Spiritmaster => new[]
            {
                CreatePolygon((16, 10.5), (18.2, 14), (16, 17.2), (13.8, 14)),
            },
            JobClass.Cleric => new[]
            {
                new EllipseGeometry(new Point(16, 13.5), 1.7, 1.7),
            },
            JobClass.Songweaver => new[]
            {
                CreatePolygon((16, 11.8), (17.8, 14.5), (16, 17.2), (14.2, 14.5)),
            },
            _ => Array.Empty<Geometry>(),
        };

    private static StreamGeometry CreateDiamond(double inset, double center)
    {
        var half = center - inset;
        return CreatePolygon(
            (center, inset),
            (center + half, center),
            (center, center + half),
            (inset, center));
    }

    private static StreamGeometry CreatePolyline(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: false, isClosed: false);

        for (int i = 1; i < points.Length; i++)
        {
            context.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static StreamGeometry CreatePolygon(params (double X, double Y)[] points)
    {
        var geometry = new StreamGeometry();
        using var context = geometry.Open();
        context.BeginFigure(new Point(points[0].X, points[0].Y), isFilled: true, isClosed: true);

        for (int i = 1; i < points.Length; i++)
        {
            context.LineTo(new Point(points[i].X, points[i].Y), isStroked: true, isSmoothJoin: true);
        }

        geometry.Freeze();
        return geometry;
    }

    private static Pen CreatePen(Color color, double thickness)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();

        var pen = new Pen(brush, thickness)
        {
            StartLineCap = PenLineCap.Round,
            EndLineCap = PenLineCap.Round,
            LineJoin = PenLineJoin.Round
        };
        pen.Freeze();
        return pen;
    }

    private static IconPalette GetPalette(JobClass jobClass) =>
        jobClass switch
        {
            JobClass.Gladiator => new(Color.FromRgb(79, 49, 20), Color.FromRgb(36, 28, 24), Color.FromRgb(245, 180, 92), Color.FromRgb(255, 217, 150)),
            JobClass.Guardian => new(Color.FromRgb(92, 67, 21), Color.FromRgb(38, 31, 21), Color.FromRgb(231, 205, 97), Color.FromRgb(255, 239, 176)),
            JobClass.Assassin => new(Color.FromRgb(60, 34, 94), Color.FromRgb(28, 24, 42), Color.FromRgb(186, 112, 255), Color.FromRgb(233, 203, 255)),
            JobClass.Archer => new(Color.FromRgb(35, 70, 96), Color.FromRgb(20, 25, 34), Color.FromRgb(118, 209, 255), Color.FromRgb(210, 246, 255)),
            JobClass.Sorcerer => new(Color.FromRgb(86, 34, 98), Color.FromRgb(30, 20, 44), Color.FromRgb(236, 113, 255), Color.FromRgb(255, 214, 255)),
            JobClass.Spiritmaster => new(Color.FromRgb(28, 89, 86), Color.FromRgb(19, 31, 34), Color.FromRgb(103, 220, 197), Color.FromRgb(210, 255, 242)),
            JobClass.Cleric => new(Color.FromRgb(95, 76, 22), Color.FromRgb(34, 29, 21), Color.FromRgb(255, 215, 92), Color.FromRgb(255, 243, 185)),
            JobClass.Songweaver => new(Color.FromRgb(66, 40, 100), Color.FromRgb(25, 20, 40), Color.FromRgb(212, 137, 255), Color.FromRgb(247, 223, 255)),
            _ => new(Color.FromRgb(67, 71, 80), Color.FromRgb(27, 29, 35), Color.FromRgb(163, 171, 184), Color.FromRgb(232, 235, 240)),
        };

    private readonly record struct IconPalette(Color Primary, Color Secondary, Color Glow, Color Stroke);
}
