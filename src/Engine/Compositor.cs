using System.Runtime.InteropServices;

namespace MovieMakerReborn.Engine;

/// <summary>A BGRA 8-bit frame buffer.</summary>
public sealed class Frame
{
    public readonly int W, H;
    public readonly byte[] D;
    public Frame(int w, int h) { W = w; H = h; D = new byte[w * h * 4]; }
    public int Stride => W * 4;
}

public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int R => X + W;
    public int B => Y + H;
    public static RectI Intersect(RectI a, RectI b)
    {
        int x = Math.Max(a.X, b.X), y = Math.Max(a.Y, b.Y);
        int r = Math.Min(a.R, b.R), bt = Math.Min(a.B, b.B);
        return r > x && bt > y ? new RectI(x, y, r - x, bt - y) : new RectI(0, 0, 0, 0);
    }
}

public static unsafe class Compositor
{
    static readonly ParallelOptions Par = new() { MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount) };

    static void Rows(int y0, int y1, Action<int> body)
    {
        if (y1 - y0 < 48) { for (int y = y0; y < y1; y++) body(y); }
        else Parallel.For(y0, y1, Par, body);
    }

    public static uint ParseColor(string hex)
    {
        try
        {
            var h = (hex ?? "#000000").TrimStart('#');
            if (h.Length == 3) h = string.Concat(h.Select(c => $"{c}{c}"));
            uint v = Convert.ToUInt32(h[..6], 16);
            return 0xFF000000u | v; // ARGB uint == BGRA bytes in little-endian
        }
        catch { return 0xFF000000u; }
    }

    public static void Fill(Frame f, uint argb) => MemoryMarshal.Cast<byte, uint>(f.D.AsSpan()).Fill(argb);

    public static void Fill(Frame f, uint argb, RectI r)
    {
        r = RectI.Intersect(r, new RectI(0, 0, f.W, f.H));
        if (r.W <= 0) return;
        var span = MemoryMarshal.Cast<byte, uint>(f.D.AsSpan());
        for (int y = r.Y; y < r.B; y++) span.Slice(y * f.W + r.X, r.W).Fill(argb);
    }

    public static void Copy(Frame dst, Frame src) => Buffer.BlockCopy(src.D, 0, dst.D, 0, Math.Min(dst.D.Length, src.D.Length));

    /// <summary>Draw src (BGRA) scaled into the destination rectangle with bilinear filtering.</summary>
    public static void DrawScaled(Frame dst, byte[] src, int sw, int sh, double dx, double dy, double dw, double dh,
        double alpha = 1, bool premultiplied = false, RectI? clip = null)
    {
        if (src == null || sw <= 0 || sh <= 0 || dw < 0.5 || dh < 0.5 || alpha <= 0.001) return;
        var full = new RectI(0, 0, dst.W, dst.H);
        var c = clip.HasValue ? RectI.Intersect(clip.Value, full) : full;
        int x0 = Math.Max(c.X, (int)Math.Floor(dx)), x1 = Math.Min(c.R, (int)Math.Ceiling(dx + dw));
        int y0 = Math.Max(c.Y, (int)Math.Floor(dy)), y1 = Math.Min(c.B, (int)Math.Ceiling(dy + dh));
        if (x1 <= x0 || y1 <= y0) return;
        int dW = dst.W;
        var dD = dst.D;

        // Fast path: 1:1 integer copy, opaque.
        if (!premultiplied && alpha >= 0.999 && Math.Abs(dw - sw) < 0.01 && Math.Abs(dh - sh) < 0.01 &&
            Math.Abs(dx - Math.Round(dx)) < 0.01 && Math.Abs(dy - Math.Round(dy)) < 0.01)
        {
            int ox = (int)Math.Round(dx), oy = (int)Math.Round(dy);
            x0 = Math.Max(x0, ox); y0 = Math.Max(y0, oy);
            x1 = Math.Min(x1, ox + sw); y1 = Math.Min(y1, oy + sh);
            if (x1 <= x0 || y1 <= y0) return;
            int len = (x1 - x0) * 4;
            Rows(y0, y1, y => Buffer.BlockCopy(src, ((y - oy) * sw + (x0 - ox)) * 4, dD, (y * dW + x0) * 4, len));
            return;
        }

        double sx = sw / dw, sy = sh / dh;
        int n = x1 - x0;
        var cx0 = new int[n]; var cx1 = new int[n]; var cfx = new float[n];
        for (int i = 0; i < n; i++)
        {
            double u = (x0 + i + 0.5 - dx) * sx - 0.5;
            if (u < 0) u = 0; if (u > sw - 1) u = sw - 1;
            int a = (int)u;
            cx0[i] = a * 4; cx1[i] = Math.Min(a + 1, sw - 1) * 4; cfx[i] = (float)(u - a);
        }
        float al = (float)alpha;
        Rows(y0, y1, y =>
        {
            double v = (y + 0.5 - dy) * sy - 0.5;
            if (v < 0) v = 0; if (v > sh - 1) v = sh - 1;
            int r0 = (int)v, r1 = Math.Min(r0 + 1, sh - 1);
            float fy = (float)(v - r0);
            int o0 = r0 * sw * 4, o1 = r1 * sw * 4;
            fixed (byte* ps = src) fixed (byte* pd = dD)
            {
                byte* d = pd + (y * dW + x0) * 4;
                for (int i = 0; i < n; i++, d += 4)
                {
                    byte* p00 = ps + o0 + cx0[i], p01 = ps + o0 + cx1[i], p10 = ps + o1 + cx0[i], p11 = ps + o1 + cx1[i];
                    float fx = cfx[i];
                    float w00 = (1 - fx) * (1 - fy), w01 = fx * (1 - fy), w10 = (1 - fx) * fy, w11 = fx * fy;
                    float b = p00[0] * w00 + p01[0] * w01 + p10[0] * w10 + p11[0] * w11;
                    float g = p00[1] * w00 + p01[1] * w01 + p10[1] * w10 + p11[1] * w11;
                    float r = p00[2] * w00 + p01[2] * w01 + p10[2] * w10 + p11[2] * w11;
                    float a = p00[3] * w00 + p01[3] * w01 + p10[3] * w10 + p11[3] * w11;
                    if (premultiplied)
                    {
                        float sa = a * al / 255f;
                        if (sa <= 0.002f) continue;
                        float inv = 1 - sa;
                        d[0] = Sat(b * al + d[0] * inv); d[1] = Sat(g * al + d[1] * inv); d[2] = Sat(r * al + d[2] * inv);
                    }
                    else
                    {
                        float sa = a / 255f * al;
                        if (sa >= 0.998f) { d[0] = (byte)(b + 0.5f); d[1] = (byte)(g + 0.5f); d[2] = (byte)(r + 0.5f); }
                        else if (sa > 0.002f)
                        {
                            float inv = 1 - sa;
                            d[0] = Sat(b * sa + d[0] * inv); d[1] = Sat(g * sa + d[1] * inv); d[2] = Sat(r * sa + d[2] * inv);
                        }
                    }
                    d[3] = 255;
                }
            }
        });
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.AggressiveInlining)]
    static byte Sat(float v) => v <= 0 ? (byte)0 : v >= 255 ? (byte)255 : (byte)(v + 0.5f);

    public static void Mix(Frame dst, Frame a, Frame b, double t)
    {
        int w = (int)(Math.Clamp(t, 0, 1) * 256);
        int iw = 256 - w;
        int stride = dst.W * 4;
        Rows(0, dst.H, y =>
        {
            fixed (byte* pa = a.D) fixed (byte* pb = b.D) fixed (byte* pd = dst.D)
            {
                int o = y * stride;
                for (int i = 0; i < stride; i++) pd[o + i] = (byte)((pa[o + i] * iw + pb[o + i] * w) >> 8);
            }
        });
    }

    public static void MixColor(Frame dst, Frame a, uint argb, double t)
    {
        int w = (int)(Math.Clamp(t, 0, 1) * 256), iw = 256 - w;
        int cb = (int)(argb & 0xFF), cg = (int)((argb >> 8) & 0xFF), cr = (int)((argb >> 16) & 0xFF);
        Rows(0, dst.H, y =>
        {
            fixed (byte* pa = a.D) fixed (byte* pd = dst.D)
            {
                byte* s = pa + y * dst.W * 4, d = pd + y * dst.W * 4;
                for (int x = 0; x < dst.W; x++, s += 4, d += 4)
                {
                    d[0] = (byte)((s[0] * iw + cb * w) >> 8); d[1] = (byte)((s[1] * iw + cg * w) >> 8); d[2] = (byte)((s[2] * iw + cr * w) >> 8); d[3] = 255;
                }
            }
        });
    }

    /// <summary>Per-pixel weighted blend: weight(x,y) = amount of B.</summary>
    static void MaskMix(Frame dst, Frame a, Frame b, Func<int, int, float> weight)
    {
        Rows(0, dst.H, y =>
        {
            fixed (byte* pa = a.D) fixed (byte* pb = b.D) fixed (byte* pd = dst.D)
            {
                int o = y * dst.W * 4;
                byte* sa = pa + o, sb = pb + o, d = pd + o;
                for (int x = 0; x < dst.W; x++, sa += 4, sb += 4, d += 4)
                {
                    float m = weight(x, y);
                    if (m <= 0) { *(uint*)d = *(uint*)sa; }
                    else if (m >= 1) { *(uint*)d = *(uint*)sb; }
                    else
                    {
                        float im = 1 - m;
                        d[0] = (byte)(sa[0] * im + sb[0] * m); d[1] = (byte)(sa[1] * im + sb[1] * m); d[2] = (byte)(sa[2] * im + sb[2] * m); d[3] = 255;
                    }
                }
            }
        });
    }

    static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    static float Hash(int x, int y)
    {
        uint h = (uint)(x * 374761393 + y * 668265263);
        h = (h ^ (h >> 13)) * 1274126177u;
        h ^= h >> 16;
        return (h & 0xFFFF) / 65535f;
    }

    /// <summary>Composite B over A according to a transition at progress p (0..1), writing into dst.</summary>
    public static void Transition(Frame dst, Frame a, Frame b, string type, double p, uint bg)
    {
        p = Math.Clamp(p, 0, 1);
        int W = dst.W, H = dst.H;
        double e = Smooth(p);
        float fp = (float)p;
        switch (type)
        {
            case "fadeblack":
            case "fadewhite":
                {
                    uint col = type == "fadeblack" ? 0xFF000000u : 0xFFFFFFFFu;
                    if (p < 0.5) MixColor(dst, a, col, p * 2); else MixColor(dst, b, col, 1 - (p - 0.5) * 2);
                    return;
                }
            case "dissolve":
                MaskMix(dst, a, b, (x, y) => Hash(x >> 1, y >> 1) < fp ? 1 : 0); return;
            case "wipeleft": { float edge = (float)(W * (1 - p)); MaskMix(dst, a, b, (x, y) => x >= edge ? 1 : 0); return; }
            case "wiperight": { float edge = (float)(W * p); MaskMix(dst, a, b, (x, y) => x < edge ? 1 : 0); return; }
            case "wipeup": { float edge = (float)(H * (1 - p)); MaskMix(dst, a, b, (x, y) => y >= edge ? 1 : 0); return; }
            case "wipedown": { float edge = (float)(H * p); MaskMix(dst, a, b, (x, y) => y < edge ? 1 : 0); return; }
            case "softwipe":
                {
                    float sw = 0.25f, pos = fp * (1 + sw);
                    MaskMix(dst, a, b, (x, y) => Math.Clamp((pos - (float)x / W) / sw, 0, 1));
                    return;
                }
            case "diagonal":
                {
                    float sw = 0.12f, pos = fp * (1 + sw);
                    MaskMix(dst, a, b, (x, y) => Math.Clamp((pos - ((float)x / W + (float)y / H) * 0.5f) / sw, 0, 1));
                    return;
                }
            case "slideleft": Copy(dst, a); DrawScaled(dst, b.D, W, H, Math.Round(W * (1 - e)), 0, W, H); return;
            case "slideright": Copy(dst, a); DrawScaled(dst, b.D, W, H, Math.Round(-W * (1 - e)), 0, W, H); return;
            case "slideup": Copy(dst, a); DrawScaled(dst, b.D, W, H, 0, Math.Round(H * (1 - e)), W, H); return;
            case "slidedown": Copy(dst, a); DrawScaled(dst, b.D, W, H, 0, Math.Round(-H * (1 - e)), W, H); return;
            case "pushleft":
                Fill(dst, bg);
                DrawScaled(dst, a.D, W, H, Math.Round(-W * e), 0, W, H);
                DrawScaled(dst, b.D, W, H, Math.Round(W * (1 - e)), 0, W, H);
                return;
            case "pushright":
                Fill(dst, bg);
                DrawScaled(dst, a.D, W, H, Math.Round(W * e), 0, W, H);
                DrawScaled(dst, b.D, W, H, Math.Round(-W * (1 - e)), 0, W, H);
                return;
            case "zoomin":
                {
                    Copy(dst, a);
                    double s = 0.15 + 0.85 * e;
                    DrawScaled(dst, b.D, W, H, W * (1 - s) / 2, H * (1 - s) / 2, W * s, H * s, Math.Min(1, p * 2));
                    return;
                }
            case "zoomout":
                {
                    Copy(dst, b);
                    double s = 1 + e * 0.6;
                    DrawScaled(dst, a.D, W, H, W * (1 - s) / 2, H * (1 - s) / 2, W * s, H * s, 1 - e);
                    return;
                }
            case "iris":
                {
                    float cx = W / 2f, cy = H / 2f, rad = (float)(e * Math.Sqrt(cx * cx + cy * cy)) + 1, soft = Math.Max(1.5f, W / 600f);
                    MaskMix(dst, a, b, (x, y) => { float dx = x - cx, dy = y - cy; return Math.Clamp((rad - MathF.Sqrt(dx * dx + dy * dy)) / soft, 0, 1); });
                    return;
                }
            case "diamond":
                {
                    float cx = W / 2f, cy = H / 2f, r = (float)e * 1.001f;
                    MaskMix(dst, a, b, (x, y) => (Math.Abs(x - cx) / W + Math.Abs(y - cy) / H) < r ? 1 : 0);
                    return;
                }
            case "clock":
                {
                    float cx = W / 2f, cy = H / 2f, ang = (float)(p * Math.PI * 2);
                    MaskMix(dst, a, b, (x, y) =>
                    {
                        float t = MathF.Atan2(x - cx, cy - y); if (t < 0) t += MathF.PI * 2;
                        return t <= ang ? 1 : 0;
                    });
                    return;
                }
            case "blinds":
                {
                    float band = H / 10f;
                    MaskMix(dst, a, b, (x, y) => (y % band) / band < fp ? 1 : 0);
                    return;
                }
            case "checker":
                {
                    float cw = W / 8f, ch = H / 6f;
                    MaskMix(dst, a, b, (x, y) =>
                    {
                        int ix = (int)(x / cw), iy = (int)(y / ch);
                        float q = ((ix + iy) & 1) == 0 ? fp * 2 : fp * 2 - 1;
                        return (x - ix * cw) / cw < q ? 1 : 0;
                    });
                    return;
                }
            case "pixelate":
                {
                    Mix(dst, a, b, Smooth((p - 0.35) / 0.3));
                    int block = 1 + (int)(Math.Sin(Math.PI * p) * Math.Max(8, W / 40.0));
                    if (block > 1) Pixelate(dst, new RectI(0, 0, W, H), block);
                    return;
                }
            case "blurx":
                {
                    Mix(dst, a, b, Smooth((p - 0.3) / 0.4));
                    int r = (int)(Math.Sin(Math.PI * p) * Math.Max(4, W / 80.0));
                    if (r > 0) BoxBlur(dst, new RectI(0, 0, W, H), r, 2);
                    return;
                }
            default: // crossfade
                Mix(dst, a, b, p);
                return;
        }
    }

    // ---------------- Effects ----------------

    /// <summary>3x4 color matrix (rows r,g,b; cols r,g,b,offset) in 0..255 space.</summary>
    public sealed class Mat
    {
        public float[] M = { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 };
        public static Mat Identity => new();
        public bool IsIdentity => M.SequenceEqual(new float[] { 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0 });

        /// <summary>Returns this followed by o (o applied after this).</summary>
        public Mat Then(Mat o)
        {
            var r = new Mat();
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                    r.M[i * 4 + j] = o.M[i * 4 + 0] * M[0 * 4 + j] + o.M[i * 4 + 1] * M[1 * 4 + j] + o.M[i * 4 + 2] * M[2 * 4 + j];
                r.M[i * 4 + 3] = o.M[i * 4 + 0] * M[3] + o.M[i * 4 + 1] * M[7] + o.M[i * 4 + 2] * M[11] + o.M[i * 4 + 3];
            }
            return r;
        }

        public static Mat Of(params float[] m) => new() { M = m };
        public static Mat Brightness(double b) { float o = (float)(b * 110); return Of(1, 0, 0, o, 0, 1, 0, o, 0, 0, 1, o); }
        public static Mat Contrast(double c)
        {
            float k = (float)(c >= 0 ? 1 + c * 1.5 : 1 + c);
            float o = 128 * (1 - k);
            return Of(k, 0, 0, o, 0, k, 0, o, 0, 0, k, o);
        }
        public static Mat Saturation(double f)
        {
            float s = (float)f, lr = 0.2126f, lg = 0.7152f, lb = 0.0722f;
            return Of(lr * (1 - s) + s, lg * (1 - s), lb * (1 - s), 0,
                      lr * (1 - s), lg * (1 - s) + s, lb * (1 - s), 0,
                      lr * (1 - s), lg * (1 - s), lb * (1 - s) + s, 0);
        }
        public static Mat Scale(float r, float g, float b, float or = 0, float og = 0, float ob = 0) => Of(r, 0, 0, or, 0, g, 0, og, 0, 0, b, ob);
    }

    public static void ApplyMatrix(Frame f, RectI rc, Mat mat)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        if (rc.W <= 0 || mat.IsIdentity) return;
        var m = mat.M;
        float m0 = m[0], m1 = m[1], m2 = m[2], m3 = m[3], m4 = m[4], m5 = m[5], m6 = m[6], m7 = m[7], m8 = m[8], m9 = m[9], m10 = m[10], m11 = m[11];
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4)
                {
                    float b = d[0], g = d[1], r = d[2];
                    d[2] = Sat(m0 * r + m1 * g + m2 * b + m3);
                    d[1] = Sat(m4 * r + m5 * g + m6 * b + m7);
                    d[0] = Sat(m8 * r + m9 * g + m10 * b + m11);
                }
            }
        });
    }

    public static void Lut(Frame f, RectI rc, byte[] lut)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4) { d[0] = lut[d[0]]; d[1] = lut[d[1]]; d[2] = lut[d[2]]; }
            }
        });
    }

    public static void Threshold(Frame f, RectI rc)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4)
                {
                    byte v = (d[2] * 54 + d[1] * 183 + d[0] * 19) >> 8 > 120 ? (byte)255 : (byte)0;
                    d[0] = d[1] = d[2] = v;
                }
            }
        });
    }

    public static void Pixelate(Frame f, RectI rc, int block)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        if (block < 2 || rc.W <= 0) return;
        int rowsOfBlocks = (rc.H + block - 1) / block;
        Parallel.For(0, rowsOfBlocks, Par, by =>
        {
            int y0 = rc.Y + by * block, y1 = Math.Min(rc.B, y0 + block);
            fixed (byte* pd = f.D)
            {
                for (int x0 = rc.X; x0 < rc.R; x0 += block)
                {
                    int x1 = Math.Min(rc.R, x0 + block);
                    int sb = 0, sg = 0, sr = 0, cnt = 0;
                    for (int y = y0; y < y1; y += 2)
                        for (int x = x0; x < x1; x += 2) { byte* p = pd + (y * f.W + x) * 4; sb += p[0]; sg += p[1]; sr += p[2]; cnt++; }
                    if (cnt == 0) continue;
                    uint c = 0xFF000000u | (uint)(sr / cnt) << 16 | (uint)(sg / cnt) << 8 | (uint)(sb / cnt);
                    for (int y = y0; y < y1; y++)
                    {
                        uint* row = (uint*)(pd + (y * f.W + x0) * 4);
                        for (int x = 0; x < x1 - x0; x++) row[x] = c;
                    }
                }
            }
        });
    }

    /// <summary>Approximate Gaussian blur via repeated box blurs.</summary>
    public static void BoxBlur(Frame f, RectI rc, int radius, int passes = 3)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        if (radius < 1 || rc.W <= 2 || rc.H <= 2) return;
        radius = Math.Min(radius, Math.Min(rc.W, rc.H) / 2);
        var tmp = new byte[rc.W * rc.H * 4];
        int W = rc.W, H = rc.H, fw = f.W;
        for (int pass = 0; pass < passes; pass++)
        {
            // Horizontal: f -> tmp
            Rows(0, H, y =>
            {
                fixed (byte* pf = f.D) fixed (byte* pt = tmp)
                {
                    byte* src = pf + ((rc.Y + y) * fw + rc.X) * 4;
                    byte* dst = pt + y * W * 4;
                    int sb = 0, sg = 0, sr = 0, win = radius * 2 + 1;
                    for (int i = -radius; i <= radius; i++) { int xi = Math.Clamp(i, 0, W - 1) * 4; sb += src[xi]; sg += src[xi + 1]; sr += src[xi + 2]; }
                    for (int x = 0; x < W; x++)
                    {
                        dst[x * 4] = (byte)(sb / win); dst[x * 4 + 1] = (byte)(sg / win); dst[x * 4 + 2] = (byte)(sr / win); dst[x * 4 + 3] = 255;
                        int xo = Math.Max(x - radius, 0) * 4, xn = Math.Min(x + radius + 1, W - 1) * 4;
                        sb += src[xn] - src[xo]; sg += src[xn + 1] - src[xo + 1]; sr += src[xn + 2] - src[xo + 2];
                    }
                }
            });
            // Vertical: tmp -> f
            Parallel.For(0, W, Par, x =>
            {
                fixed (byte* pf = f.D) fixed (byte* pt = tmp)
                {
                    int sb = 0, sg = 0, sr = 0, win = radius * 2 + 1, st = W * 4;
                    byte* col = pt + x * 4;
                    for (int i = -radius; i <= radius; i++) { int yi = Math.Clamp(i, 0, H - 1) * st; sb += col[yi]; sg += col[yi + 1]; sr += col[yi + 2]; }
                    for (int y = 0; y < H; y++)
                    {
                        byte* d = pf + ((rc.Y + y) * fw + rc.X + x) * 4;
                        d[0] = (byte)(sb / win); d[1] = (byte)(sg / win); d[2] = (byte)(sr / win);
                        int yo = Math.Max(y - radius, 0) * st, yn = Math.Min(y + radius + 1, H - 1) * st;
                        sb += col[yn] - col[yo]; sg += col[yn + 1] - col[yo + 1]; sr += col[yn + 2] - col[yo + 2];
                    }
                }
            });
        }
    }

    public static void Vignette(Frame f, RectI rc, double strength)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        float cx = rc.X + rc.W / 2f, cy = rc.Y + rc.H / 2f, s = (float)strength;
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                float dy = (y - cy) / (rc.H / 2f);
                for (int x = 0; x < rc.W; x++, d += 4)
                {
                    float dx = (rc.X + x - cx) / (rc.W / 2f);
                    float r2 = (dx * dx + dy * dy) * 0.5f;
                    float t = Math.Clamp((r2 - 0.25f) / 0.75f, 0, 1);
                    float k = 1 - s * t * t * (3 - 2 * t);
                    d[0] = (byte)(d[0] * k); d[1] = (byte)(d[1] * k); d[2] = (byte)(d[2] * k);
                }
            }
        });
    }

    public static void Grain(Frame f, RectI rc, int amount, int seed)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            uint s = (uint)(seed * 9781 + y * 6271 + 1);
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4)
                {
                    s ^= s << 13; s ^= s >> 17; s ^= s << 5;
                    int n = (int)(s % (uint)(amount * 2 + 1)) - amount;
                    d[0] = Sat(d[0] + n); d[1] = Sat(d[1] + n); d[2] = Sat(d[2] + n);
                }
            }
        });
    }

    public static void FlipH(Frame f, RectI rc)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                uint* row = (uint*)(pd + (y * f.W + rc.X) * 4);
                for (int i = 0, j = rc.W - 1; i < j; i++, j--) { uint t = row[i]; row[i] = row[j]; row[j] = t; }
            }
        });
    }

    static uint H32(int a, int b) { uint h = (uint)(a * 374761393 + b * 668265263 + 1442695041); h = (h ^ (h >> 13)) * 1274126177u; return h ^ (h >> 16); }

    /// <summary>Shift the red channel left and blue right (chromatic aberration).</summary>
    public static void ChannelShift(Frame f, RectI rc, int d)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        if (d <= 0 || rc.W <= d * 2) return;
        Rows(rc.Y, rc.B, y =>
        {
            var row = new byte[rc.W * 4];
            fixed (byte* pd = f.D) fixed (byte* pr = row)
            {
                byte* src = pd + (y * f.W + rc.X) * 4;
                Buffer.MemoryCopy(src, pr, row.Length, row.Length);
                for (int x = 0; x < rc.W; x++)
                {
                    int xr = Math.Min(rc.W - 1, x + d), xb = Math.Max(0, x - d);
                    src[x * 4 + 2] = pr[xr * 4 + 2];
                    src[x * 4] = pr[xb * 4];
                }
            }
        });
    }

    public static void RowShift(Frame f, RectI rc, int y0, int h, int dx)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        int y1 = Math.Min(rc.B, y0 + h); y0 = Math.Max(rc.Y, y0);
        if (dx == 0 || y1 <= y0) return;
        var span = MemoryMarshal.Cast<byte, uint>(f.D.AsSpan());
        var tmp = new uint[rc.W];
        for (int y = y0; y < y1; y++)
        {
            var row = span.Slice(y * f.W + rc.X, rc.W);
            row.CopyTo(tmp);
            for (int x = 0; x < rc.W; x++) row[x] = tmp[((x - dx) % rc.W + rc.W) % rc.W];
        }
    }

    public static void Scanlines(Frame f, RectI rc, int period, float dark)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        float k = 1 - dark;
        Rows(rc.Y, rc.B, y =>
        {
            if ((y / Math.Max(1, period / 2)) % 2 == 0) return;
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4) { d[0] = (byte)(d[0] * k); d[1] = (byte)(d[1] * k); d[2] = (byte)(d[2] * k); }
            }
        });
    }

    /// <summary>Screen-blend scratch over f (for glow/bloom).</summary>
    public static void Screen(Frame f, Frame s2, RectI rc, float amount)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D) fixed (byte* ps = s2.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4, s = ps + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W * 4; x++)
                {
                    if ((x & 3) == 3) continue;
                    float a = d[x] / 255f, b = s[x] / 255f * amount;
                    d[x] = Sat((1 - (1 - a) * (1 - b)) * 255);
                }
            }
        });
    }

    /// <summary>f += (f - blurred) * amount</summary>
    public static void Unsharp(Frame f, Frame blurred, RectI rc, float amount)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D) fixed (byte* ps = blurred.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4, s = ps + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W * 4; x++) { if ((x & 3) == 3) continue; d[x] = Sat(d[x] + (d[x] - s[x]) * amount); }
            }
        });
    }

    public static void Duotone(Frame f, RectI rc, uint darkArgb, uint lightArgb)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        int db = (int)(darkArgb & 255), dg = (int)(darkArgb >> 8 & 255), dr = (int)(darkArgb >> 16 & 255);
        int lb = (int)(lightArgb & 255), lg = (int)(lightArgb >> 8 & 255), lr = (int)(lightArgb >> 16 & 255);
        Rows(rc.Y, rc.B, y =>
        {
            fixed (byte* pd = f.D)
            {
                byte* d = pd + (y * f.W + rc.X) * 4;
                for (int x = 0; x < rc.W; x++, d += 4)
                {
                    int l = (d[2] * 54 + d[1] * 183 + d[0] * 19) >> 8;
                    d[0] = (byte)(db + (lb - db) * l / 255); d[1] = (byte)(dg + (lg - dg) * l / 255); d[2] = (byte)(dr + (lr - dr) * l / 255);
                }
            }
        });
    }

    /// <summary>Old-film damage: brightness flicker, vertical scratches and dust specks.</summary>
    public static void FilmDamage(Frame f, RectI rc, int frameNo, float flicker, int scratches, int dust, bool darkMarks)
    {
        rc = RectI.Intersect(rc, new RectI(0, 0, f.W, f.H));
        if (rc.W <= 0) return;
        float fl = 1 + ((H32(frameNo, 7) & 1023) / 1023f - 0.5f) * 2 * flicker;
        if (Math.Abs(fl - 1) > 0.002) ApplyMatrix(f, rc, Mat.Scale(fl, fl, fl));
        float scale = f.W / 1920f;
        var span = MemoryMarshal.Cast<byte, uint>(f.D.AsSpan());
        uint mark = darkMarks ? 0xFF1A140Cu : 0xFFD8D2C4u;
        for (int i = 0; i < scratches; i++)
        {
            uint h = H32(frameNo / 2, 100 + i);
            if ((h & 3) == 0) continue; // scratches come and go
            int x = rc.X + (int)(h % (uint)rc.W);
            int w = Math.Max(1, (int)scale);
            int y0 = rc.Y + (int)((h >> 8) % (uint)Math.Max(1, rc.H / 3)), y1 = rc.B - (int)((h >> 16) % (uint)Math.Max(1, rc.H / 4));
            for (int y = y0; y < y1; y++) for (int xx = x; xx < Math.Min(rc.R, x + w); xx++) span[y * f.W + xx] = mark;
        }
        for (int i = 0; i < dust; i++)
        {
            uint h = H32(frameNo, 1000 + i);
            if ((h & 7) > 2) continue;
            int cx = rc.X + (int)(h % (uint)rc.W), cy = rc.Y + (int)((h >> 11) % (uint)rc.H);
            int r = Math.Max(1, (int)(((h >> 22) % 4 + 1) * scale));
            for (int y = Math.Max(rc.Y, cy - r); y < Math.Min(rc.B, cy + r); y++)
                for (int x = Math.Max(rc.X, cx - r); x < Math.Min(rc.R, cx + r); x++)
                    if ((x - cx) * (x - cx) + (y - cy) * (y - cy) <= r * r) span[y * f.W + x] = (h & 1) == 0 ? 0xFF0E0B08u : 0xFFE6E0D2u;
        }
    }

    /// <summary>Returns a copy of src with alpha cut out where pixels match the key color.</summary>
    public static byte[] ChromaKey(byte[] src, int w, int h, uint keyArgb, double tolerance, double softness, bool spill, byte[] dst = null)
    {
        if (dst == null || dst.Length != src.Length) dst = new byte[src.Length];
        float kr = (keyArgb >> 16 & 255) / 255f, kg = (keyArgb >> 8 & 255) / 255f, kb = (keyArgb & 255) / 255f;
        float kcb = -0.168736f * kr - 0.331264f * kg + 0.5f * kb, kcr = 0.5f * kr - 0.418688f * kg - 0.081312f * kb;
        float tol = (float)tolerance, soft = (float)Math.Max(0.005, softness);
        int dom = kg >= kr && kg >= kb ? 1 : kb >= kr ? 0 : 2; // BGRA index of the key's dominant channel
        Rows(0, h, y =>
        {
            fixed (byte* ps = src) fixed (byte* pd = dst)
            {
                byte* s = ps + y * w * 4, d = pd + y * w * 4;
                for (int x = 0; x < w; x++, s += 4, d += 4)
                {
                    float b = s[0] / 255f, g = s[1] / 255f, r = s[2] / 255f;
                    float cb = -0.168736f * r - 0.331264f * g + 0.5f * b, cr = 0.5f * r - 0.418688f * g - 0.081312f * b;
                    float dist = MathF.Sqrt((cb - kcb) * (cb - kcb) + (cr - kcr) * (cr - kcr));
                    float a = Math.Clamp((dist - tol) / soft, 0, 1);
                    d[0] = s[0]; d[1] = s[1]; d[2] = s[2];
                    if (spill && a > 0)
                    {
                        int o1 = dom == 1 ? 0 : 1, o2 = dom == 2 ? 0 : 2;
                        if (dom == 0) { o1 = 1; o2 = 2; }
                        int lim = Math.Max(s[o1], s[o2]);
                        if (s[dom] > lim) d[dom] = (byte)(lim + (s[dom] - lim) * 0.3f * a);
                    }
                    d[3] = (byte)(a * 255);
                }
            }
        });
        return dst;
    }

    /// <summary>Apply a named effect preset plus manual adjustments to a region.</summary>
    public static void ApplyEffect(Frame f, RectI rc, string effect, double brightness, double contrast, double saturation, int frameNo, Frame scratch = null)
    {
        double scale = f.W / 1920.0;
        var m = Mat.Identity;
        switch (effect)
        {
            case "bw": m = Mat.Saturation(0); break;
            case "bwred": m = Mat.Of(0.75f, 0.25f, 0, 0, 0.75f, 0.25f, 0, 0, 0.75f, 0.25f, 0, 0).Then(Mat.Contrast(0.15)); break;
            case "sepia": m = Mat.Of(0.393f, 0.769f, 0.189f, 0, 0.349f, 0.686f, 0.168f, 0, 0.272f, 0.534f, 0.131f, 0); break;
            case "cyan": m = Mat.Saturation(0).Then(Mat.Scale(0.62f, 0.95f, 1.05f, 0, 8, 20)); break;
            case "warm": m = Mat.Scale(1.08f, 1.0f, 0.86f, 12, 4, 0); break;
            case "cool": m = Mat.Scale(0.9f, 1.0f, 1.1f, 0, 2, 12); break;
            case "vivid": m = Mat.Saturation(1.5).Then(Mat.Contrast(0.08)); break;
            case "faded": m = Mat.Saturation(0.7).Then(Mat.Contrast(-0.25)).Then(Mat.Brightness(0.12)); break;
            case "oldfilm": m = Mat.Of(0.393f, 0.769f, 0.189f, 0, 0.349f, 0.686f, 0.168f, 0, 0.272f, 0.534f, 0.131f, 0).Then(Mat.Contrast(-0.1)); break;
            case "cinematic": m = Mat.Saturation(1.12).Then(Mat.Contrast(0.12)).Then(Mat.Scale(1.04f, 1.0f, 0.97f, 0, 4, 10)); break;
            case "noir": m = Mat.Saturation(0).Then(Mat.Contrast(0.6)).Then(Mat.Brightness(-0.06)); break;
            case "silent": m = Mat.Saturation(0).Then(Mat.Contrast(0.3)).Then(Mat.Scale(1.0f, 0.99f, 0.96f, 6, 6, 4)); break;
            case "western": m = Mat.Of(0.393f, 0.769f, 0.189f, 0, 0.349f, 0.686f, 0.168f, 0, 0.272f, 0.534f, 0.131f, 0).Then(Mat.Scale(1.12f, 0.98f, 0.78f, 10, 0, 0)).Then(Mat.Contrast(0.28)); break;
            case "vhs": m = Mat.Saturation(0.75).Then(Mat.Scale(1.04f, 1.0f, 1.08f, 6, 2, 10)).Then(Mat.Contrast(-0.08)); break;
            case "cross": m = Mat.Scale(1.12f, 1.06f, 0.82f, -6, 0, 28).Then(Mat.Contrast(0.22)).Then(Mat.Saturation(1.25)); break;
            case "glitch": m = Mat.Saturation(1.2); break;
            case "negative": m = Mat.Of(-1, 0, 0, 255, 0, -1, 0, 255, 0, 0, -1, 255); break;
            case "night": m = Mat.Saturation(0).Then(Mat.Scale(0.25f, 1.35f, 0.3f, 0, 20, 0)); break;
        }
        if (Math.Abs(brightness) > 0.001) m = m.Then(Mat.Brightness(brightness));
        if (Math.Abs(contrast) > 0.001) m = m.Then(Mat.Contrast(contrast));
        if (Math.Abs(saturation) > 0.001) m = m.Then(Mat.Saturation(1 + saturation));

        switch (effect)
        {
            case "blur": BoxBlur(f, rc, (int)Math.Max(2, 10 * scale)); break;
            case "soft":
                if (scratch != null)
                {
                    Copy(scratch, f);
                    BoxBlur(scratch, rc, (int)Math.Max(2, 14 * scale));
                    Mix(f, f, scratch, 0.55);
                }
                break;
            case "pixelate": Pixelate(f, rc, (int)Math.Max(4, 24 * scale)); break;
            case "mirror": FlipH(f, rc); break;
            case "silent": BoxBlur(f, rc, (int)Math.Max(1, 1.5 * scale), 1); break;
            case "sharpen":
                if (scratch != null) { Copy(scratch, f); BoxBlur(scratch, rc, (int)Math.Max(1, 1.2 * scale), 1); Unsharp(f, scratch, rc, 0.9f); }
                break;
            case "bloom":
                if (scratch != null)
                {
                    Copy(scratch, f);
                    ApplyMatrix(scratch, rc, Mat.Contrast(0.7).Then(Mat.Brightness(-0.35)));
                    BoxBlur(scratch, rc, (int)Math.Max(4, 22 * scale));
                    Screen(f, scratch, rc, 0.9f);
                }
                break;
        }
        ApplyMatrix(f, rc, m);
        switch (effect)
        {
            case "posterize":
                {
                    var lut = new byte[256];
                    for (int i = 0; i < 256; i++) lut[i] = (byte)(Math.Round(i / 255.0 * 4) / 4 * 255);
                    Lut(f, rc, lut);
                    break;
                }
            case "threshold": Threshold(f, rc); break;
            case "oldfilm": Grain(f, rc, 22, frameNo); Vignette(f, rc, 0.65); break;
            case "noir": Grain(f, rc, 12, frameNo); Vignette(f, rc, 0.85); break;
            case "silent":
                {
                    // 1:1 square gate with black pillarbox, flicker, scratches, dust and heavy grain.
                    int side = Math.Min(f.W, f.H);
                    var sq = new RectI((f.W - side) / 2, (f.H - side) / 2, side, side);
                    if (sq.X > 0) { Fill(f, 0xFF000000u, new RectI(0, 0, sq.X, f.H)); Fill(f, 0xFF000000u, new RectI(sq.R, 0, f.W - sq.R, f.H)); }
                    if (sq.Y > 0) { Fill(f, 0xFF000000u, new RectI(0, 0, f.W, sq.Y)); Fill(f, 0xFF000000u, new RectI(0, sq.B, f.W, f.H - sq.B)); }
                    FilmDamage(f, sq, frameNo, 0.08f, 3, 10, false);
                    Grain(f, sq, 30, frameNo);
                    Vignette(f, sq, 0.9);
                    break;
                }
            case "western": FilmDamage(f, rc, frameNo, 0.04f, 2, 8, true); Grain(f, rc, 20, frameNo); Vignette(f, rc, 0.7); break;
            case "vhs":
                {
                    ChannelShift(f, rc, (int)Math.Max(1, 3 * scale));
                    Scanlines(f, rc, (int)Math.Max(2, 4 * scale), 0.12f);
                    if ((H32(frameNo, 3) & 15) == 0) RowShift(f, rc, rc.Y + (int)(H32(frameNo, 4) % (uint)rc.H), (int)(18 * scale), (int)(12 * scale));
                    Grain(f, rc, 10, frameNo);
                    break;
                }
            case "glitch":
                {
                    uint h = H32(frameNo / 2, 9);
                    ChannelShift(f, rc, (int)Math.Max(2, (6 + h % 14) * scale));
                    int bands = (int)(h >> 4) % 5;
                    for (int i = 0; i < bands; i++)
                    {
                        uint hh = H32(frameNo, 20 + i);
                        RowShift(f, rc, rc.Y + (int)(hh % (uint)Math.Max(1, rc.H)), (int)((8 + hh % 50) * scale), (int)(((int)(hh >> 10) % 80 - 40) * scale));
                    }
                    break;
                }
            case "rgbsplit": ChannelShift(f, rc, (int)Math.Max(2, 7 * scale)); break;
            case "duotone": Duotone(f, rc, 0xFF2A0E4Au, 0xFFFFB347u); break;
            case "vignette": Vignette(f, rc, 0.85); break;
            case "night": Grain(f, rc, 26, frameNo); Vignette(f, rc, 0.8); break;
            case "grain": Grain(f, rc, 18, frameNo); break;
            case "cinematic":
                {
                    int bar = (int)Math.Max(0, (f.H - f.W / 2.39) / 2);
                    if (bar > 0) { Fill(f, 0xFF000000u, new RectI(0, 0, f.W, bar)); Fill(f, 0xFF000000u, new RectI(0, f.H - bar, f.W, bar)); }
                    break;
                }
        }
    }
}
