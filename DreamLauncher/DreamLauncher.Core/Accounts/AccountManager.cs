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
    private readonly AccountProfileStore _accountProfileStore;
    private readonly ISecureTokenStore _tokenStore;
    private readonly IMicrosoftAuthService _authService;
    private readonly IThirdPartyAuthService _thirdPartyAuthService;

    public AccountManager(
        LauncherConfigStore configStore,
        AccountProfileStore accountProfileStore,
        ISecureTokenStore tokenStore,
        IMicrosoftAuthService authService,
        IThirdPartyAuthService thirdPartyAuthService)
    {
        _configStore = configStore;
        _accountProfileStore = accountProfileStore;
        _tokenStore = tokenStore;
        _authService = authService;
        _thirdPartyAuthService = thirdPartyAuthService;
    }

    public async Task<IReadOnlyList<AccountMetadata>> GetAccountsAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        return document.Accounts.ToArray();
    }

    public async Task<AccountMetadata?> GetDefaultAccountAsync(CancellationToken cancellationToken = default)
    {
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        return string.IsNullOrWhiteSpace(document.DefaultAccountId)
            ? document.Accounts.FirstOrDefault()
            : document.Accounts.FirstOrDefault(account => account.Id == document.DefaultAccountId);
    }

    public async Task<AccountMetadata> AddMicrosoftAccountAsync(CancellationToken cancellationToken = default)
    {
        var result = await _authService.SignInAsync(DefaultMicrosoftClientId, cancellationToken);

        await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        UpsertAccount(document, result.Account);
        document.DefaultAccountId = result.Account.Id;
        await _accountProfileStore.SaveAsync(document, cancellationToken);

        return result.Account;
    }

    public async Task<AccountMetadata> AddOfflineAccountAsync(
        string playerName,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = NormalizeOfflinePlayerName(playerName);
        var account = CreateOfflineAccount(normalizedName);
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);

        UpsertAccount(document, account);
        document.DefaultAccountId = account.Id;
        await _accountProfileStore.SaveAsync(document, cancellationToken);
        return account;
    }

    public async Task<AccountMetadata> AddThirdPartyAccountAsync(
        ThirdPartyLoginInput input,
        CancellationToken cancellationToken = default)
    {
        var result = await _thirdPartyAuthService.SignInAsync(input, cancellationToken);
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);

        await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
        UpsertAccount(document, result.Account);
        document.DefaultAccountId = result.Account.Id;
        await _accountProfileStore.SaveAsync(document, cancellationToken);

        return result.Account;
    }

    public async Task<AccountMetadata> RefreshAccountAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        var account = document.Accounts.FirstOrDefault(item => item.Id == accountId)
            ?? throw new InvalidOperationException("账号不存在。");

        if (IsOfflineAccount(account))
        {
            account.Status = AccountLoginStatus.Available;
            account.ExpiresAtUtc = DateTimeOffset.UtcNow.AddYears(10);
            await _accountProfileStore.SaveAsync(document, cancellationToken);
            return account;
        }

        if (account.Status == AccountLoginStatus.Invalid)
        {
            return account;
        }

        var tokens = await _tokenStore.ReadAsync(accountId, cancellationToken);
        if (tokens is null)
        {
            account.Status = AccountLoginStatus.Invalid;
            await _accountProfileStore.SaveAsync(document, cancellationToken);
            return account;
        }

        if (IsThirdPartyAccount(account))
        {
            try
            {
                var result = await _thirdPartyAuthService.RefreshAsync(
                    new ThirdPartyRefreshInput
                    {
                        Account = account,
                        Tokens = tokens
                    },
                    cancellationToken);

                await _tokenStore.SaveAsync(result.Account.Id, result.Tokens, cancellationToken);
                UpsertAccount(document, result.Account);
                await _accountProfileStore.SaveAsync(document, cancellationToken);
                return result.Account;
            }
            catch
            {
                account.Status = AccountLoginStatus.Invalid;
                await _tokenStore.DeleteAsync(accountId, cancellationToken);
                await _accountProfileStore.SaveAsync(document, cancellationToken);
                return account;
            }
        }

        if (tokens.ExpiresAtUtc > DateTimeOffset.UtcNow.AddMinutes(5))
        {
            account.Status = AccountLoginStatus.Available;
            await _accountProfileStore.SaveAsync(document, cancellationToken);
            return account;
        }

        try
        {
            var refreshed = await _authService.RefreshAsync(
                DefaultMicrosoftClientId,
                new MicrosoftRefreshInput { RefreshToken = tokens.MicrosoftRefreshToken },
                cancellationToken);
            await _tokenStore.SaveAsync(refreshed.Account.Id, refreshed.Tokens, cancellationToken);

            UpsertAccount(document, refreshed.Account);
            if (document.DefaultAccountId == accountId)
            {
                document.DefaultAccountId = refreshed.Account.Id;
            }

            await _accountProfileStore.SaveAsync(document, cancellationToken);
            return refreshed.Account;
        }
        catch
        {
            account.Status = AccountLoginStatus.Invalid;
            await _tokenStore.DeleteAsync(accountId, cancellationToken);
            await _accountProfileStore.SaveAsync(document, cancellationToken);
            return account;
        }
    }

    public async Task SetDefaultAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        if (document.Accounts.All(account => account.Id != accountId))
        {
            throw new InvalidOperationException("账号不存在。");
        }

        document.DefaultAccountId = accountId;
        await _accountProfileStore.SaveAsync(document, cancellationToken);
    }

    public async Task RemoveAccountAsync(string accountId, CancellationToken cancellationToken = default)
    {
        var document = await LoadProfilesWithMigrationAsync(cancellationToken);
        document.Accounts.RemoveAll(account => account.Id == accountId);

        if (document.DefaultAccountId == accountId)
        {
            document.DefaultAccountId = document.Accounts.FirstOrDefault()?.Id;
        }

        await _tokenStore.DeleteAsync(accountId, cancellationToken);
        await _accountProfileStore.SaveAsync(document, cancellationToken);
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

    private async Task<AccountProfileDocument> LoadProfilesWithMigrationAsync(CancellationToken cancellationToken)
    {
        var document = await _accountProfileStore.LoadAsync(cancellationToken);
        var config = await _configStore.LoadAsync(cancellationToken);
        if (config.Accounts.Count == 0 && string.IsNullOrWhiteSpace(config.DefaultAccountId))
        {
            return document;
        }

        if (document.Accounts.Count == 0)
        {
            document.Accounts.AddRange(config.Accounts);
            document.DefaultAccountId = config.DefaultAccountId;
            await _accountProfileStore.SaveAsync(document, cancellationToken);
        }

        config.Accounts.Clear();
        config.DefaultAccountId = null;
        await _configStore.SaveAsync(config, cancellationToken);

        return document;
    }

    private static void UpsertAccount(AccountProfileDocument document, AccountMetadata account)
    {
        document.Accounts.RemoveAll(existing => existing.Id == account.Id);
        document.Accounts.Add(account);
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
