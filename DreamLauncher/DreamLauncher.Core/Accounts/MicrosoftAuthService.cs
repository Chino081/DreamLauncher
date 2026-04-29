using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Core.Accounts;

public sealed class MicrosoftAuthService : IMicrosoftAuthService
{
    private const string DeviceCodeUrl = "https://login.live.com/oauth20_connect.srf";
    private const string TokenUrl = "https://login.live.com/oauth20_token.srf";
    private const string OAuthScope = "XboxLive.signin offline_access";
    private const string DeviceCodeScope = "service::user.auth.xboxlive.com::MBI_SSL offline_access";
    private const string DesktopRedirectUrl = "https://login.live.com/oauth20_desktop.srf";
    private const string RemoteConnectUrl = "https://login.live.com/oauth20_remoteconnect.srf";
    private readonly HttpClient _httpClient;
    private readonly IBrowserLauncher _browserLauncher;
    private readonly IMicrosoftDeviceCodePresenter? _deviceCodePresenter;

    public MicrosoftAuthService(
        IBrowserLauncher browserLauncher,
        IMicrosoftDeviceCodePresenter? deviceCodePresenter = null,
        HttpClient? httpClient = null)
    {
        _browserLauncher = browserLauncher;
        _deviceCodePresenter = deviceCodePresenter;
        _httpClient = httpClient ?? new HttpClient();
    }

    public async Task<MicrosoftAuthResult> SignInAsync(
        string clientId,
        CancellationToken cancellationToken = default)
    {
        var deviceCode = await RequestDeviceCodeAsync(clientId, cancellationToken);
        using var loginCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var tokenTask = PollDeviceTokenAsync(clientId, deviceCode, loginCancellation.Token);

        if (_deviceCodePresenter is null)
        {
            await _browserLauncher.OpenAsync(new Uri(deviceCode.VerificationUri), cancellationToken);
            throw new InvalidOperationException($"请在浏览器中输入 Microsoft 登录代码：{deviceCode.UserCode}");
        }

        var confirmed = await _deviceCodePresenter.ShowAsync(
            deviceCode,
            tokenTask,
            () => loginCancellation.Cancel(),
            cancellationToken);

        if (!confirmed)
        {
            if (tokenTask.IsFaulted || tokenTask.IsCanceled)
            {
                await tokenTask;
            }

            throw new OperationCanceledException("Microsoft 登录已取消。", cancellationToken);
        }

        var microsoftTokens = await tokenTask;
        return await CompleteMinecraftLoginAsync(microsoftTokens, cancellationToken);
    }

