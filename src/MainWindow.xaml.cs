using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using MovieMakerReborn.Engine;
using MovieMakerReborn.Model;
using MovieMakerReborn.Views;

namespace MovieMakerReborn;

public partial class MainWindow : Window
{
    Project _p = new();
    string _path;
    bool _dirty;
    readonly List<string> _undo = new(), _redo = new();
    readonly Settings _settings = Settings.Load();
    readonly Player _player = new();
    WriteableBitmap _wb;
    byte[] _front;
    int _frontW, _frontH;
    double _frontT;
    bool _framePending;
    readonly object _frameLock = new();
    bool _sync;          // suppress control events while syncing the ribbon
    bool _liveActive;    // an undo point already exists for an ongoing slider/text edit
    double _playhead;
    string _clipboard;
    readonly DispatcherTimer _statusTimer = new() { Interval = TimeSpan.FromSeconds(6) };
    readonly DispatcherTimer _galleryTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    FullscreenWindow _fullscreen;

    static readonly string[] FadeLabels = { "None", "Slow", "Medium", "Fast" };
    static readonly double[] FadeValues = { 0, 2, 1, 0.5 };
    static readonly double[] Speeds = { 0.125, 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4, 8, 16 };
    static readonly double[] Durations = { 1, 2, 3, 4, 5, 7, 10, 15, 20, 30 };
    static readonly int[] FontSizes = { 16, 20, 24, 28, 32, 36, 40, 48, 56, 64, 72, 96, 120, 144, 180 };
    static readonly (string Label, double Value)[] Outlines = { ("None", 0), ("Thin", 0.03), ("Medium", 0.06), ("Thick", 0.11) };
    static readonly (string Label, int H)[] PreviewQualities = { ("360p (fastest)", 360), ("540p", 540), ("720p", 720), ("1080p (sharpest)", 1080) };

