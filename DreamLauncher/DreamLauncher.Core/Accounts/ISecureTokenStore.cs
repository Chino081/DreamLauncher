using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public interface ISecureTokenStore
{
    Task SaveAsync(string accountId, SecureAccountTokens tokens, CancellationToken cancellationToken = default);

    Task<SecureAccountTokens?> ReadAsync(string accountId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string accountId, CancellationToken cancellationToken = default);
}
