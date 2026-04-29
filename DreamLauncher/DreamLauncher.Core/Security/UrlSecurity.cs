namespace DreamLauncher.Core.Security;

public static class UrlSecurity
{
    public static Uri RequireHttps(string? url, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            throw new ArgumentException("远程地址不能为空。", fieldName);
        }

        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new ArgumentException("远程地址格式无效。", fieldName);
        }

        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("远程下载与配置地址必须使用 HTTPS。");
        }

        return uri;
    }
}
