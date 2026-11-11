using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using MovieMakerReborn.Engine;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Views;

/// <summary>"Save movie" dialog: presets plus full control over resolution, frame rate, codecs and quality.</summary>
public class ExportDialog : Window
{
    public ExportSettings Result;
    readonly Project _p;
    readonly Settings _s;
    bool _sync;

    readonly ListBox _presets = new() { Width = 236 };
    readonly ComboBox _res = new();
    readonly TextBox _w = new() { Width = 62 }, _h = new() { Width = 62 };
    readonly ComboBox _fps = new();
    readonly TextBox _fpsCustom = new() { Width = 90, ToolTip = "Any rate, e.g. 37.5 or 30000/1001" };
    readonly ComboBox _interp = new();
    readonly ComboBox _codec = new();
    readonly ComboBox _container = new();
    readonly RadioButton _modeCrf = new() { Content = "Constant quality", GroupName = "q" };
    readonly RadioButton _modeBr = new() { Content = "Target bitrate", GroupName = "q" };
    readonly Slider _quality = new() { Minimum = 0, Maximum = 40, Width = 180, IsSnapToTickEnabled = true, TickFrequency = 1 };
    readonly TextBlock _qualityLabel = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly TextBox _bitrate = new() { Width = 70 };
    readonly ComboBox _speed = new();
    readonly CheckBox _tenBit = new() { Content = "10-bit color (smoother gradients, less banding)" };
    readonly ComboBox _audio = new();
    readonly ComboBox _audioKbps = new() { Width = 110 };
    readonly TextBox _path = new();
    readonly TextBlock _summary = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    static readonly int[] ShortSides = { 4320, 2880, 2160, 1440, 1080, 720, 480, 360 };
    static readonly string[] ShortNames = { "8K", "5K", "4K Ultra HD", "Quad HD", "Full HD", "HD", "SD", "Low" };
    static readonly (string Key, string Label, string Sub)[] PresetList =
    {
        ("rec", "Recommended for this project", "Best match for your clips"),
        ("4k", "4K Ultra HD", "2160p · H.264"),
        ("4khevc", "4K high quality", "2160p · HEVC 10-bit"),
        ("4kav1", "4K efficient", "2160p · AV1 (smallest)"),
        ("1440", "1440p Quad HD", "H.264"),
        ("1080", "1080p Full HD", "H.264"),
        ("720", "720p HD", "H.264"),
        ("480", "Email and small files", "480p · H.264"),
        ("vertical", "Vertical video", "1080×1920 · Shorts, Reels, TikTok"),
        ("square", "Square video", "1080×1080"),
        ("web", "Web video", "1080p · VP9 + Opus WebM"),
        ("prores", "Editing master", "ProRes 422 HQ · MOV"),
        ("ffv1", "Lossless archive", "FFV1 · MKV"),
        ("custom", "Custom settings", "Everything below is yours"),
    };

    public ExportDialog(Project p, Settings s, string quick)
    {
        _p = p; _s = s;
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Save movie";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 900; Height = 640;
        ResizeMode = ResizeMode.CanResizeWithGrip;
        ThemeHelper.ApplyTitleBar(this);
        BuildUi();
        Populate();
        string start = quick switch { "4k" => "4k", "1080" => "1080", "vertical" => "vertical", _ => "rec" };
        _presets.SelectedIndex = Array.FindIndex(PresetList, x => x.Key == start);
        CheckEncodersAsync();
    }

    // ------------------------------------------------------------------ layout

    TextBlock Lbl(string t)
    {
        var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
        return tb;
    }

