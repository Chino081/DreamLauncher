using System.Diagnostics;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Avalonia.Accounts;

public sealed class AvaloniaBrowserLauncher : IBrowserLauncher
{
    public Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Open(uri.ToString());
        return Task.CompletedTask;
    }

    public static void Open(string url)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            return;
        }

        var fileName = OperatingSystem.IsMacOS() ? "open" : "xdg-open";
        Process.Start(new ProcessStartInfo(fileName, url) { UseShellExecute = false });
    }
}
