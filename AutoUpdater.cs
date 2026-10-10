using Velopack;
using Velopack.Sources;

namespace Baka;

/// Downloads published releases quietly. Velopack applies a staged update at the next launch, so an
/// update never interrupts a page while the user is browsing.
static class AutoUpdater
{
    const string Repository = "https://github.com/OldcarryG/Baka-browser";
    static int _checking;

    public static async Task CheckAndDownloadAsync()
    {
        if (Interlocked.Exchange(ref _checking, 1) != 0) return;
        using var processLock = new Semaphore(1, 1, @"Local\BakaBrowser.UpdateCheck");
        var ownsLock = false;
        try
        {
            ownsLock = processLock.WaitOne(0);
            if (!ownsLock) return;

            var manager = new UpdateManager(new GithubSource(Repository, null, false));
            if (!manager.IsInstalled || manager.UpdatePendingRestart is not null) return;
            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is not null)
                await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            // Network, GitHub rate limits, and concurrent installs must not interrupt browsing.
            System.Diagnostics.Debug.WriteLine($"Background update check failed: {ex.Message}");
        }
        finally
        {
            if (ownsLock) processLock.Release();
            Interlocked.Exchange(ref _checking, 0);
        }
    }
}