    public async Task<MicrosoftAuthResult> RefreshAsync(
        string clientId,
        MicrosoftRefreshInput refreshInput,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshInput.RefreshToken))
        {
            throw new InvalidOperationException("账号刷新令牌不存在，请重新登录。");
        }

        var microsoftTokens = await RefreshMicrosoftTokenAsync(clientId, refreshInput.RefreshToken, cancellationToken);
        return await CompleteMinecraftLoginAsync(microsoftTokens, cancellationToken);
    }

    private async Task<MicrosoftAuthResult> CompleteMinecraftLoginAsync(
        MicrosoftTokens microsoftTokens,
        CancellationToken cancellationToken)
    {
        var xbox = await AuthenticateXboxLiveAsync(microsoftTokens.AccessToken, cancellationToken);
        var xsts = await AuthorizeXstsAsync(xbox.Token, cancellationToken);
        var minecraft = await AuthenticateMinecraftAsync(xsts.UserHash, xsts.Token, cancellationToken);
        var profile = await GetMinecraftProfileAsync(minecraft.AccessToken, cancellationToken);

        var expiresAt = DateTimeOffset.UtcNow.AddSeconds(Math.Max(60, minecraft.ExpiresInSeconds));
        var account = new AccountMetadata
        {
            Id = profile.Uuid,
            PlayerName = profile.Name,
            Uuid = profile.Uuid,
            LastLoginUtc = DateTimeOffset.UtcNow,
            ExpiresAtUtc = expiresAt,
            Status = AccountLoginStatus.Available
        };

        var tokens = new SecureAccountTokens
        {
            MicrosoftAccessToken = microsoftTokens.AccessToken,
            MicrosoftRefreshToken = microsoftTokens.RefreshToken,
            XboxLiveToken = xbox.Token,
            XstsToken = xsts.Token,
            MinecraftAccessToken = minecraft.AccessToken,
            UserHash = xsts.UserHash,
            ExpiresAtUtc = expiresAt
        };

        return new MicrosoftAuthResult
        {
            Account = account,
            Tokens = tokens
        };
    }

    private static Uri BuildAuthorizeUri(string clientId, Uri redirectUri, string challenge, string state)
    {
        var builder = new UriBuilder("https://login.live.com/oauth20_authorize.srf");
        builder.Query =
            $"client_id={Uri.EscapeDataString(clientId)}" +
            "&response_type=code" +
            $"&redirect_uri={Uri.EscapeDataString(redirectUri.ToString())}" +
            $"&scope={Uri.EscapeDataString(OAuthScope)}" +
            $"&state={Uri.EscapeDataString(state)}" +
            "&code_challenge_method=S256" +
            $"&code_challenge={Uri.EscapeDataString(challenge)}";

        return builder.Uri;
    }

    private async Task<MicrosoftTokens> ExchangeCodeAsync(
        string clientId,
        Uri redirectUri,
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = redirectUri.ToString(),
            ["code_verifier"] = verifier
        });

        using var response = await _httpClient.PostAsync("https://login.live.com/oauth20_token.srf", request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ReadMicrosoftTokens(document.RootElement);
    }

    private async Task<MicrosoftTokens> RefreshMicrosoftTokenAsync(
        string clientId,
        string refreshToken,
        CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = refreshToken,
            ["redirect_uri"] = DesktopRedirectUrl
        });

        using var response = await _httpClient.PostAsync(TokenUrl, request, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);

        return ReadMicrosoftTokens(document.RootElement);
    }

    private async Task<XboxTokenResult> AuthenticateXboxLiveAsync(
        string microsoftAccessToken,
        CancellationToken cancellationToken)
    {
        Exception? lastException = null;

        foreach (var rpsTicket in new[] { "d=" + microsoftAccessToken, "t=" + microsoftAccessToken, microsoftAccessToken })
        {
            try
            {
                var payload = new
                {
                    Properties = new
                    {
                        AuthMethod = "RPS",
                        SiteName = "user.auth.xboxlive.com",
                        RpsTicket = rpsTicket
                    },
                    RelyingParty = "http://auth.xboxlive.com",
                    TokenType = "JWT"
                };

                using var response = await PostXboxJsonAsync(
                    "https://user.auth.xboxlive.com/user/authenticate",
                    payload,
                    cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (!response.IsSuccessStatusCode)
                {
                    throw CreateServiceException("Xbox Live 授权失败", response, body);
                }

                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;

                return new XboxTokenResult(
                    ReadRequiredString(root, "Token"),
                    ReadUserHash(root));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastException = ex;
            }
        }

        var detail = lastException?.Message;
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? "Xbox Live 授权失败，请检查账号是否可用、系统时间是否正确，或稍后重试。"
                : "Xbox Live 授权失败，请检查账号是否可用、系统时间是否正确，或稍后重试。" + Environment.NewLine + detail,
            lastException);
    }

    private async Task<MicrosoftDeviceCodeInfo> RequestDeviceCodeAsync(
        string clientId,
        CancellationToken cancellationToken)
    {
        using var request = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = DeviceCodeScope,
            ["response_type"] = "device_code"
        });

        using var response = await _httpClient.PostAsync(DeviceCodeUrl, request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException("Microsoft 设备码申请失败。" + Environment.NewLine + body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new MicrosoftDeviceCodeInfo
        {
            DeviceCode = ReadRequiredString(root, "device_code"),
            UserCode = ReadRequiredString(root, "user_code"),
            VerificationUri = ReadOptionalString(root, "verification_uri") ?? RemoteConnectUrl,
            ExpiresIn = root.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 900,
            Interval = Math.Max(5, root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5)
        };
    }

    private async Task<MicrosoftTokens> PollDeviceTokenAsync(
        string clientId,
        MicrosoftDeviceCodeInfo deviceCode,
        CancellationToken cancellationToken)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(deviceCode.ExpiresIn);
        var intervalSeconds = deviceCode.Interval;

        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(intervalSeconds), cancellationToken);

            using var request = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = clientId,
                ["device_code"] = deviceCode.DeviceCode,
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code"
            });

            using var response = await _httpClient.PostAsync(TokenUrl, request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            using var document = string.IsNullOrWhiteSpace(body)
                ? JsonDocument.Parse("{}")
                : JsonDocument.Parse(body);
            var root = document.RootElement;

            if (response.IsSuccessStatusCode)
            {
                return ReadMicrosoftTokens(root);
            }

            var error = ReadOptionalString(root, "error");
            switch (error)
            {
                case "authorization_pending":
                    continue;
                case "slow_down":
                    intervalSeconds += 5;
                    continue;
                case "authorization_declined":
                    throw new InvalidOperationException("Microsoft 登录已被取消。");
                case "expired_token":
                    throw new InvalidOperationException("Microsoft 登录代码已过期，请重试。");
                default:
                    throw new InvalidOperationException("Microsoft 设备码登录失败。" + Environment.NewLine + body);
            }
        }

        throw new TimeoutException("Microsoft 登录等待超时。");
    }

    private async Task<XboxTokenResult> AuthorizeXstsAsync(
        string xboxToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xboxToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };

        using var response = await PostXboxJsonAsync(
            "https://xsts.auth.xboxlive.com/xsts/authorize",
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (response.StatusCode == HttpStatusCode.Unauthorized)
        {
            throw CreateServiceException("Xbox/XSTS 授权失败，请确认该 Microsoft 账号可登录 Minecraft", response, body);
        }

        if (!response.IsSuccessStatusCode)
        {
            throw CreateServiceException("Xbox/XSTS 授权失败", response, body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new XboxTokenResult(
            ReadRequiredString(root, "Token"),
            ReadUserHash(root));
    }

    private async Task<MinecraftTokenResult> AuthenticateMinecraftAsync(
        string userHash,
        string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            identityToken = $"XBL3.0 x={userHash};{xstsToken}"
        };

        using var response = await _httpClient.PostAsJsonAsync(
            "https://api.minecraftservices.com/authentication/login_with_xbox",
            payload,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw CreateServiceException("Minecraft 服务登录失败", response, body);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;

        return new MinecraftTokenResult(
            ReadRequiredString(root, "access_token"),
            root.TryGetProperty("expires_in", out var expiresIn) ? expiresIn.GetInt32() : 3600);
    }

    private async Task<MinecraftProfileResult> GetMinecraftProfileAsync(
        string minecraftAccessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new("Bearer", minecraftAccessToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        return new MinecraftProfileResult(
            ReadRequiredString(root, "id"),
            ReadRequiredString(root, "name"));
    }

    private async Task<HttpResponseMessage> PostXboxJsonAsync(
        string url,
        object payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };

        return await _httpClient.SendAsync(request, cancellationToken);
    }

    private static InvalidOperationException CreateServiceException(
        string message,
        HttpResponseMessage response,
        string responseBody)
    {
        var status = $"HTTP {(int)response.StatusCode} {response.ReasonPhrase}".Trim();
        var detail = FormatServiceErrorDetail(responseBody);
        return new InvalidOperationException(string.IsNullOrWhiteSpace(detail)
            ? $"{message}（{status}）。"
            : $"{message}（{status}）。{detail}");
    }

    private static string FormatServiceErrorDetail(string responseBody)
    {
        var body = SensitiveDataRedactor.Redact(responseBody);
        if (string.IsNullOrWhiteSpace(body))
        {
            return "";
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var parts = new List<string>();

            AddJsonText(parts, root, "error");
            AddJsonText(parts, root, "error_description");
            AddJsonText(parts, root, "message");
            AddJsonText(parts, root, "Message");

            if (root.TryGetProperty("XErr", out var xerr))
            {
                parts.Add($"XErr={xerr}");
            }

            return parts.Count > 0
                ? string.Join("；", parts)
                : Truncate(body, 420);
        }
        catch (JsonException)
        {
            return Truncate(body, 420);
        }
    }

    private static void AddJsonText(ICollection<string> parts, JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String)
        {
            var text = value.GetString();
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text);
            }
        }
    }

    private static string Truncate(string value, int maxLength)
    {
        return value.Length <= maxLength ? value : value[..maxLength] + "...";
    }

    private static MicrosoftTokens ReadMicrosoftTokens(JsonElement root)
    {
        return new MicrosoftTokens(
            ReadRequiredString(root, "access_token"),
            ReadRequiredString(root, "refresh_token"));
    }

    private static string ReadUserHash(JsonElement root)
    {
        if (root.TryGetProperty("DisplayClaims", out var displayClaims) &&
            displayClaims.TryGetProperty("xui", out var xui) &&
            xui.ValueKind == JsonValueKind.Array &&
            xui.GetArrayLength() > 0 &&
            xui[0].TryGetProperty("uhs", out var uhs) &&
            uhs.GetString() is { Length: > 0 } value)
        {
            return value;
        }

        throw new InvalidOperationException("Xbox 授权响应缺少用户哈希。");
    }

    private static string ReadRequiredString(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var value) &&
            value.GetString() is { Length: > 0 } text)
        {
            return text;
        }

        throw new InvalidOperationException($"授权响应缺少字段：{propertyName}。");
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string CreateCodeVerifier()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Base64UrlEncode(bytes);
    }

    private static string CreateCodeChallenge(string verifier)
    {
        var bytes = SHA256.HashData(Encoding.ASCII.GetBytes(verifier));
        return Base64UrlEncode(bytes);
    }

    private static string Base64UrlEncode(ReadOnlySpan<byte> bytes)
    {
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private sealed class LocalOAuthListener : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly string _expectedState;

        public LocalOAuthListener(Uri redirectUri, string expectedState)
        {
            _expectedState = expectedState;
            _listener.Prefixes.Add(redirectUri.ToString());
        }

        public void Start()
        {
            _listener.Start();
        }

        public async Task<string> WaitForCodeAsync(CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                try
                {
                    _listener.Stop();
                }
                catch
                {
                    // Cancellation path only.
                }
            });

            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("Microsoft 登录已取消。", ex, cancellationToken);
            }

            var query = context.Request.QueryString;
            var code = query["code"];
            var state = query["state"];
            var error = query["error"];

            await WriteBrowserResponseAsync(context.Response, string.IsNullOrWhiteSpace(error));

            if (!string.IsNullOrWhiteSpace(error))
            {
                throw new InvalidOperationException("Microsoft 授权被拒绝或失败。");
            }

            if (!string.Equals(state, _expectedState, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Microsoft 授权状态校验失败。");
            }

            if (string.IsNullOrWhiteSpace(code))
            {
                throw new InvalidOperationException("Microsoft 授权回调缺少授权码。");
            }

            return code;
        }

        public void Dispose()
        {
            _listener.Close();
        }

        private static async Task WriteBrowserResponseAsync(HttpListenerResponse response, bool success)
        {
            var message = success
                ? "登录成功，可以回到启动器。"
                : "登录失败，请回到启动器重试。";
            var bytes = Encoding.UTF8.GetBytes("<!doctype html><meta charset=\"utf-8\"><title>DreamLauncher</title><body>" + WebUtility.HtmlEncode(message) + "</body>");
            response.ContentType = "text/html; charset=utf-8";
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes);
            response.Close();
        }
    }

    private sealed record MicrosoftTokens(string AccessToken, string RefreshToken);

    private sealed record XboxTokenResult(string Token, string UserHash);

    private sealed record MinecraftTokenResult(string AccessToken, int ExpiresInSeconds);

    private sealed record MinecraftProfileResult(string Uuid, string Name);
}
