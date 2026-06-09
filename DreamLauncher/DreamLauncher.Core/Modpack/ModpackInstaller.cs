using System.IO.Compression;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Minecraft;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Modpack;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Modpack;

public sealed class ModpackInstaller
{
    private readonly LauncherPaths _paths;
    private readonly HttpDownloadService _downloadService;
    private readonly SafeZipExtractor _extractor;
    private readonly CurseForgeApiClient _curseForgeClient;
    private readonly GameInstaller _gameInstaller;
    private readonly DownloadSource _source;

    public ModpackInstaller(
        LauncherPaths paths,
        HttpDownloadService downloadService,
        SafeZipExtractor extractor,
        GameInstaller gameInstaller,
        DownloadSource source = DownloadSource.Bmclapi,
        CurseForgeApiClient? curseForgeClient = null)
    {
        _paths = paths;
        _downloadService = downloadService;
        _extractor = extractor;
        _gameInstaller = gameInstaller;
        _source = source;
        _curseForgeClient = curseForgeClient ?? new CurseForgeApiClient(source: source);
    }

    public async Task InstallAsync(
        string modpackFilePath,
        string instanceName,
        LauncherConfig config,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceName))
        {
            throw new ArgumentException("实例名称不能为空。", nameof(instanceName));
        }

        var versionsDir = Path.Combine(_paths.MinecraftDirectory, "versions");
        var instanceDir = LauncherPaths.EnsureChildPath(versionsDir, instanceName);
        Directory.CreateDirectory(instanceDir);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "parse",
            Message = "正在解析整合包",
            Progress = 0
        });

        var modpack = ModpackParser.DetectAndParse(modpackFilePath, _source);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "extract",
            Message = $"正在解压整合包 overrides（{modpack.Type} 格式，{modpack.Files.Count} 个资源）",
            Progress = 0.05
        });

        await ExtractOverridesAsync(
            modpackFilePath, modpack, instanceDir, progress, cancellationToken);

        // Install game core (Minecraft + loader) into instance folder
        if (!string.IsNullOrWhiteSpace(modpack.MinecraftVersion))
        {
            progress?.Report(new LauncherOperationProgress
            {
                Stage = "game-install",
                Message = $"正在安装 Minecraft {modpack.MinecraftVersion}",
                Progress = 0.1
            });

            await _gameInstaller.InstallAsync(
                instanceDir,
                modpack.MinecraftVersion,
                modpack.Loader,
                modpack.LoaderVersion,
                config.Download.MaxRetryCount,
                progress,
                cancellationToken);
        }

        var filesToDownload = await ResolveDownloadFilesAsync(
            modpack, instanceDir, progress, cancellationToken);

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "download",
            Message = filesToDownload.Count > 0
                ? $"正在下载 {filesToDownload.Count} 个资源文件"
                : "无需下载额外资源",
            Progress = 0.3
        });

        if (filesToDownload.Count > 0)
        {

            await DownloadFilesAsync(
                filesToDownload, instanceDir, config, progress, cancellationToken);
        }

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "done",
            Message = "整合包导入完成",
            Progress = 1
        });
    }

    private async Task ExtractOverridesAsync(
        string modpackFilePath,
        ModpackInfo modpack,
        string instanceDir,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var hasOverrides = !string.IsNullOrWhiteSpace(modpack.OverridesFolder);
        var hasClientOverrides = !string.IsNullOrWhiteSpace(modpack.ClientOverridesFolder);

        if (!hasOverrides && !hasClientOverrides)
        {
            return;
        }

        var stagingDir = Path.Combine(
            _paths.ModpackTempPath,
            $"staging-{Guid.NewGuid():N}");

        try
        {
            await Task.Run(async () =>
            {
                await _extractor.ExtractAsync(
                    modpackFilePath, stagingDir, progress, cancellationToken);

                if (hasOverrides)
                {
                    var overridesDir = FindOverridesDirectory(stagingDir, modpack.OverridesFolder);
                    if (overridesDir is not null && Directory.Exists(overridesDir))
                    {
                        MergeDirectory(overridesDir, instanceDir, cancellationToken);
                    }
                }

                if (hasClientOverrides)
                {
                    var clientOverridesDir = FindOverridesDirectory(stagingDir, modpack.ClientOverridesFolder);
                    if (clientOverridesDir is not null && Directory.Exists(clientOverridesDir))
                    {
                        MergeDirectory(clientOverridesDir, instanceDir, cancellationToken);
                    }
                }
            }, cancellationToken);
        }
        finally
        {
            TryDeleteDirectory(stagingDir);
        }
    }

    private static string? FindOverridesDirectory(string stagingDir, string overridesFolder)
    {
        var direct = Path.Combine(stagingDir, overridesFolder);
        if (Directory.Exists(direct))
        {
            return direct;
        }

        foreach (var subDir in Directory.EnumerateDirectories(stagingDir, "*", SearchOption.TopDirectoryOnly))
        {
            var candidate = Path.Combine(subDir, overridesFolder);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task<List<ModpackFileEntry>> ResolveDownloadFilesAsync(
        ModpackInfo modpack,
        string instanceDir,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        List<ModpackFileEntry> filesToDownload;

        if (modpack.Type == ModpackType.CurseForge)
        {
            var curseForgeRefs = modpack.Files
                .Where(f => f.RelativePath.StartsWith("__curseforge__/"))
                .Select(f =>
                {
                    var parts = f.RelativePath.Split('/');
                    return (int.Parse(parts[1]), int.Parse(parts[2]));
                })
                .ToList();

            progress?.Report(new LauncherOperationProgress
            {
                Stage = "resolve",
                Message = $"正在查询 {curseForgeRefs.Count} 个 CurseForge 文件信息",
                Progress = 0.2
            });

            try
            {
                filesToDownload = curseForgeRefs.Count > 0
                    ? (List<ModpackFileEntry>)(await _curseForgeClient.ResolveFilesAsync(
                        curseForgeRefs, cancellationToken))
                    : [];
            }
            catch (Exception ex)
            {
                throw new IOException($"无法获取 CurseForge 资源信息：{ex.Message}", ex);
            }
        }
        else
        {
            filesToDownload = modpack.Files
                .Where(f => f.DownloadUrls.Count > 0)
                .ToList();
        }

        var filtered = new List<ModpackFileEntry>();
        foreach (var file in filesToDownload)
        {
            if (file.RelativePath.StartsWith("__curseforge__/"))
            {
                continue;
            }

            try
            {
                var targetPath = LauncherPaths.EnsureChildPath(instanceDir,
                    file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(targetPath))
                {
                    var fileInfo = new FileInfo(targetPath);
                    if (file.FileSize > 0 && fileInfo.Length == file.FileSize)
                    {
                        continue;
                    }
                }
            }
            catch
            {
                continue;
            }

            filtered.Add(file);
        }

        return filtered;
    }

    private async Task DownloadFilesAsync(
        List<ModpackFileEntry> files,
        string instanceDir,
        LauncherConfig config,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var cacheDir = Path.Combine(_paths.ModpackTempPath, "downloads");
        Directory.CreateDirectory(cacheDir);

        try
        {
            var total = files.Count;
            var completed = 0;
            using var semaphore = new SemaphoreSlim(64);

            var tasks = files.Select(async (file, i) =>
            {
                await semaphore.WaitAsync(cancellationToken);
                try
                {
                    var cachePath = Path.Combine(cacheDir,
                        $"{i:0000}-{SanitizeFileName(Path.GetFileName(file.RelativePath))}");

                    var downloaded = false;
                    Exception? lastError = null;

                    foreach (var url in file.DownloadUrls)
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        try
                        {
                            await DownloadSingleFileAsync(
                                url, cachePath, file, config, cancellationToken);
                            downloaded = true;
                            break;
                        }
                        catch (Exception ex)
                        {
                            lastError = ex;
                            TryDeleteFile(cachePath);
                        }
                    }

                    if (!downloaded)
                    {
                        if (file.Required)
                        {
                            throw new IOException(
                                $"下载失败：{Path.GetFileName(file.RelativePath)}",
                                lastError);
                        }

                        return;
                    }

                    var targetPath = LauncherPaths.EnsureChildPath(
                        instanceDir,
                        file.RelativePath.Replace('/', Path.DirectorySeparatorChar));

                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    File.Copy(cachePath, targetPath, overwrite: true);
                    TryDeleteFile(cachePath);
                }
                finally
                {
                    semaphore.Release();
                    var done = Interlocked.Increment(ref completed);
                    progress?.Report(new LauncherOperationProgress
                    {
                        Stage = "download",
                        Message = $"正在下载资源文件 ({done}/{total})",
                        Progress = 0.3 + 0.65 * ((double)done / total)
                    });
                }
            }).ToList();

            try
            {
                await Task.WhenAll(tasks);
            }
            catch
            {
                foreach (var task in tasks)
                {
                    try { await task; } catch { /* ignored */ }
                }

                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(cacheDir);
        }
    }

    private async Task DownloadSingleFileAsync(
        string url,
        string cachePath,
        ModpackFileEntry file,
        LauncherConfig config,
        CancellationToken cancellationToken)
    {
        var hasSha1 = !string.IsNullOrWhiteSpace(file.Sha1);

        if (hasSha1)
        {
            await _downloadService.DownloadFileWithSha1Async(
                url,
                cachePath,
                file.Sha1,
                config.Download.MaxRetryCount,
                null,
                cancellationToken,
                config.Download.SpeedLimitKbPerSecond);
        }
        else
        {
            await _downloadService.DownloadFileUnsafeAsync(
                url,
                cachePath,
                config.Download.MaxRetryCount,
                null,
                cancellationToken,
                config.Download.SpeedLimitKbPerSecond);
        }
    }

    private static void MergeDirectory(
        string sourceDirectory,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var dir in Directory.EnumerateDirectories(
            sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, dir);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(
            sourceDirectory, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static string SanitizeFileName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(value.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray());
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort
        }
    }
}