    public MainWindow()
    {
        InitializeComponent();
        App.SetTheme(_settings.Theme);
        ThemeHelper.ApplyTitleBar(this);
        InitControls();
        Board.TileH = _settings.ThumbSize;
        _sync = true; ZoomSlider.Value = _settings.Zoom; _sync = false;
        ApplyZoom();

        _player.FrameReady += OnFrame;
        _player.PlayingChanged += playing => Dispatcher.BeginInvoke(() => PlayButton.Content = playing ? "" : "");
        _player.Error += msg => Dispatcher.BeginInvoke(() => ShowStatus("Preview: " + msg));

        Board.SeekRequested += t => SeekTo(t);
        Board.SelectionChanged += () => { UpdateContext(); SyncRibbon(); UpdateStatus(); UpdateInfo(); ScheduleGalleryImages(); };
        Board.MoveClips += MoveClips;
        Board.FilesDropped += (files, idx) => ImportFiles(files, idx);
        Board.EmptyClick += () => AddMedia();
        Board.ItemDoubleClick += (kind, id) =>
        {
            if (kind == "caption" || (kind == "clip" && _p.Clips.FirstOrDefault(c => c.Id == id)?.Kind == ClipKind.Title)) EditText();
            else if (kind == "music") TabMusic.IsChecked = true;
            else if (kind == "clip") TabVideo.IsChecked = true;
        };
        Board.DragStarted += () => PushUndo();
        Board.DragEnded += () => { _dirty = true; Refresh(); };
        Board.ContextMenu = BuildContextMenu();
        Thumbs.Updated += () => Dispatcher.BeginInvoke(ScheduleGalleryImages);

        _statusTimer.Tick += (_, _) => { _statusTimer.Stop(); UpdateStatus(); };
        _galleryTimer.Tick += (_, _) => { _galleryTimer.Stop(); UpdateGalleryImages(); };
        StateChanged += (_, _) =>
        {
            RootBorder.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);
            MaxButton.Content = WindowState == WindowState.Maximized ? "" : "";
        };
        PreviewKeyDown += OnKey;
        Closing += OnClosing;
        Loaded += (_, _) =>
        {
            EnsureFfmpeg();
            Refresh();
            ShowWhatsNewIfUpdated();
        };
        ProjBgColor.ColorPicked += hex => Mutate(() => _p.Background = hex);
        TitleBgColor.ColorPicked += hex => Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind == ClipKind.Title)) c.BgColor = hex; });
        TextBgColor.ColorPicked += hex => Mutate(() => { var tt = TextTarget(); if (tt.clip != null) tt.clip.BgColor = hex; });
        TextColorBtn.ColorPicked += hex => EditStyle(s => s.Color = hex);
        GlowColorBtn.ColorPicked += hex => EditStyle(s => { s.GlowColor = hex; s.Glow = true; });
        KeyColorBtn.ColorPicked += hex => EditKey(k => k.Color = hex);
        KeyBgColorBtn.ColorPicked += hex => EditKey(k => k.BgColor = hex);
        OutlineColorBtn.ColorPicked += hex => EditStyle(s => { s.OutlineColor = hex; if (s.Outline <= 0) s.Outline = 0.06; });
    }

    // ===================================================================== setup

    void InitControls()
    {
        _sync = true;
        foreach (var d in new[] { 0.25, 0.5, 0.75, 1.0, 1.25, 1.5, 2.0, 3.0 }) TransDurCombo.Items.Add(d.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var f in Presets.FrameRates) FpsCombo.Items.Add(f.Label);
        foreach (var i in Presets.Interps) InterpCombo.Items.Add(i.Label);
        foreach (var f in new[] { "Letterbox (fit inside frame)", "Fill frame (crop edges)", "Stretch to fill" }) FitCombo.Items.Add(f);
        foreach (var q in PreviewQualities) PreviewQualityCombo.Items.Add(q.Label);
        PreviewQualityCombo.SelectedIndex = Math.Max(0, Array.FindIndex(PreviewQualities, q => q.H == _settings.PreviewHeight));
        foreach (var t in new[] { "Seconds (00:00.00)", "Timecode (HH:MM:SS:FF)", "Frame number" }) TimecodeCombo.Items.Add(t);
        TimecodeCombo.SelectedIndex = _settings.Timecode switch { "smpte" => 1, "frames" => 2, _ => 0 };
        foreach (var c in new[] { ClipFadeIn, ClipFadeOut, MusicFadeIn, MusicFadeOut }) foreach (var l in FadeLabels) c.Items.Add(l);
        foreach (var s in Speeds) SpeedCombo.Items.Add($"{s.ToString("0.###", CultureInfo.InvariantCulture)}×");
        foreach (var d in Durations) DurationCombo.Items.Add(d.ToString("0.00", CultureInfo.InvariantCulture));
        foreach (var f in Fonts.SystemFontFamilies.Select(f => f.Source).Distinct().OrderBy(s => s)) FontCombo.Items.Add(f);
        foreach (var s in FontSizes) FontSizeCombo.Items.Add(s.ToString());
        foreach (var o in Outlines) OutlineCombo.Items.Add(o.Label);
        foreach (var a in Presets.AudioFx) { ClipAudioFx.Items.Add(a.Label); MusicAudioFx.Items.Add(a.Key == "match" ? "(match is for video clips)" : a.Label); }
        foreach (var pl in new[] { "No background", "Box behind text", "Full-width bar" }) PlateCombo.Items.Add(pl);
        TitleStyleGallery.SetItems(Presets.TitleStyles.Select(t => new Gallery.Item { Key = t.Key, Label = t.Label, Glyph = "\uE8D2" }));
        TitleStyleGallery.Picked += k => ApplyTitleStyle(k);

        TransGallery.SetItems(Presets.Transitions.Select(t => new Gallery.Item { Key = t.Key, Label = t.Label }));
        FxGallery.SetItems(Presets.Effects.Select(t => new Gallery.Item { Key = t.Key, Label = t.Label }));
        PanGallery.SetItems(Presets.Pans.Select(t => new Gallery.Item { Key = t.Key, Label = t.Label, Glyph = t.Glyph }));
        TextFxGallery.SetItems(Presets.TextEffects.Select(t => new Gallery.Item { Key = t.Key, Label = t.Label, Glyph = TextFxGlyph(t.Key) }));
        TransGallery.Picked += k => SetTransition(k);
        FxGallery.Picked += k => Mutate(() => { foreach (var c in TargetClips()) c.Effect = k; });
        PanGallery.Picked += k => Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind != ClipKind.Title)) c.Pan = k; });
        TextFxGallery.Picked += k => EditStyle(s => s.Effect = k);
        TransGallery.Hovered += k => HoverPreview(k, (c, key) => { if (c.Transition == "none" || key != null) c.Transition = key; }, transition: true);
        FxGallery.Hovered += k => HoverPreview(k, (c, key) => c.Effect = key);
        PanGallery.Hovered += k => HoverPreview(k, (c, key) => c.Pan = key);
        UpdateGalleryImages();

        ThumbS.IsChecked = _settings.ThumbSize <= 48;
        ThumbM.IsChecked = _settings.ThumbSize > 48 && _settings.ThumbSize < 88;
        ThumbL.IsChecked = _settings.ThumbSize >= 88;
        _sync = false;
    }

    static string TextFxGlyph(string k) => k switch
    {
        "none" => "", "fade" => "", "flyleft" => "", "flyright" => "", "flyup" => "",
        "zoomin" => "", "pop" => "", "typewriter" => "", "scroll" => "", "slowzoom" => "", "swing" => "",
        _ => ""
    };

    // ===================================================================== FFmpeg

    void EnsureFfmpeg()
    {
        if (FF.Locate(_settings.FfmpegPath)) return;
        int r = Dialogs.Ask(this, "FFmpeg needed",
            "Gerbera Studio uses FFmpeg to read and write video, and it couldn't be found.\n\n" +
            "You can download it automatically (about 140 MB, from the FFmpeg-Builds project on GitHub), or point to an ffmpeg.exe you already have " +
            "(ffprobe.exe must be in the same folder).",
            "Download automatically", "Locate ffmpeg.exe…", "Not now");
        if (r == 0) DownloadFfmpeg();
        else if (r == 1) LocateFfmpeg();
    }

    void LocateFfmpeg()
    {
        var dlg = new OpenFileDialog { Filter = "ffmpeg.exe|ffmpeg.exe|Programs|*.exe", Title = "Locate ffmpeg.exe" };
        if (dlg.ShowDialog(this) != true) return;
        if (FF.Locate(dlg.FileName)) { _settings.FfmpegPath = dlg.FileName; _settings.Save(); ShowStatus("Using " + FF.Ffmpeg); Refresh(); }
        else Dialogs.Info(this, "FFmpeg", "ffprobe.exe needs to be in the same folder as ffmpeg.exe.");
    }

    async void DownloadFfmpeg()
    {
        var win = new ProgressWindow { Owner = this };
        win.SetMode("Downloading FFmpeg…", indeterminate: false);
        var cts = new CancellationTokenSource();
        win.CancelRequested += () => cts.Cancel();
        win.Show();
        IsEnabled = false;
        try
        {
            string dir = Path.Combine(FF.AppDataDir, "ffmpeg");
            string zip = Path.Combine(FF.AppDataDir, "ffmpeg.zip");
            using (var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(30) })
            {
                http.DefaultRequestHeaders.UserAgent.ParseAdd("MovieMakerReborn/1.0");
                using var resp = await http.GetAsync("https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-gpl.zip",
                    System.Net.Http.HttpCompletionOption.ResponseHeadersRead, cts.Token);
                resp.EnsureSuccessStatusCode();
                long total = resp.Content.Headers.ContentLength ?? 150_000_000;
                await using var src = await resp.Content.ReadAsStreamAsync(cts.Token);
                await using var dst = File.Create(zip);
                var buf = new byte[1 << 16]; long got = 0; int n;
                while ((n = await src.ReadAsync(buf, cts.Token)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    got += n;
                    win.SetProgress((double)got / total, $"{got / 1048576} MB of {total / 1048576} MB");
                }
            }
            win.SetMode("Unpacking…", indeterminate: true);
            await Task.Run(() =>
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, true);
                System.IO.Compression.ZipFile.ExtractToDirectory(zip, dir);
                File.Delete(zip);
            });
            var exe = Directory.GetFiles(dir, "ffmpeg.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (exe != null && FF.Locate(exe)) { _settings.FfmpegPath = exe; _settings.Save(); ShowStatus("FFmpeg is ready."); }
            else throw new Exception("The download didn't contain ffmpeg.exe.");
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Dialogs.Info(this, "Download failed", ex.Message); }
        finally { IsEnabled = true; win.Close(); Refresh(); }
    }

    // ===================================================================== project state

    void PushUndo()
    {
        _undo.Add(_p.ToJson());
        if (_undo.Count > 150) _undo.RemoveAt(0);
        _redo.Clear();
    }

    void Mutate(Action a)
    {
        PushUndo();
        a();
        _dirty = true;
        _liveActive = false;
        Refresh();
    }

    /// <summary>For sliders and typing: one undo point per gesture, live preview.</summary>
    void LiveEdit(Action a)
    {
        if (!_liveActive) { PushUndo(); _liveActive = true; }
        a();
        _dirty = true;
        Board.Update(_p);
        SyncPlayer();
        UpdateTitle();
    }

    void SliderCommit(object sender, RoutedEventArgs e) => _liveActive = false;

    void Undo()
    {
        if (_undo.Count == 0) return;
        _redo.Add(_p.ToJson());
        _p = Project.FromJson(_undo[^1]);
        _undo.RemoveAt(_undo.Count - 1);
        _dirty = true; _liveActive = false;
        PruneSelection(); Refresh();
    }

    void Redo()
    {
        if (_redo.Count == 0) return;
        _undo.Add(_p.ToJson());
        _p = Project.FromJson(_redo[^1]);
        _redo.RemoveAt(_redo.Count - 1);
        _dirty = true; _liveActive = false;
        PruneSelection(); Refresh();
    }

    void PruneSelection()
    {
        Board.SelectedClips.RemoveWhere(id => !_p.Clips.Any(c => c.Id == id));
        if (!_p.Music.Any(m => m.Id == Board.SelectedMusic)) Board.SelectedMusic = null;
        if (!_p.Captions.Any(c => c.Id == Board.SelectedCaption)) Board.SelectedCaption = null;
    }

    (int w, int h) PreviewSize()
    {
        int q = _settings.PreviewHeight;
        double a = _p.Aspect;
        return a >= 1 ? ((int)Math.Round(q * a / 2) * 2, q) : (q, (int)Math.Round(q / a / 2) * 2);
    }

    void SyncPlayer()
    {
        var (w, h) = PreviewSize();
        _player.SetProject(_p.Clone(), w, h);
    }

    void Refresh()
    {
        Board.Update(_p);
        UpdateContext();
        SyncRibbon();
        SyncPlayer();
        UpdateTitle();
        UpdateStatus();
        UpdateInfo();
        LayoutPreview();
        ScheduleGalleryImages();
        QatUndo.IsEnabled = _undo.Count > 0;
        QatRedo.IsEnabled = _redo.Count > 0;
    }

    void UpdateTitle()
    {
        TitleText.Text = $"{(_dirty ? "*" : "")}{_p.Name} - Gerbera Studio";
        Title = TitleText.Text;
        QatUndo.IsEnabled = _undo.Count > 0;
        QatRedo.IsEnabled = _redo.Count > 0;
    }

    double Total => _p.TotalDuration(_p.ComputeTimeline());

    void UpdateStatus()
    {
        var tl = _p.ComputeTimeline();
        int n = _p.Clips.Count;
        string s;
        if (n == 0) s = "Add videos and photos to get started.";
        else
        {
            int sel = Board.SelectedClips.Count;
            if (sel > 1) s = $"{sel} items selected";
            else
            {
                int i = tl.IndexAt(_playhead);
                s = $"Item {Math.Max(1, i + 1)} of {n}";
            }
            s += $"    ·    Movie length {StoryboardView.FormatTime(Total)}";
        }
        if (FF.Ffmpeg == null) s += "    ·    FFmpeg not found (File ▸ FFmpeg location)";
        StatusText.Text = s;
        var (w, h) = PreviewSize();
        var fps = Presets.FrameRates.FirstOrDefault(f => f.Num == _p.FpsNum && f.Den == _p.FpsDen).Label ?? $"{_p.Fps:0.###} fps";
        ResText.Text = $"{_p.AspectW}:{_p.AspectH}  ·  {fps.Split(' ')[0]} fps  ·  preview {w}×{h}";
    }

    void ShowStatus(string msg)
    {
        StatusText.Text = msg;
        _statusTimer.Stop(); _statusTimer.Start();
    }

    // ===================================================================== selection helpers

    List<Clip> SelectedClips() => _p.Clips.Where(c => Board.SelectedClips.Contains(c.Id)).ToList();

    Clip ClipAtPlayhead()
    {
        if (_p.Clips.Count == 0) return null;
        var tl = _p.ComputeTimeline();
        int i = tl.IndexAt(_playhead);
        return _p.Clips[Math.Clamp(i, 0, _p.Clips.Count - 1)];
    }

    /// <summary>Selected clips, or the clip under the playhead if nothing is selected.</summary>
    List<Clip> TargetClips()
    {
        var s = SelectedClips();
        if (s.Count > 0) return s;
        if (Board.SelectedMusic != null || Board.SelectedCaption != null) return s;
        var c = ClipAtPlayhead();
        return c == null ? new List<Clip>() : new List<Clip> { c };
    }

    MusicItem SelMusic => _p.Music.FirstOrDefault(m => m.Id == Board.SelectedMusic);
    Caption SelCaption => _p.Captions.FirstOrDefault(c => c.Id == Board.SelectedCaption);

    (string text, TextStyle style, Caption caption, Clip clip) TextTarget()
    {
        var cap = SelCaption;
        if (cap != null) return (cap.Text, cap.Style, cap, null);
        var c = SelectedClips().FirstOrDefault(x => x.Kind == ClipKind.Title);
        if (c != null) return (c.Text, c.Style, null, c);
        return (null, null, null, null);
    }

    void UpdateContext()
    {
        var clips = TargetClips();
        bool hasClip = clips.Count > 0;
        bool hasText = TextTarget().style != null;
        bool hasMusic = SelMusic != null;
        CtxVideo.Visibility = hasClip ? Visibility.Visible : Visibility.Collapsed;
        CtxText.Visibility = hasText ? Visibility.Visible : Visibility.Collapsed;
        CtxMusic.Visibility = hasMusic ? Visibility.Visible : Visibility.Collapsed;
        CtxVideoLabel.Text = clips.Count > 0 && clips.All(c => c.Kind == ClipKind.Title) ? "TITLE TOOLS" : clips.All(c => c.Kind == ClipKind.Image) && hasClip ? "PHOTO TOOLS" : "VIDEO TOOLS";
        if ((TabVideo.IsChecked == true && !hasClip) || (TabText.IsChecked == true && !hasText) || (TabMusic.IsChecked == true && !hasMusic))
            TabHome.IsChecked = true;
    }

    // ===================================================================== ribbon sync

    static int Nearest(double[] arr, double v) { int best = 0; for (int i = 1; i < arr.Length; i++) if (Math.Abs(arr[i] - v) < Math.Abs(arr[best] - v)) best = i; return best; }
    static string Sec(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);
    static bool TryParseSec(string s, out double v)
    {
        s = (s ?? "").Trim().Replace(',', '.');
        if (s.Contains(':'))
        {
            var parts = s.Split(':');
            double acc = 0;
            foreach (var p in parts)
            {
                if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var x)) { v = 0; return false; }
                acc = acc * 60 + x;
            }
            v = acc; return true;
        }
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out v);
    }

    void SyncRibbon()
    {
        _sync = true;
        try
        {
            var clips = TargetClips();
            var c = clips.FirstOrDefault();
            var tl = _p.ComputeTimeline();
            // Animations / effects
            TransGallery.Select(c?.Transition ?? "none");
            PanGallery.Select(c?.Pan ?? "none");
            FxGallery.Select(c?.Effect ?? "none");
            TransDurCombo.Text = Sec(c?.TransitionDur ?? 1.5);
            BrightSlider.Value = c?.Brightness ?? 0;
            ContrastSlider.Value = c?.Contrast ?? 0;
            SatSlider.Value = c?.Saturation ?? 0;
            // Project
            MixSlider.Value = _p.AudioMix;
            FpsCombo.SelectedIndex = Array.FindIndex(Presets.FrameRates, f => f.Num == _p.FpsNum && f.Den == _p.FpsDen);
            InterpCombo.SelectedIndex = Math.Max(0, Array.FindIndex(Presets.Interps, i => i.Key == _p.Interp));
            FitCombo.SelectedIndex = _p.Fit switch { "cover" => 1, "stretch" => 2, _ => 0 };
            ProjBgColor.Color = _p.Background;
            string asp = $"{_p.AspectW}:{_p.AspectH}";
            foreach (var t in new[] { Asp169, Asp43, Asp916, Asp11, Asp219 }) t.IsChecked = (string)t.Tag == asp;
            ThemeDarkBtn.IsChecked = App.CurrentTheme == "dark";
            ThemeEveningBtn.IsChecked = App.CurrentTheme == "evening";
            ThemeLightBtn.IsChecked = App.CurrentTheme == "light";
            ThemeToggle.Content = ThemeGlyph(App.CurrentTheme);
            // Video tools
            if (c != null)
            {
                var m = _p.GetMedia(c.MediaId);
                bool isVideo = c.Kind == ClipKind.Video;
                GrpVideoAudio.IsEnabled = isVideo && m != null && m.HasAudio;
                ClipVolSlider.Value = c.Volume;
                ClipFadeIn.SelectedIndex = Nearest(FadeValues, c.FadeIn);
                ClipFadeOut.SelectedIndex = Nearest(FadeValues, c.FadeOut);
                SpeedCombo.IsEnabled = isVideo;
                SpeedCombo.SelectedIndex = Nearest(Speeds, c.Speed);
                DurationCombo.IsEnabled = !isVideo;
                DurationCombo.Text = Sec(c.Length);
                TrimInBox.IsEnabled = TrimOutBox.IsEnabled = isVideo;
                TrimInBox.Text = isVideo ? Sec(c.TrimIn) : "";
                TrimOutBox.Text = isVideo ? Sec(c.TrimOut) : "";
                GrpTitleBg.Visibility = c.Kind == ClipKind.Title ? Visibility.Visible : Visibility.Collapsed;
                ClipAudioFx.SelectedIndex = Math.Max(0, Array.FindIndex(Presets.AudioFx, a => a.Key == c.AudioFx));
                ReverseToggle.IsEnabled = c.Kind != ClipKind.Title;
                ReverseToggle.IsChecked = c.Reverse;
                var key = c.Key ?? new ChromaKey();
                KeyToggle.IsChecked = key.Enabled;
                KeyToggle.IsEnabled = c.Kind != ClipKind.Title;
                KeyColorBtn.Color = key.Color; KeyBgColorBtn.Color = key.BgColor;
                KeyTolSlider.Value = key.Tolerance; KeySoftSlider.Value = key.Softness; KeySpillCheck.IsChecked = key.Spill;
                TitleBgColor.Color = c.BgColor;
            }
            // Music tools
            var mu = SelMusic;
            if (mu != null)
            {
                MusicVolSlider.Value = mu.Volume;
                MusicFadeIn.SelectedIndex = Nearest(FadeValues, mu.FadeIn);
                MusicFadeOut.SelectedIndex = Nearest(FadeValues, mu.FadeOut);
                MusicStartBox.Text = Sec(mu.Start);
                MusicInBox.Text = Sec(mu.TrimIn);
                MusicOutBox.Text = Sec(mu.TrimOut);
                MusicAudioFx.SelectedIndex = Math.Max(0, Array.FindIndex(Presets.AudioFx, a => a.Key == mu.AudioFx));
            }
            // Text tools
            var tt = TextTarget();
            if (tt.style != null)
            {
                if (!TextEditBox.IsKeyboardFocusWithin) TextEditBox.Text = tt.text;
                FontCombo.SelectedItem = FontCombo.Items.Contains(tt.style.Font) ? tt.style.Font : null;
                FontSizeCombo.Text = Math.Round(tt.style.Size * 1080).ToString(CultureInfo.InvariantCulture);
                BoldBtn.IsChecked = tt.style.Bold;
                ItalicBtn.IsChecked = tt.style.Italic;
                TextColorBtn.Color = tt.style.Color;
                OutlineColorBtn.Color = tt.style.OutlineColor;
                OutlineCombo.SelectedIndex = Nearest(Outlines.Select(o => o.Value).ToArray(), tt.style.Outline);
                ShadowCheck.IsChecked = tt.style.Shadow;
                AlignL.IsChecked = tt.style.Align == "Left"; AlignC.IsChecked = tt.style.Align == "Center"; AlignR.IsChecked = tt.style.Align == "Right";
                VTop.IsChecked = tt.style.VAlign == "Top"; VMid.IsChecked = tt.style.VAlign == "Middle"; VBot.IsChecked = tt.style.VAlign == "Bottom";
                TextOpacitySlider.Value = tt.style.Opacity;
                TextFxGallery.Select(tt.style.Effect);
                GlowCheck.IsChecked = tt.style.Glow;
                GlowColorBtn.Color = tt.style.GlowColor;
                PlateCombo.SelectedIndex = tt.style.Plate switch { "box" => 1, "bar" => 2, _ => 0 };
                BorderCheck.IsEnabled = tt.clip != null;
                BorderCheck.IsChecked = tt.clip?.TitleBorder == "classic";
                TextBgColor.IsEnabled = tt.clip != null;
                if (tt.clip != null) TextBgColor.Color = tt.clip.BgColor;
                if (tt.caption != null)
                {
                    TextStartBox.IsEnabled = true;
                    TextStartBox.Text = Sec(tt.caption.Start);
                    TextDurBox.Text = Sec(tt.caption.Duration);
                }
                else
                {
                    TextStartBox.IsEnabled = false;
                    int idx = _p.Clips.IndexOf(tt.clip);
                    TextStartBox.Text = idx >= 0 ? Sec(tl.Start[idx]) : "";
                    TextDurBox.Text = Sec(tt.clip.Duration);
                }
            }
        }
        finally { _sync = false; }
    }

    // ===================================================================== gallery thumbnails

    void ScheduleGalleryImages() { _galleryTimer.Stop(); _galleryTimer.Start(); }

    Frame ThumbFrame(Clip c)
    {
        const int W = Thumbs.TW, H = Thumbs.TH;
        var f = new Frame(W, H);
        if (c == null) return null;
        if (c.Kind == ClipKind.Title)
        {
            Compositor.Fill(f, Compositor.ParseColor(c.BgColor));
            return f;
        }
        var arr = Thumbs.Get(_p.GetMedia(c.MediaId), c);
        if (arr == null || arr.Length == 0) return null;
        var pick = arr[arr.Length / 2] ?? arr.FirstOrDefault(x => x != null);
        if (pick == null) return null;
        var px = Thumbs.Pixels(pick);
        Buffer.BlockCopy(px, 0, f.D, 0, Math.Min(px.Length, f.D.Length));
        return f;
    }

    static Frame Synthetic(bool second)
    {
        const int W = Thumbs.TW, H = Thumbs.TH;
        var f = new Frame(W, H);
        for (int y = 0; y < H; y++)
            for (int x = 0; x < W; x++)
            {
                int o = (y * W + x) * 4;
                double u = (double)x / W, v = (double)y / H;
                if (!second)
                {
                    // Sky + hills
                    bool hill = v > 0.62 + 0.08 * Math.Sin(u * 6.5);
                    f.D[o] = (byte)(hill ? 60 : 235 - v * 60); f.D[o + 1] = (byte)(hill ? 150 : 170 - v * 40); f.D[o + 2] = (byte)(hill ? 70 : 90 + v * 40);
                }
                else
                {
                    // Sunset + sun
                    double dx = u - 0.65, dy = v - 0.45;
                    bool sun = dx * dx * 3.2 + dy * dy < 0.03;
                    f.D[o] = (byte)(sun ? 120 : 80 + v * 40); f.D[o + 1] = (byte)(sun ? 230 : 120 + v * 40); f.D[o + 2] = (byte)(sun ? 255 : 240 - v * 50);
                }
                f.D[o + 3] = 255;
            }
        return f;
    }

    static BitmapSource ToBitmap(Frame f)
    {
        var b = BitmapSource.Create(f.W, f.H, 96, 96, PixelFormats.Bgra32, null, f.D, f.W * 4);
        b.Freeze();
        return b;
    }

    void UpdateGalleryImages()
    {
        var c = TargetClips().FirstOrDefault();
        Frame b = ThumbFrame(c) ?? Synthetic(true);
        Frame a = null;
        if (c != null)
        {
            int i = _p.Clips.IndexOf(c);
            if (i > 0) a = ThumbFrame(_p.Clips[i - 1]);
        }
        a ??= Synthetic(false);
        var tmp = new Frame(b.W, b.H);
        foreach (var t in Presets.Transitions)
        {
            if (t.Key == "none") { TransGallery.SetImage(t.Key, ToBitmap(a)); continue; }
            Compositor.Transition(tmp, a, b, t.Key, 0.45, 0xFF000000u);
            TransGallery.SetImage(t.Key, ToBitmap(tmp));
        }
        TransGallery.Refresh();
        foreach (var e in Presets.Effects)
        {
            var f = new Frame(b.W, b.H);
            Compositor.Copy(f, b);
            var sc = new Frame(b.W, b.H);
            // Scale blur radii up for the tiny thumbnail so the effect reads.
            Compositor.ApplyEffect(f, new RectI(0, 0, f.W, f.H), e.Key, 0, 0, 0, 7, sc);
            FxGallery.SetImage(e.Key, ToBitmap(f));
        }
        FxGallery.Refresh();
        SyncRibbon();
    }

    // Live hover preview of a gallery choice on the selected clip(s).
    void HoverPreview(string key, Action<Clip, string> apply, bool transition = false)
    {
        if (_player.IsPlaying) return;
        if (key == null) { SyncPlayer(); return; }
        var clips = TargetClips();
        if (clips.Count == 0) return;
        var tmp = _p.Clone();
        foreach (var c in clips)
        {
            var tc = tmp.Clips.FirstOrDefault(x => x.Id == c.Id);
            if (tc != null) apply(tc, key);
        }
        var (w, h) = PreviewSize();
        _player.SetProject(tmp, w, h);
        if (transition)
        {
            int i = _p.Clips.IndexOf(clips[0]);
            var tl = tmp.ComputeTimeline();
            if (i > 0 && tl.Trans[i] > 0) _player.Seek(tl.Start[i] + tl.Trans[i] * 0.5);
        }
    }

    // ===================================================================== frames from the player

    void OnFrame(Frame f, double t)
    {
        lock (_frameLock)
        {
            if (_front == null || _front.Length != f.D.Length) _front = new byte[f.D.Length];
            Buffer.BlockCopy(f.D, 0, _front, 0, f.D.Length);
            _frontW = f.W; _frontH = f.H; _frontT = t;
            if (_framePending) return;
            _framePending = true;
        }
        Dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(PresentFrame));
    }

    void PresentFrame()
    {
        double t;
        lock (_frameLock)
        {
            _framePending = false;
            if (_front == null) return;
            if (_wb == null || _wb.PixelWidth != _frontW || _wb.PixelHeight != _frontH)
            {
                _wb = new WriteableBitmap(_frontW, _frontH, 96, 96, PixelFormats.Bgra32, null);
                PreviewImage.Source = _wb;
                if (_fullscreen != null) _fullscreen.Img.Source = _wb;
            }
            _wb.WritePixels(new Int32Rect(0, 0, _frontW, _frontH), _front, _frontW * 4, 0);
            t = _frontT;
        }
        if (_player.IsPlaying)
        {
            _playhead = t;
            UpdatePlayheadUi(true);
        }
    }

    string FormatTc(double t)
    {
        switch (_settings.Timecode)
        {
            case "smpte":
                {
                    int fpsI = (int)Math.Round(_p.Fps);
                    long frame = (long)Math.Floor(t * _p.Fps + 1e-6);
                    long secs = frame / Math.Max(1, fpsI);
                    return $"{secs / 3600:00}:{secs / 60 % 60:00}:{secs % 60:00}:{frame % Math.Max(1, fpsI):00}";
                }
            case "frames": return ((long)Math.Floor(t * _p.Fps + 1e-6)).ToString();
            default: return StoryboardView.FormatTime(t);
        }
    }

    void UpdatePlayheadUi(bool scrollIntoView)
    {
        double total = Total;
        TimeText.Text = $"{FormatTc(_playhead)} / {FormatTc(total)}";
        _sync = true;
        SeekSlider.Maximum = Math.Max(0.01, total);
        SeekSlider.Value = Math.Min(_playhead, SeekSlider.Maximum);
        _sync = false;
        Board.Playhead = _playhead;
        Board.InvalidateVisual();
        if (scrollIntoView)
        {
            var r = Board.PlayheadRect();
            if (!r.IsEmpty)
            {
                var top = Board.TranslatePoint(new Point(0, r.Top), BoardScroll).Y;
                if (top < 0 || top + r.Height > BoardScroll.ViewportHeight) Board.BringIntoView(r);
            }
        }
    }

    void SeekTo(double t)
    {
        _playhead = Math.Clamp(t, 0, Math.Max(0, Total));
        _player.Seek(_playhead);
        UpdatePlayheadUi(false);
        if (!_player.IsPlaying) { UpdateContext(); UpdateStatus(); }
    }

    void SeekSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        SeekTo(e.NewValue);
    }

    // Preview box: matches the project's aspect ratio, sized by column width and a user-draggable height limit.
    void LayoutPreview()
    {
        if (PreviewColumn == null || PreviewColumn.ActualWidth <= 0) return;
        double availW = Math.Max(120, PreviewColumn.ActualWidth - 24 - 8);
        double maxH = Math.Max(100, Math.Min(_settings.PreviewMaxHeight, PreviewColumn.ActualHeight - 140));
        double a = _p.Aspect;
        double w = availW, h = w / a;
        if (h > maxH) { h = maxH; w = h * a; }
        PreviewHost.Width = Math.Floor(w);
        PreviewHost.Height = Math.Floor(h);
    }

    void PreviewColumnSized(object sender, SizeChangedEventArgs e) => LayoutPreview();

    void PreviewGripDrag(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        _settings.PreviewMaxHeight = Math.Max(100, PreviewHost.ActualHeight + e.VerticalChange);
        LayoutPreview();
    }

    void UpdateInfo()
    {
        var c = TargetClips().FirstOrDefault();
        string s;
        if (SelMusic is MusicItem mu)
        {
            var m = _p.GetMedia(mu.MediaId);
            s = $"Music: {m?.Name}\nStarts at {StoryboardView.FormatTime(mu.Start)}  ·  plays {StoryboardView.FormatTime(mu.Length)}";
        }
        else if (SelCaption is Caption cap) s = $"Caption: \"{cap.Text.Replace('\n', ' ')}\"\nFrom {StoryboardView.FormatTime(cap.Start)} for {StoryboardView.FormatTime(cap.Duration)}";
        else if (c == null) s = "Nothing selected. Add videos and photos from the Home tab, or drag them onto the storyboard.";
        else if (c.Kind == ClipKind.Title) s = $"Title  ·  {StoryboardView.FormatTime(c.Length)}";
        else
        {
            var m = _p.GetMedia(c.MediaId);
            s = m == null ? "Missing file" : $"{m.Name}\n{m.Width}×{m.Height}" + (c.Kind == ClipKind.Video ? $"  ·  {m.Fps:0.##} fps  ·  {m.VideoCodec}  ·  {(m.HasAudio ? "with sound" : "no sound")}" : "  ·  photo") +
                $"\nOn screen for {StoryboardView.FormatTime(c.Length)}" + (c.Kind == ClipKind.Video && Math.Abs(c.Speed - 1) > 1e-6 ? $" at {c.Speed:0.##}× speed" : "");
        }
        InfoText.Text = s;
    }

    void PreviewClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2) { ToggleFullscreen(); return; }
        _player.Toggle();
    }

    void StepFrame(int dir)
    {
        _player.Pause();
        double f = 1 / _p.Fps;
        double cur = Math.Round(_playhead * _p.Fps) / _p.Fps;
        SeekTo(cur + dir * f);
    }

    void JumpClip(int dir)
    {
        var tl = _p.ComputeTimeline();
        if (tl.Start.Length == 0) return;
        _player.Pause();
        var starts = tl.Start.Append(tl.Total).ToArray();
        double t = dir > 0 ? starts.Where(s => s > _playhead + 1e-3).DefaultIfEmpty(tl.Total).First()
                           : starts.Where(s => s < _playhead - 1e-3).DefaultIfEmpty(0).Last();
        SeekTo(t);
    }

    // ===================================================================== commands

    void Cmd(object sender, RoutedEventArgs e)
    {
        FileMenu.IsOpen = false;
        var tag = (sender as FrameworkElement)?.Tag as string;
        Run(tag);
    }

    void Run(string tag)
    {
        switch (tag)
        {
            case "newproj": NewProject(); break;
            case "open": OpenProject(null); break;
            case "save": SaveProject(false); break;
            case "saveas": SaveProject(true); break;
            case "savemovie": SaveMovie(null); break;
            case "quick4k": SaveMovie("4k"); break;
            case "quick1080": SaveMovie("1080"); break;
            case "quickvert": SaveMovie("vertical"); break;
            case "exit": Close(); break;
            case "about": ShowAbout(); break;
            case "whatsnew": new WhatsNewWindow { Owner = this }.ShowDialog(); break;
            case "ffmpeg": LocateFfmpeg(); break;
            case "exportdefaults":
                if (new ExportDialog(_p, _settings, null, settingsMode: true) { Owner = this }.ShowDialog() == true)
                    ShowStatus("Default export settings saved.");
                break;
            case "undo": Undo(); break;
            case "redo": Redo(); break;
            case "copy": CopyClips(false); break;
            case "cut": CopyClips(true); break;
            case "paste": PasteClips(); break;
            case "addmedia": AddMedia(); break;
            case "addmusic": AddMusic(false); break;
            case "addmusicat": AddMusic(true); break;
            case "snapshot": Snapshot(); break;
            case "title": AddTitle(false); break;
            case "credits": AddTitle(true); break;
            case "caption": AddCaption(); break;
            case "rotl": Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind != ClipKind.Title)) c.Rotation = (c.Rotation + 270) % 360; }); break;
            case "rotr": Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind != ClipKind.Title)) c.Rotation = (c.Rotation + 90) % 360; }); break;
            case "remove": RemoveSelected(); break;
            case "selectall":
                Board.SelectedClips.Clear(); foreach (var c in _p.Clips) Board.SelectedClips.Add(c.Id);
                Board.SelectedMusic = Board.SelectedCaption = null; Refresh(); break;
            case "split": SplitClip(); break;
            case "setstart": TrimAtPlayhead(true); break;
            case "setend": TrimAtPlayhead(false); break;
            case "trans_all":
                {
                    var src = TargetClips().FirstOrDefault(); if (src == null) break;
                    Mutate(() => { for (int i = 1; i < _p.Clips.Count; i++) { _p.Clips[i].Transition = src.Transition; _p.Clips[i].TransitionDur = src.TransitionDur; } });
                    ShowStatus($"Applied \"{Presets.Transitions.First(t => t.Key == src.Transition).Label}\" to every item.");
                    break;
                }
            case "pan_all":
                {
                    var src = TargetClips().FirstOrDefault(); if (src == null) break;
                    Mutate(() => { foreach (var c in _p.Clips.Where(c => c.Kind != ClipKind.Title)) c.Pan = src.Pan; });
                    break;
                }
            case "fx_all":
                {
                    var src = TargetClips().FirstOrDefault(); if (src == null) break;
                    Mutate(() => { foreach (var c in _p.Clips) { c.Effect = src.Effect; c.Brightness = src.Brightness; c.Contrast = src.Contrast; c.Saturation = src.Saturation; } });
                    break;
                }
            case "fx_reset": Mutate(() => { foreach (var c in TargetClips()) { c.Effect = "none"; c.Brightness = c.Contrast = c.Saturation = 0; } }); break;
            case "mix_video": Mutate(() => _p.AudioMix = -0.6); break;
            case "mix_music": Mutate(() => _p.AudioMix = 0.6); break;
            case "mix_even": Mutate(() => _p.AudioMix = 0); break;
            case "fit_music": FitToMusic(); break;
            case "zoomin": ZoomSlider.Value = Math.Min(1, ZoomSlider.Value + 0.08); break;
            case "zoomout": ZoomSlider.Value = Math.Max(0, ZoomSlider.Value - 0.08); break;
            case "zoomreset": ZoomSlider.Value = 0.36; break;
            case "fullscreen": ToggleFullscreen(); break;
            case "m_split": SplitMusic(); break;
            case "m_settime": { var m = SelMusic; if (m != null) Mutate(() => m.Start = _playhead); break; }
            case "m_setstart":
                {
                    var m = SelMusic; if (m == null) break;
                    double d = _playhead - m.Start;
                    if (d > 0.05 && d < m.Length - 0.05) Mutate(() => { m.TrimIn += d; m.Start = _playhead; });
                    else ShowStatus("Move the playhead inside the music first.");
                    break;
                }
            case "m_setend":
                {
                    var m = SelMusic; if (m == null) break;
                    double d = _playhead - m.Start;
                    if (d > 0.05 && d < m.Length - 0.01) Mutate(() => m.TrimOut = m.TrimIn + d);
                    else ShowStatus("Move the playhead inside the music first.");
                    break;
                }
            case "edittext": EditText(); break;
            case "key_green": EditKey(k => { k.Color = "#00FF00"; k.Enabled = true; }); break;
            case "key_blue": EditKey(k => { k.Color = "#0047BB"; k.Enabled = true; }); break;
            case "key_bg": PickKeyBackground(); break;
            case "play": _player.Toggle(); break;
            case "prevframe": StepFrame(-1); break;
            case "nextframe": StepFrame(1); break;
            case "themetoggle": ShowThemeMenu(); break;
        }
    }

    void SetTheme(string theme)
    {
        App.SetTheme(theme);
        _settings.Theme = theme;
        _settings.Save();
        Board.InvalidateVisual();
        SyncRibbon();
    }

    static string ThemeGlyph(string t) => t switch { "light" => "\uE706", "evening" => "\uE708", _ => "\uE734" };

    void ShowThemeMenu()
    {
        var cm = new ContextMenu { PlacementTarget = ThemeToggle, Placement = PlacementMode.Bottom, FontFamily = new FontFamily("Segoe UI"), FontSize = 12 };
        foreach (var (key, label) in new[] { ("light", "Morning"), ("evening", "Evening"), ("dark", "Midnight") })
        {
            bool cur = App.CurrentTheme == key;
            var icon = new TextBlock { Text = ThemeGlyph(key), FontFamily = Icons.Font, FontSize = 14 };
            icon.SetResourceReference(TextBlock.ForegroundProperty, cur ? "B.Accent" : "B.Glyph");
            var mi = new MenuItem { Header = label, Icon = icon, InputGestureText = cur ? "\u2713" : "", FontWeight = cur ? FontWeights.SemiBold : FontWeights.Normal };
            mi.Click += (_, _) => SetTheme(key);
            cm.Items.Add(mi);
        }
        cm.IsOpen = true;
    }

    void ThemeClick(object sender, RoutedEventArgs e) => SetTheme((string)((FrameworkElement)sender).Tag);

    // ===================================================================== media import

    static string Pattern(string[] exts) => string.Join(";", exts.Select(e => "*." + e));

    void AddMedia()
    {
        var all = FF.VideoExts.Concat(FF.PhotoExts).Distinct().ToArray();
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Title = "Add videos and photos",
            Filter = $"Videos and photos|{Pattern(all)}|Videos|{Pattern(FF.VideoExts)}|Photos|{Pattern(FF.PhotoExts)}|Audio and music|{Pattern(FF.AudioExts)}|All files|*.*",
        };
        if (Directory.Exists(_settings.LastMediaFolder)) dlg.InitialDirectory = _settings.LastMediaFolder;
        if (dlg.ShowDialog(this) != true) return;
        _settings.LastMediaFolder = Path.GetDirectoryName(dlg.FileNames[0]);
        ImportFiles(dlg.FileNames, InsertIndexAfterSelection());
    }

    int InsertIndexAfterSelection()
    {
        var sel = SelectedClips();
        if (sel.Count > 0) return _p.Clips.IndexOf(sel[^1]) + 1;
        return _p.Clips.Count;
    }

    bool CheckFfmpeg()
    {
        if (FF.Ffmpeg != null) return true;
        EnsureFfmpeg();
        return FF.Ffmpeg != null;
    }

    async void ImportFiles(string[] files, int insertIndex)
    {
        if (!CheckFfmpeg()) return;
        var paths = new List<string>();
        foreach (var f in files)
        {
            if (Directory.Exists(f)) paths.AddRange(Directory.GetFiles(f).OrderBy(x => x));
            else if (File.Exists(f)) paths.Add(f);
        }
        if (paths.Count == 0) return;
        var results = new List<MediaRef>();
        var errors = new List<string>();
        Cursor = Cursors.AppStarting;
        int done = 0;
        foreach (var path in paths)
        {
            ShowStatus($"Importing {++done} of {paths.Count}: {Path.GetFileName(path)}…");
            var existing = _p.Media.FirstOrDefault(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase));
            if (existing != null) { results.Add(existing); continue; }
            try { results.Add(await Task.Run(() => FF.Probe(path))); }
            catch (Exception ex) { errors.Add(ex.Message.Split('\n')[0]); }
        }
        Cursor = null;
        if (results.Count > 0)
        {
            var newIds = new List<string>();
            string selMusic = null;
            Mutate(() =>
            {
                int idx = Math.Clamp(insertIndex, 0, _p.Clips.Count);
                double musicEnd = _p.Music.Count == 0 ? 0 : _p.Music.Max(m => m.Start + m.Length);
                foreach (var m in results)
                {
                    if (!_p.Media.Contains(m)) _p.Media.Add(m);
                    if (m.Kind == MediaKind.Audio)
                    {
                        var mu = new MusicItem { MediaId = m.Id, Start = musicEnd, TrimIn = 0, TrimOut = m.Duration };
                        musicEnd += m.Duration;
                        _p.Music.Add(mu);
                        selMusic = mu.Id;
                        continue;
                    }
                    var c = new Clip { MediaId = m.Id };
                    if (m.Kind == MediaKind.Video) { c.Kind = ClipKind.Video; c.TrimIn = 0; c.TrimOut = m.Duration; }
                    else { c.Kind = ClipKind.Image; c.Duration = 7; }
                    _p.Clips.Insert(idx++, c);
                    newIds.Add(c.Id);
                }
                if (_p.Clips.Count > 0 && newIds.Count > 0 && _p.Clips.Count == newIds.Count) AutoAspect(results);
                Board.SelectedClips.Clear();
                foreach (var id in newIds) Board.SelectedClips.Add(id);
                if (newIds.Count == 0 && selMusic != null) { Board.SelectedMusic = selMusic; Board.SelectedCaption = null; }
            });
            if (newIds.Count > 0) SeekTo(_p.ComputeTimeline().Start[_p.Clips.FindIndex(c => c.Id == newIds[0])]);
            ShowStatus($"Added {results.Count} item{(results.Count == 1 ? "" : "s")}.");
        }
        if (errors.Count > 0) Dialogs.Info(this, "Some files couldn't be added", string.Join("\n", errors.Take(8)));
    }

    /// <summary>For a brand-new project, match the aspect ratio and frame rate of the first video.</summary>
    void AutoAspect(List<MediaRef> media)
    {
        var v = media.FirstOrDefault(m => m.Kind == MediaKind.Video) ?? media.FirstOrDefault(m => m.Kind == MediaKind.Image);
        if (v == null || v.Width <= 0 || v.Height <= 0) return;
        double a = (double)v.Width / v.Height;
        (int w, int h) best = (16, 9);
        foreach (var cand in new[] { (16, 9), (4, 3), (9, 16), (1, 1), (21, 9) })
            if (Math.Abs(Math.Log((double)cand.Item1 / cand.Item2 / a)) < Math.Abs(Math.Log((double)best.w / best.h / a))) best = cand;
        _p.AspectW = best.w; _p.AspectH = best.h;
        if (v.Kind == MediaKind.Video && v.Fps > 0)
        {
            var f = Presets.FrameRates.OrderBy(r => Math.Abs((double)r.Num / r.Den - v.Fps)).First();
            if (Math.Abs((double)f.Num / f.Den - v.Fps) < 0.05) { _p.FpsNum = f.Num; _p.FpsDen = f.Den; }
        }
    }

    async void AddMusic(bool atPlayhead)
    {
        if (!CheckFfmpeg()) return;
        var dlg = new OpenFileDialog
        {
            Multiselect = false,
            Title = "Add music",
            Filter = $"Audio and music|{Pattern(FF.AudioExts)}|Videos (use their sound)|{Pattern(FF.VideoExts)}|All files|*.*",
        };
        if (Directory.Exists(_settings.LastMediaFolder)) dlg.InitialDirectory = _settings.LastMediaFolder;
        if (dlg.ShowDialog(this) != true) return;
        MediaRef m;
        try { m = await Task.Run(() => FF.Probe(dlg.FileName)); }
        catch (Exception ex) { Dialogs.Info(this, "Couldn't add music", ex.Message); return; }
        if (!m.HasAudio) { Dialogs.Info(this, "No sound", "That file doesn't have any audio."); return; }
        if (m.Kind != MediaKind.Audio) { m.Kind = MediaKind.Audio; }
        Mutate(() =>
        {
            var existing = _p.Media.FirstOrDefault(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase) && x.Kind == MediaKind.Audio);
            if (existing != null) m = existing; else _p.Media.Add(m);
            double start = atPlayhead ? _playhead : 0;
            if (!atPlayhead && _p.Music.Count > 0) start = _p.Music.Max(x => x.Start + x.Length);
            var mu = new MusicItem { MediaId = m.Id, Start = start, TrimIn = 0, TrimOut = m.Duration };
            _p.Music.Add(mu);
            Board.SelectedClips.Clear(); Board.SelectedCaption = null; Board.SelectedMusic = mu.Id;
        });
        TabMusic.IsChecked = true;
    }

    // ===================================================================== titles & captions

    void AddTitle(bool credits)
    {
        var c = new Clip
        {
            Kind = ClipKind.Title,
            Duration = credits ? 12 : 7,
            BgColor = "#000000",
            Text = credits ? "CREDITS\n\nDirected by\nYour Name\n\nStarring\nYour Cast\n\nMusic\nArtist — Song\n\nFilmed on location in\nYour City" : "My Movie",
            Style = new TextStyle
            {
                Font = credits ? "Segoe UI" : "Segoe UI Light",
                Size = credits ? 0.055 : 0.11,
                VAlign = "Middle",
                Outline = 0,
                Shadow = false,
                Effect = credits ? "scroll" : "fade",
            },
        };
        Mutate(() =>
        {
            int idx;
            if (credits) idx = _p.Clips.Count;
            else
            {
                var sel = SelectedClips();
                idx = sel.Count > 0 ? _p.Clips.IndexOf(sel[0]) : (_p.Clips.Count == 0 ? 0 : Math.Max(0, _p.ComputeTimeline().IndexAt(_playhead)));
            }
            _p.Clips.Insert(idx, c);
            Board.SelectedClips.Clear(); Board.SelectedClips.Add(c.Id);
            Board.SelectedMusic = Board.SelectedCaption = null;
        });
        int i = _p.Clips.IndexOf(c);
        SeekTo(_p.ComputeTimeline().Start[i] + 1.0);
        EditText();
    }

    void AddCaption()
    {
        if (_p.Clips.Count == 0) { Dialogs.Info(this, "Caption", "Add a video or photo first, then add a caption on top of it."); return; }
        var cap = new Caption { Start = Math.Min(_playhead, Math.Max(0, Total - 0.5)), Duration = 5, Text = "Enter text here" };
        cap.Duration = Math.Max(0.5, Math.Min(5, Total - cap.Start));
        Mutate(() =>
        {
            _p.Captions.Add(cap);
            Board.SelectedClips.Clear(); Board.SelectedMusic = null; Board.SelectedCaption = cap.Id;
        });
        SeekTo(cap.Start + 0.8);
        EditText();
    }

    void EditText()
    {
        UpdateContext();
        if (CtxText.Visibility != Visibility.Visible) return;
        TabText.IsChecked = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () => { TextEditBox.Focus(); TextEditBox.SelectAll(); });
    }

    void TextEditChanged(object sender, TextChangedEventArgs e)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.style == null) return;
        string text = TextEditBox.Text.Replace("\r\n", "\n");
        LiveEdit(() => { if (tt.caption != null) tt.caption.Text = text; else tt.clip.Text = text; });
    }

    void TextEditCommit(object sender, RoutedEventArgs e) => _liveActive = false;

    void EditStyle(Action<TextStyle> a)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.style == null) return;
        Mutate(() => a(tt.style));
    }

    void FontChanged(object sender, SelectionChangedEventArgs e) { if (FontCombo.SelectedItem is string f) EditStyle(s => s.Font = f); }
    void FontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || FontSizeCombo.SelectedItem is not string v) return;
        if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var px)) EditStyle(s => s.Size = px / 1080.0);
    }
    void FontSizeCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        if (double.TryParse(FontSizeCombo.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var px) && px >= 4 && px <= 1000)
        {
            var tt = TextTarget();
            if (tt.style != null && Math.Abs(tt.style.Size * 1080 - px) > 0.4) EditStyle(s => s.Size = px / 1080.0);
        }
    }
    void BoldClick(object sender, RoutedEventArgs e) => EditStyle(s => s.Bold = BoldBtn.IsChecked == true);
    void ItalicClick(object sender, RoutedEventArgs e) => EditStyle(s => s.Italic = ItalicBtn.IsChecked == true);
    void ShadowClick(object sender, RoutedEventArgs e) => EditStyle(s => s.Shadow = ShadowCheck.IsChecked == true);
    void OutlineChanged(object sender, SelectionChangedEventArgs e) { if (OutlineCombo.SelectedIndex >= 0) EditStyle(s => s.Outline = Outlines[OutlineCombo.SelectedIndex].Value); }
    void AlignClick(object sender, RoutedEventArgs e) => EditStyle(s => s.Align = (string)((FrameworkElement)sender).Tag);
    void VAlignClick(object sender, RoutedEventArgs e) => EditStyle(s => s.VAlign = (string)((FrameworkElement)sender).Tag);
    void TextOpacityChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.style != null) LiveEdit(() => tt.style.Opacity = e.NewValue);
    }

    void TextTimingCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.style == null) return;
        if (tt.caption != null)
        {
            if (TryParseSec(TextStartBox.Text, out var st) && TryParseSec(TextDurBox.Text, out var du) && du > 0.1 &&
                (Math.Abs(st - tt.caption.Start) > 0.005 || Math.Abs(du - tt.caption.Duration) > 0.005))
                Mutate(() => { tt.caption.Start = Math.Max(0, st); tt.caption.Duration = du; });
        }
        else if (TryParseSec(TextDurBox.Text, out var du) && du > 0.1 && Math.Abs(du - tt.clip.Duration) > 0.005)
            Mutate(() => tt.clip.Duration = du);
    }

    // ===================================================================== editing

    void RemoveSelected()
    {
        if (SelMusic is MusicItem mu) { Mutate(() => { _p.Music.Remove(mu); Board.SelectedMusic = null; }); return; }
        if (SelCaption is Caption cap) { Mutate(() => { _p.Captions.Remove(cap); Board.SelectedCaption = null; }); return; }
        var sel = SelectedClips();
        if (sel.Count == 0) return;
        int first = _p.Clips.IndexOf(sel[0]);
        Mutate(() =>
        {
            foreach (var c in sel) _p.Clips.Remove(c);
            Board.SelectedClips.Clear();
            if (_p.Clips.Count > 0) Board.SelectedClips.Add(_p.Clips[Math.Min(first, _p.Clips.Count - 1)].Id);
            // Drop media no longer used anywhere.
            _p.Media.RemoveAll(m => !_p.Clips.Any(c => c.MediaId == m.Id) && !_p.Music.Any(x => x.MediaId == m.Id));
        });
        SeekTo(Math.Min(_playhead, Total));
    }

    void MoveClips(List<string> ids, int insertIndex)
    {
        var moving = _p.Clips.Where(c => ids.Contains(c.Id)).ToList();
        if (moving.Count == 0) return;
        int before = _p.Clips.Take(insertIndex).Count(c => ids.Contains(c.Id));
        int target = insertIndex - before;
        var rest = _p.Clips.Where(c => !ids.Contains(c.Id)).ToList();
        target = Math.Clamp(target, 0, rest.Count);
        var order = rest.Take(target).Concat(moving).Concat(rest.Skip(target)).Select(c => c.Id).ToList();
        if (order.SequenceEqual(_p.Clips.Select(c => c.Id))) return;
        Mutate(() =>
        {
            var newList = order.Select(id => _p.Clips.First(c => c.Id == id)).ToList();
            _p.Clips.Clear(); _p.Clips.AddRange(newList);
        });
    }

    void SplitClip()
    {
        var tl = _p.ComputeTimeline();
        int i = tl.IndexAt(_playhead);
        if (i < 0) return;
        var c = _p.Clips[i];
        double local = _playhead - tl.Start[i];
        if (local < 0.05 || local > tl.Dur[i] - 0.05) { ShowStatus("Move the playhead inside a clip to split it."); return; }
        Mutate(() =>
        {
            var b = Project.FromJson(_p.ToJson()).Clips[i];
            b.Id = MediaRef.NewId();
            b.Transition = "none";
            if (c.Kind == ClipKind.Video)
            {
                double src = c.TrimIn + local * c.Speed;
                b.TrimIn = src; c.TrimOut = src;
                b.FadeIn = 0; c.FadeOut = 0;
            }
            else { b.Duration = c.Duration - local; c.Duration = local; }
            _p.Clips.Insert(i + 1, b);
            Board.SelectedClips.Clear(); Board.SelectedClips.Add(b.Id);
        });
    }

    void TrimAtPlayhead(bool start)
    {
        var tl = _p.ComputeTimeline();
        int i = tl.IndexAt(_playhead);
        if (i < 0) return;
        var c = _p.Clips[i];
        double local = _playhead - tl.Start[i];
        if (local < 0.04 || local > tl.Dur[i] - 0.04) { ShowStatus("Move the playhead inside a clip first."); return; }
        if (c.Kind == ClipKind.Video)
        {
            double src = c.TrimIn + local * c.Speed;
            Mutate(() => { if (start) c.TrimIn = src; else c.TrimOut = src; });
            if (start) SeekTo(_p.ComputeTimeline().Start[i]);
        }
        else
        {
            Mutate(() => c.Duration = start ? c.Duration - local : local);
            if (start) SeekTo(_p.ComputeTimeline().Start[i]);
        }
    }

    void SplitMusic()
    {
        var m = SelMusic ?? _p.Music.FirstOrDefault(x => _playhead > x.Start && _playhead < x.Start + x.Length);
        if (m == null) return;
        double local = _playhead - m.Start;
        if (local < 0.05 || local > m.Length - 0.05) { ShowStatus("Move the playhead inside the music to split it."); return; }
        Mutate(() =>
        {
            var b = new MusicItem { MediaId = m.MediaId, Start = _playhead, TrimIn = m.TrimIn + local, TrimOut = m.TrimOut, Volume = m.Volume, FadeOut = m.FadeOut };
            m.TrimOut = m.TrimIn + local; m.FadeOut = 0;
            _p.Music.Add(b);
            Board.SelectedMusic = b.Id;
        });
    }

    void FitToMusic()
    {
        if (_p.Music.Count == 0) { Dialogs.Info(this, "Fit to music", "Add some music first."); return; }
        double target = _p.Music.Max(m => m.Start + m.Length);
        var adjustable = _p.Clips.Where(c => c.Kind != ClipKind.Video).ToList();
        if (adjustable.Count == 0) { Dialogs.Info(this, "Fit to music", "Fit to music changes how long photos and titles are shown, and there aren't any in this project."); return; }
        Mutate(() =>
        {
            for (int iter = 0; iter < 6; iter++)
            {
                double total = _p.ComputeTimeline().Total;
                double sum = adjustable.Sum(c => c.Duration);
                double want = sum + (target - total);
                if (want < adjustable.Count * 0.5) want = adjustable.Count * 0.5;
                double k = want / sum;
                foreach (var c in adjustable) c.Duration = Math.Max(0.5, c.Duration * k);
                if (Math.Abs(_p.ComputeTimeline().Total - target) < 0.02) break;
            }
        });
        ShowStatus($"Photos and titles adjusted. Movie length is now {StoryboardView.FormatTime(Total)}.");
    }

    void SetTransition(string key)
    {
        var clips = TargetClips();
        if (clips.Count == 0) return;
        Mutate(() => { foreach (var c in clips) { c.Transition = key; } });
        int i = _p.Clips.IndexOf(clips[0]);
        if (i == 0 && key != "none") ShowStatus("Transitions play between items, so the first item doesn't show one.");
        else if (i > 0) { var tl = _p.ComputeTimeline(); SeekTo(Math.Max(0, tl.Start[i] - 0.5)); _player.Play(); }
    }

    // ----- clipboard
    sealed class ClipboardData { public List<Clip> Clips { get; set; } public List<MediaRef> Media { get; set; } }

    void CopyClips(bool cut)
    {
        var sel = SelectedClips();
        if (sel.Count == 0) return;
        var media = _p.Media.Where(m => sel.Any(c => c.MediaId == m.Id)).ToList();
        _clipboard = System.Text.Json.JsonSerializer.Serialize(new ClipboardData { Clips = sel, Media = media });
        if (cut) RemoveSelected();
        ShowStatus($"{(cut ? "Cut" : "Copied")} {sel.Count} item{(sel.Count == 1 ? "" : "s")}.");
    }

    void PasteClips()
    {
        if (_clipboard == null) return;
        var data = System.Text.Json.JsonSerializer.Deserialize<ClipboardData>(_clipboard);
        if (data?.Clips == null) return;
        Mutate(() =>
        {
            foreach (var m in data.Media) if (_p.GetMedia(m.Id) == null) _p.Media.Add(m);
            int idx = InsertIndexAfterSelection();
            Board.SelectedClips.Clear();
            foreach (var c in data.Clips)
            {
                c.Id = MediaRef.NewId();
                _p.Clips.Insert(idx++, c);
                Board.SelectedClips.Add(c.Id);
            }
        });
    }

    // ===================================================================== ribbon field handlers

    void TabChecked(object sender, RoutedEventArgs e)
    {
        if (PanelHome == null) return;
        var map = new (RadioButton tab, FrameworkElement panel)[]
        {
            (TabHome, PanelHome), (TabAnim, PanelAnim), (TabFx, PanelFx), (TabProject, PanelProject), (TabView, PanelView),
            (TabVideo, PanelVideo), (TabMusic, PanelMusic), (TabText, PanelText)
        };
        foreach (var (tab, panel) in map) panel.Visibility = tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void FileTabClick(object sender, RoutedEventArgs e)
    {
        RecentList.Children.Clear();
        foreach (var r in _settings.Recent.Where(File.Exists).Take(8))
        {
            var b = new Button { Style = (Style)FindResource("FileMenuButton"), Tag = r };
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = Path.GetFileNameWithoutExtension(r), FontSize = 13 });
            var sub = new TextBlock { Text = Path.GetDirectoryName(r), FontSize = 11, TextTrimming = TextTrimming.CharacterEllipsis };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
            sp.Children.Add(sub);
            b.Content = sp;
            b.Click += (_, _) => { FileMenu.IsOpen = false; OpenProject(r); };
            RecentList.Children.Add(b);
        }
        if (RecentList.Children.Count == 0)
        {
            var t = new TextBlock { Text = "Projects you save will show up here.", Margin = new Thickness(0, 4, 0, 0) };
            t.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
            RecentList.Children.Add(t);
        }
        FileMenu.IsOpen = true;
    }

    void ComboEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        if (sender == TransDurCombo) TransDurCommit(sender, e);
        else if (sender == DurationCombo) DurationCommit(sender, e);
        else if (sender == FontSizeCombo) FontSizeCommit(sender, e);
        Board.Focus();
    }

    void BoxEnter(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        Board.Focus(); // triggers LostFocus commit
    }

    void TransDurChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || TransDurCombo.SelectedItem is not string s) return;
        if (TryParseSec(s, out var d)) Mutate(() => { foreach (var c in TargetClips()) c.TransitionDur = d; });
    }

    void TransDurCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var c = TargetClips().FirstOrDefault();
        if (c != null && TryParseSec(TransDurCombo.Text, out var d) && d > 0 && Math.Abs(d - c.TransitionDur) > 0.001)
            Mutate(() => { foreach (var x in TargetClips()) x.TransitionDur = d; });
    }

    void AdjustChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        var clips = TargetClips();
        if (clips.Count == 0) return;
        double v = Math.Abs(e.NewValue) < 0.03 ? 0 : e.NewValue;
        LiveEdit(() =>
        {
            foreach (var c in clips)
            {
                if (sender == BrightSlider) c.Brightness = v;
                else if (sender == ContrastSlider) c.Contrast = v;
                else c.Saturation = v;
            }
        });
    }

    void MixChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        double v = Math.Abs(e.NewValue) < 0.04 ? 0 : e.NewValue;
        LiveEdit(() => _p.AudioMix = v);
    }

    void AspectClick(object sender, RoutedEventArgs e)
    {
        var parts = ((string)((FrameworkElement)sender).Tag).Split(':');
        Mutate(() => { _p.AspectW = int.Parse(parts[0]); _p.AspectH = int.Parse(parts[1]); });
    }

    void FpsChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || FpsCombo.SelectedIndex < 0) return;
        var f = Presets.FrameRates[FpsCombo.SelectedIndex];
        Mutate(() => { _p.FpsNum = f.Num; _p.FpsDen = f.Den; });
    }

    void InterpChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || InterpCombo.SelectedIndex < 0) return;
        Mutate(() => _p.Interp = Presets.Interps[InterpCombo.SelectedIndex].Key);
    }

    void FitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || FitCombo.SelectedIndex < 0) return;
        Mutate(() => _p.Fit = FitCombo.SelectedIndex switch { 1 => "cover", 2 => "stretch", _ => "contain" });
    }

    void ApplyZoom()
    {
        Board.PxPerSec = 4 * Math.Pow(2, ZoomSlider.Value * 5);
        Board.Update(_p);
    }

    void ZoomSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        _settings.Zoom = e.NewValue;
        ApplyZoom();
    }

    void ThumbSizeClick(object sender, RoutedEventArgs e)
    {
        double s = double.Parse((string)((FrameworkElement)sender).Tag, CultureInfo.InvariantCulture);
        _settings.ThumbSize = s;
        ThumbS.IsChecked = s <= 48; ThumbM.IsChecked = s > 48 && s < 88; ThumbL.IsChecked = s >= 88;
        Board.TileH = s;
        Board.Update(_p);
    }

    void PreviewQualityChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || PreviewQualityCombo.SelectedIndex < 0) return;
        _settings.PreviewHeight = PreviewQualities[PreviewQualityCombo.SelectedIndex].H;
        SyncPlayer();
        UpdateStatus();
    }

    void TimecodeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync) return;
        _settings.Timecode = TimecodeCombo.SelectedIndex switch { 1 => "smpte", 2 => "frames", _ => "seconds" };
        UpdatePlayheadUi(false);
    }

    void ClipVolChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        var clips = TargetClips().Where(c => c.Kind == ClipKind.Video).ToList();
        if (clips.Count > 0) LiveEdit(() => { foreach (var c in clips) c.Volume = e.NewValue; });
    }

    void ClipFadeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync) return;
        var cb = (ComboBox)sender;
        if (cb.SelectedIndex < 0) return;
        double v = FadeValues[cb.SelectedIndex];
        Mutate(() => { foreach (var c in TargetClips()) { if (cb == ClipFadeIn) c.FadeIn = v; else c.FadeOut = v; } });
    }

    void SpeedChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || SpeedCombo.SelectedIndex < 0) return;
        double s = Speeds[SpeedCombo.SelectedIndex];
        Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind == ClipKind.Video)) c.Speed = s; });
    }

    void DurationChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || DurationCombo.SelectedItem is not string s) return;
        if (TryParseSec(s, out var d)) Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind != ClipKind.Video)) c.Duration = d; });
    }

    void DurationCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var c = TargetClips().FirstOrDefault(x => x.Kind != ClipKind.Video);
        if (c != null && TryParseSec(DurationCombo.Text, out var d) && d >= 0.1 && Math.Abs(d - c.Duration) > 0.001)
            Mutate(() => { foreach (var x in TargetClips().Where(x => x.Kind != ClipKind.Video)) x.Duration = d; });
    }

    void TrimBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var c = TargetClips().FirstOrDefault(x => x.Kind == ClipKind.Video);
        if (c == null) return;
        var m = _p.GetMedia(c.MediaId);
        if (!TryParseSec(TrimInBox.Text, out var a) || !TryParseSec(TrimOutBox.Text, out var b)) { SyncRibbon(); return; }
        a = Math.Clamp(a, 0, m?.Duration ?? a); b = Math.Clamp(b, 0, m?.Duration ?? b);
        if (b - a < 0.05) { SyncRibbon(); return; }
        if (Math.Abs(a - c.TrimIn) > 0.001 || Math.Abs(b - c.TrimOut) > 0.001) Mutate(() => { c.TrimIn = a; c.TrimOut = b; });
    }

    void MusicVolChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync) return;
        var m = SelMusic;
        if (m != null) LiveEdit(() => m.Volume = e.NewValue);
    }

    void MusicFadeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync) return;
        var cb = (ComboBox)sender;
        var m = SelMusic;
        if (m == null || cb.SelectedIndex < 0) return;
        double v = FadeValues[cb.SelectedIndex];
        Mutate(() => { if (cb == MusicFadeIn) m.FadeIn = v; else m.FadeOut = v; });
    }

    void MusicBoxCommit(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var m = SelMusic;
        if (m == null) return;
        var media = _p.GetMedia(m.MediaId);
        if (!TryParseSec(MusicStartBox.Text, out var st) || !TryParseSec(MusicInBox.Text, out var a) || !TryParseSec(MusicOutBox.Text, out var b)) { SyncRibbon(); return; }
        a = Math.Clamp(a, 0, media?.Duration ?? a); b = Math.Clamp(b, 0, media?.Duration ?? b);
        if (b - a < 0.05) { SyncRibbon(); return; }
        if (Math.Abs(st - m.Start) > 0.001 || Math.Abs(a - m.TrimIn) > 0.001 || Math.Abs(b - m.TrimOut) > 0.001)
            Mutate(() => { m.Start = Math.Max(0, st); m.TrimIn = a; m.TrimOut = b; });
    }

    // ===================================================================== green screen, audio fx, reverse, title styles

    void EditKey(Action<ChromaKey> a, bool live = false)
    {
        if (_sync || Board == null || !IsLoaded) return;
        var clips = TargetClips().Where(c => c.Kind != ClipKind.Title).ToList();
        if (clips.Count == 0) { ShowStatus("Select a video or photo first."); return; }
        void Apply() { foreach (var c in clips) { c.Key ??= new ChromaKey(); a(c.Key); } }
        if (live) LiveEdit(Apply); else Mutate(Apply);
    }

    void KeyToggleClick(object sender, RoutedEventArgs e) => EditKey(k => k.Enabled = KeyToggle.IsChecked == true);
    void KeySpillClick(object sender, RoutedEventArgs e) => EditKey(k => k.Spill = KeySpillCheck.IsChecked == true);
    void KeySliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_sync || !IsLoaded) return;
        double v = e.NewValue;
        EditKey(k => { if (sender == KeyTolSlider) k.Tolerance = v; else k.Softness = v; k.Enabled = true; }, live: true);
    }

    async void PickKeyBackground()
    {
        if (!CheckFfmpeg()) return;
        var all = FF.VideoExts.Concat(FF.PhotoExts).Distinct().ToArray();
        var dlg = new OpenFileDialog { Title = "Choose a background", Filter = $"Videos and photos|{Pattern(all)}|All files|*.*" };
        if (Directory.Exists(_settings.LastMediaFolder)) dlg.InitialDirectory = _settings.LastMediaFolder;
        if (dlg.ShowDialog(this) != true) return;
        MediaRef m;
        try { m = await Task.Run(() => FF.Probe(dlg.FileName)); }
        catch (Exception ex) { Dialogs.Info(this, "Background", ex.Message); return; }
        if (m.Kind == MediaKind.Audio) { Dialogs.Info(this, "Background", "Choose a video or photo for the background."); return; }
        var existing = _p.Media.FirstOrDefault(x => string.Equals(x.Path, m.Path, StringComparison.OrdinalIgnoreCase));
        EditKey(k => { if (existing == null && !_p.Media.Contains(m)) _p.Media.Add(m); k.BgMediaId = (existing ?? m).Id; k.Enabled = true; });
    }

    void ClipAudioFxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || ClipAudioFx.SelectedIndex < 0) return;
        var key = Presets.AudioFx[ClipAudioFx.SelectedIndex].Key;
        Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind == ClipKind.Video)) c.AudioFx = key; });
        if (key == "match")
        {
            var c = TargetClips().FirstOrDefault();
            var m = c == null ? "none" : Presets.MatchingAudio(c.Effect);
            ShowStatus(m == "none" ? "This clip's visual effect has no matching sound. Try Film noir, Silent film, Sepia western or VHS." : $"Sound matched to the visual effect: {Presets.AudioFx.First(a => a.Key == m).Label}.");
        }
    }

    void MusicAudioFxChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_sync || MusicAudioFx.SelectedIndex < 0) return;
        var key = Presets.AudioFx[MusicAudioFx.SelectedIndex].Key;
        if (key == "match") key = "none";
        var m = SelMusic;
        if (m != null) Mutate(() => m.AudioFx = key);
    }

    void ReverseClick(object sender, RoutedEventArgs e)
    {
        bool on = ReverseToggle.IsChecked == true;
        Mutate(() => { foreach (var c in TargetClips().Where(c => c.Kind != ClipKind.Title)) c.Reverse = on; });
    }

    void GlowClick(object sender, RoutedEventArgs e) => EditStyle(s => s.Glow = GlowCheck.IsChecked == true);
    void PlateChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PlateCombo.SelectedIndex < 0) return;
        EditStyle(s => s.Plate = PlateCombo.SelectedIndex switch { 1 => "box", 2 => "bar", _ => "none" });
    }
    void BorderClick(object sender, RoutedEventArgs e)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.clip != null) Mutate(() => tt.clip.TitleBorder = BorderCheck.IsChecked == true ? "classic" : "none");
    }

    void ApplyTitleStyle(string key)
    {
        if (_sync) return;
        var tt = TextTarget();
        if (tt.style == null) return;
        bool title = tt.clip != null;
        Mutate(() =>
        {
            var s = tt.style;
            double big = title ? 0.1 : 0.06;
            s.Glow = false; s.Plate = "none"; s.Bold = false; s.Italic = false; s.Shadow = !title; s.Outline = title ? 0 : 0.06;
            s.Align = "Center"; s.VAlign = title ? "Middle" : "Bottom"; s.Opacity = 1; s.Color = "#FFFFFF"; s.OutlineColor = "#000000";
            string bg = "#000000", border = "none";
            switch (key)
            {
                case "classic": s.Font = "Segoe UI"; s.Size = big; s.Effect = "fade"; s.Outline = title ? 0 : 0.06; break;
                case "clean": s.Font = "Segoe UI Light"; s.Size = big; s.Effect = "fade"; s.Shadow = false; s.Outline = 0; break;
                case "kinetic": s.Font = "Segoe UI Black"; s.Size = big * 1.1; s.Effect = "pop"; s.Color = "#F2B134"; bg = "#0D0D0D"; break;
                case "lowerthird": s.Font = "Segoe UI Semibold"; s.Size = 0.045; s.Align = "Left"; s.VAlign = "Bottom"; s.Plate = "bar"; s.PlateColor = "#000000"; s.PlateOpacity = 0.6; s.Effect = "wipe"; s.Outline = 0; s.Shadow = false; break;
                case "neon": s.Font = "Segoe UI Light"; s.Size = big; s.Color = "#E8FFFF"; s.Glow = true; s.GlowColor = "#00E5FF"; s.Effect = "flicker"; s.Outline = 0; s.Shadow = false; bg = "#05050A"; break;
                case "glitch": s.Font = "Consolas"; s.Bold = true; s.Size = big * 0.9; s.Effect = "glitch"; s.Outline = 0; bg = "#000000"; break;
                case "silent": s.Font = "Palatino Linotype"; s.Italic = true; s.Size = big * 0.8; s.Color = "#F0EDE6"; s.Effect = "flicker"; s.Outline = 0; s.Shadow = false; bg = "#0A0A0A"; border = "classic"; break;
                case "noir": s.Font = "Georgia"; s.Bold = true; s.Size = big; s.Color = "#E6E6E6"; s.Effect = "slowzoom"; s.Shadow = true; s.Outline = 0; bg = "#000000"; break;
                case "western": s.Font = "Rockwell Extra Bold"; s.Size = big; s.Color = "#F1DDB0"; s.OutlineColor = "#3A2410"; s.Outline = 0.05; s.Effect = "stamp"; bg = "#3B2614"; border = "classic"; break;
                case "typewriter": s.Font = "Courier New"; s.Size = big * 0.75; s.Color = "#EDEDED"; s.Effect = "typewriter"; s.Outline = title ? 0 : 0.05; bg = "#101010"; break;
            }
            if (title) { tt.clip.BgColor = bg; tt.clip.TitleBorder = border; }
        });
        ShowStatus($"Applied the {Presets.TitleStyles.First(t => t.Key == key).Label} style.");
    }

    void RibbonWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.OriginalSource is DependencyObject d && FindParent<ScrollViewer>(d) is ScrollViewer inner && inner != sender) return;
        var sv = (ScrollViewer)sender;
        sv.ScrollToHorizontalOffset(sv.HorizontalOffset - e.Delta * 0.5);
        e.Handled = true;
    }

    static T FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null && d is not T) d = d is Visual || d is System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    // ===================================================================== context menu

    ContextMenu BuildContextMenu()
    {
        var cm = new ContextMenu();
        MenuItem Item(string header, string glyph, string tag, string gesture = null)
        {
            var mi = new MenuItem { Header = header, InputGestureText = gesture ?? "" };
            mi.Icon = new TextBlock { Text = glyph, FontFamily = Icons.Font, FontSize = 13 };
            mi.Click += (_, _) => Run(tag);
            return mi;
        }
        cm.Items.Add(Item("Cut", "", "cut", "Ctrl+X"));
        cm.Items.Add(Item("Copy", "", "copy", "Ctrl+C"));
        cm.Items.Add(Item("Paste", "", "paste", "Ctrl+V"));
        cm.Items.Add(Item("Remove", "", "remove", "Del"));
        cm.Items.Add(new Separator());
        {
            var split = Item("Split", "", "split", "M");
            var ic = new System.Windows.Shapes.Path { Data = Geometry.Parse("M1,3.5 L6,3.5 L6,12.5 L1,12.5 Z M10,3.5 L15,3.5 L15,12.5 L10,12.5 Z M8,1 L8,15"), StrokeThickness = 1.3, Stretch = Stretch.Uniform, Width = 13, Height = 13 };
            ic.SetResourceReference(System.Windows.Shapes.Shape.StrokeProperty, "B.Text");
            split.Icon = ic;
            cm.Items.Add(split);
        }
        cm.Items.Add(Item("Set start point", "", "setstart", "I"));
        cm.Items.Add(Item("Set end point", "", "setend", "O"));
        cm.Items.Add(new Separator());
        cm.Items.Add(Item("Rotate left", "", "rotl"));
        cm.Items.Add(Item("Rotate right", "", "rotr"));
        cm.Items.Add(Item("Add title before", "", "title"));
        cm.Items.Add(Item("Add caption here", "", "caption"));
        cm.Items.Add(Item("Edit text", "", "edittext"));
        cm.Items.Add(new Separator());
        cm.Items.Add(Item("Add videos and photos…", "", "addmedia", "Ctrl+I"));
        cm.Items.Add(Item("Select all", "", "selectall", "Ctrl+A"));
        return cm;
    }

    // ===================================================================== keyboard

    void OnKey(object sender, KeyEventArgs e)
    {
        var focused = Keyboard.FocusedElement;
        bool typing = focused is TextBox || (focused is ComboBox cb && cb.IsEditable);
        var mods = Keyboard.Modifiers;
        bool ctrl = mods.HasFlag(ModifierKeys.Control), shift = mods.HasFlag(ModifierKeys.Shift);
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (typing)
        {
            if (key == Key.Escape) { Board.Focus(); e.Handled = true; }
            return;
        }
        string cmd = null;
        if (ctrl)
        {
            cmd = key switch
            {
                Key.Z when shift => "redo", Key.Z => "undo", Key.Y => "redo", Key.X => "cut", Key.C => "copy", Key.V => "paste",
                Key.A => "selectall", Key.S when shift => "saveas", Key.S => "save", Key.O => "open", Key.N => "newproj",
                Key.E => "savemovie", Key.I => "addmedia", Key.T => "title", Key.Left => null, Key.Right => null,
                _ => null
            };
            if (key == Key.Left) { JumpClip(-1); e.Handled = true; return; }
            if (key == Key.Right) { JumpClip(1); e.Handled = true; return; }
        }
        else
        {
            switch (key)
            {
                case Key.Space: cmd = "play"; break;
                case Key.Left: if (shift) { SeekTo(_playhead - 1); e.Handled = true; return; } cmd = "prevframe"; break;
                case Key.Right: if (shift) { SeekTo(_playhead + 1); e.Handled = true; return; } cmd = "nextframe"; break;
                case Key.Home: _player.Pause(); SeekTo(0); e.Handled = true; return;
                case Key.End: _player.Pause(); SeekTo(Total); e.Handled = true; return;
                case Key.Delete: cmd = "remove"; break;
                case Key.M: cmd = SelMusic != null ? "m_split" : "split"; break;
                case Key.I: cmd = "setstart"; break;
                case Key.O: cmd = "setend"; break;
                case Key.F11: cmd = "fullscreen"; break;
                case Key.OemPlus: case Key.Add: cmd = "zoomin"; break;
                case Key.OemMinus: case Key.Subtract: cmd = "zoomout"; break;
                case Key.Escape: if (_fullscreen != null) { ToggleFullscreen(); e.Handled = true; } return;
            }
        }
        if (cmd != null) { Run(cmd); e.Handled = true; }
    }

    // ===================================================================== fullscreen

    void ToggleFullscreen()
    {
        if (_fullscreen != null) { _fullscreen.Close(); return; }
        _fullscreen = new FullscreenWindow(this);
        _fullscreen.Img.Source = _wb;
        _fullscreen.Closed += (_, _) => { _fullscreen = null; Activate(); };
        _fullscreen.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || e.Key == Key.F11) _fullscreen?.Close();
            else if (e.Key == Key.Space) _player.Toggle();
            else if (e.Key == Key.Left) StepFrame(-1);
            else if (e.Key == Key.Right) StepFrame(1);
        };
        _fullscreen.MouseLeftButtonDown += (_, e) => { if (e.ClickCount == 2) _fullscreen?.Close(); else _player.Toggle(); };
        _fullscreen.Show();
    }

    // ===================================================================== snapshot

    async void Snapshot()
    {
        if (_p.Clips.Count == 0) return;
        if (!CheckFfmpeg()) return;
        _player.Pause();
        double a = _p.Aspect;
        int w = a >= 1 ? 3840 : (int)Math.Round(3840 * a / 2) * 2;
        int h = a >= 1 ? (int)Math.Round(3840 / a / 2) * 2 : 3840;
        var dlg = new SaveFileDialog
        {
            Title = "Save snapshot",
            Filter = "PNG image|*.png|JPEG image|*.jpg",
            FileName = $"{_p.Name} {FormatTc(_playhead).Replace(':', '-').Replace('.', '-')}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        };
        if (dlg.ShowDialog(this) != true) return;
        ShowStatus("Saving snapshot…");
        var snapshotProject = _p.Clone();
        double t = _playhead;
        try
        {
            var frame = await Task.Run(() => Exporter.RenderStill(snapshotProject, t, w, h));
            var bmp = BitmapSource.Create(frame.W, frame.H, 96, 96, PixelFormats.Bgra32, null, frame.D, frame.W * 4);
            BitmapEncoder enc = dlg.FileName.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ? new JpegBitmapEncoder { QualityLevel = 95 } : new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (var fs = File.Create(dlg.FileName)) enc.Save(fs);
            var m = await Task.Run(() => FF.Probe(dlg.FileName));
            var tl = _p.ComputeTimeline();
            int idx = Math.Max(0, tl.IndexAt(t)) + 1;
            ImportFilesKnown(m, idx);
            ShowStatus($"Snapshot saved to {dlg.FileName} and added to the storyboard.");
        }
        catch (Exception ex) { Dialogs.Info(this, "Snapshot", ex.Message); }
    }

    void ImportFilesKnown(MediaRef m, int idx)
    {
        Mutate(() =>
        {
            _p.Media.Add(m);
            var c = new Clip { Kind = ClipKind.Image, MediaId = m.Id, Duration = 7 };
            _p.Clips.Insert(Math.Clamp(idx, 0, _p.Clips.Count), c);
            Board.SelectedClips.Clear(); Board.SelectedClips.Add(c.Id);
        });
    }

    // ===================================================================== project files

    const string ProjectFilter = "Gerbera Studio project|*.mmrproj|All files|*.*";

    bool ConfirmSave()
    {
        if (!_dirty || (_p.Clips.Count == 0 && _p.Music.Count == 0)) return true;
        int r = Dialogs.Ask(this, "Gerbera Studio", $"Do you want to save changes to {_p.Name}?", "Save", "Don't save", "Cancel");
        if (r == 0) return SaveProject(false);
        return r == 1;
    }

    void NewProject()
    {
        if (!ConfirmSave()) return;
        _player.Pause();
        _p = new Project();
        _path = null; _dirty = false;
        _undo.Clear(); _redo.Clear();
        Board.SelectedClips.Clear(); Board.SelectedMusic = Board.SelectedCaption = null;
        _playhead = 0;
        Refresh();
        SeekTo(0);
    }

    bool SaveProject(bool saveAs)
    {
        if (_path == null || saveAs)
        {
            var dlg = new SaveFileDialog { Filter = ProjectFilter, FileName = _p.Name + ".mmrproj", Title = "Save project" };
            if (Directory.Exists(_settings.LastProjectFolder)) dlg.InitialDirectory = _settings.LastProjectFolder;
            else dlg.InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (dlg.ShowDialog(this) != true) return false;
            _path = dlg.FileName;
            _p.Name = Path.GetFileNameWithoutExtension(_path);
            _settings.LastProjectFolder = Path.GetDirectoryName(_path);
        }
        try
        {
            File.WriteAllText(_path, _p.ToJson());
            _dirty = false;
            _settings.AddRecent(_path);
            _settings.Save();
            UpdateTitle();
            ShowStatus("Project saved.");
            return true;
        }
        catch (Exception ex) { Dialogs.Info(this, "Couldn't save", ex.Message); return false; }
    }

    public void OpenFromCommandLine(string[] args)
    {
        var f = args.FirstOrDefault(File.Exists);
        if (f == null) return;
        if (f.EndsWith(".mmrproj", StringComparison.OrdinalIgnoreCase)) OpenProject(f);
        else Dispatcher.BeginInvoke(() => ImportFiles(args.Where(File.Exists).ToArray(), _p.Clips.Count), DispatcherPriority.ApplicationIdle);
    }

    void OpenProject(string path)
    {
        if (!ConfirmSave()) return;
        if (path == null)
        {
            var dlg = new OpenFileDialog { Filter = ProjectFilter, Title = "Open project" };
            if (Directory.Exists(_settings.LastProjectFolder)) dlg.InitialDirectory = _settings.LastProjectFolder;
            if (dlg.ShowDialog(this) != true) return;
            path = dlg.FileName;
        }
        Project p;
        try { p = Project.FromJson(File.ReadAllText(path)); }
        catch (Exception ex) { Dialogs.Info(this, "Couldn't open project", ex.Message); return; }
        p.Name = Path.GetFileNameWithoutExtension(path);
        // Relink missing media
        foreach (var m in p.Media.Where(m => !File.Exists(m.Path)).ToList())
        {
            var guess = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileName(m.Path));
            if (File.Exists(guess)) { m.Path = guess; continue; }
            int r = Dialogs.Ask(this, "Missing file", $"\"{m.Name}\" can't be found. It may have been moved or renamed.\n\nWould you like to find it?", "Find it…", "Skip");
            if (r != 0) continue;
            var dlg = new OpenFileDialog { Title = $"Find {m.Name}", FileName = m.Name, Filter = "All files|*.*" };
            if (dlg.ShowDialog(this) == true) m.Path = dlg.FileName;
        }
        _player.Pause();
        _p = p; _path = path; _dirty = false;
        _undo.Clear(); _redo.Clear();
        Board.SelectedClips.Clear(); Board.SelectedMusic = Board.SelectedCaption = null;
        _settings.AddRecent(path); _settings.LastProjectFolder = Path.GetDirectoryName(path); _settings.Save();
        _playhead = 0;
        Refresh();
        SeekTo(0);
    }

    // ===================================================================== save movie

    void SaveMovie(string quick)
    {
        if (!CheckFfmpeg()) return;
        if (_p.Clips.Count == 0) { Dialogs.Info(this, "Export movie", "Add some videos or photos before exporting your movie."); return; }
        _player.Pause();
        var dlg = new ExportDialog(_p, _settings, quick) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Result == null) return;
        var s = dlg.Result;
        _settings.LastExportFolder = Path.GetDirectoryName(s.OutputPath);
        _settings.Save();
        var win = new ProgressWindow { Owner = this };
        win.RunExport(_p.Clone(), s);
    }

    // ===================================================================== about / closing

    void ShowAbout()
    {
        string ff = FF.Ffmpeg != null ? FF.Version() : "FFmpeg not found";
        Dialogs.Info(this, "About Gerbera Studio",
            "Gerbera Studio 1.1\nA love letter to the classic Windows movie editor, rebuilt with modern formats, 4K and beyond.\nNot affiliated with or endorsed by Microsoft.\n\n" +
            $"Engine: {ff}\n\n" +
            "Keyboard shortcuts\n" +
            "Space  Play / pause\n←  →  Previous / next frame   (Shift: 1 second, Ctrl: previous / next item)\n" +
            "Home / End  Start / end of movie\nM  Split    I / O  Set start / end point\nDelete  Remove\n" +
            "Ctrl+Z / Ctrl+Y  Undo / redo\nCtrl+X / C / V  Cut / copy / paste\nCtrl+I  Add videos and photos    Ctrl+T  Add title\n" +
            "Ctrl+S  Save project    Ctrl+E  Export movie\nF11  Full-screen preview    + / −  Zoom storyboard");
    }

    void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (!ConfirmSave()) { e.Cancel = true; return; }
        _settings.Save();
        _fullscreen?.Close();
        _player.Dispose();
    }

    // ===================================================================== screenshot tour (for the README)

    /// <summary>Capture the window plus an open popup (e.g. the File menu) composited at its on-screen position.</summary>
    void CaptureWithPopup(FrameworkElement popupChild, string file)
    {
        UpdateLayout(); popupChild.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(this);
        var root = (FrameworkElement)Content;
        int pw = (int)Math.Round(root.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Round(root.ActualHeight * dpi.DpiScaleY);
        var a = root.PointToScreen(new Point(0, 0)); var b = popupChild.PointToScreen(new Point(0, 0));
        var off = new Point((b.X - a.X) / dpi.DpiScaleX, (b.Y - a.Y) / dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            dc.DrawRectangle(Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(popupChild), null, new Rect(off.X, off.Y, popupChild.ActualWidth, popupChild.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    static void CaptureWindow(Window w, string file)
    {
        w.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(w);
        var root = (FrameworkElement)w.Content;
        int pw = (int)Math.Round(root.ActualWidth * dpi.DpiScaleX), ph = (int)Math.Round(root.ActualHeight * dpi.DpiScaleY);
        var rtb = new RenderTargetBitmap(pw, ph, 96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
        {
            if (!w.AllowsTransparency) dc.DrawRectangle(w.Background ?? Brushes.Black, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        rtb.Render(dv);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(file);
        enc.Save(fs);
    }

    void SelectClipAt(int index, double into = 0.5)
    {
        if (index < 0 || index >= _p.Clips.Count) return;
        Board.SelectedClips.Clear(); Board.SelectedClips.Add(_p.Clips[index].Id);
        Board.SelectedMusic = Board.SelectedCaption = null;
        UpdateContext(); SyncRibbon(); UpdateInfo(); ScheduleGalleryImages();
        var tl = _p.ComputeTimeline();
        SeekTo(tl.Start[index] + tl.Dur[index] * into);
        Board.InvalidateVisual();
    }

    public async void StartShots(string outDir, string projectPath)
    {
        try
        {
            Directory.CreateDirectory(outDir);
            string originalTheme = App.CurrentTheme;
            WindowState = WindowState.Normal;
            Width = 1600; Height = 920; Left = 40; Top = 30;
            OpenProject(projectPath);
            ZoomSlider.Value = 0.42;
            async Task Shot(string name, int wait = 1800) { await Task.Delay(wait); CaptureWindow(this, Path.Combine(outDir, name)); }

            SelectClipAt(1, 0.55);
            TabHome.IsChecked = true;
            SetTheme("dark");
            await Shot("main-midnight.png", 7000);
            SetTheme("evening");
            await Shot("main-evening.png");
            SetTheme("light");
            await Shot("main-morning.png");
            SetTheme("dark");

            SelectClipAt(4, 0.5);
            TabFx.IsChecked = true;
            await Shot("effects.png", 3500);

            SelectClipAt(5, 0.6);
            TabFx.IsChecked = true;
            var fxScroll = FindParent<ScrollViewer>(PanelFx);
            fxScroll?.ScrollToHorizontalOffset(10000);
            await Shot("green-screen.png", 3500);
            fxScroll?.ScrollToHorizontalOffset(0);

            SelectClipAt(0, 0.6);
            TabText.IsChecked = true;
            await Shot("titles.png", 3000);

            // Animations (transitions + pan and zoom), in Evening, paused mid-transition
            SetTheme("evening");
            SelectClipAt(2, 0.0);
            { var tl0 = _p.ComputeTimeline(); SeekTo(tl0.Start[2] + tl0.Trans[2] * 0.5); }
            TabAnim.IsChecked = true;
            await Shot("animations.png", 3500);

            // Edit tab: audio effects (Match visual effect), reverse, speed — Midnight
            SetTheme("dark");
            SelectClipAt(4, 0.4);
            TabVideo.IsChecked = true;
            await Shot("edit-audio.png", 3000);

            // Music tools, in Morning
            SetTheme("light");
            if (_p.Music.Count > 0)
            {
                Board.SelectedClips.Clear(); Board.SelectedCaption = null; Board.SelectedMusic = _p.Music[0].Id;
                UpdateContext(); SyncRibbon(); UpdateInfo(); Board.InvalidateVisual();
                SeekTo(_p.Music[0].Start + 6);
                TabMusic.IsChecked = true;
                await Shot("music.png", 2500);
            }

            // Project tab: aspect ratios, frame rates, audio mix — Evening
            SetTheme("evening");
            SelectClipAt(3, 0.5);
            TabProject.IsChecked = true;
            await Shot("project.png", 2500);

            // View tab with the theme switcher — Midnight
            SetTheme("dark");
            TabView.IsChecked = true;
            await Shot("view.png", 2000);

            // File menu (recent projects, thank-you rainbow)
            TabHome.IsChecked = true;
            FileTabClick(this, new RoutedEventArgs());
            await Task.Delay(1200);
            if (FileMenu.Child is FrameworkElement fm) CaptureWithPopup(fm, Path.Combine(outDir, "file-menu.png"));
            FileMenu.IsOpen = false;

            // Edit tab with the new Split icon, in Morning
            SetTheme("light");
            SelectClipAt(2, 0.5);
            TabVideo.IsChecked = true;
            await Shot("split.png", 2500);
            SetTheme("dark");

            // Labs window, in each theme (the beaker changes color)
            foreach (var th in new[] { "dark", "evening", "light" })
            {
                SetTheme(th);
                var labs = new LabsWindow(_settings) { Owner = this };
                labs.Show();
                await Task.Delay(900);
                CaptureWindow(labs, Path.Combine(outDir, $"labs-{(th == "dark" ? "midnight" : th == "light" ? "morning" : "evening")}.png"));
                labs.Close();
            }
            SetTheme("dark");

            // Nostalgic loading screen, one per theme
            foreach (var th in new[] { "dark", "evening", "light" })
            {
                var sp = new SplashWindow(th) { Seconds = 999 };
                sp.Show();
                await Task.Delay(2200);
                CaptureWindow(sp, Path.Combine(outDir, $"splash-{(th == "dark" ? "midnight" : th == "light" ? "morning" : "evening")}.png"));
                sp.Close();
            }

            // What's new window
            var wn = new WhatsNewWindow { Owner = this };
            wn.Show();
            await Task.Delay(900);
            CaptureWindow(wn, Path.Combine(outDir, "whats-new.png"));
            wn.Close();

            TabHome.IsChecked = true;
            var dlg = new ExportDialog(_p, _settings, "4k") { Owner = this };
            dlg.Show();
            await Task.Delay(2500);
            CaptureWindow(dlg, Path.Combine(outDir, "save-movie.png"));
            dlg.Close();

            SetTheme(originalTheme);
            _dirty = false;
            Close();
        }
        catch (Exception ex)
        {
            Dialogs.Info(this, "Screenshot tour", ex.Message);
        }
    }

    System.Media.SoundPlayer _bubbles;

    void LabsClick(object sender, RoutedEventArgs e)
    {
        FileMenu.IsOpen = false;
        if (_settings.LabSounds)
        {
            try
            {
                _bubbles ??= new System.Media.SoundPlayer(Application.GetResourceStream(new Uri("pack://application:,,,/Assets/bubbles.wav")).Stream);
                _bubbles.Play();
            }
            catch { }
        }
        new LabsWindow(_settings) { Owner = this }.ShowDialog();
        _settings.Save();
    }

    /// <summary>After an update (not a fresh install), show the new version's changelog once.</summary>
    void ShowWhatsNewIfUpdated()
    {
        if (Environment.GetCommandLineArgs().Contains("--shots")) return;
        string v = WhatsNewWindow.AppVersion;
        bool updated = !_settings.IsFirstRun && _settings.LastSeenVersion != v;
        _settings.LastSeenVersion = v;
        _settings.Save();
        if (updated)
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => new WhatsNewWindow { Owner = this }.ShowDialog());
    }

    void RainbowClick(object sender, RoutedEventArgs e)
    {
        FileMenu.IsOpen = false;
        var w = new Window { Title = "Thank you", Owner = this, SizeToContent = SizeToContent.WidthAndHeight, Style = (Style)FindResource("DialogWindow"), WindowStartupLocation = WindowStartupLocation.CenterOwner };
        ThemeHelper.ApplyTitleBar(w);
        var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 380, FontSize = 15, LineHeight = 22, TextAlignment = TextAlignment.Center };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        tb.Inlines.Add(new System.Windows.Documents.Run("Thank you Larry, Bruce, Mom, Dad and Lily! I could not have done this without you all. "));
        tb.Inlines.Add(new System.Windows.Documents.Run("\u2764") { Foreground = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)), FontFamily = new FontFamily("Segoe UI Symbol") });
        var ok = new Button { Content = "OK", IsDefault = true, IsCancel = true, MinWidth = 84, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 18, 0, 0) };
        ok.Click += (_, _) => w.Close();
        var sp = new StackPanel { Margin = new Thickness(28, 24, 28, 18) };
        sp.Children.Add(tb); sp.Children.Add(ok);
        w.Content = sp;
        w.ShowDialog();
    }

    void MinClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    void MaxClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    void CloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Borderless full-screen preview.</summary>
public class FullscreenWindow : Window
{
    public readonly Image Img = new() { Stretch = Stretch.Uniform };
    public FullscreenWindow(Window owner)
    {
        Owner = owner;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Maximized;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        RenderOptions.SetBitmapScalingMode(Img, BitmapScalingMode.HighQuality);
        var g = new Grid();
        g.Children.Add(Img);
        var hint = new TextBlock { Text = "Esc to exit full screen  ·  Space to play/pause", Foreground = new SolidColorBrush(Color.FromArgb(150, 255, 255, 255)), Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top };
        g.Children.Add(hint);
        Content = g;
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        t.Tick += (_, _) => { hint.Visibility = Visibility.Collapsed; t.Stop(); };
        t.Start();
        Cursor = Cursors.None;
        MouseMove += (_, _) => { Cursor = null; };
    }
}
