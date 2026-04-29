using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public sealed class ThirdPartyLoginInput
{
    public required string ApiRoot { get; init; }

    public required string Username { get; init; }

    public required string Password { get; init; }
}

public sealed class ThirdPartyRefreshInput
{
    public required AccountMetadata Account { get; init; }

    public required SecureAccountTokens Tokens { get; init; }
}

public sealed class ThirdPartyAuthResult
{
    public required AccountMetadata Account { get; init; }

    public required SecureAccountTokens Tokens { get; init; }
}
