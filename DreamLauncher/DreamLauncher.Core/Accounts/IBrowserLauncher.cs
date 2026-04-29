namespace DreamLauncher.Core.Accounts;

public interface IBrowserLauncher
{
    Task OpenAsync(Uri uri, CancellationToken cancellationToken = default);
}
