using MovieMakerReborn.Model;

namespace MovieMakerReborn.Engine;

/// <summary>Composes a project into frames: clips, transitions, effects, pan &amp; zoom and text.</summary>
public sealed class Renderer : IDisposable
{
    public readonly int W, H;
    readonly bool _export;
    readonly string _interpOverride;
    Project _p;
    Timeline _tl;
    Frame _acc, _layer, _tmp, _scratch;
    readonly Dictionary<string, (string key, VideoDecoder dec)> _decs = new();

    public Renderer(int w, int h, bool export, string interpOverride = null)
    {
        W = Math.Max(2, w & ~1); H = Math.Max(2, h & ~1);
        _export = export;
        _interpOverride = interpOverride;
        _acc = new Frame(W, H); _layer = new Frame(W, H); _tmp = new Frame(W, H);
    }

    public Project Project => _p;
    public Timeline Timeline => _tl;

    public void SetProject(Project p)
    {
        _p = p;
        _tl = p.ComputeTimeline();
    }

    uint Bg => Compositor.ParseColor(_p.Background);
    int FpsNum => _p.FpsNum;
    int FpsDen => _p.FpsDen;
    double Fps => _p.Fps;

    public Frame Render(double t, int frameNo = 0)
    {
        if (_p == null) { Compositor.Fill(_acc, 0xFF000000u); return _acc; }
        var tl = _tl;
        int n = _p.Clips.Count;
        var active = new List<int>(3);
        for (int i = 0; i < n; i++)
            if (tl.Start[i] <= t + 1e-6 && t < tl.Start[i] + tl.Dur[i] - 1e-6) active.Add(i);
        if (active.Count == 0 && n > 0 && t >= tl.Total - 1e-6) active.Add(n - 1);

        if (active.Count == 0) Compositor.Fill(_acc, Bg);
        else
        {
            int i0 = active[0];
            RenderClip(i0, Math.Min(t - tl.Start[i0], tl.Dur[i0] - 1 / Fps), _acc, frameNo);
            for (int a = 1; a < active.Count; a++)
            {
                int j = active[a];
                RenderClip(j, t - tl.Start[j], _layer, frameNo);
                double T = tl.Trans[j];
                double p = T > 0 ? (t - tl.Start[j]) / T : 1;
                if (p >= 1) (_acc, _layer) = (_layer, _acc);
                else
                {
                    Compositor.Transition(_tmp, _acc, _layer, _p.Clips[j].Transition, p, Bg);
                    (_acc, _tmp) = (_tmp, _acc);
                }
            }
        }

        foreach (var cap in _p.Captions)
            if (t >= cap.Start && t < cap.Start + cap.Duration)
                DrawText(_acc, cap.Text, cap.Style, t - cap.Start, cap.Duration, frameNo);

        ManageDecoders(t, active);
        return _acc;
    }

    void ManageDecoders(double t, List<int> active)
    {
        var keep = new HashSet<string>();
        foreach (var i in active) { keep.Add(_p.Clips[i].Id); keep.Add("bg:" + _p.Clips[i].Id); }
        double look = _export ? 2.0 : 1.2;
        for (int i = 0; i < _p.Clips.Count; i++)
        {
            var c = _p.Clips[i];
            if (c.Kind != ClipKind.Video) continue;
            double s = _tl.Start[i];
            if (s > t && s - t <= look)
            {
                keep.Add(c.Id);
                var dec = GetDecoder(c, out _, out _);
                dec?.Prefetch(0);
            }
        }
        foreach (var id in _decs.Keys.Where(k => !keep.Contains(k)).ToList())
        {
            _decs[id].dec.Dispose();
            _decs.Remove(id);
        }
    }

    (int w, int h) FitSize(int rw, int rh)
    {
        if (rw <= 0 || rh <= 0) return (W, H);
        if (_p.Fit == "stretch") return (W, H);
        double s = _p.Fit == "cover" ? Math.Max((double)W / rw, (double)H / rh) : Math.Min((double)W / rw, (double)H / rh);
        return (Math.Max(2, (int)Math.Round(rw * s / 2) * 2), Math.Max(2, (int)Math.Round(rh * s / 2) * 2));
    }

