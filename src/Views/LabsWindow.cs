using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MovieMakerReborn.Views;

/// <summary>Gerbera Labs: experimental and just-for-fun extras that can be switched on and off.</summary>
public class LabsWindow : Window
{
    readonly Settings _s;

    public LabsWindow(Settings s)
    {
        _s = s;
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Gerbera Labs";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.Height;
        Width = 460;
        ThemeHelper.ApplyTitleBar(this);

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 6) };
        var flask = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 10, 0) };
        flask.SetResourceReference(Image.SourceProperty, "I.Flask");
        head.Children.Add(flask);
        var title = new TextBlock { Text = "Labs", FontSize = 22, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        head.Children.Add(title);
        root.Children.Add(head);
        var intro = new TextBlock { Text = "Experimental and just-for-fun extras. More will show up here over time.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) };
        intro.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
        root.Children.Add(intro);

        var preview = new Button { Content = "Preview", Padding = new Thickness(12, 3, 12, 3), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        preview.Click += (_, _) =>
        {
            var sp = new SplashWindow(App.CurrentTheme);
            sp.Completed += sp.FadeOutAndClose;
            sp.Show();
        };
        root.Children.Add(Lab("Nostalgic loading screen",
            "Shows a classic glassy loading screen for about 10 seconds each time Gerbera Studio starts.",
            _s.LabSplash, v => { _s.LabSplash = v; _s.Save(); }, preview));
        root.Children.Add(Lab("Bubbling sounds",
            "Plays a little bubbling sound when you open Labs.",
            _s.LabSounds, v => { _s.LabSounds = v; _s.Save(); }, null));

        var close = new Button { Content = "Close", IsDefault = true, IsCancel = true, MinWidth = 90, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        close.Click += (_, _) => Close();
        root.Children.Add(close);
        Content = root;
    }

    static FrameworkElement Lab(string name, string desc, bool on, Action<bool> changed, FrameworkElement extra)
    {
        var card = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1), Padding = new Thickness(14, 12, 14, 12), Margin = new Thickness(0, 0, 0, 10) };
        card.SetResourceReference(Border.BackgroundProperty, "B.Ribbon");
        card.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        var sp = new StackPanel();
        var cb = new CheckBox { Content = new TextBlock { Text = name, FontSize = 14, FontWeight = FontWeights.SemiBold }, IsChecked = on };
        cb.Click += (_, _) => changed(cb.IsChecked == true);
        sp.Children.Add(cb);
        var d = new TextBlock { Text = desc, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(21, 4, 0, 0), FontSize = 12 };
        d.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
        sp.Children.Add(d);
        if (extra != null) { extra.Margin = new Thickness(21, 8, 0, 0); sp.Children.Add(extra); }
        card.Child = sp;
        return card;
    }
}
