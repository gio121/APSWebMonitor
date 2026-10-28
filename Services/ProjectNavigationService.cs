using Microsoft.JSInterop;

namespace ApsMonitor.Services;

// Full reloads create a fresh circuit with services bound to the selected database.
// Await our own interop call so a lost tab/connection doesn't become an unhandled
// exception inside RemoteNavigationManager's fire-and-forget navigation task.
public sealed class ProjectNavigationService(IJSRuntime js)
{
    public async Task<bool> OpenAsync(bool replace, CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        try
        {
            timeout.Token.ThrowIfCancellationRequested();
            await js.InvokeVoidAsync("apsNavigation.openProjects", timeout.Token, replace);
            return true;
        }
        catch (JSDisconnectedException) { return false; }
        catch (OperationCanceledException) { return false; }
    }
}
