using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace MovieMakerReborn.Views;

/// <summary>
/// Labs: a nostalgic glass loading screen shown for about ten seconds at startup.
/// The sky follows the theme: bright clouds (Morning), amber sunset (Evening), starry night (Midnight).
/// </summary>
public class SplashWindow : Window
{
    public event Action Completed;
    readonly TextBlock _status;
    readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    readonly List<(UIElement el, double period, double phase)> _twinkles = new();
    DateTime _start;
    public double Seconds { get; set; } = 10;
    const double W = 460, H = 285, TrackW = 360;

    static readonly (double at, string text)[] Steps =
    {
        (0.00, "Initiating software…"), (0.15, "Booting up…"), (0.33, "Loading effects and transitions…"),
        (0.52, "Warming up the projector…"), (0.72, "Threading the film…"), (0.90, "Rolling…"),
    };

    static Color C(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
    static LinearGradientBrush Lin(Point a, Point b, params (double o, uint c)[] stops)
    {
        var br = new LinearGradientBrush { StartPoint = a, EndPoint = b };
        foreach (var (o, c) in stops) br.GradientStops.Add(new GradientStop(C(c), o));
        br.Freeze(); return br;
    }
    static LinearGradientBrush V(params (double o, uint c)[] s) => Lin(new Point(0, 0), new Point(0, 1), s);
    static RadialGradientBrush Rad(uint inner, uint outer, double rx = 0.5, double ry = 0.5)
    {
        var b = new RadialGradientBrush(C(inner), C(outer)) { RadiusX = rx, RadiusY = ry };
        b.Freeze(); return b;
    }
    /// <summary>Large display text: smooth outlines instead of pixel-snapped "Display" text.</summary>
    static T Smooth<T>(T tb) where T : FrameworkElement
    {
        TextOptions.SetTextFormattingMode(tb, TextFormattingMode.Ideal);
        TextOptions.SetTextRenderingMode(tb, TextRenderingMode.Grayscale);
        TextOptions.SetTextHintingMode(tb, TextHintingMode.Animated);
        return tb;
    }

    static Ellipse Blob(double x, double y, double w, double h, Brush fill)
    {
        var e = new Ellipse { Width = w, Height = h, Fill = fill };
        Canvas.SetLeft(e, x); Canvas.SetTop(e, y);
        return e;
    }

    /// <summary>A soft, puffy cloud built from overlapping blurred ellipses.</summary>
    static Canvas Cloud(double x, double y, double s, uint top, uint bottom, double blur = 5)
    {
        var c = new Canvas { Width = 200 * s, Height = 90 * s, Effect = new BlurEffect { Radius = blur } };
        var fill = V((0, top), (1, bottom));
        c.Children.Add(Blob(10 * s, 40 * s, 180 * s, 45 * s, fill));
        c.Children.Add(Blob(35 * s, 18 * s, 70 * s, 60 * s, fill));
        c.Children.Add(Blob(80 * s, 4 * s, 80 * s, 72 * s, fill));
        c.Children.Add(Blob(135 * s, 24 * s, 55 * s, 50 * s, fill));
        Canvas.SetLeft(c, x); Canvas.SetTop(c, y);
        return c;
    }

    Canvas BuildSky(string theme)
    {
        var sky = new Canvas { Width = W, Height = H };
        string file = theme switch { "light" => "splash-morning.jpg", "evening" => "splash-evening.jpg", _ => "splash-midnight.jpg" };
        try
        {
            var img = new BitmapImage();
            img.BeginInit();
            img.UriSource = new Uri($"pack://application:,,,/Assets/{file}");
            img.CacheOption = BitmapCacheOption.OnLoad;
            img.EndInit();
            sky.Background = new ImageBrush(img) { Stretch = Stretch.UniformToFill };
        }
        catch { sky.Background = V((0, 0xFF0A1530), (1, 0xFF1A2A50)); }

        // Gentle tint so white text stays readable on every sky
        uint tint = theme switch { "light" => 0x38103050u, "evening" => 0x1A000000u, _ => 0x20000000u };
        sky.Children.Add(new Rectangle { Width = W, Height = H, Fill = new SolidColorBrush(C(tint)) });
        // Soft darker pool behind the logo, name and bar
        var pool = new Ellipse { Width = W * 1.1, Height = H * 0.95, Fill = Rad(theme == "light" ? 0x40082440u : 0x30000000u, 0x00000000) };
        Canvas.SetLeft(pool, -W * 0.05); Canvas.SetTop(pool, H * 0.05);
        sky.Children.Add(pool);

        if (theme is not "light" and not "evening")
        {
            // A few twinkling stars on top of the photo
            var rnd = new Random(1987);
            for (int i = 0; i < 34; i++)
            {
                double sz = 1.4 + rnd.NextDouble() * 1.6;
                var star = Blob(rnd.NextDouble() * W, rnd.NextDouble() * H * 0.7, sz, sz, Brushes.White);
                star.Effect = new DropShadowEffect { Color = Colors.White, BlurRadius = 5, ShadowDepth = 0, Opacity = 0.9 };
                sky.Children.Add(star);
                _twinkles.Add((star, 1.2 + rnd.NextDouble() * 2.5, rnd.NextDouble() * 6));
            }
        }
        else if (theme == "evening")
        {
            var rnd = new Random(42);
            for (int i = 0; i < 14; i++)
            {
                double sz = 1.2 + rnd.NextDouble() * 1.2;
                var star = Blob(rnd.NextDouble() * W, rnd.NextDouble() * H * 0.45, sz, sz, Brushes.White);
                sky.Children.Add(star);
                _twinkles.Add((star, 1.5 + rnd.NextDouble() * 2.5, rnd.NextDouble() * 6));
            }
        }
        return sky;
    }

    public SplashWindow(string theme = "dark")
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        Width = W + 24; Height = H + 24;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Topmost = true;
        Title = "Gerbera Studio";
        try { Icon = new BitmapImage(new Uri("pack://application:,,,/app.ico")); } catch { }
        Opacity = 0;

        var root = new Grid { Margin = new Thickness(12) };
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Ideal);
        var shadowHost = new Border { CornerRadius = new CornerRadius(5), Background = Brushes.Black, Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.45 } };
        root.Children.Add(shadowHost);

        var sky = BuildSky(theme);
        var skyHost = new Grid { ClipToBounds = true, Clip = new RectangleGeometry(new Rect(0, 0, W, H), 5, 5) };
        skyHost.Children.Add(sky);
        // glass gloss across the top and a gentle vignette
        skyHost.Children.Add(new Border { Height = H * 0.45, VerticalAlignment = VerticalAlignment.Top, Background = V((0, 0x26FFFFFF), (1, 0x00FFFFFF)) });
        skyHost.Children.Add(new Rectangle { Fill = Rad(0x00000000, 0x40000000, 0.75, 0.75) });
        root.Children.Add(skyHost);

        // Glass frame
        root.Children.Add(new Border { CornerRadius = new CornerRadius(5), BorderThickness = new Thickness(1.2), BorderBrush = new SolidColorBrush(C(0xCC0A1424)), IsHitTestVisible = false });
        root.Children.Add(new Border { CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = V((0, 0x99FFFFFF), (1, 0x22FFFFFF)), Margin = new Thickness(1.2), IsHitTestVisible = false });

        // Content: logo + name centred, status + bar below
        var shadow = new DropShadowEffect { BlurRadius = 14, ShadowDepth = 0, Opacity = 0.55, Color = Colors.Black, RenderingBias = RenderingBias.Quality };
        var content = new Grid { Margin = new Thickness(40, 18, 40, 26) };
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var head = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 6, 0, 0) };
        var logo = new Image { Width = 72, Height = 72, Margin = new Thickness(0, 0, 12, 0) };
        try { logo.Source = new BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
        RenderOptions.SetBitmapScalingMode(logo, BitmapScalingMode.HighQuality);
        logo.Effect = new DropShadowEffect { BlurRadius = 12, ShadowDepth = 1, Opacity = 0.5 };
        head.Children.Add(logo);
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontSize = 38, Foreground = Brushes.White, Effect = shadow };
        // Bundled Outfit typeface (falls back to Segoe UI if the font files are missing)
        var baseUri = new Uri("pack://application:,,,/");
        var outfitMedium = new FontFamily(baseUri, "./Assets/Fonts/#Outfit Medium, ./Assets/Fonts/#Outfit, Segoe UI Semibold");
        var outfitLight = new FontFamily(baseUri, "./Assets/Fonts/#Outfit ExtraLight, ./Assets/Fonts/#Outfit, Segoe UI Light");
        name.Inlines.Add(new System.Windows.Documents.Run("Gerbera ") { FontFamily = outfitMedium });
        name.Inlines.Add(new System.Windows.Documents.Run("Studio") { FontFamily = outfitLight });
        head.Children.Add(Smooth(name));
        content.Children.Add(head);

        _status = new TextBlock
        {
            FontSize = 15, FontFamily = new FontFamily("Segoe UI"), Foreground = Brushes.White, Effect = shadow,
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 9), Text = Steps[0].text,
        };
        Smooth(_status);
        Grid.SetRow(_status, 1);
        content.Children.Add(_status);

        // Silver glass track with a glowing violet pulse gliding across it
        var track = new Border
        {
            Width = TrackW, Height = 16, HorizontalAlignment = HorizontalAlignment.Center, CornerRadius = new CornerRadius(3),
            BorderThickness = new Thickness(1), BorderBrush = new SolidColorBrush(C(0xFF6E6E6E)),
            Background = V((0, 0xFFF4F4F4), (0.42, 0xFFE2E2E2), (0.5, 0xFFCDCDCD), (1, 0xFFDCDCDC)),
        };
        var inside = new Grid { ClipToBounds = true, Clip = new RectangleGeometry(new Rect(0, 0, TrackW - 2, 14), 2, 2) };
        var glow = new Grid { Width = 200, HorizontalAlignment = HorizontalAlignment.Left };
        // Glow colour follows the theme: green (Morning), amber (Evening), violet (Midnight)
        var (haze, core, hot) = theme switch
        {
            "light" => (0x00A8F07Au, 0x0012B83Au, 0x00C8FFD2u),
            "evening" => (0x00FFC255u, 0x00E8860Fu, 0x00FFE7B0u),
            _ => (0x00B66CFFu, 0x008A2BE2u, 0x00E3C2FFu),
        };
        glow.Children.Add(new Rectangle { Fill = Rad(0xB0000000 | haze, haze, 0.5, 0.9) });
        glow.Children.Add(new Rectangle { Margin = new Thickness(38, 3, 38, 3), Fill = Rad(0xFF000000 | core, core, 0.5, 0.55) });
        glow.Children.Add(new Rectangle { Margin = new Thickness(68, 5, 68, 5), Fill = Rad(0xCC000000 | hot, hot, 0.5, 0.5) });
        var tt = new TranslateTransform(-200, 0);
        glow.RenderTransform = tt;
        inside.Children.Add(glow);
        inside.Children.Add(new Border { Height = 7, VerticalAlignment = VerticalAlignment.Top, Background = V((0, 0x99FFFFFF), (1, 0x22FFFFFF)) });
        inside.Children.Add(new Border { BorderThickness = new Thickness(0, 1, 0, 0), BorderBrush = new SolidColorBrush(C(0xCCFFFFFF)) });
        track.Child = inside;
        Grid.SetRow(track, 2);
        content.Children.Add(track);

        var ver = new TextBlock { Text = "Version 1.1", FontSize = 10.5, Foreground = new SolidColorBrush(C(0xCCFFFFFF)), HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 8, 0, 0), Effect = shadow };
        Smooth(ver);
        Grid.SetRow(ver, 3);
        content.Children.Add(ver);
        root.Children.Add(content);
        Content = root;

        MouseLeftButtonDown += (_, _) => { try { DragMove(); } catch { } };
        Loaded += (_, _) =>
        {
            BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(500)));
            tt.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-200, TrackW, TimeSpan.FromSeconds(2.2))
            { RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut } });
            foreach (var (el, period, phase) in _twinkles)
                el.BeginAnimation(OpacityProperty, new DoubleAnimation(0.15, 1, TimeSpan.FromSeconds(period / 2)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(phase % period) });
            _start = DateTime.Now;
            _timer.Tick += Tick;
            _timer.Start();
        };
    }

    void Tick(object sender, EventArgs e)
    {
        double p = Math.Clamp((DateTime.Now - _start).TotalSeconds / Math.Max(0.5, Seconds), 0, 1);
        foreach (var (at, text) in Steps) if (p >= at) _status.Text = text;
        if (p >= 1)
        {
            _timer.Stop();
            Completed?.Invoke();
        }
    }

    public void FadeOutAndClose()
    {
        var a = new DoubleAnimation(Opacity, 0, TimeSpan.FromMilliseconds(400));
        a.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, a);
    }
}
