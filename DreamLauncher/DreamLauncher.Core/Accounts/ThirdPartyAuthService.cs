using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public sealed class ThirdPartyAuthService : IThirdPartyAuthService
{
    private readonly HttpClient _httpClient;

    public ThirdPartyAuthService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public async Task<ThirdPartyAuthResult> SignInAsync(
        ThirdPartyLoginInput input,
        CancellationToken cancellationToken = default)
    {
        var apiRoot = NormalizeApiRoot(input.ApiRoot);
        var metadata = await GetMetadataAsync(apiRoot, cancellationToken);
        var clientToken = Guid.NewGuid().ToString("N");

        var payload = new
        {
            username = input.Username,
            password = input.Password,
            clientToken,
            requestUser = true,
            agent = new
            {
                name = "Minecraft",
                version = 1
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(apiRoot, "authserver/authenticate"),
            payload,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("皮肤站账号或密码不正确。");
        }

        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return CreateResult(apiRoot, metadata, document.RootElement);
    }

    public async Task<ThirdPartyAuthResult> RefreshAsync(
        ThirdPartyRefreshInput input,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(input.Account.AuthServerUrl))
        {
            throw new InvalidOperationException("皮肤站账号缺少认证服务器地址。");
        }

        var apiRoot = NormalizeApiRoot(input.Account.AuthServerUrl);
        if (await ValidateAsync(apiRoot, input.Tokens, cancellationToken))
        {
            input.Account.Status = AccountLoginStatus.Available;
            input.Account.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);
            input.Tokens.ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30);

            return new ThirdPartyAuthResult
            {
                Account = input.Account,
                Tokens = input.Tokens
            };
        }

        var payload = new
        {
            accessToken = input.Tokens.MinecraftAccessToken,
            clientToken = input.Tokens.ClientToken,
            requestUser = true
        };

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(apiRoot, "authserver/refresh"),
            payload,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.Forbidden ||
            response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw new InvalidOperationException("皮肤站登录已过期，请重新登录。");
        }

        response.EnsureSuccessStatusCode();

        var metadata = new AuthServerMetadata(
            input.Account.AuthServerName ?? new Uri(apiRoot.ToString()).Host,
            input.Account.AuthServerMetadataBase64 ?? "");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return CreateResult(apiRoot, metadata, document.RootElement);
    }

    private async Task<bool> ValidateAsync(
        Uri apiRoot,
        SecureAccountTokens tokens,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(tokens.MinecraftAccessToken))
        {
            return false;
        }

        var payload = new
        {
            accessToken = tokens.MinecraftAccessToken,
            clientToken = tokens.ClientToken
        };

        using var response = await _httpClient.PostAsJsonAsync(
            new Uri(apiRoot, "authserver/validate"),
            payload,
            cancellationToken);

        return response.StatusCode is HttpStatusCode.NoContent or HttpStatusCode.OK;
    }

    private async Task<AuthServerMetadata> GetMetadataAsync(
        Uri apiRoot,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(apiRoot, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new InvalidOperationException("皮肤站认证服务器没有返回元数据。");
        }

        var serverName = apiRoot.Host;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("meta", out var meta) &&
                meta.TryGetProperty("serverName", out var name) &&
                name.GetString() is { Length: > 0 } text)
            {
                serverName = text;
            }
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("皮肤站认证服务器元数据格式无效。");
        }

        return new AuthServerMetadata(
            serverName,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(json)));
    }

    private static ThirdPartyAuthResult CreateResult(
        Uri apiRoot,
        AuthServerMetadata metadata,
        JsonElement root)
    {
        var accessToken = ReadRequiredString(root, "accessToken");
        var clientToken = ReadRequiredString(root, "clientToken");
        var profile = ReadSelectedProfile(root);
        var uuid = ReadRequiredString(profile, "id");
        var name = ReadRequiredString(profile, "name");

        var account = new AccountMetadata
        {
            Id = $"thirdparty:{HashApiRoot(apiRoot)}:{uuid}",
            PlayerName = name,
            Uuid = uuid,
            Type = AccountType.ThirdParty,
            AuthServerUrl = apiRoot.ToString(),
            AuthServerName = metadata.ServerName,
            AuthServerMetadataBase64 = metadata.MetadataBase64,
            LastLoginUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30),
            Status = AccountLoginStatus.Available
        };

        var tokens = new SecureAccountTokens
        {
            MinecraftAccessToken = accessToken,
            ClientToken = clientToken,
            UserHash = uuid,
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddDays(30)
        };

        return new ThirdPartyAuthResult
        {
            Account = account,
            Tokens = tokens
        };
    }

    private static JsonElement ReadSelectedProfile(JsonElement root)
    {
        if (root.TryGetProperty("selectedProfile", out var selectedProfile) &&
            selectedProfile.ValueKind == JsonValueKind.Object)
        {
            return selectedProfile;
        }

        if (root.TryGetProperty("availableProfiles", out var availableProfiles) &&
            availableProfiles.ValueKind == JsonValueKind.Array &&
            availableProfiles.GetArrayLength() > 0)
        {
            return availableProfiles[0];
        }

        throw new InvalidOperationException("皮肤站账号没有可用角色。");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw new InvalidOperationException($"皮肤站响应缺少字段：{propertyName}。");
    }

    private static Uri NormalizeApiRoot(string value)
    {
        var uri = UrlSecurity.RequireHttps(value, nameof(value));
        var text = uri.ToString();
        if (!text.EndsWith("/", StringComparison.Ordinal))
        {
            text += "/";
        }

        return new Uri(text);
    }

    private static string HashApiRoot(Uri apiRoot)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(apiRoot.ToString().ToLowerInvariant()));
        return Convert.ToHexString(bytes)[..12].ToLowerInvariant();
    }

    private sealed record AuthServerMetadata(string ServerName, string MetadataBase64);
}
