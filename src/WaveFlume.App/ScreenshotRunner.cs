using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using WaveFlume.App.Services;
using WaveFlume.App.ViewModels;
using WaveFlume.Plc;

namespace WaveFlume.App;

/// <summary>
/// <c>WaveFlume.App.exe --screenshots &lt;dir&gt;</c>: runs the golden wave on the simulator with a
/// throw-away settings file in &lt;dir&gt;, then saves every tab in both themes as PNG (spec §11.0 deliverable).
/// </summary>
public static class ScreenshotRunner
{
    public const double DemoAccelLimitMs2 = 5.0;
    public const double DemoRunSeconds = 12;

    public static async Task RunAsync(MainWindow window, IServiceProvider services, string dir)
    {
        Directory.CreateDirectory(dir);
        var app = services.GetRequiredService<AppState>();
        var flume = app.Settings.Flume;
        app.Save(app.Settings with
        {
            Flume = flume with { Limits = flume.Limits with { MaxAccelMs2 = DemoAccelLimitMs2 } },
            Connection = new ConnectionSettings { Protocol = PlcProtocolType.Simulator },
            DataDirectory = Path.Combine(Path.GetFullPath(dir), "data"),
            Theme = ThemeKind.Light,
        });

        var main = services.GetRequiredService<MainViewModel>();
        main.Settings.RevertCommand.Execute(null); // reload the form from the demo settings
        var gen = main.WaveGeneration;
        gen.NumWaves = 40;
        gen.AngleDeg = 10;
        await gen.CalculateCommand.ExecuteAsync(null);

        var pad = main.WavePaddle;
        await pad.ConnectCommand.ExecuteAsync(null);
        await Settle();
        await pad.EnableServosCommand.ExecuteAsync(null);
        await Settle();
        await pad.HomeCommand.ExecuteAsync(null);
        await Settle();
        await pad.SendCommand.ExecuteAsync(null);
        await Settle();
        await pad.StartCommand.ExecuteAsync(null);
        window.Tabs.SelectedIndex = 1; // scopes only draw while visible; this also measures FPS
        await Task.Delay(TimeSpan.FromSeconds(DemoRunSeconds));
        var timing = services.GetRequiredService<PlcSession>().Telemetry?.Timing;
        Serilog.Log.Information("Screenshot run: scope {Fps}; telemetry {Timing}",
            (FindByName(window, "FpsText") as System.Windows.Controls.TextBlock)?.Text, timing);
        services.GetRequiredService<AlarmService>().Raise(WaveFlume.Core.AlertSeverity.Warning, WaveFlume.Core.AlertSources.WaveProbe,
            "Demo alarm for the screenshot: probe 3 near range limit.");

        foreach (var theme in new[] { ThemeKind.Light, ThemeKind.Dark })
        {
            app.SavePreference(app.Settings with { Theme = theme });
            for (int tab = 0; tab < window.Tabs.Items.Count; tab++)
            {
                window.Tabs.SelectedIndex = tab;
                string name = ((System.Windows.Controls.TabItem)window.Tabs.Items[tab]).Header.ToString()!.Replace("_", "").Replace(" ", "");
                if (tab == 1)
                {
                    await Capture(window, Path.Combine(dir, $"{theme}_{tab + 1}_{name}_Board.png"));
                    SelectTab(window, "PlotTabs", 1);
                    await Capture(window, Path.Combine(dir, $"{theme}_{tab + 1}_{name}_Probes.png"));
                    continue;
                }
                await Capture(window, Path.Combine(dir, $"{theme}_{tab + 1}_{name}.png"));
            }
        }
        app.SavePreference(app.Settings with { Theme = ThemeKind.Light });
        await pad.SmoothStopCommand.ExecuteAsync(null);
        await CaptureWaveTypes(window, gen, dir);
        await services.GetRequiredService<PlcSession>().DisconnectAsync();
    }

    private static void SelectTab(MainWindow window, string name, int index)
    {
        if (FindByName(window, name) is System.Windows.Controls.TabControl tabs) tabs.SelectedIndex = index;
    }

    /// <summary>Phase 4 wave types on the Wave Generation tab (light theme).</summary>
    private static async Task CaptureWaveTypes(MainWindow window, WaveGenerationViewModel gen, string dir)
    {
        window.Tabs.SelectedIndex = 0;
        gen.Type = WaveFlume.Core.WaveType.Random;
        (gen.PeriodS, gen.NumWaves, gen.RampS, gen.AngleDeg) = (1.5, 100, 5, 0);
        await gen.CalculateCommand.ExecuteAsync(null);
        SelectTab(window, "GenPlotTabs", 3);
        await Capture(window, Path.Combine(dir, "Light_5_Random_Spectrum.png"));
        SelectTab(window, "GenPlotTabs", 1);
        await Capture(window, Path.Combine(dir, "Light_6_Random_WaveBoard.png"));

        gen.Type = WaveFlume.Core.WaveType.Focusing;
        (gen.WaveHeightM, gen.NumWaves, gen.FocusXM, gen.FocusTimeS) = (0.06, 40, 10, 30);
        await gen.CalculateCommand.ExecuteAsync(null);
        SelectTab(window, "GenPlotTabs", 1);
        await Capture(window, Path.Combine(dir, "Light_7_Focusing_WaveBoard.png"));

        gen.Type = WaveFlume.Core.WaveType.Solitary;
        gen.WaveHeightM = 0.03;
        await gen.CalculateCommand.ExecuteAsync(null);
        SelectTab(window, "GenPlotTabs", 2);
        await Capture(window, Path.Combine(dir, "Light_8_Solitary_Elevation.png"));
    }

    private static FrameworkElement? FindByName(DependencyObject root, string name)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement fe && fe.Name == name && fe.IsVisible) return fe;
            if (FindByName(child, name) is { } found) return found;
        }
        return null;
    }

    private static async Task Settle()
    {
        await Task.Delay(300);
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
    }

    private static async Task Capture(Window window, string path)
    {
        await Task.Delay(700); // let plot timers draw at least once
        await Dispatcher.Yield(DispatcherPriority.ApplicationIdle);
        var root = (FrameworkElement)window.Content;
        var dpi = VisualTreeHelper.GetDpi(window);
        var bmp = new RenderTargetBitmap((int)(root.ActualWidth * dpi.DpiScaleX), (int)(root.ActualHeight * dpi.DpiScaleY),
            dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle((Brush)window.FindResource("WindowBg"), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
            dc.DrawRectangle(new VisualBrush(root), null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
        }
        bmp.Render(visual);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        await using var f = File.Create(path);
        png.Save(f);
    }
}
