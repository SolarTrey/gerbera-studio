using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;

namespace MovieMakerReborn.Views;

public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");
}

/// <summary>Ribbon button with a font-glyph icon, optional badge glyph, and a label.</summary>
public class RibbonButton : Button
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(RibbonButton));
    public static readonly DependencyProperty BadgeProperty = DependencyProperty.Register(nameof(Badge), typeof(string), typeof(RibbonButton));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RibbonButton));
    public static readonly DependencyProperty IsLargeProperty = DependencyProperty.Register(nameof(IsLarge), typeof(bool), typeof(RibbonButton));
    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(nameof(GlyphBrush), typeof(Brush), typeof(RibbonButton));
    public static readonly DependencyProperty BadgeBrushProperty = DependencyProperty.Register(nameof(BadgeBrush), typeof(Brush), typeof(RibbonButton));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Badge { get => (string)GetValue(BadgeProperty); set => SetValue(BadgeProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsLarge { get => (bool)GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }
    public Brush GlyphBrush { get => (Brush)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
    public Brush BadgeBrush { get => (Brush)GetValue(BadgeBrushProperty); set => SetValue(BadgeBrushProperty, value); }
}

public class RibbonToggle : ToggleButton
{
    public static readonly DependencyProperty GlyphProperty = DependencyProperty.Register(nameof(Glyph), typeof(string), typeof(RibbonToggle));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(RibbonToggle));
    public static readonly DependencyProperty IsLargeProperty = DependencyProperty.Register(nameof(IsLarge), typeof(bool), typeof(RibbonToggle));
    public static readonly DependencyProperty GlyphBrushProperty = DependencyProperty.Register(nameof(GlyphBrush), typeof(Brush), typeof(RibbonToggle));
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public bool IsLarge { get => (bool)GetValue(IsLargeProperty); set => SetValue(IsLargeProperty, value); }
    public Brush GlyphBrush { get => (Brush)GetValue(GlyphBrushProperty); set => SetValue(GlyphBrushProperty, value); }
}

/// <summary>A swatch button that opens a palette popup.</summary>
public class ColorButton : Button
{
    public static readonly DependencyProperty ColorProperty = DependencyProperty.Register(nameof(Color), typeof(string), typeof(ColorButton),
        new FrameworkPropertyMetadata("#FFFFFF", (d, e) => ((ColorButton)d).UpdateSwatch()));
    public static readonly DependencyProperty LabelProperty = DependencyProperty.Register(nameof(Label), typeof(string), typeof(ColorButton));
    public string Color { get => (string)GetValue(ColorProperty); set => SetValue(ColorProperty, value); }
    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }
    public event Action<string> ColorPicked;

    public static readonly DependencyProperty SwatchProperty = DependencyProperty.Register(nameof(Swatch), typeof(Brush), typeof(ColorButton));
    public Brush Swatch { get => (Brush)GetValue(SwatchProperty); set => SetValue(SwatchProperty, value); }

    static readonly string[] Palette =
    {
        "#FFFFFF","#F2F2F2","#BFBFBF","#808080","#404040","#262626","#000000","#1A1A2E",
        "#FFE699","#FFD966","#FFC000","#F4B183","#ED7D31","#C55A11","#FF0000","#C00000",
        "#E2EFDA","#A9D08E","#70AD47","#548235","#00B050","#375623","#00B0A0","#0B6E64",
        "#DDEBF7","#9BC2E6","#5B9BD5","#2F75B5","#0070C0","#1F4E79","#002060","#7030A0",
        "#F8CBAD","#FF99CC","#FF66CC","#CC3399","#B4A7D6","#8E7CC3","#674EA7","#3F2A73",
    };

    public ColorButton() { UpdateSwatch(); }

    void UpdateSwatch()
    {
        try { Swatch = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(Color ?? "#FFFFFF")); }
        catch { Swatch = Brushes.White; }
    }

    protected override void OnClick()
    {
        base.OnClick();
        var popup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true };
        var border = new Border { Padding = new Thickness(8), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
        border.SetResourceReference(Border.BackgroundProperty, "B.Menu");
        border.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        var stack = new StackPanel();
        var grid = new UniformGrid { Columns = 8 };
        foreach (var hex in Palette)
        {
            var b = new Button { Width = 20, Height = 20, Margin = new Thickness(2), Style = (Style)FindResource("SwatchButton"), ToolTip = hex };
            b.Background = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            b.Click += (_, _) => { popup.IsOpen = false; Pick(hex); };
            grid.Children.Add(b);
        }
        stack.Children.Add(grid);
        var row = new DockPanel { Margin = new Thickness(2, 8, 2, 0) };
        var lbl = new TextBlock { Text = "Hex ", VerticalAlignment = VerticalAlignment.Center };
        lbl.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
        var tb = new TextBox { Text = Color, Width = 90 };
        var ok = new Button { Content = "OK", Margin = new Thickness(6, 0, 0, 0), Padding = new Thickness(10, 2, 10, 2) };
        void Commit()
        {
            var t = tb.Text.Trim();
            if (!t.StartsWith("#")) t = "#" + t;
            try { ColorConverter.ConvertFromString(t); popup.IsOpen = false; Pick(t.ToUpperInvariant()); } catch { tb.Foreground = Brushes.IndianRed; }
        }
        ok.Click += (_, _) => Commit();
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Commit(); };
        DockPanel.SetDock(ok, Dock.Right);
        row.Children.Add(ok); row.Children.Add(lbl); row.Children.Add(tb);
        stack.Children.Add(row);
        border.Child = stack;
        popup.Child = border;
        popup.IsOpen = true;
    }

    void Pick(string hex)
    {
        Color = hex;
        ColorPicked?.Invoke(hex);
    }
}

