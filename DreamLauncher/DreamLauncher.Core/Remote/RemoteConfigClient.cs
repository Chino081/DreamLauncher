using System.Text.Json;
using DreamLauncher.Core.Security;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Announcements;
using DreamLauncher.Models.Clients;
using DreamLauncher.Models.Java;
using DreamLauncher.Models.Updates;

namespace DreamLauncher.Core.Remote;

public sealed class RemoteConfigClient
{
    private readonly HttpClient _httpClient;

    public RemoteConfigClient(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public Task<RemoteClientsManifest> GetClientsManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<RemoteClientsManifest>(url, cancellationToken);
    }

    public Task<JavaRuntimesManifest> GetJavaRuntimesAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<JavaRuntimesManifest>(url, cancellationToken);
    }

    public Task<AnnouncementDocument> GetAnnouncementAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<AnnouncementDocument>(url, cancellationToken);
    }

    public Task<LauncherUpdateManifest> GetLauncherUpdateManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<LauncherUpdateManifest>(url, cancellationToken);
    }

    public Task<ClientFileManifest> GetClientFileManifestAsync(
        string url,
        CancellationToken cancellationToken = default)
    {
        return GetJsonAsync<ClientFileManifest>(url, cancellationToken);
    }

    private async Task<T> GetJsonAsync<T>(string url, CancellationToken cancellationToken)
    {
        await using var stream = await OpenConfigStreamAsync(url, cancellationToken);
        return await JsonSerializer.DeserializeAsync<T>(stream, LauncherJson.Options, cancellationToken)
            ?? throw new InvalidDataException("远程配置内容为空或格式无效。");
    }

    private async Task<Stream> OpenConfigStreamAsync(string source, CancellationToken cancellationToken)
    {
        if (TryGetLocalJsonPath(source, out var localPath))
        {
            return File.OpenRead(localPath);
        }

        var uri = UrlSecurity.RequireHttps(source, nameof(source));
        return await _httpClient.GetStreamAsync(uri, cancellationToken);
    }

    private static bool TryGetLocalJsonPath(string source, out string localPath)
    {
        localPath = "";

        if (string.IsNullOrWhiteSpace(source))
        {
            return false;
        }

        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.IsFile)
        {
            localPath = uri.LocalPath;
            return IsJsonFile(localPath) && File.Exists(localPath);
        }

        var expanded = Environment.ExpandEnvironmentVariables(source.Trim('"', ' '));
        if (!Path.IsPathRooted(expanded))
        {
            return false;
        }

        localPath = Path.GetFullPath(expanded);
        return IsJsonFile(localPath) && File.Exists(localPath);
    }

    private static bool IsJsonFile(string path)
    {
        return string.Equals(Path.GetExtension(path), ".json", StringComparison.OrdinalIgnoreCase);
    }
}
