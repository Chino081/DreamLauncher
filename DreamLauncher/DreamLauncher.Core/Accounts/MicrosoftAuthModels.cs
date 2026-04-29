using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public sealed class MicrosoftAuthResult
{
    public required AccountMetadata Account { get; init; }

    public required SecureAccountTokens Tokens { get; init; }
}

public sealed class MicrosoftRefreshInput
{
    public required string RefreshToken { get; init; }
}
