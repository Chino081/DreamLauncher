namespace DreamLauncher.Models.Accounts;

public sealed class AccountMetadata
{
    public string Id { get; set; } = "";

    public string PlayerName { get; set; } = "";

    public string Uuid { get; set; } = "";

    public string? AvatarUrl { get; set; }

    public AccountType Type { get; set; } = AccountType.Microsoft;

    public string? AuthServerUrl { get; set; }

    public string? AuthServerName { get; set; }

    public string? AuthServerMetadataBase64 { get; set; }

    public DateTimeOffset LastLoginUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;

    public AccountLoginStatus Status { get; set; } = AccountLoginStatus.Unknown;
}
