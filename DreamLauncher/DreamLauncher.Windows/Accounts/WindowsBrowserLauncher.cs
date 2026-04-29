using System.Diagnostics;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Windows.Accounts;

public sealed class WindowsBrowserLauncher : IBrowserLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Process.Start(new ProcessStartInfo
        {
            FileName = uri.ToString(),
            UseShellExecute = true
        });

        return Task.CompletedTask;
    }
}
