using AssetManager.Core.Platform;
using Microsoft.Extensions.Logging;
using MudBlazor;

namespace AssetManager.Shared.Services;

/// <summary>
/// Runs a UI-triggered action and reports its outcome through the snackbar, so pages do not repeat
/// try/catch + notification code. Unsupported platform features and errors become messages.
/// </summary>
public sealed class UiActions(ISnackbar snackbar, ILogger<UiActions> logger)
{
    public async Task<bool> RunAsync(Func<Task> action, string? successMessage = null)
    {
        try
        {
            await action();
            if (successMessage is not null) snackbar.Add(successMessage, Severity.Success);
            return true;
        }
        catch (PlatformFeatureNotSupportedException ex)
        {
            snackbar.Add(ex.Message, Severity.Warning);
        }
        catch (OperationCanceledException)
        {
            snackbar.Add("Cancelled.", Severity.Info);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "UI action failed");
            snackbar.Add(ex.Message, Severity.Error);
        }
        return false;
    }

    public async Task<T?> RunAsync<T>(Func<Task<T>> action, string? successMessage = null)
    {
        T? result = default;
        await RunAsync(async () => { result = await action(); }, successMessage);
        return result;
    }
}
