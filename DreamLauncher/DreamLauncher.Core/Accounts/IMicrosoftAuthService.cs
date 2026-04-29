namespace DreamLauncher.Core.Accounts;

public interface IMicrosoftAuthService
{
    Task<MicrosoftAuthResult> SignInAsync(string clientId, CancellationToken cancellationToken = default);

    Task<MicrosoftAuthResult> RefreshAsync(
        string clientId,
        MicrosoftRefreshInput refreshInput,
        CancellationToken cancellationToken = default);
}
