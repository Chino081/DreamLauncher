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
                InstallPath = _paths.GetClientLaunchDirectory(client),
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
        var metadataDirectory = _paths.GetClientDirectory(client);
        SafeDirectory.DeleteChildDirectory(_paths.ClientsPath, metadataDirectory);
        SafeDirectory.DeleteChildDirectory(_paths.ProgramDirectory, _paths.MinecraftDirectory);
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

        var finalDirectory = _paths.MinecraftDirectory;
        var metadataDirectory = _paths.GetClientDirectory(client);
        var stagingDirectory = Path.Combine(
            _paths.ProgramDirectory,
            $".minecraft.staging-{SanitizeFileName(client.Id)}-{Guid.NewGuid():N}");

        try
        {
            progress?.Report(new LauncherOperationProgress
            {
                Stage = "extract",
                Message = $"正在解压 {client.Name}",
                Progress = 0
            });

            await _extractor.ExtractMinecraftContentAsync(packPath, stagingDirectory, progress, cancellationToken);

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "install",
                Message = "正在写入 .minecraft 目录",
                Progress = null
            });

            SafeDirectory.DeleteChildDirectory(_paths.ProgramDirectory, finalDirectory);
            Directory.Move(stagingDirectory, finalDirectory);
            await WriteLocalConfigAsync(metadataDirectory, client, cancellationToken);
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                SafeDirectory.DeleteChildDirectory(_paths.ProgramDirectory, stagingDirectory);
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

        if (!Directory.Exists(_paths.MinecraftDirectory))
        {
            return ClientInstallStatus.NotInstalled;
        }

        if (!HasRequiredClientFiles(client))
        {
            return ClientInstallStatus.VerificationFailed;
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

    private bool HasRequiredClientFiles(ClientDefinition client)
    {
        var minecraftDirectory = _paths.MinecraftDirectory;
        if (!Directory.Exists(minecraftDirectory))
        {
            return false;
        }

        if (!Directory.Exists(Path.Combine(minecraftDirectory, "libraries")) ||
            !Directory.Exists(Path.Combine(minecraftDirectory, "assets")))
        {
            return false;
        }

        var versionJsonPath = FindVersionJson(minecraftDirectory, client);
        if (versionJsonPath is null)
        {
            return false;
        }

        var versionJarPath = Path.ChangeExtension(versionJsonPath, ".jar");
        return File.Exists(versionJarPath);
    }

    private static string? FindVersionJson(string minecraftDirectory, ClientDefinition client)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return null;
        }

        var candidates = new List<string>
        {
            Path.Combine(versionsDirectory, client.MinecraftVersion, client.MinecraftVersion + ".json")
        };

        if (!string.Equals(client.Loader, "vanilla", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(client.LoaderVersion))
        {
            candidates.Add(Path.Combine(
                versionsDirectory,
                $"{client.MinecraftVersion}-{client.Loader}-{client.LoaderVersion}",
                $"{client.MinecraftVersion}-{client.Loader}-{client.LoaderVersion}.json"));
            candidates.Add(Path.Combine(
                versionsDirectory,
                $"{client.Loader}-{client.MinecraftVersion}-{client.LoaderVersion}",
                $"{client.Loader}-{client.MinecraftVersion}-{client.LoaderVersion}.json"));
        }

        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }

        return Directory
            .EnumerateFiles(versionsDirectory, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                path.Contains(client.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(client.LoaderVersion) ||
                 path.Contains(client.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(client.Loader, StringComparison.OrdinalIgnoreCase)));
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
        Directory.CreateDirectory(clientDirectory);
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
