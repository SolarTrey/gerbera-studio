using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MovieMakerReborn.Engine;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Views;

/// <summary>
/// The storyboard: clips laid out as wrapping filmstrip tiles (width proportional to duration),
/// with caption and music lanes beneath each row, a playhead, drag-to-reorder and drag-to-move bars.
/// </summary>
public class StoryboardView : FrameworkElement
{
    public Project Project;
    public Timeline TL = new();
    public double PxPerSec = 14;
    public double TileH = 64;
    public double Playhead;
    public readonly HashSet<string> SelectedClips = new();
    public string SelectedMusic, SelectedCaption;

    public event Action<double> SeekRequested;
    public event Action SelectionChanged;
    public event Action<List<string>, int> MoveClips;
    public event Action<string[], int> FilesDropped;
    public event Action<string, string> ItemDoubleClick; // kind, id
    public event Action EmptyClick;
    public event Action DragStarted;
    public event Action DragEnded;

    const double Sprocket = 9, Pad = 12, Gap = 0, Lane = 15, LaneGap = 2, RowGap = 14, MinTileW = 54;
    double RowH => Sprocket + TileH + Sprocket + LaneGap + Lane + LaneGap + Lane + RowGap;

    struct Seg { public int Clip; public double A, B; public Rect R; public int Row; }
    readonly List<Seg> _segs = new();
    double _layoutWidth = -1;

    public StoryboardView()
    {
        AllowDrop = true;
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        Thumbs.Updated += () => Dispatcher.BeginInvoke(new Action(InvalidateVisual));
    }

