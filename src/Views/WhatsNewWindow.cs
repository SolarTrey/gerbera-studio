using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace MovieMakerReborn.Views;

/// <summary>Shows the newest changelog section the first time the app runs after an update.</summary>
public class WhatsNewWindow : Window
{
    public static string AppVersion
    {
        get { var v = typeof(App).Assembly.GetName().Version; return v == null ? "1.0" : $"{v.Major}.{v.Minor}"; }
    }

    /// <summary>Reads the bundled CHANGELOG.md and returns the section for the current version.</summary>
    static string LatestSection()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/CHANGELOG.md"));
            if (res == null) return null;
            using var sr = new StreamReader(res.Stream);
            var text = sr.ReadToEnd().Replace("\r\n", "\n");
            var parts = Regex.Split(text, @"(?m)^## ");
            foreach (var part in parts)
                if (part.StartsWith(AppVersion)) return part;
            return parts.Length > 1 ? parts[1] : null;
        }
        catch { return null; }
    }

    static void AddInlines(InlineCollection inlines, string text)
    {
        // **bold** and *italic*
        foreach (var tok in Regex.Split(text, @"(\*\*[^*]+\*\*|\*[^*]+\*)"))
        {
            if (tok.Length == 0) continue;
            if (tok.StartsWith("**") && tok.EndsWith("**") && tok.Length > 4) inlines.Add(new Bold(new Run(tok[2..^2])));
            else if (tok.StartsWith("*") && tok.EndsWith("*") && tok.Length > 2) inlines.Add(new Italic(new Run(tok[1..^1])));
            else inlines.Add(new Run(tok));
        }
    }

    public WhatsNewWindow()
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "What's new";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Width = 560; Height = 600;
        ResizeMode = ResizeMode.CanResize;
        ThemeHelper.ApplyTitleBar(this);

        var root = new DockPanel { Margin = new Thickness(24, 18, 24, 16) };
        var head = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var logo = new Image { Width = 40, Height = 40, Margin = new Thickness(0, 0, 12, 0) };
        try { logo.Source = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/logo.png")); } catch { }
        head.Children.Add(logo);
        var title = new TextBlock { Text = $"What's new in Gerbera Studio {AppVersion}", FontSize = 20, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center };
        title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        head.Children.Add(title);
        DockPanel.SetDock(head, Dock.Top);
        root.Children.Add(head);

        var ok = new Button { Content = "Let's go", IsDefault = true, IsCancel = true, MinWidth = 100, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        ok.Click += (_, _) => Close();
        DockPanel.SetDock(ok, Dock.Bottom);
        root.Children.Add(ok);

        var body = new StackPanel();
        var section = LatestSection() ?? "Thanks for updating!";
        foreach (var raw in section.Split('\n').Skip(1))
        {
            var line = raw.TrimEnd();
            if (line.Length == 0) continue;
            if (line.StartsWith("### "))
            {
                var h = new TextBlock { Text = line[4..], FontSize = 15, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 12, 0, 4) };
                h.SetResourceReference(TextBlock.ForegroundProperty, "B.Accent");
                body.Children.Add(h);
                continue;
            }
            int indent = line.Length - line.TrimStart().Length;
            var t = line.TrimStart();
            bool bullet = t.StartsWith("- ");
            if (bullet) t = t[2..];
            var tb = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19, Margin = new Thickness(bullet ? 6 + indent * 7 : 0, 2, 0, 2) };
            tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
            if (bullet) tb.Inlines.Add(new Run(indent > 0 ? "◦  " : "•  "));
            AddInlines(tb.Inlines, t);
            body.Children.Add(tb);
        }
        root.Children.Add(new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Content = root;
    }
}