    VideoDecoder GetDecoder(Clip c, out int fw, out int fh) => GetDecoder(c.Id, _p.GetMedia(c.MediaId), c, _p.Fit == "cover", out fw, out fh);

    VideoDecoder GetDecoder(string id, MediaRef m, Clip c, bool cover, out int fw, out int fh)
    {
        fw = fh = 0;
        if (m == null || !File.Exists(m.Path)) return null;
        var (rw, rh) = FF.RotatedSize(m, c.Rotation);
        (fw, fh) = cover ? (W, H) : FitSize(rw, rh);
        string interp = _interpOverride ?? (_export ? _p.Interp : "drop");
        string key = $"{m.Path}|{c.TrimIn:F4}|{c.TrimOut:F4}|{c.Speed:F4}|{c.Rotation}|{c.Reverse}|{fw}x{fh}|{cover}|{FpsNum}/{FpsDen}|{interp}";
        if (_decs.TryGetValue(id, out var e))
        {
            if (e.key == key) return e.dec;
            e.dec.Dispose();
        }
        var dec = new VideoDecoder(m, c, fw, fh, FpsNum, FpsDen, interp, cover, _export ? 8 : 5);
        _decs[id] = (key, dec);
        return dec;
    }

    readonly Dictionary<string, Clip> _bgClips = new();
    byte[] _keyBuf;

    /// <summary>Draws the green-screen background (image, video or color) for a keyed clip.</summary>
    void DrawKeyBackground(Clip c, double local, Frame dst)
    {
        var k = c.Key;
        Compositor.Fill(dst, Compositor.ParseColor(k.BgColor));
        var bm = k.BgMediaId != null ? _p.GetMedia(k.BgMediaId) : null;
        if (bm == null || !File.Exists(bm.Path)) return;
        if (bm.Kind == MediaKind.Image)
        {
            var (rw, rh) = FF.RotatedSize(bm, 0);
            double s = Math.Max((double)W / rw, (double)H / rh);
            var img = ImageLoader.Load(bm, 0, (int)(rw * s) + 2, (int)(rh * s) + 2);
            if (img != null) Compositor.DrawScaled(dst, img.Data, img.W, img.H, (W - rw * s) / 2, (H - rh * s) / 2, rw * s, rh * s);
            return;
        }
        if (!_bgClips.TryGetValue(c.Id, out var bc) || bc.MediaId != bm.Id)
            _bgClips[c.Id] = bc = new Clip { Id = "bg:" + c.Id, Kind = ClipKind.Video, MediaId = bm.Id, TrimIn = 0, TrimOut = bm.Duration };
        var dec = GetDecoder("bg:" + c.Id, bm, bc, true, out _, out _);
        if (dec == null) return;
        double t = bm.Duration > 0.1 ? local % bm.Duration : 0; // loop short backgrounds
        var buf = dec.GetFrame((int)Math.Floor(t * Fps + 1e-6));
        if (buf != null) Compositor.DrawScaled(dst, buf, dec.FrameW, dec.FrameH, 0, 0, W, H);
    }

