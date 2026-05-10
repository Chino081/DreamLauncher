using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Clients;
using DreamLauncher.Models.Minecraft;

namespace DreamLauncher.Core.Minecraft;

public sealed class MinecraftContentManager
{
    private const string ResourcePacksFolderName = "resourcepacks";
    private const string ShaderPacksFolderName = "shaderpacks";
    private const string ModsFolderName = "mods";
    private readonly LauncherPaths _paths;

    public MinecraftContentManager(LauncherPaths paths)
    {
        _paths = paths;
    }

    public Task<GameContentInventory> GetInventoryAsync(
        ClientInstallation client,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = GetGameDirectory(client);
            var resourcePacksDirectory = EnsureContentDirectory(gameDirectory, ResourcePacksFolderName);
            var shaderPacksDirectory = EnsureContentDirectory(gameDirectory, ShaderPacksFolderName);
            var modsDirectory = EnsureContentDirectory(gameDirectory, ModsFolderName);

            var enabledResourcePacks = ReadEnabledResourcePacks(gameDirectory);
            var activeShaderPack = ReadActiveShaderPack(gameDirectory);

            return new GameContentInventory
            {
                GameDirectory = gameDirectory,
                ResourcePacksDirectory = resourcePacksDirectory,
                ShaderPacksDirectory = shaderPacksDirectory,
                ModsDirectory = modsDirectory,
                ResourcePacks = ListResourcePacks(resourcePacksDirectory, enabledResourcePacks),
                ShaderPacks = ListShaderPacks(shaderPacksDirectory, activeShaderPack),
                Mods = ListMods(modsDirectory)
            };
        }, cancellationToken);
    }

    public Task InstallAsync(
        ClientInstallation client,
        GameContentKind kind,
        IEnumerable<string> sourcePaths,
        CancellationToken cancellationToken = default)
    {
        var paths = sourcePaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Path.GetFullPath)
            .ToArray();

        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = GetGameDirectory(client);
            var destinationDirectory = GetContentDirectory(gameDirectory, kind);

            foreach (var sourcePath in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                InstallOne(kind, sourcePath, destinationDirectory);
            }
        }, cancellationToken);
    }

    public Task SetEnabledAsync(
        ClientInstallation client,
        GameContentKind kind,
        string fileName,
        bool enabled,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var gameDirectory = GetGameDirectory(client);

            switch (kind)
            {
                case GameContentKind.ResourcePack:
                    SetResourcePackEnabled(gameDirectory, fileName, enabled);
                    break;
                case GameContentKind.ShaderPack:
                    SetShaderPackEnabled(gameDirectory, fileName, enabled);
                    break;
                case GameContentKind.Mod:
                    SetModEnabled(GetContentDirectory(gameDirectory, GameContentKind.Mod), fileName, enabled);
                    break;
            }
        }, cancellationToken);
    }

    public string GetGameDirectory(ClientInstallation client)
    {
        var minecraftDirectory = Path.Combine(client.InstallPath, ".minecraft");
        if (!Directory.Exists(minecraftDirectory))
        {
            throw new DirectoryNotFoundException("当前客户端还没有安装 .minecraft，请先下载客户端。");
        }

        var versionJsonPath = FindVersionJson(minecraftDirectory, client.Definition);
        return Path.GetDirectoryName(versionJsonPath)
            ?? throw new InvalidDataException("版本 JSON 路径无效。");
    }

    private static void InstallOne(
        GameContentKind kind,
        string sourcePath,
        string destinationDirectory)
    {
        if (File.Exists(sourcePath))
        {
            var extension = Path.GetExtension(sourcePath);
            if (kind == GameContentKind.Mod &&
                !string.Equals(extension, ".jar", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("Mod 只支持拖入 .jar 文件。");
            }

            if (kind is GameContentKind.ResourcePack or GameContentKind.ShaderPack &&
                !string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("资源包和光影包只支持拖入 .zip 文件或文件夹。");
            }

            var destinationPath = LauncherPaths.EnsureChildPath(destinationDirectory, Path.GetFileName(sourcePath));
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            File.Copy(sourcePath, destinationPath, overwrite: true);
            return;
        }

        if (Directory.Exists(sourcePath))
        {
            if (kind == GameContentKind.Mod)
            {
                throw new InvalidDataException("Mod 安装只支持 .jar 文件。");
            }

            var directoryName = Path.GetFileName(sourcePath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            var destinationPath = LauncherPaths.EnsureChildPath(destinationDirectory, directoryName);
            if (string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(destinationPath), StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            CopyDirectory(sourcePath, destinationPath);
            return;
        }

        throw new FileNotFoundException("拖入的文件不存在。", sourcePath);
    }

    private static string GetContentDirectory(string gameDirectory, GameContentKind kind)
    {
        return kind switch
        {
            GameContentKind.ResourcePack => EnsureContentDirectory(gameDirectory, ResourcePacksFolderName),
            GameContentKind.ShaderPack => EnsureContentDirectory(gameDirectory, ShaderPacksFolderName),
            GameContentKind.Mod => EnsureContentDirectory(gameDirectory, ModsFolderName),
            _ => gameDirectory
        };
    }

    private static string EnsureContentDirectory(string gameDirectory, string folderName)
    {
        var directory = LauncherPaths.EnsureChildPath(gameDirectory, folderName);
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static IReadOnlyList<GameContentItem> ListResourcePacks(
        string directory,
        IReadOnlySet<string> enabledResourcePacks)
    {
        return ListDirectoryContent(directory, GameContentKind.ResourcePack, item =>
            enabledResourcePacks.Contains(ToResourcePackKey(item.Name)));
    }

    private static IReadOnlyList<GameContentItem> ListShaderPacks(
        string directory,
        string activeShaderPack)
    {
        return ListDirectoryContent(directory, GameContentKind.ShaderPack, item =>
            string.Equals(item.Name, activeShaderPack, StringComparison.OrdinalIgnoreCase));
    }

    private static IReadOnlyList<GameContentItem> ListMods(string directory)
    {
        var files = Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
                string.Equals(Path.GetExtension(path), ".jar", StringComparison.OrdinalIgnoreCase) ||
                path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var fileInfo = new FileInfo(path);
                var fileName = fileInfo.Name;
                var displayName = fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                    ? fileName[..^".disabled".Length]
                    : fileName;

                return new GameContentItem
                {
                    Kind = GameContentKind.Mod,
                    Name = displayName,
                    FileName = fileName,
                    FullPath = path,
                    IsEnabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase),
                    IsDirectory = false,
                    SizeBytes = fileInfo.Length,
                    LastWriteTime = fileInfo.LastWriteTime
                };
            })
            .ToList();

        return files;
    }

    private static IReadOnlyList<GameContentItem> ListDirectoryContent(
        string directory,
        GameContentKind kind,
        Func<FileSystemInfo, bool> enabledSelector)
    {
        var items = Directory.EnumerateFileSystemEntries(directory, "*", SearchOption.TopDirectoryOnly)
            .Where(path =>
            {
                if (Directory.Exists(path))
                {
                    return kind != GameContentKind.Mod;
                }

                var extension = Path.GetExtension(path);
                return string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase);
            })
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                FileSystemInfo info = Directory.Exists(path)
                    ? new DirectoryInfo(path)
                    : new FileInfo(path);

                return new GameContentItem
                {
                    Kind = kind,
                    Name = info.Name,
                    FileName = info.Name,
                    FullPath = info.FullName,
                    IsEnabled = enabledSelector(info),
                    IsDirectory = info is DirectoryInfo,
                    SizeBytes = info is FileInfo fileInfo ? fileInfo.Length : GetDirectorySize(info.FullName),
                    LastWriteTime = info.LastWriteTime
                };
            })
            .ToList();

        return items;
    }

    private static void SetResourcePackEnabled(
        string gameDirectory,
        string fileName,
        bool enabled)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        var lines = File.Exists(optionsPath)
            ? File.ReadAllLines(optionsPath, Encoding.UTF8).ToList()
            : [];
        const string key = "resourcePacks:";
        var index = lines.FindIndex(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        var entries = index >= 0
            ? ReadResourcePackEntries(lines[index][key.Length..]).ToList()
            : [];
        var resourceKey = ToResourcePackKey(fileName);

        entries.RemoveAll(item => string.Equals(item, resourceKey, StringComparison.OrdinalIgnoreCase));
        if (enabled)
        {
            entries.Add(resourceKey);
        }

        var lineValue = key + LauncherJson.Serialize(entries);
        if (index >= 0)
        {
            lines[index] = lineValue;
        }
        else
        {
            lines.Add(lineValue);
        }

        File.WriteAllLines(optionsPath, lines, Encoding.UTF8);
    }

    private static void SetShaderPackEnabled(
        string gameDirectory,
        string fileName,
        bool enabled)
    {
        var optionsPath = Path.Combine(gameDirectory, "optionsshaders.txt");
        var lines = File.Exists(optionsPath)
            ? File.ReadAllLines(optionsPath, Encoding.UTF8).ToList()
            : [];
        const string key = "shaderPack=";
        var index = lines.FindIndex(line => line.StartsWith(key, StringComparison.OrdinalIgnoreCase));
        var current = index >= 0 ? lines[index][key.Length..] : "";
        var nextValue = enabled
            ? fileName
            : string.Equals(current, fileName, StringComparison.OrdinalIgnoreCase) ? "OFF" : current;

        if (string.IsNullOrWhiteSpace(nextValue))
        {
            nextValue = "OFF";
        }

        if (index >= 0)
        {
            lines[index] = key + nextValue;
        }
        else
        {
            lines.Add(key + nextValue);
        }

        File.WriteAllLines(optionsPath, lines, Encoding.UTF8);
    }

    private static void SetModEnabled(
        string modsDirectory,
        string fileName,
        bool enabled)
    {
        var sourcePath = LauncherPaths.EnsureChildPath(modsDirectory, fileName);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("找不到这个 Mod 文件。", sourcePath);
        }

        var destinationName = enabled
            ? fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".disabled".Length]
                : fileName
            : fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)
                ? fileName
                : fileName + ".disabled";

        var destinationPath = LauncherPaths.EnsureChildPath(modsDirectory, destinationName);
        if (string.Equals(sourcePath, destinationPath, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (File.Exists(destinationPath))
        {
            throw new IOException("目标 Mod 文件已存在，不能覆盖。");
        }

        File.Move(sourcePath, destinationPath);
    }

    private static IReadOnlySet<string> ReadEnabledResourcePacks(string gameDirectory)
    {
        var optionsPath = Path.Combine(gameDirectory, "options.txt");
        if (!File.Exists(optionsPath))
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        const string key = "resourcePacks:";
        var line = File.ReadLines(optionsPath, Encoding.UTF8)
            .FirstOrDefault(item => item.StartsWith(key, StringComparison.OrdinalIgnoreCase));

        if (line is null)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        return ReadResourcePackEntries(line[key.Length..])
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadResourcePackEntries(string value)
    {
        try
        {
            return LauncherJson.Deserialize<List<string>>(value) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string ReadActiveShaderPack(string gameDirectory)
    {
        var optionsPath = Path.Combine(gameDirectory, "optionsshaders.txt");
        if (!File.Exists(optionsPath))
        {
            return "";
        }

        const string key = "shaderPack=";
        return File.ReadLines(optionsPath, Encoding.UTF8)
            .FirstOrDefault(item => item.StartsWith(key, StringComparison.OrdinalIgnoreCase))
            ?[key.Length..]
            ?? "";
    }

    private static string ToResourcePackKey(string fileName)
    {
        return "file/" + fileName.Replace('\\', '/');
    }

    private static long GetDirectorySize(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                .Sum(path => new FileInfo(path).Length);
        }
        catch
        {
            return 0;
        }
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);

        foreach (var directory in Directory.EnumerateDirectories(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, directory);
            Directory.CreateDirectory(Path.Combine(destinationDirectory, relativePath));
        }

        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            var relativePath = Path.GetRelativePath(sourceDirectory, file);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.Copy(file, destinationPath, overwrite: true);
        }
    }

    private static string FindVersionJson(string minecraftDirectory, ClientDefinition client)
    {
        var versionsDirectory = Path.Combine(minecraftDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            throw new DirectoryNotFoundException("客户端缺少 .minecraft/versions 目录。");
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

        found = Directory.EnumerateFiles(versionsDirectory, "*.json", SearchOption.AllDirectories)
            .FirstOrDefault(path =>
                path.Contains(client.MinecraftVersion, StringComparison.OrdinalIgnoreCase) &&
                (string.IsNullOrWhiteSpace(client.LoaderVersion) ||
                 path.Contains(client.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
                 path.Contains(client.Loader, StringComparison.OrdinalIgnoreCase)));

        if (found is not null)
        {
            return found;
        }

        throw new FileNotFoundException("没有找到可启动的 Minecraft 版本 JSON。");
    }
}