    TextBlock Section(string t)
    {
        var tb = new TextBlock { Text = t, FontSize = 14, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 14, 0, 6) };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        return tb;
    }

    void BuildUi()
    {
        var root = new Grid { Margin = new Thickness(16) };
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // Presets
        foreach (var pr in PresetList)
        {
            var sp = new StackPanel();
            sp.Children.Add(new TextBlock { Text = pr.Label, FontSize = 13 });
            var sub = new TextBlock { Text = pr.Sub, FontSize = 11 };
            sub.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
            sp.Children.Add(sub);
            _presets.Items.Add(new ListBoxItem { Content = sp, Tag = pr.Key });
        }
        _presets.SelectionChanged += (_, _) => { if (_presets.SelectedItem is ListBoxItem li) ApplyPreset((string)li.Tag); };
        root.Children.Add(_presets);

        // Settings grid
        var g = new Grid { Margin = new Thickness(20, 0, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(150) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        int row = 0;
        void Row(UIElement label, UIElement ctl)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            if (label != null) { Grid.SetRow(label, row); g.Children.Add(label); }
            if (ctl != null)
            {
                if (ctl is FrameworkElement fe) fe.Margin = new Thickness(fe.Margin.Left, 3, fe.Margin.Right, 3);
                Grid.SetRow(ctl, row); Grid.SetColumn(ctl, label == null ? 0 : 1);
                if (label == null) Grid.SetColumnSpan(ctl, 2);
                g.Children.Add(ctl);
            }
            row++;
        }
        StackPanel H(params UIElement[] items) { var sp = new StackPanel { Orientation = Orientation.Horizontal }; foreach (var i in items) sp.Children.Add(i); return sp; }
        TextBlock Dim(string t) { var tb = new TextBlock { Text = t, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0) }; tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim"); return tb; }

        Row(null, Section("Video"));
        _res.Width = 260;
        Row(Lbl("Resolution"), H(_res, Dim("  "), _w, Dim("×"), _h));
        _fps.Width = 260;
        Row(Lbl("Frame rate"), H(_fps, Dim("  "), _fpsCustom));
        _interp.Width = 360;
        Row(Lbl("Frame rate conversion"), _interp);
        _codec.Width = 360;
        Row(Lbl("Video format"), _codec);
        _container.Width = 200;
        Row(Lbl("File type"), _container);
        Row(Lbl("Quality"), H(_modeCrf, Dim(""), _quality, _qualityLabel));
        Row(null, null);
        Grid.SetColumn(g.Children[^1] as UIElement ?? new TextBlock(), 1);
        Row(Lbl(""), H(_modeBr, Dim(""), _bitrate, Dim("Mbps")));
        _speed.Width = 260;
        Row(Lbl("Encoding speed"), _speed);
        Row(Lbl(""), _tenBit);

        Row(null, Section("Audio"));
        _audio.Width = 260;
        Row(Lbl("Audio format"), H(_audio, Dim("  "), _audioKbps));

        Row(null, Section("Save as"));
        var browse = new Button { Content = "Browse…", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(12, 3, 12, 3) };
        browse.Click += (_, _) => Browse();
        var pathRow = new DockPanel();
        DockPanel.SetDock(browse, Dock.Right);
        pathRow.Children.Add(browse);
        pathRow.Children.Add(_path);
        Row(Lbl("File"), pathRow);
        Row(null, new Border { Child = _summary, Margin = new Thickness(0, 12, 0, 0), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(4) });
        ((Border)g.Children[^1]).SetResourceReference(Border.BackgroundProperty, "B.Input");
        _summary.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");

        var scroll = new ScrollViewer { Content = g, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1);
        root.Children.Add(scroll);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var save = new Button { Content = "Save movie", IsDefault = true, MinWidth = 110 };
        var cancel = new Button { Content = "Cancel", IsCancel = true, MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        save.Click += (_, _) => Save();
        buttons.Children.Add(save); buttons.Children.Add(cancel);
        Grid.SetRow(buttons, 1); Grid.SetColumnSpan(buttons, 2);
        root.Children.Add(buttons);
        Content = root;

        // Events
        _res.SelectionChanged += (_, _) => { if (!_sync) { SyncDims(); Custom(); } };
        _w.LostFocus += (_, _) => { if (!_sync) { _res.SelectedIndex = _res.Items.Count - 1; Custom(); } };
        _h.LostFocus += (_, _) => { if (!_sync) { _res.SelectedIndex = _res.Items.Count - 1; Custom(); } };
        _fps.SelectionChanged += (_, _) => { _fpsCustom.Visibility = _fps.SelectedIndex == _fps.Items.Count - 1 ? Visibility.Visible : Visibility.Collapsed; if (!_sync) Custom(); };
        _fpsCustom.LostFocus += (_, _) => Custom();
        _interp.SelectionChanged += (_, _) => Custom();
        _codec.SelectionChanged += (_, _) => { if (!_sync) { FillContainers(); Custom(); } };
        _container.SelectionChanged += (_, _) => { if (!_sync) { FillAudio(); FixExtension(); Custom(); } };
        _modeCrf.Checked += (_, _) => Custom(); _modeBr.Checked += (_, _) => Custom();
        _quality.ValueChanged += (_, _) => { UpdateQualityLabel(); Custom(); };
        _bitrate.LostFocus += (_, _) => Custom();
        _speed.SelectionChanged += (_, _) => Custom();
        _tenBit.Click += (_, _) => Custom();
        _audio.SelectionChanged += (_, _) => { _audioKbps.IsEnabled = _audio.SelectedItem is ComboBoxItem ai && (string)ai.Tag is "aac" or "opus"; Custom(); };
        _audioKbps.SelectionChanged += (_, _) => Custom();
    }

    void Populate()
    {
        _sync = true;
        double a = _p.Aspect;
        for (int i = 0; i < ShortSides.Length; i++)
        {
            var (w, h) = Dims(ShortSides[i], a);
            _res.Items.Add(new ComboBoxItem { Content = $"{ShortSides[i]}p {ShortNames[i]} — {w}×{h}", Tag = ShortSides[i] });
        }
        _res.Items.Add(new ComboBoxItem { Content = "Custom size", Tag = 0 });
        foreach (var f in Presets.FrameRates) _fps.Items.Add(new ComboBoxItem { Content = f.Label, Tag = f });
        _fps.Items.Add(new ComboBoxItem { Content = "Custom…" });
        foreach (var i in Presets.Interps) _interp.Items.Add(new ComboBoxItem { Content = i.Label, Tag = i.Key });
        foreach (var c in Codecs.All) _codec.Items.Add(new ComboBoxItem { Content = c.Label + (c.Hardware ? "  (checking…)" : ""), Tag = c });
        foreach (var sp in new[] { ("fast", "Fast (bigger file)"), ("medium", "Balanced"), ("slow", "Slow (smaller file)"), ("slowest", "Slowest (smallest file)") })
            _speed.Items.Add(new ComboBoxItem { Content = sp.Item2, Tag = sp.Item1 });
        foreach (var k in new[] { 128, 160, 192, 256, 320, 384, 512 }) _audioKbps.Items.Add(new ComboBoxItem { Content = $"{k} kbps", Tag = k });
        _fpsCustom.Visibility = Visibility.Collapsed;
        string folder = Directory.Exists(_s.LastExportFolder) ? _s.LastExportFolder : Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
        _path.Text = Path.Combine(folder, SafeName(_p.Name) + ".mp4");
        _sync = false;
    }

    static string SafeName(string n)
    {
        foreach (var c in Path.GetInvalidFileNameChars()) n = n.Replace(c, '_');
        return string.IsNullOrWhiteSpace(n) ? "My Movie" : n;
    }

    static (int w, int h) Dims(int shortSide, double aspect)
    {
        if (aspect >= 1) return ((int)Math.Round(shortSide * aspect / 2) * 2, shortSide);
        return (shortSide, (int)Math.Round(shortSide / aspect / 2) * 2);
    }

    void SyncDims()
    {
        if (_res.SelectedItem is ComboBoxItem it && (int)it.Tag > 0)
        {
            var (w, h) = Dims((int)it.Tag, _p.Aspect);
            _w.Text = w.ToString(); _h.Text = h.ToString();
        }
        _w.IsEnabled = _h.IsEnabled = true;
    }

    void Custom()
    {
        if (_sync) return;
        _sync = true;
        _presets.SelectedIndex = PresetList.Length - 1;
        _sync = false;
        UpdateSummary();
    }

    VideoCodecInfo Codec => (_codec.SelectedItem as ComboBoxItem)?.Tag as VideoCodecInfo ?? Codecs.All[0];

    void SelectCodec(string key)
    {
        for (int i = 0; i < _codec.Items.Count; i++)
            if (((VideoCodecInfo)((ComboBoxItem)_codec.Items[i]).Tag).Key == key) { _codec.SelectedIndex = i; break; }
        FillContainers();
    }

    void FillContainers(string prefer = null)
    {
        var cur = prefer ?? (_container.SelectedItem as ComboBoxItem)?.Tag as string;
        bool old = _sync; _sync = true;
        _container.Items.Clear();
        foreach (var c in Codec.Containers)
            _container.Items.Add(new ComboBoxItem { Content = c switch { "mp4" => "MP4 (.mp4)", "mkv" => "Matroska (.mkv)", "mov" => "QuickTime (.mov)", "webm" => "WebM (.webm)", _ => c }, Tag = c });
        int idx = Array.IndexOf(Codec.Containers, cur);
        _container.SelectedIndex = idx >= 0 ? idx : 0;
        bool noQ = Codec.NoQuality;
        _quality.IsEnabled = _modeCrf.IsEnabled = _modeBr.IsEnabled = _bitrate.IsEnabled = _speed.IsEnabled = !noQ;
        _tenBit.IsEnabled = Codec.Family != "prores";
        _sync = old;
        FillAudio();
        FixExtension();
    }

    string Container => (_container.SelectedItem as ComboBoxItem)?.Tag as string ?? "mp4";

    void FillAudio(string prefer = null)
    {
        var cur = prefer ?? (_audio.SelectedItem as ComboBoxItem)?.Tag as string ?? "aac";
        bool old = _sync; _sync = true;
        _audio.Items.Clear();
        foreach (var a in Codecs.Audio.Where(a => a.Containers.Contains(Container)))
            _audio.Items.Add(new ComboBoxItem { Content = a.Label, Tag = a.Key });
        int idx = -1;
        for (int i = 0; i < _audio.Items.Count; i++) if ((string)((ComboBoxItem)_audio.Items[i]).Tag == cur) idx = i;
        _audio.SelectedIndex = idx >= 0 ? idx : 0;
        _sync = old;
    }

    void FixExtension()
    {
        try
        {
            var p = _path.Text.Trim();
            if (p.Length == 0) return;
            _path.Text = Path.ChangeExtension(p, "." + Container);
        }
        catch { }
    }

    void UpdateQualityLabel()
    {
        int crf = 40 - (int)_quality.Value;
        string q = crf <= 14 ? "Visually lossless" : crf <= 18 ? "Very high" : crf <= 22 ? "High" : crf <= 27 ? "Good" : crf <= 32 ? "Smaller file" : "Smallest file";
        _qualityLabel.Text = $"{q}  (CRF {crf})";
    }

    void SelectTag(ComboBox cb, object tag)
    {
        for (int i = 0; i < cb.Items.Count; i++)
            if (Equals(((ComboBoxItem)cb.Items[i]).Tag, tag)) { cb.SelectedIndex = i; return; }
    }

    void ApplyPreset(string key)
    {
        if (_sync) return;
        if (key == "custom") { UpdateSummary(); return; }
        _sync = true;
        int maxShort = 1080;
        foreach (var m in _p.Media.Where(m => m.Kind != MediaKind.Audio && _p.Clips.Any(c => c.MediaId == m.Id)))
            maxShort = Math.Max(maxShort, Math.Min(m.Width, m.Height));
        int shortSide = key switch
        {
            "rec" => maxShort >= 2000 ? 2160 : maxShort >= 1400 ? 1440 : 1080,
            "4k" or "4khevc" or "4kav1" or "prores" or "ffv1" => 2160,
            "1440" => 1440,
            "720" => 720,
            "480" => 480,
            _ => 1080
        };
        SelectTag(_res, shortSide);
        SyncDims();
        if (key == "vertical") { _res.SelectedIndex = _res.Items.Count - 1; _w.Text = "1080"; _h.Text = "1920"; }
        if (key == "square") { _res.SelectedIndex = _res.Items.Count - 1; _w.Text = "1080"; _h.Text = "1080"; }
        // Frame rate: project's
        int fi = Array.FindIndex(Presets.FrameRates, f => f.Num == _p.FpsNum && f.Den == _p.FpsDen);
        if (fi >= 0) _fps.SelectedIndex = fi;
        else { _fps.SelectedIndex = _fps.Items.Count - 1; _fpsCustom.Text = $"{_p.FpsNum}/{_p.FpsDen}"; }
        SelectTag(_interp, _p.Interp);
        string codec = key switch { "4khevc" => "hevc", "4kav1" => "av1", "web" => "vp9", "prores" => "prores", "ffv1" => "ffv1", _ => "h264" };
        SelectCodec(codec);
        FillContainers(key switch { "web" => "webm", "prores" => "mov", "ffv1" => "mkv", _ => "mp4" });
        FillAudio(key switch { "web" => "opus", "prores" => "pcm", "ffv1" => "flac", _ => "aac" });
        _modeCrf.IsChecked = true;
        int crf = key switch { "720" => 20, "480" => 23, "web" => 20, _ => 18 };
        _quality.Value = 40 - crf;
        UpdateQualityLabel();
        _bitrate.Text = (shortSide >= 2160 ? 45 : shortSide >= 1440 ? 24 : shortSide >= 1080 ? 12 : 6).ToString();
        SelectTag(_speed, key == "4kav1" ? "medium" : "medium");
        _tenBit.IsChecked = key == "4khevc";
        SelectTag(_audioKbps, key == "480" ? 128 : key == "web" ? 160 : 256);
        _audioKbps.IsEnabled = _audio.SelectedItem is ComboBoxItem ai && (string)ai.Tag is "aac" or "opus";
        FixExtension();
        _sync = false;
        UpdateSummary();
    }

    async void CheckEncodersAsync()
    {
        var results = await Task.Run(() => Codecs.All.Select(c => (c, FF.EncoderWorks(c.Encoder))).ToList());
        foreach (var (c, ok) in results)
        {
            foreach (ComboBoxItem it in _codec.Items)
            {
                if (it.Tag != c) continue;
                it.Content = c.Label + (ok ? "" : "  — not available on this PC");
                it.IsEnabled = ok;
            }
        }
        if (_codec.SelectedItem is ComboBoxItem sel && !sel.IsEnabled) SelectCodec("h264");
        UpdateSummary();
    }

    bool TryGetFps(out int num, out int den)
    {
        num = 30; den = 1;
        if (_fps.SelectedItem is ComboBoxItem it && it.Tag is ValueTuple<int, int, string> f) { num = f.Item1; den = f.Item2; return true; }
        var t = _fpsCustom.Text.Trim().Replace(',', '.');
        if (t.Contains('/'))
        {
            var parts = t.Split('/');
            if (parts.Length == 2 && int.TryParse(parts[0], out num) && int.TryParse(parts[1], out den) && num > 0 && den > 0) return (double)num / den <= 480;
            return false;
        }
        if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0.5 && d <= 480)
        {
            den = 1000; num = (int)Math.Round(d * 1000);
            int g = Gcd(num, den); num /= g; den /= g;
            return true;
        }
        return false;
    }

    static int Gcd(int a, int b) { while (b != 0) (a, b) = (b, a % b); return Math.Abs(a); }

    bool TryGetDims(out int w, out int h)
    {
        bool ok = int.TryParse(_w.Text, out w) & int.TryParse(_h.Text, out h);
        w &= ~1; h &= ~1;
        return ok && w >= 16 && h >= 16 && w <= 8192 && h <= 8192;
    }

    void UpdateSummary()
    {
        if (!IsInitialized) return;
        var tl = _p.ComputeTimeline();
        double dur = _p.TotalDuration(tl);
        if (!TryGetDims(out int w, out int h) || !TryGetFps(out int num, out int den)) { _summary.Text = "Check the resolution and frame rate."; return; }
        double fps = (double)num / den;
        var c = Codec;
        double vbr; // Mbps
        if (c.Family == "prores") vbr = 0.00000294 * w * h * fps * 0.98 * 1.0;
        else if (c.Family == "ffv1") vbr = w * h * fps * 12 * 0.5 / 1e6;
        else if (_modeBr.IsChecked == true && double.TryParse(_bitrate.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var mb)) vbr = mb;
        else
        {
            int crf = 40 - (int)_quality.Value;
            double bpp = 0.11 * Math.Pow(0.5, (crf - 18) / 6.0);
            double k = c.Family switch { "hevc" => 0.6, "av1" => 0.5, "vp9" => 0.65, _ => 1.0 };
            if (c.Hardware) k *= 1.3;
            vbr = w * h * Math.Min(fps, 60) * Math.Pow(fps / Math.Min(fps, 60), 0.5) * bpp * k / 1e6 * 0.45;
        }
        string a = (_audio.SelectedItem as ComboBoxItem)?.Tag as string ?? "aac";
        int akbps = (_audioKbps.SelectedItem as ComboBoxItem)?.Tag is int ak ? ak : 256;
        double abr = a switch { "none" => 0, "flac" => 0.9, "pcm" => 2.3, _ => akbps / 1000.0 };
        double mbTotal = (vbr + abr) * dur / 8;
        string size = mbTotal > 1024 ? $"{mbTotal / 1024:0.0} GB" : $"{mbTotal:0} MB";
        _summary.Text = $"{w}×{h} at {fps:0.###} fps  ·  {StoryboardView.FormatTime(dur)} long  ·  estimated size about {size}" +
                        (c.Hardware ? "\nUses your graphics card to encode — much faster, slightly larger files." : "") +
                        (_interp.SelectedItem is ComboBoxItem ii && (string)ii.Tag == "mci" ? "\nMotion interpolation looks smoothest but makes saving much slower." : "") +
                        (w * h > 3840 * 2160 && c.Family == "h264" ? "\nH.264 above 4K plays on few devices — HEVC or AV1 is a better fit." : "");
    }

    void Browse()
    {
        var dlg = new SaveFileDialog
        {
            Title = "Save movie",
            FileName = Path.GetFileName(_path.Text),
            Filter = $"{Container.ToUpperInvariant()} video|*.{Container}",
            DefaultExt = "." + Container,
        };
        try { dlg.InitialDirectory = Path.GetDirectoryName(_path.Text); } catch { }
        if (dlg.ShowDialog(this) == true) _path.Text = dlg.FileName;
    }

    void Save()
    {
        if (!TryGetDims(out int w, out int h)) { Dialogs.Info(this, "Save movie", "The width and height must be between 16 and 8192 pixels."); return; }
        if (!TryGetFps(out int num, out int den)) { Dialogs.Info(this, "Save movie", "Enter a frame rate like 30, 59.94 or 30000/1001."); return; }
        string path = _path.Text.Trim();
        try
        {
            path = Path.GetFullPath(path);
            var dir = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(dir)) throw new Exception();
            Directory.CreateDirectory(dir);
        }
        catch { Dialogs.Info(this, "Save movie", "That file location isn't valid."); return; }
        if (File.Exists(path) && Dialogs.Ask(this, "Save movie", $"{Path.GetFileName(path)} already exists. Replace it?", "Replace", "Cancel") != 0) return;
        double br = 20;
        if (_modeBr.IsChecked == true && (!double.TryParse(_bitrate.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out br) || br <= 0.1 || br > 2000))
        { Dialogs.Info(this, "Save movie", "Enter a bitrate in megabits per second, e.g. 40."); return; }
        var codec = Codec;
        Result = new ExportSettings
        {
            Width = w, Height = h, FpsNum = num, FpsDen = den,
            Interp = (_interp.SelectedItem as ComboBoxItem)?.Tag as string ?? "drop",
            Codec = codec, Container = Container,
            UseBitrate = _modeBr.IsChecked == true, BitrateKbps = (int)(br * 1000),
            Crf = 40 - (int)_quality.Value,
            Speed = (_speed.SelectedItem as ComboBoxItem)?.Tag as string ?? "medium",
            TenBit = _tenBit.IsChecked == true && codec.Family != "prores",
            AudioCodec = (_audio.SelectedItem as ComboBoxItem)?.Tag as string ?? "aac",
            AudioKbps = (_audioKbps.SelectedItem as ComboBoxItem)?.Tag is int ak ? ak : 256,
            OutputPath = path,
        };
        _s.ExportCodec = codec.Key; _s.ExportContainer = Container; _s.ExportCrf = Result.Crf; _s.ExportSpeed = Result.Speed;
        _s.ExportTenBit = Result.TenBit; _s.ExportAudio = Result.AudioCodec; _s.ExportAudioKbps = Result.AudioKbps;
        DialogResult = true;
    }
}
