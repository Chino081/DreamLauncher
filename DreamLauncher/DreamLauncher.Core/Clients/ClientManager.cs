using System.Text.Json;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Remote;
using DreamLauncher.Core.Security;
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
    private readonly RemoteConfigClient _remoteConfigClient;

    public ClientManager(
        LauncherPaths paths,
        HttpDownloadService downloadService,
        SafeZipExtractor extractor,
        RemoteConfigClient? remoteConfigClient = null)
    {
        _paths = paths;
        _downloadService = downloadService;
        _extractor = extractor;
        _remoteConfigClient = remoteConfigClient ?? new RemoteConfigClient();
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

            if (!client.Enabled)
            {
                continue;
            }

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
        return InstallOrUpdateCoreAsync(client, config.Download.MaxRetryCount, progress, cancellationToken);
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
                Message = "正在合并 .minecraft 文件",
                Progress = null
            });

            MergeDirectory(stagingDirectory, finalDirectory, cancellationToken);
            SafeDirectory.DeleteChildDirectory(_paths.ProgramDirectory, stagingDirectory);
            await WriteLocalConfigAsync(metadataDirectory, client, cancellationToken);

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "cleanup",
                Message = "正在清理下载缓存",
                Progress = null
            });

            DeleteFileIfExists(packPath);
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

    private async Task InstallOrUpdateCoreAsync(
        ClientDefinition client,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var localConfig = await ReadLocalConfigAsync(client, cancellationToken);
        if (ShouldUseManifestUpdate(client, localConfig))
        {
            await InstallManifestUpdateAsync(client, localConfig!, maxRetryCount, progress, cancellationToken);
            return;
        }

        await InstallCoreAsync(client, maxRetryCount, progress, cancellationToken);
    }

    private async Task InstallManifestUpdateAsync(
        ClientDefinition client,
        LocalClientConfig localConfig,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!client.Enabled)
        {
            throw new InvalidOperationException("该客户端已被远程配置禁用。");
        }

        _paths.EnsureCreated();

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "verify",
            Message = "正在读取文件级更新清单",
            Progress = null
        });

        var remoteManifest = await _remoteConfigClient.GetClientFileManifestAsync(
            client.ManifestUrl,
            cancellationToken);

        ValidateManifest(remoteManifest, client);

        var localManifest = await ReadLocalManifestAsync(client, cancellationToken);
        var remoteFiles = remoteManifest.Files
            .Select(entry => NormalizeManifestEntry(entry))
            .ToArray();

        var remotePathSet = remoteFiles
            .Select(item => item.NormalizedPath)
            .ToHashSet(CreatePathComparer());

        var filesToDownload = new List<ManifestDownloadItem>();
        long completedBytes = 0;
        long totalBytes = remoteFiles.Sum(item => Math.Max(0, item.Entry.Size));

        foreach (var item in remoteFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "verify",
                Message = $"正在校验 {item.NormalizedPath}",
                Progress = totalBytes == 0 ? null : (double)completedBytes / totalBytes,
                BytesCompleted = completedBytes,
                TotalBytes = totalBytes
            });

            if (await NeedsDownloadAsync(item, cancellationToken))
            {
                filesToDownload.Add(item);
            }
            else
            {
                completedBytes += Math.Max(0, item.Entry.Size);
            }
        }

        var cacheDirectory = Path.Combine(
            _paths.DownloadsPath,
            "manifest",
            SanitizeFileName(client.Id));
        Directory.CreateDirectory(cacheDirectory);

        var downloadedFiles = new List<(ManifestDownloadItem Item, string CachePath)>();
        for (var index = 0; index < filesToDownload.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = filesToDownload[index];
            var cachePath = Path.Combine(
                cacheDirectory,
                $"{index:0000}-{SanitizeFileName(Path.GetFileName(item.NormalizedPath))}");

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "download",
                Message = $"正在下载 {item.NormalizedPath}",
                Progress = totalBytes == 0 ? null : (double)completedBytes / totalBytes,
                BytesCompleted = completedBytes,
                TotalBytes = totalBytes
            });

            await _downloadService.DownloadFileAsync(
                BuildManifestFileUrl(remoteManifest, item.Entry),
                cachePath,
                item.Entry.Sha256,
                maxRetryCount,
                progress,
                cancellationToken);

            completedBytes += Math.Max(0, item.Entry.Size);
            downloadedFiles.Add((item, cachePath));
        }

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "install",
            Message = "正在应用文件级更新",
            Progress = null
        });

        foreach (var downloaded in downloadedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = downloaded.Item.TargetPath;
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            File.Copy(downloaded.CachePath, targetPath, overwrite: true);
        }

        foreach (var deletePath in GetManifestDeletePaths(localManifest, remoteManifest, remotePathSet))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var targetPath = GetManifestTargetPath(deletePath);
            if (File.Exists(targetPath))
            {
                File.Delete(targetPath);
            }
        }

        var metadataDirectory = _paths.GetClientDirectory(client);
        await WriteLocalManifestAsync(metadataDirectory, remoteManifest, cancellationToken);
        await WriteLocalConfigAsync(metadataDirectory, client, cancellationToken);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "cleanup",
            Message = "正在清理文件级更新缓存",
            Progress = null
        });

        SafeDirectory.DeleteChildDirectory(_paths.DownloadsPath, cacheDirectory);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "ready",
            Message = $"{client.Name} 已完成文件级更新",
            Progress = 1
        });
    }

    private bool ShouldUseManifestUpdate(ClientDefinition client, LocalClientConfig? localConfig)
    {
        return localConfig is not null &&
               !string.IsNullOrWhiteSpace(client.ManifestUrl) &&
               Directory.Exists(_paths.MinecraftDirectory) &&
               !IsMajorVersionChanged(client.Version, localConfig.Version);
    }

    private void ValidateManifest(ClientFileManifest manifest, ClientDefinition client)
    {
        if (!string.IsNullOrWhiteSpace(manifest.Id) &&
            !string.Equals(manifest.Id, client.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException("文件级更新清单与当前客户端不匹配。");
        }

        if (manifest.Files.Count == 0 && manifest.Delete.Count == 0)
        {
            throw new InvalidDataException("文件级更新清单没有可更新的文件。");
        }
    }

    private ManifestDownloadItem NormalizeManifestEntry(ClientFileManifestEntry entry)
    {
        var normalizedPath = NormalizeManifestPath(entry.Path);
        if (entry.Size < 0)
        {
            throw new InvalidDataException($"文件级更新清单中的文件大小无效：{entry.Path}");
        }

        if (string.IsNullOrWhiteSpace(entry.Sha256))
        {
            throw new InvalidDataException($"文件级更新清单缺少 SHA256：{entry.Path}");
        }

        return new ManifestDownloadItem(entry, normalizedPath, GetManifestTargetPath(normalizedPath));
    }

    private async Task<bool> NeedsDownloadAsync(
        ManifestDownloadItem item,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(item.TargetPath))
        {
            return true;
        }

        var fileInfo = new FileInfo(item.TargetPath);
        if (item.Entry.Size > 0 && fileInfo.Length != item.Entry.Size)
        {
            return true;
        }

        var actualSha256 = await Sha256Hasher.ComputeFileAsync(item.TargetPath, cancellationToken);
        return !string.Equals(actualSha256, item.Entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private IEnumerable<string> GetManifestDeletePaths(
        ClientFileManifest? localManifest,
        ClientFileManifest remoteManifest,
        IReadOnlySet<string> remotePathSet)
    {
        var comparer = CreatePathComparer();
        var deletePaths = new HashSet<string>(comparer);

        foreach (var path in remoteManifest.Delete)
        {
            deletePaths.Add(NormalizeManifestPath(path));
        }

        if (localManifest is not null)
        {
            foreach (var entry in localManifest.Files)
            {
                if (string.IsNullOrWhiteSpace(entry.Path))
                {
                    continue;
                }

                var oldPath = NormalizeManifestPath(entry.Path);
                if (!remotePathSet.Contains(oldPath))
                {
                    deletePaths.Add(oldPath);
                }
            }
        }

        return deletePaths.Where(path => !remotePathSet.Contains(path));
    }

    private string GetManifestTargetPath(string normalizedPath)
    {
        return LauncherPaths.EnsureChildPath(
            _paths.ProgramDirectory,
            normalizedPath.Replace('/', Path.DirectorySeparatorChar));
    }

    private static string NormalizeManifestPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidDataException("文件级更新清单包含空路径。");
        }

        var trimmed = path.Trim();
        if (Path.IsPathRooted(trimmed))
        {
            throw new InvalidDataException($"文件级更新清单包含绝对路径：{path}");
        }

        var parts = trimmed
            .Replace('\\', '/')
            .Trim('/')
            .Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length < 2 ||
            !string.Equals(parts[0], ".minecraft", StringComparison.OrdinalIgnoreCase) ||
            parts.Any(part => part is "." or ".."))
        {
            throw new InvalidDataException($"文件级更新清单路径必须位于 .minecraft 内：{path}");
        }

        return string.Join('/', parts);
    }

    private static string BuildManifestFileUrl(
        ClientFileManifest manifest,
        ClientFileManifestEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Url))
        {
            if (Uri.TryCreate(entry.Url, UriKind.Absolute, out _))
            {
                return entry.Url;
            }

            return CombineUrl(manifest.BaseUrl, entry.Url.Replace('\\', '/').TrimStart('/'));
        }

        return CombineUrl(manifest.BaseUrl, EscapeRelativeUrlPath(NormalizeManifestPath(entry.Path)));
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            throw new InvalidDataException("文件级更新清单缺少 baseUrl。");
        }

        var normalizedBaseUrl = baseUrl.EndsWith("/", StringComparison.Ordinal)
            ? baseUrl
            : baseUrl + "/";
        return new Uri(new Uri(normalizedBaseUrl, UriKind.Absolute), relativePath).ToString();
    }

    private static string EscapeRelativeUrlPath(string normalizedPath)
    {
        return string.Join(
            '/',
            normalizedPath.Split('/').Select(Uri.EscapeDataString));
    }

    private async Task<ClientFileManifest?> ReadLocalManifestAsync(
        ClientDefinition client,
        CancellationToken cancellationToken)
    {
        var path = GetLocalManifestPath(client);
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<ClientFileManifest>(
                stream,
                LauncherJson.Options,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteLocalManifestAsync(
        string clientDirectory,
        ClientFileManifest manifest,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(clientDirectory);
        var manifestPath = Path.Combine(clientDirectory, "manifest.json");
        await using var stream = new FileStream(manifestPath, FileMode.Create, FileAccess.Write, FileShare.None);
        await JsonSerializer.SerializeAsync(stream, manifest, LauncherJson.Options, cancellationToken);
    }

    private string GetLocalManifestPath(ClientDefinition client)
    {
        return Path.Combine(_paths.GetClientDirectory(client), "manifest.json");
    }

    private static IEqualityComparer<string> CreatePathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
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

    private static bool IsMajorVersionChanged(string remoteVersion, string localVersion)
    {
        if (string.IsNullOrWhiteSpace(localVersion))
        {
            return true;
        }

        if (Version.TryParse(remoteVersion, out var remote) &&
            Version.TryParse(localVersion, out var local))
        {
            return remote.Major != local.Major || remote.Minor != local.Minor;
        }

        return !string.Equals(GetVersionBand(remoteVersion), GetVersionBand(localVersion), StringComparison.OrdinalIgnoreCase);
    }

    private static string GetVersionBand(string version)
    {
        var value = (version ?? "").Trim();
        if (value.Length == 0)
        {
            return "";
        }

        var separators = new[] { '.', '-', '_', '+' };
        var first = value.IndexOfAny(separators);
        if (first < 0)
        {
            return value;
        }

        var second = value.IndexOfAny(separators, first + 1);
        return second < 0 ? value : value[..second];
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var clean = new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
        return string.IsNullOrWhiteSpace(clean) ? "client" : clean;
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static void MergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private sealed record ManifestDownloadItem(
        ClientFileManifestEntry Entry,
        string NormalizedPath,
        string TargetPath);
}
