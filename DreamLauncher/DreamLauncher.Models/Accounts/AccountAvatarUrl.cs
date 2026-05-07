namespace DreamLauncher.Models.Accounts;

public static class AccountAvatarUrl
{
    public static string? FromAccount(AccountMetadata? account, int size = 96)
    {
        return FromAccountCandidates(account, size).FirstOrDefault();
    }

    public static IEnumerable<string> FromAccountCandidates(AccountMetadata? account, int size = 96)
    {
        if (account is null)
        {
            yield break;
        }

        if (!string.IsNullOrWhiteSpace(account.AvatarUrl))
        {
            yield return account.AvatarUrl.Trim();
        }

        if (account.Type != AccountType.Microsoft ||
            string.IsNullOrWhiteSpace(account.Uuid))
        {
            yield break;
        }

        var normalizedUuid = account.Uuid.Trim().Replace("-", "", StringComparison.Ordinal);
        if (normalizedUuid.Length == 0)
        {
            yield break;
        }

        var avatarSize = Math.Clamp(size, 16, 512);
        var escapedUuid = Uri.EscapeDataString(normalizedUuid);
        yield return $"https://crafatar.com/avatars/{escapedUuid}?size={avatarSize}&overlay";
        yield return $"https://mc-heads.net/avatar/{escapedUuid}/{avatarSize}";
        yield return $"https://minotar.net/avatar/{escapedUuid}/{avatarSize}.png";
        yield return $"https://visage.surgeplay.com/face/{avatarSize}/{escapedUuid}";

        if (!string.IsNullOrWhiteSpace(account.PlayerName))
        {
            var escapedName = Uri.EscapeDataString(account.PlayerName.Trim());
            yield return $"https://mc-heads.net/avatar/{escapedName}/{avatarSize}";
            yield return $"https://minotar.net/avatar/{escapedName}/{avatarSize}.png";
        }
    }
}
