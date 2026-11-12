using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Soundstage.UI;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var window = new MainWindow();

        // Check for a newer release in the background; the window shows a link if one exists.
        UpdateChecker.Check();

        // Developer aid: "--snapshot file.png" renders the window to an image and exits.
        int i = Array.IndexOf(e.Args, "--snapshot");
        // "--tall" grows the window so a long page fits in one snapshot.
        if (i >= 0 && e.Args.Contains("--tall")) { window.Width = 1260; window.Height = 1500; }
        // "--tray" (used by the start-with-Windows entry) starts hidden in the tray.
        bool startInTray = e.Args.Contains("--tray") && i < 0;
        if (startInTray) window.Hide();
        else window.Show();

        if (i >= 0 && i + 1 < e.Args.Length)
        {
            string path = e.Args[i + 1];
            if (e.Args.Contains("--headphones")) window.PreviewHeadphones();
            if (e.Args.Contains("--settings")) window.PreviewSettings();
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                SaveSnapshot(window, path);
                Shutdown();
            };
            timer.Start();
        }
    }

    static void SaveSnapshot(Window window, string path)
    {
        var content = (FrameworkElement)window.Content;
        double w = content.ActualWidth, h = content.ActualHeight;
        var dpi = VisualTreeHelper.GetDpi(window);

        // The window background is the system backdrop, which an offscreen render
        // cannot see, so paint a flat stand-in behind the content.
        bool dark = window.TryFindResource("TextFillColorPrimaryBrush") is SolidColorBrush b && b.Color.R > 128;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(dark ? Color.FromRgb(0x00, 0x00, 0x00) : Color.FromRgb(0xF3, 0xF3, 0xF3)),
                             null, new Rect(0, 0, w, h));
            // Pin the viewbox to the content's own rectangle: a visual brush otherwise
            // uses the descendant bounds, and the glow behind the title bar has a
            // negative top margin that would shift the whole capture down.
            var brush = new VisualBrush(content)
            {
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, w, h),
                Stretch = Stretch.Fill,
            };
            dc.DrawRectangle(brush, null, new Rect(0, 0, w, h));
        }
        var bmp = new RenderTargetBitmap((int)(w * dpi.DpiScaleX), (int)(h * dpi.DpiScaleY),
                                         dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        bmp.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bmp));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
