using AssetManager.Core;
using AssetManager.Core.Jobs;
using AssetManager.Core.Platform;
using AssetManager.Shared;
using AssetManager.Tests.Fixtures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssetManager.Tests;

/// <summary>The real service graph both hosts build: every service resolves, without cycles or deadlocks.</summary>
public class CompositionTests
{
    [Fact]
    public async Task Host_services_resolve()
    {
        using var dir = FixtureBuilder.TempDir();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IFolderPicker, NoPlatform>();
        services.AddSingleton<IFilePicker, NoPlatform>();
        services.AddSingleton<IClipboardFileService, NoPlatform>();
        services.AddSingleton<IShellService, NoPlatform>();
        services.AddAssetManager(o => o.SettingsPath = Path.Combine(dir.Path, "settings.json"));
        await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });

        // Factory registrations escape cycle detection and deadlock instead, so resolution runs under a timeout.
        var resolve = Task.Run(() =>
        {
            var queue = provider.GetRequiredService<IJobQueue>();
            var handlers = provider.GetServices<IJobHandler>().ToList();
            provider.GetRequiredService<AppStartup>();
            provider.GetRequiredService<AssetManager.AI.Import.ImportService>();
            provider.GetRequiredService<AssetManager.Processing.Import.ReimportService>();
            provider.GetRequiredService<FileDropBroker>();
            return (queue, handlers);
        }, TestContext.Current.CancellationToken);
        Assert.True(await Task.WhenAny(resolve, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)) == resolve,
            "Resolving the host services did not finish (dependency cycle through a factory registration?)");
        var (_, handlers) = await resolve;
        Assert.Contains(handlers, h => h.Type == "import.commit");
        Assert.Contains(handlers, h => h.Type == "assets.reimport");
        Assert.Contains(handlers, h => h.Type == "ai.enrich");
        Assert.Equal(handlers.Count, handlers.Select(h => h.Type).Distinct().Count());
    }

    private sealed class NoPlatform : IFolderPicker, IFilePicker, IClipboardFileService, IShellService
    {
        public Task<string?> PickFolderAsync(CancellationToken ct = default) => throw new PlatformFeatureNotSupportedException("folders");
        public Task<IReadOnlyList<string>> PickFilesAsync(bool multiple, CancellationToken ct = default) => throw new PlatformFeatureNotSupportedException("files");
        public Task SetFilesAsync(IReadOnlyList<string> paths, CancellationToken ct = default) => Task.CompletedTask;
        public Task OpenAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
        public Task RevealAsync(string path, CancellationToken ct = default) => Task.CompletedTask;
    }
}
