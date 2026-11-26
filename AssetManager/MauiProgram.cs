using Microsoft.Extensions.Logging;
using Microsoft.Maui.LifecycleEvents;
using AssetManager.Core.Settings;
using AssetManager.Shared;
using Serilog;

namespace AssetManager;

public static class MauiProgram
{
    public static MauiApp CreateMauiApp()
    {
        // First: everything below (logs, settings, catalog, models, browser profile) lives in the data folder.
        var location = AppDataLocation.Resolve(AppContext.BaseDirectory);
        AppPaths.Use(location);
        Environment.SetEnvironmentVariable("WEBVIEW2_USER_DATA_FOLDER", AppPaths.WebView);

        var builder = MauiApp.CreateBuilder();
        builder
            .UseMauiApp<App>()
            .ConfigureFonts(fonts =>
            {
                fonts.AddFont("OpenSans-Regular.ttf", "OpenSansRegular");
            })
            .ConfigureLifecycleEvents(events => events.AddWindows(windows =>
                windows.OnWindowCreated(window =>
                    IPlatformApplication.Current!.Services.GetRequiredService<Services.WindowsWindowChrome>().Attach(window))));

        // Rolling log file under %LOCALAPPDATA%\AssetManager\logs (also read by tools when debugging).
        var logger = new LoggerConfiguration()
#if DEBUG
            .MinimumLevel.Debug()
#else
            .MinimumLevel.Information()
#endif
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .WriteTo.File(Path.Combine(AppPaths.Logs, "app-.log"), rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 7, shared: true)
            .CreateLogger();
        builder.Logging.AddSerilog(logger, dispose: true);
        AppDomain.CurrentDomain.UnhandledException += (_, e) => logger.Fatal(e.ExceptionObject as Exception, "Unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) => logger.Error(e.Exception, "Unobserved task exception");

        builder.Services.AddSingleton<AssetManager.Core.Platform.IFilePicker, Services.WindowsFilePicker>();
        builder.Services.AddSingleton<AssetManager.Core.Platform.IFolderPicker, Services.WindowsFolderPicker>();
        builder.Services.AddSingleton<AssetManager.Core.Platform.IShellService, Services.WindowsShellService>();
        builder.Services.AddSingleton<AssetManager.Core.Platform.IClipboardFileService, Services.WindowsClipboardFileService>();
        builder.Services.AddSingleton<Services.WindowsDragOutService>();
        builder.Services.AddSingleton<AssetManager.Core.Platform.IDragOutService>(sp => sp.GetRequiredService<Services.WindowsDragOutService>());
        builder.Services.AddSingleton<Services.WindowsWindowChrome>();
        builder.Services.AddSingleton<AssetManager.Core.Platform.IWindowChrome>(sp => sp.GetRequiredService<Services.WindowsWindowChrome>());
        builder.Services.AddAssetManager();
        builder.Services.AddMauiBlazorWebView();

#if DEBUG
        // Expose the WebView2 DevTools protocol on localhost so tools/cdp.mjs (or chrome://inspect)
        // can inspect the running app. Override the port with ASSETMANAGER_CDP_PORT; set it to 0 to disable.
        var cdpPort = Environment.GetEnvironmentVariable("ASSETMANAGER_CDP_PORT") ?? "9333";
        var browserArgs = new List<string>();
        if (cdpPort != "0") browserArgs.Add($"--remote-debugging-port={cdpPort}");

        // ASSETMANAGER_OFFLINE=1 simulates having no network (spec "Offline operation") without unplugging anything:
        // every request of .NET and of the WebView goes to a proxy that does not exist. Loopback stays reachable
        // for the local llama-server; the app's own pages are served in-process and are not affected.
        if (Environment.GetEnvironmentVariable("ASSETMANAGER_OFFLINE") == "1")
        {
            const string deadProxy = "http://127.0.0.1:9";
            System.Net.Http.HttpClient.DefaultProxy = new System.Net.WebProxy(deadProxy, BypassOnLocal: true, BypassList: [@"^127\.0\.0\.1$", "^localhost$"]);
            browserArgs.Add($"--proxy-server={deadProxy}");
            logger.Warning("Offline test mode: network requests are blocked (ASSETMANAGER_OFFLINE=1)");
        }
        if (browserArgs.Count > 0)
            Environment.SetEnvironmentVariable("WEBVIEW2_ADDITIONAL_BROWSER_ARGUMENTS", string.Join(" ", browserArgs));
        builder.Services.AddBlazorWebViewDeveloperTools();
        builder.Logging.AddDebug();
#endif

        var app = builder.Build();
        logger.Information("Asset Manager {Version} started; data folder {Root} ({Mode:l}{Reason:l})", AppVersion.Current,
            location.Root, location.Mode.ToString(), location.Reason is null ? "" : ": " + location.Reason);
        return app;
    }
}
