using Avalonia;
using System;
using VvCash.Services.Logging;
using VvCash.Services.Rendering;

namespace VvCash;

class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything else — see AppLogging.Start's own remarks for why this has to
        // come before BuildAvaloniaApp() rather than, say, App.OnFrameworkInitializationCompleted.
        AppLogging.Start();

        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        var builder = AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

        // Only applied when something actually has to change; on a current Windows the
        // platform default is left exactly as it was. See RenderingSelector for why the
        // older ones cannot be left to fall back on their own.
        var options = RenderingSelector.Select(
            Environment.GetEnvironmentVariable(RenderingSelector.OverrideVariable),
            Environment.OSVersion);
        if (options is not null) builder = builder.With(options);

        return builder;
    }
}
