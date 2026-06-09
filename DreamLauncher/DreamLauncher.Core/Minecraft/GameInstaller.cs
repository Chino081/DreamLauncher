using System.Text.Json;
using System.Text.Json.Nodes;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Minecraft;

public sealed class GameInstaller
{
    private const string FabricMetaUrl =
        "https://meta.fabricmc.net/v2/versions/loader/{0}/{1}/profile/json";

    private const string QuiltMetaUrl =
        "https://meta.quiltmc.org/v3/versions/loader/{0}/{1}/profile/json";

    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private readonly LauncherPaths _paths;
    private readonly HttpDownloadService _downloadService;
    private readonly DownloadSource _source;

    public GameInstaller(LauncherPaths paths, HttpDownloadService downloadService,
        DownloadSource source = DownloadSource.Bmclapi)
    {
        _paths = paths;
        _downloadService = downloadService;
        _source = source;
    }

    public async Task InstallAsync(
        string instanceDir,
        string minecraftVersion,
        string loader,
        string loaderVersion,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(instanceDir);

        var librariesDir = Path.Combine(_paths.MinecraftDirectory, "libraries");
        var assetsDir = Path.Combine(_paths.MinecraftDirectory, "assets");
        Directory.CreateDirectory(librariesDir);
        Directory.CreateDirectory(assetsDir);

        // Step 1: Download version manifest
        progress?.Report(new LauncherOperationProgress
        {
            Stage = "manifest",
            Message = "正在下载版本清单（镜像源）",
            Progress = 0
        });

        var versionJsonUrl = await GetVersionJsonUrlAsync(minecraftVersion, cancellationToken);

        // Step 2: Download version json
        progress?.Report(new LauncherOperationProgress
        {
            Stage = "version-json",
            Message = $"正在下载 {minecraftVersion} 版本信息",
            Progress = 0.05
        });

        var instanceName = Path.GetFileName(instanceDir);
        var versionJsonPath = Path.Combine(instanceDir, instanceName + ".json");

        var vanillaJson = await GetVersionJsonAsync(versionJsonUrl, cancellationToken);

        // Step 3: Download version jar
        progress?.Report(new LauncherOperationProgress
        {
            Stage = "version-jar",
            Message = $"正在下载 {minecraftVersion} 游戏本体",
            Progress = 0.1
        });

        await DownloadVersionJarAsync(
            vanillaJson, minecraftVersion, instanceDir, instanceName,
            maxRetryCount, cancellationToken);

        // Step 4: Download libraries
        progress?.Report(new LauncherOperationProgress
        {
            Stage = "libraries",
            Message = "正在下载游戏依赖库",
            Progress = 0.2
        });

        await DownloadLibrariesAsync(
            vanillaJson, librariesDir, maxRetryCount, progress, cancellationToken);

        // Step 5: Download assets
        progress?.Report(new LauncherOperationProgress
        {
            Stage = "assets",
            Message = "正在下载游戏资源文件",
            Progress = 0.4
        });

        await DownloadAssetsAsync(
            vanillaJson, assetsDir, maxRetryCount, progress, cancellationToken);

        // Step 6: Build final version JSON (merge loader if needed)
        var hasLoader = !string.IsNullOrWhiteSpace(loader) &&
            !string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) &&
            !string.IsNullOrWhiteSpace(loaderVersion);

