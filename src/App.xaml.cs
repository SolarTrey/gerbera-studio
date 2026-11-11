using System.Windows;
using System.Windows.Threading;
using MovieMakerReborn.Views;

namespace MovieMakerReborn;

public partial class App : Application
{
    public static string CurrentTheme { get; private set; } = "dark";

    public static void SetTheme(string theme)
    {
        theme = theme is "light" or "evening" ? theme : "dark";
        CurrentTheme = theme;
        var dicts = Current.Resources.MergedDictionaries;
        string file = theme switch { "light" => "Light", "evening" => "Evening", _ => "Dark" };
        dicts[0] = new ResourceDictionary { Source = new Uri($"Themes/{file}.xaml", UriKind.Relative) };
        foreach (Window w in Current.Windows) ThemeHelper.ApplyTitleBar(w);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, ex) =>
        {
            try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "MovieMakerReborn-crash.txt"), ex.ExceptionObject?.ToString() ?? ""); } catch { }
        };
        var w = new MainWindow();
        MainWindow = w;
        w.Show();
        if (e.Args.Length >= 3 && e.Args[0] == "--shots") { var a = e.Args; w.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() => w.StartShots(a[1], a[2]))); }
        else if (e.Args.Length > 0) w.OpenFromCommandLine(e.Args);
    }

    bool _inHandler;

    void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        try { File.AppendAllText(Path.Combine(Path.GetTempPath(), "MovieMakerReborn-errors.txt"), DateTime.Now + "\n" + e.Exception + "\n\n"); } catch { }
        if (_inHandler) return;
        _inHandler = true;
        try { Dialogs.Info(MainWindow, "Something went wrong", e.Exception.Message); }
        catch { MessageBox.Show(e.Exception.Message, "Gerbera Studio", MessageBoxButton.OK, MessageBoxImage.Error); }
        finally { _inHandler = false; }
    }
}
