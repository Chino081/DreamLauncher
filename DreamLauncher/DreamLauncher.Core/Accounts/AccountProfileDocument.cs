using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public sealed class AccountProfileDocument
{
    public string? DefaultAccountId { get; set; }

    public List<AccountMetadata> Accounts { get; set; } = [];
}
