using System.Collections.Concurrent;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>Renders styled text to premultiplied BGRA bitmaps on a dedicated STA thread.</summary>
public static class TextRasterizer
{
    public sealed class TextImage { public byte[] Data; public int W, H; }

    static Dispatcher _disp;
    static readonly object Lock = new();
    static readonly ConcurrentDictionary<string, TextImage> Cache = new();

    static Dispatcher Disp
    {
        get
        {
            lock (Lock)
            {
                if (_disp != null) return _disp;
                var ready = new ManualResetEventSlim();
                var th = new Thread(() =>
                {
                    _disp = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                }) { IsBackground = true, Name = "text-raster" };
                th.SetApartmentState(ApartmentState.STA);
                th.Start();
                ready.Wait();
                return _disp;
            }
        }
    }

    public static TextImage Render(string text, TextStyle st, int outW, int outH, double sizeScale = 1)
    {
        if (string.IsNullOrEmpty(text) || st == null) return null;
        string key = $"{text}\u0001{st.Key}\u0001{outW}x{outH}\u0001{sizeScale:F3}";
        if (Cache.TryGetValue(key, out var img)) return img;
        if (Cache.Count > 300) Cache.Clear();
        img = Disp.Invoke(() => RenderCore(text, st, outW, outH, sizeScale));
        if (img != null) Cache[key] = img;
        return img;
    }

    static Color ToColor(string hex, Color fallback)
    {
        try { return (Color)ColorConverter.ConvertFromString(hex); } catch { return fallback; }
    }

    static TextImage RenderCore(string text, TextStyle st, int outW, int outH, double sizeScale)
    {
        double size = Math.Max(4, st.Size * outH * sizeScale);
        var family = new FontFamily(string.IsNullOrWhiteSpace(st.Font) ? "Segoe UI" : st.Font);
        var tf = new Typeface(family, st.Italic ? FontStyles.Italic : FontStyles.Normal, st.Bold ? FontWeights.Bold : FontWeights.Normal, FontStretches.Normal);
        var fill = new SolidColorBrush(ToColor(st.Color, Colors.White)); fill.Freeze();
        var ft = new FormattedText(text, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf, size, fill, 1.0)
        {
            MaxTextWidth = outW * 0.9,
            TextAlignment = st.Align switch { "Left" => TextAlignment.Left, "Right" => TextAlignment.Right, _ => TextAlignment.Center },
            Trimming = TextTrimming.None,
        };
        var geo = ft.BuildGeometry(new Point(0, 0));
        double outline = st.Outline * size;
        double pad = Math.Ceiling(outline + size * (st.Glow ? 0.4 : 0.12) + (st.Plate != "none" ? size * 0.4 : 0) + 2);
        var bounds = geo.Bounds;
        double left = bounds.IsEmpty ? 0 : Math.Min(0, bounds.Left);
        double right = bounds.IsEmpty ? ft.Width : Math.Max(bounds.Right, 1);
        // Keep horizontal placement relative to the layout box so alignment works.
        left = 0; right = outW * 0.9;
        double top = Math.Min(0, bounds.IsEmpty ? 0 : bounds.Top), bottom = Math.Max(ft.Height, bounds.IsEmpty ? 0 : bounds.Bottom);
        int w = (int)Math.Ceiling(right - left + pad * 2), h = (int)Math.Ceiling(bottom - top + pad * 2);
        if (w <= 0 || h <= 0 || (long)w * h > 60_000_000) return null;

        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.PushTransform(new TranslateTransform(pad - left, pad - top));
            if (st.Plate is "box" or "bar" && !bounds.IsEmpty)
            {
                var pc = ToColor(st.PlateColor, Colors.Black);
                pc.A = (byte)(Math.Clamp(st.PlateOpacity, 0, 1) * 255);
                double px = size * 0.35, py = size * 0.18;
                Rect pr = st.Plate == "bar"
                    ? new Rect(-px, bounds.Top - py, outW * 0.9 + px * 2, bounds.Height + py * 2)
                    : new Rect(bounds.Left - px, bounds.Top - py, bounds.Width + px * 2, bounds.Height + py * 2);
                dc.DrawRoundedRectangle(new SolidColorBrush(pc), null, pr, st.Plate == "bar" ? 0 : size * 0.12, st.Plate == "bar" ? 0 : size * 0.12);
            }
            if (st.Glow)
            {
                var gc = ToColor(st.GlowColor, Colors.Orange);
                foreach (var (wf, a) in new[] { (0.7, 18), (0.45, 32), (0.25, 60), (0.12, 110) })
                {
                    var gb = new SolidColorBrush(Color.FromArgb((byte)a, gc.R, gc.G, gc.B));
                    dc.DrawGeometry(gb, new Pen(gb, size * wf) { LineJoin = PenLineJoin.Round }, geo);
                }
            }
            if (st.Shadow)
            {
                var sh = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0));
                double off = Math.Max(1, size * 0.045);
                dc.PushTransform(new TranslateTransform(off, off));
                if (outline > 0.3) dc.DrawGeometry(sh, new Pen(sh, outline * 2) { LineJoin = PenLineJoin.Round }, geo);
                else dc.DrawGeometry(sh, null, geo);
                dc.Pop();
            }
            if (outline > 0.3)
            {
                var ob = new SolidColorBrush(ToColor(st.OutlineColor, Colors.Black));
                dc.DrawGeometry(null, new Pen(ob, outline * 2) { LineJoin = PenLineJoin.Round }, geo);
            }
            dc.DrawGeometry(fill, null, geo);
            dc.Pop();
        }
        var rtb = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        rtb.Render(dv);
        var data = new byte[w * h * 4];
        rtb.CopyPixels(data, w * 4, 0);
        // Trim fully transparent columns on both sides to keep blits cheap, but remember alignment by keeping full width.
        return new TextImage { Data = data, W = w, H = h };
    }

    public static void ClearCache() => Cache.Clear();
}