/// <summary>Ribbon gallery: a scrolling strip of choices plus a drop-down showing them all.</summary>
public class Gallery : Grid
{
    public sealed class Item
    {
        public string Key, Label, Glyph;
        public ImageSource Image;
    }

    readonly StackPanel _strip = new() { Orientation = Orientation.Horizontal };
    readonly ScrollViewer _scroll;
    readonly WrapPanel _all = new() { Orientation = Orientation.Horizontal };
    readonly Popup _popup;
    List<Item> _items = new();
    string _selected;
    public event Action<string> Picked;
    public event Action<string> Hovered;
    public double ItemWidth { get; set; } = 70;

    public Gallery()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _strip,
        };
        _scroll.PreviewMouseWheel += (_, e) => { _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - e.Delta * 0.6); e.Handled = true; };
        var border = new Border { BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3), Child = _scroll, Padding = new Thickness(1) };
        border.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        border.SetResourceReference(Border.BackgroundProperty, "B.Input");
        Children.Add(border);

        var side = new StackPanel { Margin = new Thickness(2, 0, 0, 0) };
        Button Arrow(string glyph, Action a, string tip)
        {
            var b = new Button { Content = glyph, Style = (Style)Application.Current.FindResource("GalleryArrow"), ToolTip = tip };
            b.Click += (_, _) => a();
            return b;
        }
        side.Children.Add(Arrow("", () => _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset - ItemWidth * 3), "Scroll left"));
        side.Children.Add(Arrow("", () => _scroll.ScrollToHorizontalOffset(_scroll.HorizontalOffset + ItemWidth * 3), "Scroll right"));
        side.Children.Add(Arrow("", () => _popup.IsOpen = true, "Show all"));
        SetColumn(side, 1);
        Children.Add(side);

        var pb = new Border { BorderThickness = new Thickness(1), Padding = new Thickness(6), CornerRadius = new CornerRadius(4) };
        pb.SetResourceReference(Border.BackgroundProperty, "B.Menu");
        pb.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        pb.Child = new ScrollViewer { Content = _all, MaxHeight = 420, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Width = 470 };
        _popup = new Popup { PlacementTarget = this, Placement = PlacementMode.Bottom, StaysOpen = false, AllowsTransparency = true, Child = pb };
    }

    public void SetItems(IEnumerable<Item> items)
    {
        _items = items.ToList();
        Rebuild();
    }

    public void SetImage(string key, ImageSource img)
    {
        var it = _items.FirstOrDefault(i => i.Key == key);
        if (it != null) it.Image = img;
    }

    public void Refresh() => Rebuild();

    void Rebuild()
    {
        _strip.Children.Clear();
        _all.Children.Clear();
        foreach (var it in _items)
        {
            _strip.Children.Add(MakeTile(it, false));
            _all.Children.Add(MakeTile(it, true));
        }
    }

    FrameworkElement MakeTile(Item it, bool inPopup)
    {
        var tb = new ToggleButton { Style = (Style)Application.Current.FindResource("GalleryItem"), Width = ItemWidth, ToolTip = it.Label, IsChecked = it.Key == _selected, Tag = it.Key };
        var sp = new StackPanel();
        if (it.Image != null)
            sp.Children.Add(new Image { Source = it.Image, Width = ItemWidth - 12, Height = (ItemWidth - 12) * 9 / 16, Stretch = Stretch.UniformToFill });
        else
        {
            var g = new TextBlock { Text = it.Glyph ?? "", FontFamily = Icons.Font, FontSize = 20, HorizontalAlignment = HorizontalAlignment.Center, Height = (ItemWidth - 12) * 9 / 16, Padding = new Thickness(0, 6, 0, 0) };
            g.SetResourceReference(TextBlock.ForegroundProperty, "B.GlyphBlue");
            sp.Children.Add(g);
        }
        var l = new TextBlock { Text = it.Label, FontSize = 10.5, TextTrimming = TextTrimming.CharacterEllipsis, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 2, 0, 0) };
        sp.Children.Add(l);
        tb.Content = sp;
        tb.Click += (_, _) =>
        {
            if (inPopup) _popup.IsOpen = false;
            Select(it.Key);
            Picked?.Invoke(it.Key);
        };
        tb.MouseEnter += (_, _) => Hovered?.Invoke(it.Key);
        tb.MouseLeave += (_, _) => Hovered?.Invoke(null);
        return tb;
    }

    public void Select(string key)
    {
        _selected = key;
        foreach (var p in new[] { _strip, (Panel)_all })
            foreach (ToggleButton t in p.Children) t.IsChecked = (string)t.Tag == key;
        // Bring selection into view.
        int idx = _items.FindIndex(i => i.Key == key);
        if (idx >= 0 && _scroll.ViewportWidth > 0)
        {
            double x = idx * ItemWidth;
            if (x < _scroll.HorizontalOffset || x + ItemWidth > _scroll.HorizontalOffset + _scroll.ViewportWidth)
                _scroll.ScrollToHorizontalOffset(Math.Max(0, x - ItemWidth));
        }
    }
}
