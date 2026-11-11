using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using MovieMakerReborn.Engine;
using MovieMakerReborn.Model;

namespace MovieMakerReborn.Views;

/// <summary>Progress for saving a movie (and other long jobs), with a live preview of rendered frames.</summary>
public class ProgressWindow : Window
{
    readonly TextBlock _title = new() { FontSize = 16, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 10) };
    readonly Image _preview = new() { Width = 384, Height = 216, Stretch = Stretch.Uniform, Margin = new Thickness(0, 0, 0, 12) };
    readonly Border _previewBox;
    readonly ProgressBar _bar = new() { Height = 8, Minimum = 0, Maximum = 1, Margin = new Thickness(0, 0, 0, 8) };
    readonly TextBlock _detail = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap };
    readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
    readonly Button _cancel = new() { Content = "Cancel", MinWidth = 90 };
    public event Action CancelRequested;
    bool _finished;

    public ProgressWindow()
    {
        Style = (Style)Application.Current.FindResource("DialogWindow");
        Title = "Gerbera Studio";
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SizeToContent = SizeToContent.WidthAndHeight;
        ThemeHelper.ApplyTitleBar(this);
        _detail.SetResourceReference(TextBlock.ForegroundProperty, "B.Dim");
        _title.SetResourceReference(TextBlock.ForegroundProperty, "B.Text");
        var root = new StackPanel { Margin = new Thickness(24, 20, 24, 18), Width = 400 };
        root.Children.Add(_title);
        _previewBox = new Border { Background = Brushes.Black, CornerRadius = new CornerRadius(3), Child = _preview, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 12) };
        _preview.Margin = new Thickness(0);
        root.Children.Add(_previewBox);
        root.Children.Add(_bar);
        root.Children.Add(_detail);
        _buttons.Children.Add(_cancel);
        root.Children.Add(_buttons);
        Content = root;
        _cancel.Click += (_, _) => { _cancel.IsEnabled = false; _detail.Text = "Cancelling…"; CancelRequested?.Invoke(); };
        Closing += (_, e) => { if (!_finished && _cancel.IsEnabled) { e.Cancel = true; _cancel.IsEnabled = false; CancelRequested?.Invoke(); } };
    }

    public void SetMode(string title, bool indeterminate)
    {
        Dispatcher.Invoke(() => { _title.Text = title; _bar.IsIndeterminate = indeterminate; });
    }

    public void SetProgress(double v, string detail)
    {
        Dispatcher.BeginInvoke(() => { _bar.IsIndeterminate = false; _bar.Value = v; _detail.Text = detail; });
    }

    public new void Close()
    {
        _finished = true;
        base.Close();
    }

    static string Fmt(TimeSpan t) => t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    public async void RunExport(Project project, ExportSettings s)
    {
        var cts = new CancellationTokenSource();
        CancelRequested += () => cts.Cancel();
        _title.Text = "Saving your movie…";
        _previewBox.Visibility = Visibility.Visible;
        _preview.Height = Math.Min(216, 384.0 * s.Height / s.Width);
        _detail.Text = $"{s.Width}×{s.Height} · {(double)s.FpsNum / s.FpsDen:0.###} fps · {s.Codec.Label}\nStarting…";
        Owner?.SetValue(IsEnabledProperty, false);
        Show();
        WriteableBitmap wb = null;
        var progress = new Progress<ExportProgress>(p =>
        {
            _bar.Value = (double)p.Frame / p.Total;
            _detail.Text = $"{s.Width}×{s.Height} · {(double)s.FpsNum / s.FpsDen:0.###} fps · {s.Codec.Label}\n" +
                           $"Frame {p.Frame:N0} of {p.Total:N0}  ·  {p.Fps:0.0} fps  ·  {Fmt(p.Remaining)} remaining";
            if (p.Preview != null)
            {
                if (wb == null || wb.PixelWidth != p.Preview.W || wb.PixelHeight != p.Preview.H)
                {
                    wb = new WriteableBitmap(p.Preview.W, p.Preview.H, 96, 96, PixelFormats.Bgra32, null);
                    _preview.Source = wb;
                }
                wb.WritePixels(new Int32Rect(0, 0, p.Preview.W, p.Preview.H), p.Preview.D, p.Preview.W * 4, 0);
            }
            Title = $"{(int)(100.0 * p.Frame / p.Total)}% - Saving movie";
        });
        var sw = Stopwatch.StartNew();
        bool ok = false; string error = null;
        try { ok = await Task.Run(() => Exporter.Run(project, s, progress, cts.Token)); }
        catch (Exception ex) { error = ex.Message; }
        Owner?.SetValue(IsEnabledProperty, true);
        _finished = true;
        if (!ok)
        {
            if (error != null)
            {
                Close();
                Dialogs.Info(Owner, "The movie couldn't be saved", error.Length > 1500 ? error[..1500] + "…" : error);
            }
            else Close();
            return;
        }
        // Done
        Title = "Gerbera Studio";
        _title.Text = "Your movie has been saved.";
        _bar.Value = 1;
        long size = 0; try { size = new FileInfo(s.OutputPath).Length; } catch { }
        _detail.Text = $"{Path.GetFileName(s.OutputPath)}\n{size / 1048576.0:0.0} MB · took {Fmt(sw.Elapsed)}";
        _buttons.Children.Clear();
        var play = new Button { Content = "Play", MinWidth = 90, IsDefault = true };
        var folder = new Button { Content = "Open folder", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0) };
        var close = new Button { Content = "Close", MinWidth = 90, Margin = new Thickness(8, 0, 0, 0), IsCancel = true };
        play.Click += (_, _) => { try { Process.Start(new ProcessStartInfo(s.OutputPath) { UseShellExecute = true }); } catch { } Close(); };
        folder.Click += (_, _) => { try { Process.Start("explorer.exe", $"/select,\"{s.OutputPath}\""); } catch { } Close(); };
        close.Click += (_, _) => Close();
        _buttons.Children.Add(play); _buttons.Children.Add(folder); _buttons.Children.Add(close);
        Activate();
    }
}
