using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Modpack;

namespace DreamLauncher.Core.Modpack;

public static class ModpackParser
{
    public static ModpackInfo DetectAndParse(
        string archivePath,
        DownloadSource source = DownloadSource.Bmclapi)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("整合包文件不存在。", archivePath);
        }

        using var archive = OpenArchive(archivePath);

        if (TryDetectModrinth(archive, out var baseFolder))
        {
            return ParseModrinth(archive, baseFolder, source);
        }

        if (TryDetectCurseForge(archive, out baseFolder))
        {
            return ParseCurseForge(archive, baseFolder, source);
        }

        throw new InvalidDataException("无法识别的整合包格式。支持 Modrinth (.mrpack) 和 CurseForge 格式。");
    }

    private static bool TryDetectModrinth(ZipArchive archive, out string baseFolder)
    {
        baseFolder = "";
        if (archive.GetEntry("modrinth.index.json") is not null)
        {
            return true;
        }

        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/');
            if (parts.Length == 2 && parts[1] == "modrinth.index.json")
            {
                baseFolder = parts[0] + "/";
                return true;
            }
        }

        return false;
    }

    private static bool TryDetectCurseForge(ZipArchive archive, out string baseFolder)
    {
        baseFolder = "";

        var rootManifest = archive.GetEntry("manifest.json");
        if (rootManifest is not null)
        {
            using var stream = rootManifest.Open();
            var json = ReadJsonObject(stream);
            if (json["addons"] is null)
            {
                return true;
            }
        }

        foreach (var entry in archive.Entries)
        {
            var parts = entry.FullName.Split('/');
            if (parts.Length == 2 && parts[1] == "manifest.json")
            {
                using var stream = entry.Open();
                var json = ReadJsonObject(stream);
                if (json["addons"] is null)
                {
                    baseFolder = parts[0] + "/";
                    return true;
                }
            }
        }

        return false;
    }

    private static ModpackInfo ParseModrinth(
        ZipArchive archive, string baseFolder, DownloadSource source)
    {
        var entry = archive.GetEntry(baseFolder + "modrinth.index.json")
            ?? throw new InvalidDataException("找不到 modrinth.index.json。");

        JsonObject json;
        using (var stream = entry.Open())
        {
            json = ReadJsonObject(stream);
        }

        var name = json["name"]?.ToString() ?? "";
        var dependencies = json["dependencies"]?.AsObject();
        var minecraftVersion = dependencies?["minecraft"]?.ToString() ?? "";
        var (loader, loaderVersion) = DetectModrinthLoader(dependencies);

        var files = new List<ModpackFileEntry>();
        foreach (var file in json["files"]?.AsArray() ?? [])
        {
            if (file is not JsonObject fileObj)
            {
                continue;
            }

            var env = fileObj["env"]?["client"]?.ToString();
            if (string.Equals(env, "unsupported", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = fileObj["path"]?.ToString() ?? "";
            var urls = fileObj["downloads"]?.AsArray()
                .Select(n => n?.ToString() ?? "")
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToArray() ?? [];

            if (urls.Length == 0)
            {
                continue;
            }

            var hashes = fileObj["hashes"]?.AsObject();
            var allUrls = BuildModrinthDownloadUrls(urls, source);
            files.Add(new ModpackFileEntry
            {
                RelativePath = path.Replace('\\', '/'),
                DownloadUrls = allUrls,
                FileSize = fileObj["fileSize"]?.GetValue<long>() ?? 0,
                Sha1 = hashes?["sha1"]?.ToString() ?? "",
                Required = !string.Equals(env, "optional", StringComparison.OrdinalIgnoreCase)
            });
        }

        return new ModpackInfo
        {
            Type = ModpackType.Modrinth,
            Name = name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            OverridesFolder = "overrides",
            ClientOverridesFolder = "client-overrides",
            Files = files
        };
    }

    private static ModpackInfo ParseCurseForge(
        ZipArchive archive, string baseFolder, DownloadSource source)
    {
        var entry = archive.GetEntry(baseFolder + "manifest.json")
            ?? throw new InvalidDataException("找不到 manifest.json。");

        JsonObject json;
        using (var stream = entry.Open())
        {
            json = ReadJsonObject(stream);
        }

        var name = json["name"]?.ToString() ?? "";
        var minecraft = json["minecraft"]?.AsObject();
        var minecraftVersion = minecraft?["version"]?.ToString() ?? "";

        var (loader, loaderVersion) = DetectCurseForgeLoader(minecraft);
        var overridesFolder = json["overrides"]?.ToString() ?? "overrides";

        var curseFiles = new List<(int ProjectId, int FileId)>();
        foreach (var file in json["files"]?.AsArray() ?? [])
        {
            if (file is not JsonObject fileObj)
            {
                continue;
            }

            var projectId = fileObj["projectID"]?.GetValue<int>() ?? 0;
            var fileId = fileObj["fileID"]?.GetValue<int>() ?? 0;
            if (projectId > 0 && fileId > 0)
            {
                curseFiles.Add((projectId, fileId));
            }
        }

        return new ModpackInfo
        {
            Type = ModpackType.CurseForge,
            Name = name,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            OverridesFolder = overridesFolder,
            Files = curseFiles.Select(f => new ModpackFileEntry
            {
                RelativePath = $"__curseforge__/{f.ProjectId}/{f.FileId}",
                Required = true
            }).ToList()
        };
    }

    private static (string Loader, string LoaderVersion) DetectModrinthLoader(JsonObject? dependencies)
    {
        if (dependencies is null)
        {
            return ("", "");
        }

        foreach (var entry in dependencies)
        {
            var key = entry.Key.ToLowerInvariant();
            var value = entry.Value?.ToString() ?? "";
            if (string.IsNullOrWhiteSpace(value))
            {
                continue;
            }

            switch (key)
            {
                case "forge":
                    return ("forge", value);
                case "neoforge":
                case "neo-forge":
                    return ("neoforge", value);
                case "fabric-loader":
                    return ("fabric", value);
                case "quilt-loader":
                    return ("quilt", value);
            }
        }

        return ("", "");
    }

    private static (string Loader, string LoaderVersion) DetectCurseForgeLoader(JsonObject? minecraft)
    {
        if (minecraft?["modLoaders"] is not JsonArray modLoaders)
        {
            return ("", "");
        }

        foreach (var loaderEntry in modLoaders)
        {
            if (loaderEntry is not JsonObject loaderObj)
            {
                continue;
            }

            var id = loaderObj["id"]?.ToString()?.ToLowerInvariant() ?? "";
            if (id.StartsWith("forge-"))
            {
                return ("forge", id["forge-".Length..]);
            }

            if (id.StartsWith("neoforge-"))
            {
                return ("neoforge", id["neoforge-".Length..]);
            }

            if (id.StartsWith("fabric-"))
            {
                return ("fabric", id["fabric-".Length..]);
            }

            if (id.StartsWith("quilt-"))
            {
                return ("quilt", id["quilt-".Length..]);
            }
        }

        return ("", "");
    }

    internal static ZipArchive OpenArchive(string archivePath)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var encoding = Encoding.GetEncoding("GB18030");
        return ZipFile.Open(archivePath, ZipArchiveMode.Read, encoding);
    }

    private static List<string> BuildModrinthDownloadUrls(
        string[] originalUrls, DownloadSource source)
    {
        var allUrls = new List<string>();

        foreach (var url in originalUrls)
        {
            // Fix CurseForge CDN issues
            var corrected = url
                .Replace("-service.overwolf.wtf", ".forgecdn.net")
                .Replace("://media.", "://edge.");

            // Primary: convert to selected source
            var primary = DownloadSourceUrls.ConvertModrinthUrl(source, corrected);
            allUrls.Add(primary);

            // Fallback: the other source
            var fallbackSource = source == DownloadSource.Official
                ? DownloadSource.Bmclapi
                : DownloadSource.Official;
            var fallback = DownloadSourceUrls.ConvertModrinthUrl(fallbackSource, corrected);
            if (!string.Equals(fallback, primary, StringComparison.OrdinalIgnoreCase))
            {
                allUrls.Add(fallback);
            }

            // Add original if different
            if (!string.Equals(url, primary, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(url, fallback, StringComparison.OrdinalIgnoreCase))
            {
                allUrls.Add(url);
            }
        }

        return allUrls;
    }

    private static JsonObject ReadJsonObject(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return JsonNode.Parse(text)?.AsObject()
            ?? throw new InvalidDataException("JSON 解析失败。");
    }
}
