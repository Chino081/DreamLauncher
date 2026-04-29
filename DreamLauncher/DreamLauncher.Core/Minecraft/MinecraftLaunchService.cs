using System.Diagnostics;
using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Java;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Accounts;
using DreamLauncher.Models.Clients;

namespace DreamLauncher.Core.Minecraft;

public sealed class MinecraftLaunchService
{
    private readonly LauncherPaths _paths;

    public MinecraftLaunchService(LauncherPaths paths)
    {
        _paths = paths;
    }

    public async Task<MinecraftLaunchResult> LaunchAsync(
        ClientInstallation client,
        AccountMetadata account,
        SecureAccountTokens tokens,
        JavaRuntimeInfo java,
        string? authlibInjectorJarPath = null,
        CancellationToken cancellationToken = default)
    {
        if (client.Status != ClientInstallStatus.Ready)
        {
            throw new InvalidOperationException("客户端尚未就绪。");
        }

        if (tokens.ExpiresAtUtc <= DateTimeOffset.UtcNow.AddMinutes(2))
        {
            throw new InvalidOperationException("账号登录已过期，请重新登录。");
        }

        var plan = await BuildLaunchPlanAsync(
            client,
            account,
            tokens,
            java,
            authlibInjectorJarPath,
            cancellationToken);
        var logPath = Path.Combine(
            _paths.LogsPath,
            $"launch-{DateTimeOffset.Now:yyyyMMdd-HHmmss}-{client.Definition.Id}.log");

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);

        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = java.JavaPath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = false
        };

        foreach (var argument in plan.Arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.OutputDataReceived += (_, args) => AppendLogLine(logPath, args.Data);
        process.ErrorDataReceived += (_, args) => AppendLogLine(logPath, args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动 Minecraft 进程。");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        return new MinecraftLaunchResult
        {
            ProcessId = process.Id,
            LogPath = logPath
        };
    }

    private static void AppendLogLine(string logPath, string? line)
    {
        if (line is null)
        {
            return;
        }

        File.AppendAllText(logPath, SensitiveDataRedactor.Redact(line) + Environment.NewLine, Encoding.UTF8);
    }

    private async Task<LaunchPlan> BuildLaunchPlanAsync(
        ClientInstallation client,
        AccountMetadata account,
        SecureAccountTokens tokens,
        JavaRuntimeInfo java,
        string? authlibInjectorJarPath,
        CancellationToken cancellationToken)
    {
        var minecraftDirectory = Path.Combine(client.InstallPath, ".minecraft");
        if (!Directory.Exists(minecraftDirectory))
        {
            throw new DirectoryNotFoundException("客户端目录中没有找到 .minecraft 文件夹。");
        }

        var versionJsonPath = FindVersionJson(minecraftDirectory, client.Definition);
        await using var stream = File.OpenRead(versionJsonPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;

        var versionId = ReadString(root, "id") ?? client.Definition.MinecraftVersion;
        var mainClass = ReadString(root, "mainClass")
            ?? throw new InvalidDataException("版本 JSON 缺少 mainClass。");
        var assetIndex = ReadAssetIndex(root);
        var classpath = BuildClasspath(minecraftDirectory, versionId, root);

        if (classpath.Count == 0)
        {
            throw new InvalidDataException("没有找到可用的 Minecraft 类路径。");
        }

        var gameDirectory = GetVersionGameDirectory(versionJsonPath);
        var nativesDirectory = Path.Combine(minecraftDirectory, "natives", versionId);
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(nativesDirectory);

        var variables = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = account.PlayerName,
            ["version_name"] = versionId,
            ["game_directory"] = gameDirectory,
            ["assets_root"] = Path.Combine(minecraftDirectory, "assets"),
            ["assets_index_name"] = assetIndex,
            ["auth_uuid"] = account.Uuid,
            ["auth_access_token"] = tokens.MinecraftAccessToken,
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = account.Type switch
            {
                AccountType.Offline => "legacy",
                AccountType.ThirdParty => "mojang",
                _ => "msa"
            },
            ["user_properties"] = "{}",
            ["version_type"] = client.Definition.Loader,
            ["launcher_name"] = "DreamLauncher",
            ["launcher_version"] = "0.1.0",
            ["natives_directory"] = nativesDirectory,
            ["classpath"] = string.Join(Path.PathSeparator, classpath)
        };

        var arguments = new List<string>
        {
            $"-Xmx{Math.Max(512, client.MemoryMb)}M",
            "-Dfile.encoding=UTF-8",
            "-Dminecraft.launcher.brand=DreamLauncher",
            "-Dminecraft.launcher.version=0.1.0"
        };

        AddThirdPartyAuthArguments(account, authlibInjectorJarPath, arguments);
        arguments.AddRange(SplitCommandLine(client.Definition.JvmArgs).Select(item => ReplaceVariables(item, variables)));
        AddVersionJvmArguments(root, variables, arguments);

        arguments.Add("-cp");
        arguments.Add(variables["classpath"]);
        arguments.Add(mainClass);

        var gameArguments = ReadArgumentList(root, "game").ToList();
        if (gameArguments.Count == 0 && ReadString(root, "minecraftArguments") is { } legacyArguments)
        {
            gameArguments.AddRange(SplitCommandLine(legacyArguments));
        }

        arguments.AddRange(gameArguments.Select(item => ReplaceVariables(item, variables)));
        arguments.AddRange(SplitCommandLine(client.Definition.GameArgs).Select(item => ReplaceVariables(item, variables)));

        if (!string.IsNullOrWhiteSpace(client.Definition.ServerAddress) &&
            !arguments.Any(item => string.Equals(item, "--server", StringComparison.OrdinalIgnoreCase)))
        {
            arguments.Add("--server");
            arguments.Add(client.Definition.ServerAddress);
        }

        return new LaunchPlan(gameDirectory, arguments);
    }

    private static string GetVersionGameDirectory(string versionJsonPath)
    {
        return Path.GetDirectoryName(versionJsonPath)
            ?? throw new InvalidDataException("版本 JSON 路径无效。");
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

        var versionFiles = Directory.EnumerateFiles(versionsDirectory, "*.json", SearchOption.AllDirectories);
        found = versionFiles.FirstOrDefault(path =>
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

    private static List<string> BuildClasspath(string minecraftDirectory, string versionId, JsonElement root)
    {
        var classpath = new List<string>();
        var librariesDirectory = Path.Combine(minecraftDirectory, "libraries");

        if (root.TryGetProperty("libraries", out var libraries) && libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var library in libraries.EnumerateArray())
            {
                if (!IsAllowedByRules(library))
                {
                    continue;
                }

                var artifactPath = ReadNestedString(library, "downloads", "artifact", "path")
                    ?? BuildLibraryPathFromName(library);

                if (artifactPath is null)
                {
                    continue;
                }

                var fullPath = Path.Combine(
                    librariesDirectory,
                    artifactPath.Replace('/', Path.DirectorySeparatorChar));

                if (File.Exists(fullPath))
                {
                    classpath.Add(fullPath);
                }
            }
        }

        var clientJar = Path.Combine(minecraftDirectory, "versions", versionId, versionId + ".jar");
        if (File.Exists(clientJar))
        {
            classpath.Add(clientJar);
        }

        return classpath;
    }

    private static string? BuildLibraryPathFromName(JsonElement library)
    {
        var name = ReadString(library, "name");
        if (name is null)
        {
            return null;
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var groupPath = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        return $"{groupPath}/{artifact}/{version}/{artifact}-{version}.jar";
    }

    private static IEnumerable<string> ReadArgumentList(JsonElement root, string kind)
    {
        if (!root.TryGetProperty("arguments", out var arguments) ||
            !arguments.TryGetProperty(kind, out var values) ||
            values.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind == JsonValueKind.String)
            {
                result.Add(value.GetString()!);
                continue;
            }

            if (value.ValueKind != JsonValueKind.Object || !IsAllowedByRules(value))
            {
                continue;
            }

            if (!value.TryGetProperty("value", out var argumentValue))
            {
                continue;
            }

            if (argumentValue.ValueKind == JsonValueKind.String)
            {
                result.Add(argumentValue.GetString()!);
            }
            else if (argumentValue.ValueKind == JsonValueKind.Array)
            {
                result.AddRange(argumentValue.EnumerateArray()
                    .Where(item => item.ValueKind == JsonValueKind.String)
                    .Select(item => item.GetString()!));
            }
        }

        return result;
    }

    private static bool IsAllowedByRules(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules) || rules.ValueKind != JsonValueKind.Array)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            var action = ReadString(rule, "action");
            var osMatches = true;

            if (rule.TryGetProperty("os", out var os) &&
                ReadString(os, "name") is { } osName)
            {
                osMatches = OperatingSystem.IsWindows()
                    ? osName.Equals("windows", StringComparison.OrdinalIgnoreCase)
                    : OperatingSystem.IsMacOS()
                        ? osName.Equals("osx", StringComparison.OrdinalIgnoreCase)
                        : osName.Equals("linux", StringComparison.OrdinalIgnoreCase);
            }

            if (!osMatches)
            {
                continue;
            }

            if (!IsAllowedByFeatureRules(rule))
            {
                continue;
            }

            allowed = action?.Equals("allow", StringComparison.OrdinalIgnoreCase) == true;
            if (action?.Equals("disallow", StringComparison.OrdinalIgnoreCase) == true)
            {
                allowed = false;
            }
        }

        return allowed;
    }

    private static bool IsAllowedByFeatureRules(JsonElement rule)
    {
        if (!rule.TryGetProperty("features", out var features) || features.ValueKind != JsonValueKind.Object)
        {
            return true;
        }

        // 当前启动器没有启用 demo、quick play、自定义分辨率等 launcher features。
        // Minecraft 版本 JSON 中带 features 的 allow 规则必须跳过，否则会把多个 quick play 参数同时传入。
        foreach (var feature in features.EnumerateObject())
        {
            if (feature.Value.ValueKind == JsonValueKind.True)
            {
                return false;
            }
        }

        return true;
    }

    private static void AddVersionJvmArguments(
        JsonElement root,
        IReadOnlyDictionary<string, string> variables,
        ICollection<string> arguments)
    {
        var versionArguments = ReadArgumentList(root, "jvm").ToList();
        for (var index = 0; index < versionArguments.Count; index++)
        {
            var argument = versionArguments[index];
            if (IsClasspathSwitch(argument))
            {
                if (index + 1 < versionArguments.Count &&
                    string.Equals(versionArguments[index + 1], "${classpath}", StringComparison.OrdinalIgnoreCase))
                {
                    index++;
                }

                continue;
            }

            if (string.Equals(argument, "${classpath}", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            arguments.Add(ReplaceVariables(argument, variables));
        }
    }

    private static bool IsClasspathSwitch(string argument)
    {
        return string.Equals(argument, "-cp", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(argument, "-classpath", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(argument, "--class-path", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReadAssetIndex(JsonElement root)
    {
        if (ReadString(root, "assets") is { } assets)
        {
            return assets;
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex) &&
            ReadString(assetIndex, "id") is { } id)
        {
            return id;
        }

        return "";
    }

    private static string? ReadNestedString(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var item in path)
        {
            if (!current.TryGetProperty(item, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static string? ReadString(JsonElement root, string propertyName)
    {
        return root.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ReplaceVariables(string value, IReadOnlyDictionary<string, string> variables)
    {
        var result = value;
        foreach (var (key, replacement) in variables)
        {
            result = result.Replace("${" + key + "}", replacement, StringComparison.OrdinalIgnoreCase);
        }

        return result;
    }

    private static IReadOnlyList<string> SplitCommandLine(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return [];
        }

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var ch in commandLine)
        {
            if (ch == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(ch) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    private static void AddThirdPartyAuthArguments(
        AccountMetadata account,
        string? authlibInjectorJarPath,
        ICollection<string> arguments)
    {
        if (account.Type != AccountType.ThirdParty)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(account.AuthServerUrl))
        {
            throw new InvalidOperationException("皮肤站账号缺少认证服务器地址。");
        }

        if (string.IsNullOrWhiteSpace(authlibInjectorJarPath))
        {
            throw new InvalidOperationException("请先在设置中填写 authlib-injector.jar 路径。");
        }

        var jarPath = Environment.ExpandEnvironmentVariables(authlibInjectorJarPath.Trim('"', ' '));
        if (!File.Exists(jarPath))
        {
            throw new FileNotFoundException("没有找到 authlib-injector.jar。", jarPath);
        }

        arguments.Add($"-javaagent:{Path.GetFullPath(jarPath)}={account.AuthServerUrl}");

        if (!string.IsNullOrWhiteSpace(account.AuthServerMetadataBase64))
        {
            arguments.Add("-Dauthlibinjector.yggdrasil.prefetched=" + account.AuthServerMetadataBase64);
        }
    }

    private sealed record LaunchPlan(string WorkingDirectory, IReadOnlyList<string> Arguments);
}
