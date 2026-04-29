namespace DreamLauncher.Core.Accounts;

public interface IThirdPartyAuthService
{
    Task<ThirdPartyAuthResult> SignInAsync(
        ThirdPartyLoginInput input,
        CancellationToken cancellationToken = default);

    Task<ThirdPartyAuthResult> RefreshAsync(
        ThirdPartyRefreshInput input,
        CancellationToken cancellationToken = default);
}
