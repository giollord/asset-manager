using AssetManager.AI;
using AssetManager.Core;
using AssetManager.Data;
using AssetManager.Processing;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor.Services;

namespace AssetManager.Shared;

/// <summary>Options passed by a host to <see cref="AssetManagerServiceCollectionExtensions.AddAssetManager"/>.</summary>
public sealed class AssetManagerHostOptions
{
    /// <summary>Overrides the app settings JSON path (tests, dev hosts).</summary>
    public string? SettingsPath { get; set; }
}

public static class AssetManagerServiceCollectionExtensions
{
    /// <summary>The one entry point both hosts call: wires Core, Data, Processing, AI and UI services.</summary>
    /// <summary>How long a toast stays visible, in milliseconds.</summary>
    public const int ToastDuration = 15_000;

    public static IServiceCollection AddAssetManager(this IServiceCollection services, Action<AssetManagerHostOptions>? configure = null)
    {
        var options = new AssetManagerHostOptions();
        configure?.Invoke(options);
        services.AddAssetManagerCore(options.SettingsPath);
        services.AddAssetManagerData();
        services.AddAssetManagerProcessing();
        services.AddAssetManagerAi();
        services.AddMudServices(mud =>
        {
            // Toasts close on their own after 15 s (hovering keeps one open); none waits for a click.
            mud.SnackbarConfiguration.VisibleStateDuration = ToastDuration;
            mud.SnackbarConfiguration.RequireInteraction = false;
            mud.SnackbarConfiguration.ShowCloseIcon = true;
        });
        services.AddScoped<Services.UiActions>();
        services.AddScoped<Services.PreviewCache>();
        services.AddSingleton<Services.WebViewModelRenderer>();
        services.AddSingleton<Processing.Models.IModelRenderer>(sp => sp.GetRequiredService<Services.WebViewModelRenderer>());
        return services;
    }
}
