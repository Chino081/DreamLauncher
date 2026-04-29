namespace DreamLauncher.Models.Accounts;

public sealed class SecureAccountTokens
{
    public string MicrosoftAccessToken { get; set; } = "";

    public string MicrosoftRefreshToken { get; set; } = "";

    public string XboxLiveToken { get; set; } = "";

    public string XstsToken { get; set; } = "";

    public string MinecraftAccessToken { get; set; } = "";

    public string ClientToken { get; set; } = "";

    public string UserHash { get; set; } = "";

    public DateTimeOffset ExpiresAtUtc { get; set; } = DateTimeOffset.MinValue;
}
