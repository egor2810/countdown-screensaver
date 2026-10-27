using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace CountdownSaver;

// Живой фон «как ambient-лампа»: несколько больших мягких цветных пятен медленно плывут,
// дышат и перетекают друг в друга. Цвета берутся из обложки и меняются плавно.
// Сделано на радиальных градиентах без BlurEffect — почти не нагружает видеокарту.
public sealed class Ambient
{
    public static readonly Color[] DefaultPalette =
    {
        Color.FromRgb(0x7B, 0x3F, 0xE4),
        Color.FromRgb(0xFF, 0x4F, 0x81),
        Color.FromRgb(0xFF, 0x9A, 0x62),
        Color.FromRgb(0x3A, 0x6F, 0xF7),
    };

    // Центр, радиус, период движения (сек) и амплитуда для каждого пятна (в координатах 1920×1080).
    static readonly (double X, double Y, double R, double Period, double Amp)[] Blobs =
    {
        (350, 250, 950, 26, 320),
        (1550, 280, 1000, 33, 360),
        (500, 880, 900, 29, 300),
        (1450, 860, 950, 23, 340),
        (960, 540, 750, 41, 420),
    };

    readonly List<(GradientStop Inner, GradientStop Mid, GradientStop Outer)> _stops = new();
    readonly FrameworkElement _layer;
    readonly Random _random = new();

    public Ambient(Canvas canvas)
    {
        _layer = canvas;
        for (int i = 0; i < Blobs.Length; i++)
        {
            var (x, y, r, period, amp) = Blobs[i];
            var color = DefaultPalette[i % DefaultPalette.Length];
            var inner = new GradientStop(WithAlpha(color, 190), 0);
            var mid = new GradientStop(WithAlpha(color, 90), 0.45);
            var outer = new GradientStop(WithAlpha(color, 0), 1);
            _stops.Add((inner, mid, outer));

            double w = r * 2, h = r * 1.6;
            var ellipse = new Ellipse
            {
                Width = w,
                Height = h,
                Fill = new RadialGradientBrush(new GradientStopCollection { inner, mid, outer }),
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(ellipse, x - w / 2);
            Canvas.SetTop(ellipse, y - h / 2);

            var scale = new ScaleTransform(1, 1, w / 2, h / 2);
            var rotate = new RotateTransform(0, w / 2, h / 2);
            var move = new TranslateTransform();
            ellipse.RenderTransform = new TransformGroup { Children = { scale, rotate, move } };
            canvas.Children.Add(ellipse);

            // Разные периоды по X и Y дают плавные «восьмёрки», а не простые качели.
            Wave(move, TranslateTransform.XProperty, -amp, amp, period);
            Wave(move, TranslateTransform.YProperty, -amp * 0.7, amp * 0.7, period * 1.37);
            Wave(scale, ScaleTransform.ScaleXProperty, 0.85, 1.2, period * 0.83);
            Wave(scale, ScaleTransform.ScaleYProperty, 0.9, 1.15, period * 0.71);
            Wave(rotate, RotateTransform.AngleProperty, -25, 25, period * 1.9);
        }
    }

    void Wave(Animatable target, DependencyProperty property, double from, double to, double seconds)
    {
        var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
            // Отрицательное начало — пятна стартуют с разных фаз, а не синхронно.
            BeginTime = TimeSpan.FromSeconds(-_random.NextDouble() * seconds * 2),
        };
        Timeline.SetDesiredFrameRate(animation, 30);
        target.BeginAnimation(property, animation);
    }

    public void SetPalette(IReadOnlyList<Color> palette)
    {
        var duration = TimeSpan.FromSeconds(3);
        for (int i = 0; i < _stops.Count; i++)
        {
            var color = palette[i % palette.Count];
            var (inner, mid, outer) = _stops[i];
            Fade(inner, WithAlpha(color, 190), duration);
            Fade(mid, WithAlpha(color, 90), duration);
            Fade(outer, WithAlpha(color, 0), duration);
        }
    }

    // На паузе лампа приглушается.
    public void SetBrightness(double opacity) =>
        _layer.BeginAnimation(UIElement.OpacityProperty,
            new DoubleAnimation(opacity, TimeSpan.FromSeconds(2)), HandoffBehavior.SnapshotAndReplace);

    static void Fade(GradientStop stop, Color to, TimeSpan duration) =>
        stop.BeginAnimation(GradientStop.ColorProperty,
            new ColorAnimation(to, duration) { EasingFunction = new SineEase() }, HandoffBehavior.SnapshotAndReplace);

    static Color WithAlpha(Color c, byte a) => Color.FromArgb(a, c.R, c.G, c.B);

    // ---------- Палитра из обложки ----------

    public static Color[] ExtractPalette(ImageSource? source)
    {
        if (source is not BitmapSource bitmap || bitmap.PixelWidth == 0 || bitmap.PixelHeight == 0)
            return DefaultPalette;
        try
        {
            const double size = 24;
            var small = new FormatConvertedBitmap(
                new TransformedBitmap(bitmap, new ScaleTransform(size / bitmap.PixelWidth, size / bitmap.PixelHeight)),
                PixelFormats.Bgra32, null, 0);
            int w = small.PixelWidth, h = small.PixelHeight;
            var pixels = new byte[w * h * 4];
            small.CopyPixels(pixels, w * 4, 0);

            var colors = new List<(double H, double S, double V)>();
            for (int i = 0; i < pixels.Length; i += 4)
                colors.Add(ToHsv(pixels[i + 2], pixels[i + 1], pixels[i]));

            // Самые «живые» цвета (насыщенные и не слишком тёмные), с разными оттенками.
            var picked = new List<(double H, double S, double V)>();
            foreach (var c in colors.OrderByDescending(c => c.S * (0.3 + c.V)))
            {
                if (c.V < 0.15)
                    continue;
                if (picked.All(p => HueDistance(p.H, c.H) > 28))
                    picked.Add(c);
                if (picked.Count == 4)
                    break;
            }
            if (picked.Count == 0)
                picked.Add(colors.OrderByDescending(c => c.V).First());

            // Не хватает оттенков — достраиваем соседними от главного.
            var main = picked[0];
            double[] shifts = { 30, -30, 60 };
            for (int i = 0; picked.Count < 4; i++)
                picked.Add(((main.H + shifts[i] + 360) % 360, main.S, main.V));

            return picked.Select(c =>
            {
                // Серые обложки оставляем спокойными, цветные — делаем сочнее и светлее, как свет лампы.
                double s = c.S < 0.12 ? c.S : Math.Clamp(c.S * 1.25, 0.5, 0.95);
                return FromHsv(c.H, s, 0.92);
            }).ToArray();
        }
        catch (Exception ex)
        {
            Log.Error("Ambient: палитра", ex);
            return DefaultPalette;
        }
    }

    static double HueDistance(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    static (double H, double S, double V) ToHsv(byte r8, byte g8, byte b8)
    {
        double r = r8 / 255.0, g = g8 / 255.0, b = b8 / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
        double h = d == 0 ? 0
            : max == r ? 60 * (((g - b) / d + 6) % 6)
            : max == g ? 60 * ((b - r) / d + 2)
            : 60 * ((r - g) / d + 4);
        return (h, max == 0 ? 0 : d / max, max);
    }

    static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }
}
