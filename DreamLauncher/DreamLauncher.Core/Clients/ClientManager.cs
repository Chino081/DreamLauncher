using System.Text.Json;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Clients;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Clients;

public sealed class ClientManager
{
    private readonly LauncherPaths _paths;
    private readonly HttpDownloadService _downloadService;
    private readonly SafeZipExtractor _extractor;

    public ClientManager(
        LauncherPaths paths,
        HttpDownloadService downloadService,
        SafeZipExtractor extractor)
    {
        _paths = paths;
        _downloadService = downloadService;
        _extractor = extractor;
    }

    public async Task<IReadOnlyList<ClientInstallation>> GetInstallationsAsync(
        IEnumerable<ClientDefinition> remoteClients,
        LauncherConfig config,
        CancellationToken cancellationToken = default)
    {
        var installations = new List<ClientInstallation>();

        foreach (var client in remoteClients)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localConfig = await ReadLocalConfigAsync(client, cancellationToken);
            var settings = config.ClientSettings.GetValueOrDefault(client.Id);

            installations.Add(new ClientInstallation
            {
                Definition = client,
                LocalConfig = localConfig,
                InstallPath = _paths.GetClientDirectory(client),
                MemoryMb = settings?.MemoryMb ?? client.DefaultMemoryMb,
                JavaPath = settings?.JavaPath,
                Status = GetStatus(client, localConfig)
            });
        }

        return installations;
    }

    public Task InstallOrUpdateAsync(
        ClientDefinition client,
        LauncherConfig config,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallCoreAsync(client, config.Download.MaxRetryCount, progress, cancellationToken);
    }

    public Task RepairAsync(
        ClientDefinition client,
        LauncherConfig config,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return InstallCoreAsync(client, config.Download.MaxRetryCount, progress, cancellationToken);
    }

    public Task DeleteClientAsync(ClientDefinition client, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clientDirectory = _paths.GetClientDirectory(client);
        SafeDirectory.DeleteChildDirectory(_paths.ClientsPath, clientDirectory);
        return Task.CompletedTask;
    }

    public async Task SaveClientSettingsAsync(
        LauncherConfigStore configStore,
        string clientId,
        ClientUserSettings settings,
        CancellationToken cancellationToken = default)
    {
        var config = await configStore.LoadAsync(cancellationToken);
        config.ClientSettings[clientId] = settings;
        await configStore.SaveAsync(config, cancellationToken);
    }

    private async Task InstallCoreAsync(
        ClientDefinition client,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!client.Enabled)
        {
            throw new InvalidOperationException("该客户端已被远程配置禁用。");
        }

        _paths.EnsureCreated();
        var packFileName = $"{SanitizeFileName(client.Id)}-{SanitizeFileName(client.Version)}.zip";
        var packPath = Path.Combine(_paths.PackDownloadsPath, packFileName);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "download",
            Message = $"正在下载 {client.Name}",
            Progress = 0
        });

        await _downloadService.DownloadFileAsync(
            client.PackUrl,
            packPath,
            client.PackSha256,
            maxRetryCount,
            progress,
            cancellationToken);

        var finalDirectory = _paths.GetClientDirectory(client);
        var stagingDirectory = Path.Combine(_paths.ClientsPath, $"{SanitizeFileName(client.Id)}.staging-{Guid.NewGuid():N}");

        try
        {
            progress?.Report(new LauncherOperationProgress
            {
                Stage = "extract",
                Message = $"正在解压 {client.Name}",
                Progress = 0
            });

            await _extractor.ExtractAsync(packPath, stagingDirectory, progress, cancellationToken);
            await WriteLocalConfigAsync(stagingDirectory, client, cancellationToken);

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "install",
                Message = "正在写入客户端目录",
                Progress = null
            });

            SafeDirectory.DeleteChildDirectory(_paths.ClientsPath, finalDirectory);
            Directory.Move(stagingDirectory, finalDirectory);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                SafeDirectory.DeleteChildDirectory(_paths.ClientsPath, stagingDirectory);
            }

            throw;
        }

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "ready",
            Message = $"{client.Name} 已就绪",
            Progress = 1
        });
    }

    private ClientInstallStatus GetStatus(ClientDefinition client, LocalClientConfig? localConfig)
    {
        if (!client.Enabled)
        {
            return ClientInstallStatus.Disabled;
        }

        if (localConfig is null)
        {
            return ClientInstallStatus.NotInstalled;
        }

        if (!string.Equals(localConfig.PackSha256, client.PackSha256, StringComparison.OrdinalIgnoreCase) ||
            IsRemoteVersionNewer(client.Version, localConfig.Version))
        {
            return ClientInstallStatus.UpdateRequired;
        }

        return ClientInstallStatus.Ready;
    }

    private async Task<LocalClientConfig?> ReadLocalConfigAsync(
        ClientDefinition client,
        CancellationToken cancellationToken)
    {
        var path = _paths.GetClientConfigPath(client);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<LocalClientConfig>(
                stream,
                LauncherJson.Options,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteLocalConfigAsync(
        string clientDirectory,
        ClientDefinition client,
        CancellationToken cancellationToken)
    {
        var localConfig = new LocalClientConfig
        {
            Id = client.Id,
            Version = client.Version,
            MinecraftVersion = client.MinecraftVersion,
            Loader = client.Loader,
            LoaderVersion = client.LoaderVersion,
            JavaVersion = client.JavaVersion,
            PackSha256 = client.PackSha256,
            InstalledAtUtc = DateTimeOffset.UtcNow,
            LastValidatedUtc = DateTimeOffset.UtcNow
        };

        var configPath = Path.Combine(clientDirectory, "client.json");
        await using var stream = new FileStream(configPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, localConfig, LauncherJson.Options, cancellationToken);
    }

    private static bool IsRemoteVersionNewer(string remoteVersion, string localVersion)
    {
        if (Version.TryParse(remoteVersion, out var remote) &&
            Version.TryParse(localVersion, out var local))
        {
            return remote > local;
        }

        return !string.Equals(remoteVersion, localVersion, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "client" : clean;
    }
}