    public void Update(Project p)
    {
        Project = p;
        TL = p.ComputeTimeline();
        _layoutWidth = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    void Layout(double width)
    {
        _segs.Clear();
        _layoutWidth = width;
        if (Project == null) return;
        double x = Pad, y = Pad; int row = 0;
        double right = Math.Max(width - Pad, Pad + 120);
        for (int i = 0; i < Project.Clips.Count; i++)
        {
            double D = TL.Dur[i];
            double wTotal = Math.Max(MinTileW, D * PxPerSec);
            double remaining = wTotal, a = 0;
            while (remaining > 0.5)
            {
                double space = right - x;
                if (space < Math.Min(60, remaining) && x > Pad) { x = Pad; y += RowH; row++; continue; }
                double take = Math.Min(remaining, space);
                double b = remaining - take <= 0.5 ? D : a + D * take / wTotal;
                _segs.Add(new Seg { Clip = i, A = a, B = b, R = new Rect(x, y + Sprocket, take, TileH), Row = row });
                x += take; remaining -= take; a = b;
                if (remaining > 0.5) { x = Pad; y += RowH; row++; }
            }
            x += Gap;
        }
    }

    protected override Size MeasureOverride(Size available)
    {
        double w = double.IsInfinity(available.Width) ? 800 : available.Width;
        Layout(w);
        double h = _segs.Count == 0 ? 160 : _segs.Max(s => s.R.Bottom) + Sprocket + LaneGap * 2 + Lane * 2 + RowGap + Pad;
        return new Size(w, Math.Max(h, double.IsInfinity(available.Height) ? h : 0));
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        if (Math.Abs(info.NewSize.Width - _layoutWidth) > 0.5) { InvalidateMeasure(); InvalidateVisual(); }
    }

    Brush Res(string key) => (Brush)(TryFindResource(key) ?? Brushes.Gray);

    static readonly Typeface UiFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    static readonly Typeface UiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    static readonly Typeface IconFace = new(new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    FormattedText Txt(string s, double size, Brush b, Typeface tf = null) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, tf ?? UiFace, size, b, VisualTreeHelper.GetDpi(this).PixelsPerDip);

    // Drag state
    enum DragMode { None, PendingClip, Clip, Music, Caption }
    DragMode _drag;
    Point _dragStart;
    double _dragOrigStart;
    string _dragId;
    int _insertIndex = -1;
    int _hoverClip = -2;

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(Res("B.Storyboard"), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (Project == null) return;
        if (Math.Abs(ActualWidth - _layoutWidth) > 0.5) Layout(ActualWidth);
        var dim = Res("B.Dim");
        if (_segs.Count == 0)
        {
            var ft = Txt("Click here to browse for videos and photos", 15, dim);
            var g = Txt("", 28, Res("B.GlyphBlue"), IconFace);
            double cx = ActualWidth / 2, cy = Math.Max(60, ActualHeight / 2 - 20);
            dc.DrawText(g, new Point(cx - g.Width / 2, cy - 40));
            dc.DrawText(ft, new Point(cx - ft.Width / 2, cy));
            var ft2 = Txt("You can also drag files from File Explorer onto this area.", 12, dim);
            dc.DrawText(ft2, new Point(cx - ft2.Width / 2, cy + 26));
            return;
        }

        var tileBg = Res("B.TileBg");
        var tileBorder = new Pen(Res("B.SprocketBand"), 2);
        var selPen = new Pen(Res("B.Selection"), 2.5);
        var musicB = Res("B.Music");
        var capB = Res("B.Caption");
        var white = Brushes.White;
        var musicSelPen = new Pen(Res("B.Text"), 2);

        var band = Res("B.SprocketBand"); var hole = Res("B.Sprocket");
        foreach (var s in _segs)
        {
            var rr = s.R;
            dc.DrawRectangle(band, null, new Rect(rr.X, rr.Y - Sprocket, rr.Width, Sprocket));
            dc.DrawRectangle(band, null, new Rect(rr.X, rr.Bottom, rr.Width, Sprocket));
            for (double hx = Math.Ceiling((rr.X + 2) / 11) * 11; hx + 5 < rr.Right; hx += 11)
            {
                dc.DrawRoundedRectangle(hole, null, new Rect(hx, rr.Y - Sprocket + 2.5, 5, 4), 1, 1);
                dc.DrawRoundedRectangle(hole, null, new Rect(hx, rr.Bottom + 2.5, 5, 4), 1, 1);
            }
        }
        foreach (var s in _segs)
        {
            var c = Project.Clips[s.Clip];
            var r = s.R;
            var m = Project.GetMedia(c.MediaId);
            dc.PushClip(new RectangleGeometry(r, 2, 2));
            dc.DrawRectangle(tileBg, null, r);
            if (c.Kind == ClipKind.Title)
            {
                Brush bg;
                try { bg = new SolidColorBrush((Color)ColorConverter.ConvertFromString(c.BgColor)); } catch { bg = Brushes.Black; }
                dc.DrawRectangle(bg, null, r);
                if (s.A == 0)
                {
                    var line = (c.Text ?? "").Split('\n').FirstOrDefault() ?? "";
                    var ft = Txt(line, 11, white, UiBold);
                    ft.MaxTextWidth = Math.Max(10, r.Width - 8); ft.MaxLineCount = 2; ft.Trimming = TextTrimming.CharacterEllipsis;
                    dc.DrawText(ft, new Point(r.X + 4, r.Y + r.Height / 2 - ft.Height / 2));
                }
            }
            else
            {
                var thumbs = Thumbs.Get(m, c);
                if (thumbs != null && thumbs.Length > 0)
                {
                    double tw = TileH * 16 / 9;
                    for (double x = r.X; x < r.Right; x += tw)
                    {
                        double local = s.A + (x - r.X) / r.Width * (s.B - s.A);
                        int idx = Math.Clamp((int)(local / Math.Max(0.001, TL.Dur[s.Clip]) * thumbs.Length), 0, thumbs.Length - 1);
                        var img = thumbs[idx];
                        for (int d = 1; img == null && d < thumbs.Length; d++)
                            img = (idx - d >= 0 ? thumbs[idx - d] : null) ?? (idx + d < thumbs.Length ? thumbs[idx + d] : null);
                        if (img != null) dc.DrawImage(img, new Rect(x, r.Y, tw, TileH));
                    }
                }
                else if (m == null || !File.Exists(m.Path))
                {
                    var ft = Txt("Missing file", 11, Brushes.IndianRed);
                    dc.DrawText(ft, new Point(r.X + 4, r.Y + 4));
                }
            }
            // Transition marker (left corner) and badges
            if (s.A == 0)
            {
                if (s.Clip > 0 && c.Transition != "none")
                {
                    var tri = new StreamGeometry();
                    using (var g = tri.Open())
                    {
                        g.BeginFigure(new Point(r.X, r.Y), true, true);
                        g.LineTo(new Point(r.X + 16, r.Y), true, false);
                        g.LineTo(new Point(r.X, r.Y + 16), true, false);
                    }
                    dc.DrawGeometry(new SolidColorBrush(Color.FromArgb(230, 240, 240, 240)), new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 1), tri);
                }
                double bx = r.Right - 4;
                void Badge(string glyph, bool icon)
                {
                    var ft = icon ? Txt(glyph, 10, white, IconFace) : Txt(glyph, 9.5, white, UiBold);
                    var br = new Rect(bx - ft.Width - 6, r.Y + 3, ft.Width + 6, 14);
                    if (br.X < r.X + 18) return;
                    dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)), null, br, 3, 3);
                    dc.DrawText(ft, new Point(br.X + 3, br.Y + (14 - ft.Height) / 2));
                    bx = br.X - 2;
                }
                if (c.Effect != "none" || c.Brightness != 0 || c.Contrast != 0 || c.Saturation != 0) Badge("fx", false);
                if (c.Pan != "none") Badge("", true);
                if (Math.Abs(c.Speed - 1) > 1e-6) Badge($"{c.Speed:0.##}×", false);
                if (c.Kind == ClipKind.Video && m != null && m.HasAudio && c.Volume <= 0.001) Badge("", true);
            }
            dc.Pop();
            bool sel = SelectedClips.Contains(c.Id);
            dc.DrawRoundedRectangle(null, sel ? selPen : tileBorder, r, 2, 2);

            // Lanes
            double t0 = TL.Start[s.Clip] + s.A, t1 = TL.Start[s.Clip] + s.B;
            double laneCapY = r.Bottom + Sprocket + LaneGap, laneMusY = laneCapY + Lane + LaneGap;
            double X(double t) => r.X + (t - t0) / Math.Max(1e-6, t1 - t0) * r.Width;
            foreach (var cap in Project.Captions)
            {
                double a = Math.Max(cap.Start, t0), b = Math.Min(cap.Start + cap.Duration, t1);
                if (b <= a) continue;
                var br = new Rect(X(a), laneCapY, Math.Max(2, X(b) - X(a)), Lane);
                bool isSel = cap.Id == SelectedCaption;
                dc.DrawRoundedRectangle(capB, isSel ? musicSelPen : null, br, 2, 2);
                dc.PushClip(new RectangleGeometry(br));
                var ft = Txt("", 9, white, IconFace);
                dc.DrawText(ft, new Point(br.X + 3, br.Y + 2.5));
                var lt = Txt(cap.Text.Replace('\n', ' '), 10, white);
                dc.DrawText(lt, new Point(br.X + 15, br.Y + 0.5));
                dc.Pop();
            }
            foreach (var mu in Project.Music)
            {
                double end = mu.Start + mu.Length;
                double a = Math.Max(mu.Start, t0), b = Math.Min(end, t1);
                if (b <= a) continue;
                var br = new Rect(X(a), laneMusY, Math.Max(2, X(b) - X(a)), Lane);
                bool isSel = mu.Id == SelectedMusic;
                dc.DrawRoundedRectangle(musicB, isSel ? musicSelPen : null, br, 2, 2);
                dc.PushClip(new RectangleGeometry(br));
                var ft = Txt("", 9, white, IconFace);
                dc.DrawText(ft, new Point(br.X + 3, br.Y + 2.5));
                var name = Project.GetMedia(mu.MediaId)?.Name ?? "Music";
                dc.DrawText(Txt(name, 10, white), new Point(br.X + 15, br.Y + 0.5));
                dc.Pop();
            }
        }

        // Playhead
        var ph = PlayheadPoint(Playhead);
        if (ph.HasValue)
        {
            var p = ph.Value;
            var pb = Res("B.Playhead");
            dc.DrawRectangle(pb, null, new Rect(Math.Round(p.X) - 1, p.Y - Sprocket, 2, TileH + Sprocket * 2 + Lane * 2 + LaneGap * 2));
            var tri = new StreamGeometry();
            using (var g = tri.Open())
            {
                g.BeginFigure(new Point(p.X - 5, p.Y - Sprocket - 6), true, true);
                g.LineTo(new Point(p.X + 5, p.Y - Sprocket - 6), true, false);
                g.LineTo(new Point(p.X, p.Y - Sprocket), true, false);
            }
            dc.DrawGeometry(pb, null, tri);
        }

        // Insertion marker
        if ((_drag == DragMode.Clip || _externalDrag) && _insertIndex >= 0)
        {
            var ip = InsertPoint(_insertIndex);
            if (ip.HasValue)
                dc.DrawRectangle(Res("B.Insert"), null, new Rect(ip.Value.X - 2, ip.Value.Y - 4, 4, TileH + 8));
        }
    }

    public Point? PlayheadPoint(double t)
    {
        if (_segs.Count == 0 || Project == null) return null;
        int i = TL.IndexAt(t);
        if (i < 0) i = 0;
        double local = t - TL.Start[i];
        foreach (var s in _segs)
        {
            if (s.Clip != i) continue;
            if (local >= s.A - 1e-9 && (local <= s.B + 1e-9))
                return new Point(s.R.X + (local - s.A) / Math.Max(1e-9, s.B - s.A) * s.R.Width, s.R.Y);
        }
        var last = _segs[^1];
        return new Point(last.R.Right, last.R.Y);
    }

    public Rect PlayheadRect()
    {
        var p = PlayheadPoint(Playhead);
        return p.HasValue ? new Rect(p.Value.X - 20, p.Value.Y - 10, 40, RowH + 10) : Rect.Empty;
    }

    Point? InsertPoint(int index)
    {
        if (_segs.Count == 0) return new Point(Pad, Pad);
        if (index < Project.Clips.Count)
        {
            var s = _segs.First(x => x.Clip == index);
            return new Point(s.R.X - Gap / 2, s.R.Y);
        }
        var l = _segs[^1];
        return new Point(l.R.Right + Gap / 2, l.R.Y);
    }

    enum HitKind { None, Clip, Music, Caption }

    (HitKind kind, int clip, double time, string id) Hit(Point p)
    {
        foreach (var s in _segs)
        {
            var r = s.R;
            if (p.X < r.X || p.X > r.Right) continue;
            double t0 = TL.Start[s.Clip] + s.A, t1 = TL.Start[s.Clip] + s.B;
            double t = t0 + (p.X - r.X) / Math.Max(1, r.Width) * (t1 - t0);
            if (p.Y >= r.Y && p.Y <= r.Bottom) return (HitKind.Clip, s.Clip, t, Project.Clips[s.Clip].Id);
            double capY = r.Bottom + Sprocket + LaneGap, musY = capY + Lane + LaneGap;
            if (p.Y >= capY && p.Y < capY + Lane)
            {
                var cap = Project.Captions.LastOrDefault(c => t >= c.Start && t < c.Start + c.Duration);
                return cap != null ? (HitKind.Caption, s.Clip, t, cap.Id) : (HitKind.None, s.Clip, t, null);
            }
            if (p.Y >= musY && p.Y < musY + Lane)
            {
                var mu = Project.Music.LastOrDefault(m => t >= m.Start && t < m.Start + m.Length);
                return mu != null ? (HitKind.Music, s.Clip, t, mu.Id) : (HitKind.None, s.Clip, t, null);
            }
        }
        return (HitKind.None, -1, -1, null);
    }

    int InsertIndexAt(Point p)
    {
        if (_segs.Count == 0) return 0;
        Seg best = _segs[0]; double bestD = double.MaxValue;
        foreach (var s in _segs)
        {
            double dy = p.Y < s.R.Y ? s.R.Y - p.Y : p.Y > s.R.Y + RowH ? p.Y - s.R.Y - RowH : 0;
            double dx = p.X < s.R.X ? s.R.X - p.X : p.X > s.R.Right ? p.X - s.R.Right : 0;
            double d = dy * 10 + dx;
            if (d < bestD) { bestD = d; best = s; }
        }
        double local = best.A + Math.Clamp((p.X - best.R.X) / Math.Max(1, best.R.Width), 0, 1) * (best.B - best.A);
        return local < TL.Dur[best.Clip] / 2 ? best.Clip : best.Clip + 1;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        Focus();
        var p = e.GetPosition(this);
        if (Project == null) return;
        if (Project.Clips.Count == 0) { EmptyClick?.Invoke(); return; }
        var h = Hit(p);
        bool ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (e.ClickCount == 2 && h.kind != HitKind.None)
        {
            ItemDoubleClick?.Invoke(h.kind == HitKind.Caption ? "caption" : h.kind == HitKind.Music ? "music" : "clip", h.id);
            return;
        }
        switch (h.kind)
        {
            case HitKind.Clip:
                if (ctrl)
                {
                    if (!SelectedClips.Remove(h.id)) SelectedClips.Add(h.id);
                }
                else if (shift && SelectedClips.Count > 0)
                {
                    int anchor = Project.Clips.FindIndex(c => SelectedClips.Contains(c.Id));
                    int a = Math.Min(anchor, h.clip), b = Math.Max(anchor, h.clip);
                    SelectedClips.Clear();
                    for (int i = a; i <= b; i++) SelectedClips.Add(Project.Clips[i].Id);
                }
                else if (!SelectedClips.Contains(h.id))
                {
                    SelectedClips.Clear(); SelectedClips.Add(h.id);
                }
                SelectedMusic = SelectedCaption = null;
                _drag = DragMode.PendingClip; _dragStart = p; _dragId = h.id;
                CaptureMouse();
                SelectionChanged?.Invoke();
                SeekRequested?.Invoke(h.time);
                break;
            case HitKind.Music:
            case HitKind.Caption:
                SelectedClips.Clear();
                if (h.kind == HitKind.Music) { SelectedMusic = h.id; SelectedCaption = null; _drag = DragMode.Music; _dragOrigStart = Project.Music.First(m => m.Id == h.id).Start; }
                else { SelectedCaption = h.id; SelectedMusic = null; _drag = DragMode.Caption; _dragOrigStart = Project.Captions.First(c => c.Id == h.id).Start; }
                _dragStart = p; _dragId = h.id; _dragNotified = false;
                CaptureMouse();
                SelectionChanged?.Invoke();
                SeekRequested?.Invoke(h.time);
                break;
            default:
                if (h.time >= 0) SeekRequested?.Invoke(h.time);
                else
                {
                    SelectedClips.Clear(); SelectedMusic = SelectedCaption = null;
                    SelectionChanged?.Invoke();
                }
                break;
        }
        InvalidateVisual();
    }

    bool _dragNotified;

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var p = e.GetPosition(this);
        if (_drag == DragMode.None)
        {
            var h = Hit(p);
            int hc = h.kind == HitKind.Clip ? h.clip : -1;
            if (hc != _hoverClip)
            {
                _hoverClip = hc;
                if (hc >= 0)
                {
                    var c = Project.Clips[hc];
                    var m = Project.GetMedia(c.MediaId);
                    string name = c.Kind == ClipKind.Title ? "Title" : m?.Name ?? "?";
                    var info = $"{name}\nDuration: {FormatTime(TL.Dur[hc])}";
                    if (c.Kind == ClipKind.Video && m != null) info += $"\n{m.Width}×{m.Height}, {m.Fps:0.##} fps, {m.VideoCodec}";
                    ToolTip = info;
                }
                else ToolTip = null;
            }
            return;
        }
        if (e.LeftButton != MouseButtonState.Pressed) { EndDrag(); return; }
        double dx = p.X - _dragStart.X;
        switch (_drag)
        {
            case DragMode.PendingClip:
                if (Math.Abs(dx) > 6 || Math.Abs(p.Y - _dragStart.Y) > 6) { _drag = DragMode.Clip; ToolTip = null; }
                break;
            case DragMode.Clip:
                _insertIndex = InsertIndexAt(p);
                InvalidateVisual();
                break;
            case DragMode.Music:
            case DragMode.Caption:
                if (!_dragNotified && Math.Abs(dx) > 3) { _dragNotified = true; DragStarted?.Invoke(); }
                if (!_dragNotified) break;
                double ns = Math.Max(0, _dragOrigStart + dx / PxPerSec);
                if (_drag == DragMode.Music) { var mu = Project.Music.FirstOrDefault(m => m.Id == _dragId); if (mu != null) mu.Start = ns; }
                else { var cp = Project.Captions.FirstOrDefault(c => c.Id == _dragId); if (cp != null) cp.Start = ns; }
                InvalidateVisual();
                break;
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (_drag == DragMode.Clip && _insertIndex >= 0)
        {
            var ids = Project.Clips.Where(c => SelectedClips.Contains(c.Id)).Select(c => c.Id).ToList();
            if (ids.Count == 0 && _dragId != null) ids.Add(_dragId);
            int ins = _insertIndex;
            EndDrag();
            MoveClips?.Invoke(ids, ins);
            return;
        }
        bool moved = (_drag == DragMode.Music || _drag == DragMode.Caption) && _dragNotified;
        EndDrag();
        if (moved) DragEnded?.Invoke();
    }

    protected override void OnMouseRightButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonDown(e);
        Focus();
        if (Project == null) return;
        var h = Hit(e.GetPosition(this));
        if (h.kind == HitKind.Clip && !SelectedClips.Contains(h.id)) { SelectedClips.Clear(); SelectedClips.Add(h.id); SelectedMusic = SelectedCaption = null; }
        else if (h.kind == HitKind.Music) { SelectedClips.Clear(); SelectedMusic = h.id; SelectedCaption = null; }
        else if (h.kind == HitKind.Caption) { SelectedClips.Clear(); SelectedCaption = h.id; SelectedMusic = null; }
        if (h.time >= 0) SeekRequested?.Invoke(h.time);
        SelectionChanged?.Invoke();
        InvalidateVisual();
    }

    void EndDrag()
    {
        _drag = DragMode.None;
        _insertIndex = -1;
        if (IsMouseCaptured) ReleaseMouseCapture();
        InvalidateVisual();
    }

    bool _externalDrag;

    protected override void OnDragOver(DragEventArgs e)
    {
        base.OnDragOver(e);
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            _externalDrag = true;
            _insertIndex = Project == null ? 0 : InsertIndexAt(e.GetPosition(this));
            InvalidateVisual();
        }
        else e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    protected override void OnDragLeave(DragEventArgs e)
    {
        base.OnDragLeave(e);
        _externalDrag = false; _insertIndex = -1;
        InvalidateVisual();
    }

    protected override void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);
        int ins = _insertIndex;
        _externalDrag = false; _insertIndex = -1;
        InvalidateVisual();
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
            FilesDropped?.Invoke(files, ins < 0 ? (Project?.Clips.Count ?? 0) : ins);
    }

    public static string FormatTime(double s)
    {
        if (s < 0) s = 0;
        var ts = TimeSpan.FromSeconds(s);
        return ts.TotalHours >= 1 ? $"{(int)ts.TotalHours}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}" : $"{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds / 10:00}";
    }
}
