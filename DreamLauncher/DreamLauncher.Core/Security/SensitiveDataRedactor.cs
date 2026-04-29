using System.Text.RegularExpressions;

namespace DreamLauncher.Core.Security;

public static partial class SensitiveDataRedactor
{
    public static string Redact(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var redacted = TokenQueryRegex().Replace(value, "$1=<redacted>");
        redacted = TokenJsonRegex().Replace(redacted, "$1\"<redacted>\"");
        return redacted;
    }

    [GeneratedRegex(@"(?i)\b(access_token|refresh_token|id_token|minecraft_access_token|xsts_token)=([^&\s]+)")]
    private static partial Regex TokenQueryRegex();

    [GeneratedRegex(@"(?i)(""(?:access_token|refresh_token|id_token|minecraftAccessToken|xstsToken|minecraft_access_token|xsts_token)""\s*:\s*)"".*?""")]
    private static partial Regex TokenJsonRegex();
}
