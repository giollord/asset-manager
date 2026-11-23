using AssetManager.Shared.Services;
using AssetManager.Core.Platform;
using Bunit;
using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using MudBlazor.Services;

namespace AssetManager.Tests.Ui;

public class UiActionsTests : BunitContext
{
    public UiActionsTests()
    {
        Services.AddMudServices();
        Services.AddLogging();
        Services.AddScoped<UiActions>();
    }

    [Fact]
    public async Task Web_stub_surfaces_not_supported_snackbar()
    {
        var actions = Services.GetRequiredService<UiActions>();
        // Same behavior as the Web host's UnsupportedPlatformServices stub.
        Task<string?> Pick() => throw new PlatformFeatureNotSupportedException("The folder picker");

        var ok = await actions.RunAsync(Pick);

        Assert.Null(ok);
        var shown = Assert.Single(Services.GetRequiredService<ISnackbar>().ShownSnackbars);
        Assert.Equal(Severity.Warning, shown.Severity);
        Assert.Contains("not supported", shown.Message);
    }
}