    static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }

    static readonly string[] AutoPans = { "zoomin", "panright", "zoomout", "panleft", "zoomleft", "pandown", "zoomright", "panup" };

    (double x, double y, double w, double h) PanRect(Clip c, int index, double bw, double bh, double local, double dur)
    {
        string pan = c.Pan;
        if (pan == "auto") pan = AutoPans[(index * 7 + c.Id.GetHashCode() & 0x7fffffff) % AutoPans.Length];
        double lp = dur > 0 ? local / dur : 0;
        if (c.Reverse) lp = 1 - lp;
        double p = Smooth(lp) * 0.85 + lp * 0.15;
        double z = 1, ox = 0, oy = 0;
        const double Z = 0.18, O = 0.07;
        switch (pan)
        {
            case "zoomin": z = 1 + Z * p; break;
            case "zoomout": z = 1 + Z * (1 - p); break;
            case "panleft": z = 1 + O * 2.2; ox = -O + 2 * O * p; break;
            case "panright": z = 1 + O * 2.2; ox = O - 2 * O * p; break;
            case "panup": z = 1 + O * 2.2; oy = -O + 2 * O * p; break;
            case "pandown": z = 1 + O * 2.2; oy = O - 2 * O * p; break;
            case "zoomleft": z = 1 + Z * p; ox = Z / 2 * p; break;
            case "zoomright": z = 1 + Z * p; ox = -Z / 2 * p; break;
        }
        double w = bw * z, h = bh * z;
        return (W / 2.0 - w / 2 + ox * bw, H / 2.0 - h / 2 + oy * bh, w, h);
    }

    void RenderClip(int index, double local, Frame dst, int frameNo)
    {
        var c = _p.Clips[index];
        double dur = _tl.Dur[index];
        var full = new RectI(0, 0, W, H);
        RectI content = full;
        if (c.Kind == ClipKind.Title)
        {
            Compositor.Fill(dst, Compositor.ParseColor(c.BgColor));
            if (c.Effect != "none" || c.Brightness != 0 || c.Contrast != 0 || c.Saturation != 0)
                ApplyFx(dst, c, full, frameNo);
            DrawText(dst, c.Text, c.Style, local, dur, frameNo);
            if (c.TitleBorder == "classic") DrawClassicBorder(dst, c.Style?.Color ?? "#FFFFFF");
            return;
        }
        bool keyed = c.Key != null && c.Key.Enabled && c.Kind != ClipKind.Title;
        if (keyed) DrawKeyBackground(c, local, dst); else Compositor.Fill(dst, Bg);
        var m = _p.GetMedia(c.MediaId);
        if (m == null) return;
        if (c.Kind == ClipKind.Video)
        {
            var dec = GetDecoder(c, out int fw, out int fh);
            if (dec == null) { DrawMissing(dst, m); return; }
            int k = (int)Math.Floor(local * Fps + 1e-6);
            var buf = dec.GetFrame(k);
            if (buf != null)
            {
                var r = PanRect(c, index, dec.FrameW, dec.FrameH, local, dur);
                if (keyed) buf = _keyBuf = Compositor.ChromaKey(buf, dec.FrameW, dec.FrameH, Compositor.ParseColor(c.Key.Color), c.Key.Tolerance, c.Key.Softness, c.Key.Spill, _keyBuf);
                Compositor.DrawScaled(dst, buf, dec.FrameW, dec.FrameH, r.x, r.y, r.w, r.h, keyed ? 0.998 : 1); // <1 skips the opaque fast path so transparency is honoured
                content = RectI.Intersect(full, new RectI((int)Math.Floor(r.x), (int)Math.Floor(r.y), (int)Math.Ceiling(r.w) + 1, (int)Math.Ceiling(r.h) + 1));
            }
        }
        else if (c.Kind == ClipKind.Image)
        {
            if (!File.Exists(m.Path)) { DrawMissing(dst, m); return; }
            var (rw, rh) = FF.RotatedSize(m, c.Rotation);
            var (bw, bh) = FitSize(rw, rh);
            bool hasPan = c.Pan != "none";
            var img = ImageLoader.Load(m, c.Rotation, (int)(bw * (hasPan ? 1.25 : 1.0)) + 2, (int)(bh * (hasPan ? 1.25 : 1.0)) + 2);
            if (img != null)
            {
                var r = PanRect(c, index, bw, bh, local, dur);
                var data = keyed ? (_keyBuf = Compositor.ChromaKey(img.Data, img.W, img.H, Compositor.ParseColor(c.Key.Color), c.Key.Tolerance, c.Key.Softness, c.Key.Spill, _keyBuf)) : img.Data;
                Compositor.DrawScaled(dst, data, img.W, img.H, r.x, r.y, r.w, r.h, 0.998);
                content = RectI.Intersect(full, new RectI((int)Math.Floor(r.x), (int)Math.Floor(r.y), (int)Math.Ceiling(r.w) + 1, (int)Math.Ceiling(r.h) + 1));
            }
        }
        if (c.Effect != "none" || c.Brightness != 0 || c.Contrast != 0 || c.Saturation != 0)
            ApplyFx(dst, c, content, frameNo);
    }

    void ApplyFx(Frame dst, Clip c, RectI content, int frameNo)
    {
        if (c.Effect is "soft" or "bloom" or "sharpen") _scratch ??= new Frame(W, H);
        // Cinematic bars and vignettes look right on the whole frame.
        var rc = c.Effect is "cinematic" or "vignette" or "noir" or "oldfilm" or "night" or "silent" or "western" or "vhs" or "glitch" ? new RectI(0, 0, W, H) : content;
        Compositor.ApplyEffect(dst, rc, c.Effect, c.Brightness, c.Contrast, c.Saturation, frameNo, _scratch);
    }

    void DrawMissing(Frame dst, MediaRef m)
    {
        Compositor.Fill(dst, 0xFF301010u);
        var st = new TextStyle { Size = 0.045, VAlign = "Middle", Color = "#FFB0B0", Outline = 0, Shadow = false };
        var img = TextRasterizer.Render($"Missing file:\n{m.Name}", st, W, H);
        if (img != null) Compositor.DrawScaled(dst, img.Data, img.W, img.H, (W - img.W) / 2.0, (H - img.H) / 2.0, img.W, img.H, 1, true);
    }

    void DrawClassicBorder(Frame dst, string color)
    {
        uint col = Compositor.ParseColor(color);
        int m = (int)(Math.Min(W, H) * 0.06), t = Math.Max(1, H / 360), gap = Math.Max(3, H / 120);
        void Box(int inset, int th)
        {
            Compositor.Fill(dst, col, new RectI(inset, inset, W - inset * 2, th));
            Compositor.Fill(dst, col, new RectI(inset, H - inset - th, W - inset * 2, th));
            Compositor.Fill(dst, col, new RectI(inset, inset, th, H - inset * 2));
            Compositor.Fill(dst, col, new RectI(W - inset - th, inset, th, H - inset * 2));
        }
        Box(m, t * 2); Box(m + gap + t * 2, t);
        int d = m / 3;
        foreach (var (cx, cy) in new[] { (m, m), (W - m, m), (m, H - m), (W - m, H - m) })
            for (int y = -d; y <= d; y++) { int w = d - Math.Abs(y); Compositor.Fill(dst, col, new RectI(cx - w, cy + y, w * 2 + 1, 1)); }
    }

    void DrawText(Frame dst, string text, TextStyle st, double local, double dur, int frameNo = 0)
    {
        if (string.IsNullOrEmpty(text) || st == null) return;
        string fx = st.Effect ?? "none";
        string shown = text;
        if (fx == "typewriter")
        {
            double frac = Math.Clamp(local / Math.Max(0.1, dur * 0.6), 0, 1);
            int count = (int)Math.Ceiling(text.Length * frac);
            if (count <= 0) return;
            shown = text[..Math.Min(text.Length, count)];
        }
        var img = TextRasterizer.Render(shown, st, W, H);
        if (img == null) return;
        // For typewriter, anchor to the full text's size so lines don't jump.
        int refH = img.H;
        if (fx == "typewriter" && shown.Length != text.Length)
        {
            var full = TextRasterizer.Render(text, st, W, H);
            if (full != null) refH = full.H;
        }

        double x = (W - img.W) / 2.0;
        double y = st.VAlign switch
        {
            "Top" => H * 0.05 - img.H * 0.05,
            "Middle" => (H - refH) / 2.0,
            _ => H * 0.95 - refH,
        };
        double inDur = Math.Min(0.6, dur / 3), outDur = Math.Min(0.45, dur / 4);
        double aIn = inDur > 0 ? Math.Clamp(local / inDur, 0, 1) : 1;
        double aOut = outDur > 0 ? Math.Clamp((dur - local) / outDur, 0, 1) : 1;
        double eIn = Smooth(aIn);
        double alpha = Math.Clamp(st.Opacity, 0, 1), scale = 1, dx = 0, dy = 0;
        switch (fx)
        {
            case "none": break;
            case "fade": alpha *= Math.Min(eIn, aOut); break;
            case "flyleft": dx = -(1 - eIn) * W; alpha *= aOut; break;
            case "flyright": dx = (1 - eIn) * W; alpha *= aOut; break;
            case "flyup": dy = (1 - eIn) * H * 0.6; alpha *= aOut; break;
            case "zoomin": scale = 0.25 + 0.75 * eIn; alpha *= Math.Min(eIn, aOut); break;
            case "pop":
                {
                    double t = aIn - 1;
                    scale = Math.Max(0.01, 1 + 2.70158 * t * t * t + 1.70158 * t * t);
                    alpha *= Math.Min(Math.Clamp(aIn * 3, 0, 1), aOut);
                    break;
                }
            case "typewriter": alpha *= aOut; break;
            case "scroll":
                {
                    double f = dur > 0 ? local / dur : 0;
                    y = H - (H + img.H) * f;
                    break;
                }
            case "slowzoom": scale = 1 + 0.12 * (dur > 0 ? local / dur : 0); alpha *= Math.Min(eIn, aOut); break;
            case "swing": dy = (1 - eIn) * H * 0.08; alpha *= Math.Min(eIn, aOut); break;
            case "stamp":
                {
                    double a2 = Math.Clamp(local / 0.22, 0, 1);
                    scale = 1.7 - 0.7 * a2 * a2; alpha *= Math.Min(a2, aOut);
                    if (local > 0.22 && local < 0.32) dy = Math.Sin((local - 0.22) * 90) * H * 0.006;
                    break;
                }
            case "flicker":
                {
                    uint hh = (uint)(frameNo * 2654435761u);
                    alpha *= Math.Min(eIn, aOut) * (0.82 + 0.18 * ((hh >> 8) & 255) / 255.0);
                    dy = (((hh >> 16) & 3) - 1.5) * H * 0.0015;
                    break;
                }
            case "glitch":
                {
                    alpha *= Math.Min(Math.Clamp(aIn * 2, 0, 1), aOut);
                    uint hh = (uint)((frameNo / 2) * 2654435761u);
                    if (aIn < 1 || (hh & 7) == 0)
                    {
                        dx = (((int)(hh >> 8) % 9) - 4) * W * 0.002;
                        double ww = img.W * scale, hh2 = img.H * scale, cx0 = x + img.W / 2.0, cy0 = y + img.H / 2.0;
                        Compositor.DrawScaled(dst, img.Data, img.W, img.H, cx0 - ww / 2 - W * 0.006, cy0 - hh2 / 2, ww, hh2, alpha * 0.45, true);
                        Compositor.DrawScaled(dst, img.Data, img.W, img.H, cx0 - ww / 2 + W * 0.006, cy0 - hh2 / 2, ww, hh2, alpha * 0.45, true);
                    }
                    break;
                }
        }
        if (alpha <= 0.002) return;
        double w = img.W * scale, h = img.H * scale;
        double cx = x + img.W / 2.0 + dx, cy = y + img.H / 2.0 + dy;
        RectI? clip = null;
        if (fx == "wipe")
        {
            double rv = Smooth(Math.Clamp(local / Math.Min(0.9, dur / 2), 0, 1));
            clip = new RectI(0, 0, (int)(cx - w / 2 + w * rv) + 1, H);
            alpha *= aOut;
        }
        Compositor.DrawScaled(dst, img.Data, img.W, img.H, cx - w / 2, cy - h / 2, w, h, alpha, true, clip);
    }

    public void Dispose()
    {
        foreach (var d in _decs.Values) d.dec.Dispose();
        _decs.Clear();
    }
}
