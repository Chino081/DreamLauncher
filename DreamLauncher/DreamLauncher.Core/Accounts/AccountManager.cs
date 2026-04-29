using DreamLauncher.Core.Config;
using DreamLauncher.Models.Accounts;
using DreamLauncher.Models.Config;
using System.Security.Cryptography;
using System.Text;

namespace DreamLauncher.Core.Accounts;

public sealed class AccountManager
{
    private const string DefaultMicrosoftClientId = "00000000402b5328";
    private readonly LauncherConfigStore _configStore;
    private readonly ISecureTokenStore _tokenStore;
    private readonly IMicrosoftAuthService _authService;
    private readonly IThirdPartyAuthService _thirdPartyAuthService;

    public AccountManager(
        LauncherConfigStore configStore,
        ISecureTokenStore tokenStore,
        IMicrosoftAuthService authService,
        IThirdPartyAuthService thirdPartyAuthService)
    {
        _configStore = configStore;
        _tokenStore = tokenStore;
        _authService = authService;
        _thirdPartyAuthService = thirdPartyAuthService;
    }

    public async Task<IReadOnlyList<AccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        return config.Accounts;
    }

    public async Task<AccountMetadata?> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(config.DefaultAccountId)
            ? config.Accounts.FirstOrDefault()
            : config.Accounts.FirstOrDefault(account => account.Id == config.DefaultAccountId);
    }

    public async Task<AccountMetadata> AddMicrosoftAccountAsync(CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        var clientId = RequireMicrosoftClientId(config);
        var result = await _authService.SignInAsync(clientId, cancellationToken);

        await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
        UpsertAccount(config, result.Account);
        config.DefaultAccountId = result.Account.Id;
        await _configStore.SaveAsync(config, cancellationToken);

        return result.Account;
    }

    public async Task<AccountMetadata> AddOfflineAccountAsync(
        string playerName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeOfflinePlayerName(playerName);
        var account = CreateOfflineAccount(normalizedName);
        var config = await _configStore.LoadAsync(cancellationToken);

        UpsertAccount(config, account);
        config.DefaultAccountId = account.Id;
        await _configStore.SaveAsync(config, cancellationToken);
        return account;
    }

    public async Task<AccountMetadata> AddThirdPartyAccountAsync(
        ThirdPartyLoginInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _thirdPartyAuthService.SignInAsync(input, cancellationToken);
        var config = await _configStore.LoadAsync(cancellationToken);

        await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
        UpsertAccount(config, result.Account);
        config.DefaultAccountId = result.Account.Id;
        await _configStore.SaveAsync(config, cancellationToken);

        return result.Account;
    }

    public async Task<AccountMetadata> RefreshAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        var account = config.Accounts.FirstOrDefault(item => item.Id == accountId)
            ?? throw new InvalidOperationException("账号不存在。");

        if (IsOfflineAccount(account))
        {
            account.Status = AccountLoginStatus.Available;
            account.ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(10);
            await _configStore.SaveAsync(config, cancellationToken);
            return account;
        }

        var tokens = await _tokenStore.ReadAsync(accountId, cancellationToken)
            ?? throw new InvalidOperationException("账号令牌不存在，请重新登录。");

        if (IsThirdPartyAccount(account))
        {
            var result = await _thirdPartyAuthService.RefreshAsync(
                new ThirdPartyRefreshInput
                {
                    Account = account,
                    Tokens = tokens
                },
                cancellationToken);

            await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
            UpsertAccount(config, result.Account);
            await _configStore.SaveAsync(config, cancellationToken);
            return result.Account;
        }

        if (tokens.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            account.Status = AccountLoginStatus.Available;
            await _configStore.SaveAsync(config, cancellationToken);
            return account;
        }

        var clientId = RequireMicrosoftClientId(config);
        var refreshed = await _authService.RefreshAsync(
            clientId,
            new MicrosoftRefreshInput { RefreshToken = tokens.MicrosoftRefreshToken },
            cancellationToken);
        await _tokenStore.SaveAsync(refreshed.Account.Id, refreshed.Tokens, cancellationToken);

        UpsertAccount(config, refreshed.Account);
        if (config.DefaultAccountId == accountId)
        {
            config.DefaultAccountId = refreshed.Account.Id;
        }

        await _configStore.SaveAsync(config, cancellationToken);
        return refreshed.Account;
    }

    public async Task SetDefaultAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        if (config.Accounts.All(account => account.Id != accountId))
        {
            throw new InvalidOperationException("账号不存在。");
        }

        config.DefaultAccountId = accountId;
        await _configStore.SaveAsync(config, cancellationToken);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var config = await _configStore.LoadAsync(cancellationToken);
        config.Accounts.RemoveAll(account => account.Id == accountId);

        if (config.DefaultAccountId == accountId)
        {
            config.DefaultAccountId = config.Accounts.FirstOrDefault()?.Id;
        }

        await _tokenStore.DeleteAsync(accountId, cancellationToken);
        await _configStore.SaveAsync(config, cancellationToken);
    }

    public static bool IsOfflineAccount(AccountMetadata account)
    {
        return account.Type == AccountType.Offline ||
               account.Id.StartsWith("offline:", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsThirdPartyAccount(AccountMetadata account)
    {
        return account.Type == AccountType.ThirdParty ||
               account.Id.StartsWith("thirdparty:", StringComparison.OrdinalIgnoreCase);
    }

    public static SecureAccountTokens CreateOfflineTokens(AccountMetadata account)
    {
        return new SecureAccountTokens
        {
            MinecraftAccessToken = "offline",
            UserHash = account.Uuid,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(10)
        };
    }

    private static string RequireMicrosoftClientId(LauncherConfig config)
    {
        if (string.IsNullOrWhiteSpace(config.MicrosoftClientId))
        {
            return DefaultMicrosoftClientId;
        }

        return config.MicrosoftClientId;
    }

    private static void UpsertAccount(LauncherConfig config, AccountMetadata account)
    {
        config.Accounts.RemoveAll(existing => existing.Id == account.Id);
        config.Accounts.Add(account);
    }

    private static AccountMetadata CreateOfflineAccount(string playerName)
    {
        var uuid = CreateOfflineUuid(playerName);
        return new AccountMetadata
        {
            Id = "offline:" + playerName.ToLowerInvariant(),
            PlayerName = playerName,
            Uuid = uuid,
            Type = AccountType.Offline,
            LastLoginUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(10),
            Status = AccountLoginStatus.Available
        };
    }

    private static string NormalizeOfflinePlayerName(string playerName)
    {
        var value = playerName.Trim();
        if (value.Length is < 3 or > 16 || value.Any(ch => !char.IsLetterOrDigit(ch) && ch != '_'))
        {
            throw new ArgumentException("离线测试账号名称只能使用 3-16 位英文、数字或下划线。", nameof(playerName));
        }

        return value;
    }

    private static string CreateOfflineUuid(string playerName)
    {
        var bytes = Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName);
        var hash = MD5.HashData(bytes);

        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
