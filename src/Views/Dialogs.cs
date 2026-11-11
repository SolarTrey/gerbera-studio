using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;

namespace MovieMakerReborn.Views;

public static class ThemeHelper
{
    [DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    public static bool IsDark => App.CurrentTheme != "light";

    /// <summary>Make the native title bar match the theme (Windows 10 2004+ / 11).</summary>
    public static void ApplyTitleBar(Window w)
    {
        void Apply()
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int v = IsDark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref v, sizeof(int)) != 0) DwmSetWindowAttribute(h, 19, ref v, sizeof(int));
            }
            catch { }
        }
        if (new WindowInteropHelper(w).Handle != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();
    }
}

/// <summary>Theme-matching message boxes.</summary>
public static class Dialogs
{
    public static int Ask(Window owner, string title, string message, params string[] buttons)
    {
        var w = new Window
        {
            Title = title,
            Owner = owner,
            SizeToContent = SizeToContent.WidthAndHeight,
            Style = (Style)Application.Current.FindResource("DialogWindow"),
            MinWidth = 380,
            MaxWidth = 640,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        if (owner == null || !owner.IsVisible) { w.WindowStartupLocation = WindowStartupLocation.CenterScreen; w.Owner = null; }
        ThemeHelper.ApplyTitleBar(w);
        int result = -1;
        var root = new DockPanel { Margin = new Thickness(22, 18, 22, 16) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 18, 0, 0) };
        DockPanel.SetDock(btns, Dock.Bottom);
        for (int i = 0; i < buttons.Length; i++)
        {
            int idx = i;
            var b = new Button { Content = buttons[i], Margin = new Thickness(8, 0, 0, 0), MinWidth = 84, IsDefault = i == 0, IsCancel = i == buttons.Length - 1 && buttons.Length > 1 };
            b.Click += (_, _) => { result = idx; w.Close(); };
            btns.Children.Add(b);
        }
        root.Children.Add(btns);
        var tb = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13, LineHeight = 19 };
        tb.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        root.Children.Add(tb);
        w.Content = root;
        w.ShowDialog();
        return result;
    }

    public static void Info(Window owner, string title, string message) => Ask(owner, title, message, "OK");
}