        if (hasLoader)
        {
            progress?.Report(new LauncherOperationProgress
            {
                Stage = "loader",
                Message = $"正在安装 {loader} {loaderVersion}",
                Progress = 0.8
            });

            var loaderJson = await GetLoaderJsonAsync(
                minecraftVersion, loader, loaderVersion,
                librariesDir, maxRetryCount, cancellationToken);

            if (loaderJson is not null)
            {
                var merged = MergeVersionJson(vanillaJson, loaderJson, instanceName);
                await File.WriteAllTextAsync(versionJsonPath, merged.ToJsonString(), cancellationToken);
            }
            else
            {
                // Loader merge failed, save vanilla JSON as fallback
                vanillaJson["id"] = instanceName;
                await File.WriteAllTextAsync(versionJsonPath, vanillaJson.ToJsonString(), cancellationToken);
            }
        }
        else
        {
            // Vanilla: save version JSON with instance name as id
            vanillaJson["id"] = instanceName;
            await File.WriteAllTextAsync(versionJsonPath, vanillaJson.ToJsonString(), cancellationToken);
        }

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "done",
            Message = "游戏安装完成",
            Progress = 1
        });
    }

    private async Task<string> GetVersionJsonUrlAsync(
        string minecraftVersion, CancellationToken cancellationToken)
    {
        var manifestUrl = DownloadSourceUrls.GetManifestUrl(_source);
        var manifest = await FetchJsonObjectAsync(manifestUrl, cancellationToken);

        foreach (var version in manifest["versions"]?.AsArray() ?? [])
        {
            if (version?["id"]?.ToString() == minecraftVersion)
            {
                var url = version["url"]?.ToString();
                if (!string.IsNullOrWhiteSpace(url))
                {
                    return DownloadSourceUrls.ConvertUrl(_source, url);
                }
            }
        }

        throw new InvalidDataException($"找不到版本 {minecraftVersion}。");
    }

    private async Task<JsonObject> GetVersionJsonAsync(
        string url, CancellationToken cancellationToken)
    {
        return await FetchJsonObjectAsync(url, cancellationToken);
    }

    private async Task DownloadVersionJarAsync(
        JsonObject versionJson,
        string minecraftVersion,
        string instanceDir,
        string jarName,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var jarPath = Path.Combine(instanceDir, jarName + ".jar");
        if (File.Exists(jarPath))
        {
            return;
        }

        var clientInfo = versionJson["downloads"]?["client"];
        var jarUrl = clientInfo?["url"]?.ToString()
            ?? throw new InvalidDataException("版本 jar 下载地址缺失。");
        var sha1 = clientInfo["sha1"]?.ToString() ?? "";

        jarUrl = DownloadSourceUrls.ConvertUrl(_source, jarUrl);

        if (!string.IsNullOrWhiteSpace(sha1))
        {
            await _downloadService.DownloadFileWithSha1Async(
                jarUrl, jarPath, sha1, maxRetryCount,
                cancellationToken: cancellationToken);
        }
        else
        {
            await _downloadService.DownloadFileUnsafeAsync(
                jarUrl, jarPath, maxRetryCount,
                cancellationToken: cancellationToken);
        }
    }

    private async Task DownloadLibrariesAsync(
        JsonObject versionJson,
        string librariesDir,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var libraries = versionJson["libraries"]?.AsArray() ?? [];

        // Collect items to download
        var items = new List<(string Url, string Path, string Sha1)>();
        foreach (var library in libraries)
        {
            if (library is not JsonObject libObj || !IsLibraryAllowed(libObj))
            {
                continue;
            }

            var artifact = libObj["downloads"]?["artifact"];
            if (artifact is not JsonObject artifactObj)
            {
                continue;
            }

            var path = artifactObj["path"]?.ToString() ?? "";
            var url = artifactObj["url"]?.ToString() ?? "";
            var sha1 = artifactObj["sha1"]?.ToString() ?? "";

            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(url))
            {
                var targetPath = Path.Combine(librariesDir,
                    path.Replace('/', Path.DirectorySeparatorChar));

                if (!File.Exists(targetPath))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                    items.Add((DownloadSourceUrls.ConvertUrl(_source, url), targetPath, sha1));
                }
            }
        }

        if (items.Count == 0)
        {
            return;
        }

        // Parallel download
        var total = items.Count;
        var completed = 0;
        using var semaphore = new SemaphoreSlim(64);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                if (!string.IsNullOrWhiteSpace(item.Sha1))
                {
                    await _downloadService.DownloadFileWithSha1Async(
                        item.Url, item.Path, item.Sha1, maxRetryCount,
                        cancellationToken: cancellationToken);
                }
                else
                {
                    await _downloadService.DownloadFileUnsafeAsync(
                        item.Url, item.Path, maxRetryCount,
                        cancellationToken: cancellationToken);
                }
            }
            catch
            {
                // Library download failed, continue with others
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                progress?.Report(new LauncherOperationProgress
                {
                    Stage = "libraries",
                    Message = $"正在下载依赖库 ({done}/{total})",
                    Progress = 0.2 + 0.2 * ((double)done / total)
                });
            }
        }).ToList();

        try
        {
            await Task.WhenAll(tasks);
        }
        catch
        {
            // Ensure all tasks complete before semaphore is disposed
            foreach (var task in tasks)
            {
                try { await task; } catch { /* ignored */ }
            }

            throw;
        }
    }

    private async Task DownloadAssetsAsync(
        JsonObject versionJson,
        string assetsDir,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var assetIndexInfo = versionJson["assetIndex"];
        var assetIndexId = assetIndexInfo?["id"]?.ToString() ?? "legacy";
        var assetIndexUrl = assetIndexInfo?["url"]?.ToString() ?? "";

        if (string.IsNullOrWhiteSpace(assetIndexUrl))
        {
            return;
        }

        assetIndexUrl = DownloadSourceUrls.ConvertUrl(_source, assetIndexUrl);

        var assetIndexesDir = Path.Combine(assetsDir, "indexes");
        Directory.CreateDirectory(assetIndexesDir);
        var assetIndexPath = Path.Combine(assetIndexesDir, assetIndexId + ".json");

        if (!File.Exists(assetIndexPath))
        {
            await DownloadJsonFileAsync(assetIndexUrl, assetIndexPath, cancellationToken);
        }

        var assetIndex = await ReadJsonObjectAsync(assetIndexPath);
        var objects = assetIndex["objects"]?.AsObject();
        if (objects is null)
        {
            return;
        }

        var objectsDir = Path.Combine(assetsDir, "objects");
        Directory.CreateDirectory(objectsDir);

        // Collect items to download
        var items = new List<(string Url, string Path, string Hash)>();
        foreach (var entry in objects)
        {
            if (entry.Value is not JsonObject objInfo)
            {
                continue;
            }

            var hash = objInfo["hash"]?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(hash))
            {
                continue;
            }

            var subDir = hash[..2];
            var targetPath = Path.Combine(objectsDir, subDir, hash);

            if (File.Exists(targetPath))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var assetBaseUrl = DownloadSourceUrls.GetAssetBaseUrl(_source);
            items.Add(($"{assetBaseUrl}{subDir}/{hash}", targetPath, hash));
        }

        if (items.Count == 0)
        {
            return;
        }

        // Parallel download
        var total = items.Count;
        var completed = 0;
        using var semaphore = new SemaphoreSlim(64);

        var tasks = items.Select(async item =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                await _downloadService.DownloadFileWithSha1Async(
                    item.Url, item.Path, item.Hash, maxRetryCount,
                    cancellationToken: cancellationToken);
            }
            catch
            {
                // Asset download failed, continue with others
            }
            finally
            {
                semaphore.Release();
                var done = Interlocked.Increment(ref completed);
                if (done % 50 == 0 || done == total)
                {
                    progress?.Report(new LauncherOperationProgress
                    {
                        Stage = "assets",
                        Message = $"正在下载资源文件 ({done}/{total})",
                        Progress = 0.4 + 0.4 * ((double)done / total)
                    });
                }
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

    private async Task<JsonObject?> GetLoaderJsonAsync(
        string minecraftVersion,
        string loader,
        string loaderVersion,
        string librariesDir,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        switch (loader.ToLowerInvariant())
        {
            case "fabric":
                return await GetFabricLoaderJsonAsync(
                    minecraftVersion, loaderVersion, librariesDir,
                    maxRetryCount, cancellationToken);

            case "quilt":
                return await GetQuiltLoaderJsonAsync(
                    minecraftVersion, loaderVersion, librariesDir,
                    maxRetryCount, cancellationToken);

            case "forge":
            case "neoforge":
                return await GetForgeLikeLoaderJsonAsync(
                    minecraftVersion, loader, loaderVersion,
                    librariesDir, maxRetryCount, cancellationToken);

            default:
                return null;
        }
    }

    private async Task<JsonObject?> GetFabricLoaderJsonAsync(
        string minecraftVersion,
        string loaderVersion,
        string librariesDir,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var url = string.Format(FabricMetaUrl, minecraftVersion, loaderVersion);
        try
        {
            var json = await FetchJsonObjectAsync(url, cancellationToken);
            await DownloadLibrariesAsync(json, librariesDir, maxRetryCount, null, cancellationToken);
            return json;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonObject?> GetQuiltLoaderJsonAsync(
        string minecraftVersion,
        string loaderVersion,
        string librariesDir,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var url = string.Format(QuiltMetaUrl, minecraftVersion, loaderVersion);
        try
        {
            var json = await FetchJsonObjectAsync(url, cancellationToken);
            await DownloadLibrariesAsync(json, librariesDir, maxRetryCount, null, cancellationToken);
            return json;
        }
        catch
        {
            return null;
        }
    }

    private async Task<JsonObject?> GetForgeLikeLoaderJsonAsync(
        string minecraftVersion,
        string loader,
        string loaderVersion,
        string librariesDir,
        int maxRetryCount,
        CancellationToken cancellationToken)
    {
        var installerUrl = string.Equals(loader, "neoforge", StringComparison.OrdinalIgnoreCase)
            ? DownloadSourceUrls.GetNeoForgeInstallerUrl(_source, loaderVersion)
            : DownloadSourceUrls.GetForgeInstallerUrl(_source, loaderVersion);

        var installerDir = Path.Combine(_paths.CachePath, "installers");
        Directory.CreateDirectory(installerDir);
        var installerPath = Path.Combine(installerDir, $"{loader}-{loaderVersion}-installer.jar");

        try
        {
            if (!File.Exists(installerPath))
            {
                await _downloadService.DownloadFileUnsafeAsync(
                    installerUrl, installerPath, maxRetryCount,
                    cancellationToken: cancellationToken);
            }

            using var archive = System.IO.Compression.ZipFile.Open(
                installerPath, System.IO.Compression.ZipArchiveMode.Read);

            var versionJsonEntry = archive.GetEntry("version.json")
                ?? archive.GetEntry("install_profile.json");

            if (versionJsonEntry is not null)
            {
                using var stream = versionJsonEntry.Open();
                using var reader = new StreamReader(stream);
                var content = await reader.ReadToEndAsync(cancellationToken);

                var json = JsonNode.Parse(content)?.AsObject();
                if (json is not null)
                {
                    await DownloadLibrariesAsync(
                        json, librariesDir, maxRetryCount, null, cancellationToken);
                    return json;
                }
            }
        }
        catch
        {
            // Forge/NeoForge installer extraction failed
        }
        finally
        {
            TryDeleteFile(installerPath);
        }

        return null;
    }

    private static JsonObject MergeVersionJson(
        JsonObject vanilla, JsonObject loader, string instanceName)
    {
        var merged = new JsonObject();

        // Copy all vanilla fields
        foreach (var prop in vanilla)
        {
            merged[prop.Key] = prop.Value?.DeepClone();
        }

        // Override with loader fields
        merged["id"] = instanceName;

        if (loader.TryGetPropertyValue("mainClass", out var mainClass) && mainClass is not null)
        {
            merged["mainClass"] = mainClass.DeepClone();
        }

        if (loader.TryGetPropertyValue("arguments", out var loaderArgs) && loaderArgs is not null)
        {
            if (!merged.TryGetPropertyValue("arguments", out var vanillaArgs) || vanillaArgs is null)
            {
                merged["arguments"] = loaderArgs.DeepClone();
            }
            else
            {
                // Merge game arguments
                if (loaderArgs is JsonObject loaderArgsObj &&
                    loaderArgsObj.TryGetPropertyValue("game", out var loaderGame) &&
                    loaderGame is JsonArray loaderGameArr)
                {
                    if (vanillaArgs is JsonObject vanillaArgsObj)
                    {
                        var mergedGame = new JsonArray();
                        if (vanillaArgsObj.TryGetPropertyValue("game", out var vanillaGame) &&
                            vanillaGame is JsonArray vanillaGameArr)
                        {
                            foreach (var item in vanillaGameArr)
                            {
                                mergedGame.Add(item?.DeepClone());
                            }
                        }
                        foreach (var item in loaderGameArr)
                        {
                            mergedGame.Add(item?.DeepClone());
                        }
                        vanillaArgsObj["game"] = mergedGame;
                    }
                }

                // Merge jvm arguments
                if (loaderArgs is JsonObject loaderArgsObj2 &&
                    loaderArgsObj2.TryGetPropertyValue("jvm", out var loaderJvm) &&
                    loaderJvm is JsonArray loaderJvmArr)
                {
                    if (vanillaArgs is JsonObject vanillaArgsObj2)
                    {
                        var mergedJvm = new JsonArray();
                        if (vanillaArgsObj2.TryGetPropertyValue("jvm", out var vanillaJvm) &&
                            vanillaJvm is JsonArray vanillaJvmArr)
                        {
                            foreach (var item in vanillaJvmArr)
                            {
                                mergedJvm.Add(item?.DeepClone());
                            }
                        }
                        foreach (var item in loaderJvmArr)
                        {
                            mergedJvm.Add(item?.DeepClone());
                        }
                        vanillaArgsObj2["jvm"] = mergedJvm;
                    }
                }
            }
        }

        // Merge libraries
        var mergedLibraries = new JsonArray();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (loader.TryGetPropertyValue("libraries", out var loaderLibs) &&
            loaderLibs is JsonArray loaderLibsArr)
        {
            foreach (var lib in loaderLibsArr)
            {
                if (lib is JsonObject libObj &&
                    libObj.TryGetPropertyValue("name", out var name) &&
                    name is not null)
                {
                    seenNames.Add(name.ToString());
                }
                mergedLibraries.Add(lib?.DeepClone());
            }
        }

        if (vanilla.TryGetPropertyValue("libraries", out var vanillaLibs) &&
            vanillaLibs is JsonArray vanillaLibsArr)
        {
            foreach (var lib in vanillaLibsArr)
            {
                if (lib is JsonObject libObj &&
                    libObj.TryGetPropertyValue("name", out var name) &&
                    name is not null &&
                    seenNames.Contains(name.ToString()))
                {
                    continue;
                }
                mergedLibraries.Add(lib?.DeepClone());
            }
        }

        merged["libraries"] = mergedLibraries;

        return merged;
    }

    private async Task<JsonObject> FetchJsonObjectAsync(
        string url, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException("JSON 解析失败。");
    }

    private async Task DownloadJsonFileAsync(
        string url, string targetPath, CancellationToken cancellationToken)
    {
        using var response = await SharedHttpClient.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        await File.WriteAllTextAsync(targetPath, content, cancellationToken);
    }

    private static async Task<JsonObject> ReadJsonObjectAsync(string path)
    {
        var text = await File.ReadAllTextAsync(path);
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException($"JSON 解析失败：{path}");
    }

    private static bool IsLibraryAllowed(JsonObject library)
    {
        var rules = library["rules"]?.AsArray();
        if (rules is null || rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule is not JsonObject ruleObj)
            {
                continue;
            }

            var action = ruleObj["action"]?.ToString() ?? "";
            var os = ruleObj["os"]?["name"]?.ToString() ?? "";

            if (string.Equals(action, "allow", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(os))
                {
                    allowed = true;
                }
                else if (string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                {
                    allowed = true;
                }
            }
            else if (string.Equals(action, "disallow", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrWhiteSpace(os))
                {
                    allowed = false;
                }
                else if (string.Equals(os, "windows", StringComparison.OrdinalIgnoreCase))
                {
                    allowed = false;
                }
            }
        }

        return allowed;
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
}
