using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

        var launchJavaPath = GetJavaLauncherPath(java.JavaPath);
        var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = launchJavaPath,
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
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

    private static string GetJavaLauncherPath(string javaPath)
    {
        if (!OperatingSystem.IsWindows())
        {
            return javaPath;
        }

        if (string.Equals(javaPath, "java", StringComparison.OrdinalIgnoreCase))
        {
            return "javaw";
        }

        var fileName = Path.GetFileName(javaPath);
        if (string.Equals(fileName, "javaw.exe", StringComparison.OrdinalIgnoreCase))
        {
            return javaPath;
        }

        if (!string.Equals(fileName, "java.exe", StringComparison.OrdinalIgnoreCase))
        {
            return javaPath;
        }

        var directory = Path.GetDirectoryName(javaPath);
        if (string.IsNullOrWhiteSpace(directory))
        {
            return javaPath;
        }

        var javawPath = Path.Combine(directory, "javaw.exe");
        return File.Exists(javawPath) ? javawPath : javaPath;
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
        var nativesDirectory = Path.Combine(minecraftDirectory, "natives", versionId, GetNativeRuntimeId());
        Directory.CreateDirectory(gameDirectory);
        Directory.CreateDirectory(nativesDirectory);
        ExtractNativeLibraries(minecraftDirectory, root, nativesDirectory, cancellationToken);

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

        var fallbackAuthlibInjectorJarPath = Path.Combine(_paths.RuntimePath, "authlib-injector.jar");
        var resolvedAuthlibInjectorJarPath = string.IsNullOrWhiteSpace(authlibInjectorJarPath) &&
                                             File.Exists(fallbackAuthlibInjectorJarPath)
            ? fallbackAuthlibInjectorJarPath
            : authlibInjectorJarPath;

        AddThirdPartyAuthArguments(
            account,
            resolvedAuthlibInjectorJarPath,
            fallbackAuthlibInjectorJarPath,
            arguments);
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

            if (rule.TryGetProperty("os", out var os))
            {
                osMatches = IsCurrentOsMatch(os);
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

    private static void ExtractNativeLibraries(
        string minecraftDirectory,
        JsonElement root,
        string nativesDirectory,
        CancellationToken cancellationToken)
    {
        if (!root.TryGetProperty("libraries", out var libraries) || libraries.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        var librariesDirectory = Path.Combine(minecraftDirectory, "libraries");
        foreach (var library in libraries.EnumerateArray())
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (!IsAllowedByRules(library) ||
                !TryResolveNativeLibraryPath(librariesDirectory, library, out var nativeLibraryPath))
            {
                continue;
            }

            if (!File.Exists(nativeLibraryPath))
            {
                throw new FileNotFoundException("缺少当前系统需要的 Minecraft natives 文件。", nativeLibraryPath);
            }

            ExtractNativeArchive(nativeLibraryPath, nativesDirectory, ReadNativeExtractExcludes(library), cancellationToken);
        }
    }

    private static bool TryResolveNativeLibraryPath(
        string librariesDirectory,
        JsonElement library,
        out string nativeLibraryPath)
    {
        nativeLibraryPath = "";

        if (!library.TryGetProperty("natives", out var natives) ||
            natives.ValueKind != JsonValueKind.Object ||
            !natives.TryGetProperty(GetMinecraftOsName(), out var classifierTemplate) ||
            classifierTemplate.ValueKind != JsonValueKind.String)
        {
            return false;
        }

        var classifier = classifierTemplate.GetString();
        if (string.IsNullOrWhiteSpace(classifier))
        {
            return false;
        }

        foreach (var candidate in BuildNativeClassifierCandidates(classifier))
        {
            var path = ReadNestedString(library, "downloads", "classifiers", candidate, "path")
                ?? BuildLibraryPathFromName(library, candidate);

            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            nativeLibraryPath = Path.Combine(librariesDirectory, path.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(nativeLibraryPath))
            {
                return true;
            }
        }

        var fallbackClassifier = classifier.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.Ordinal);
        var fallbackPath = ReadNestedString(library, "downloads", "classifiers", fallbackClassifier, "path")
            ?? BuildLibraryPathFromName(library, fallbackClassifier);

        if (string.IsNullOrWhiteSpace(fallbackPath))
        {
            return false;
        }

        nativeLibraryPath = Path.Combine(librariesDirectory, fallbackPath.Replace('/', Path.DirectorySeparatorChar));
        return true;
    }

    private static IEnumerable<string> BuildNativeClassifierCandidates(string classifierTemplate)
    {
        var classifier = classifierTemplate.Replace("${arch}", Environment.Is64BitOperatingSystem ? "64" : "32", StringComparison.Ordinal);
        var architecture = RuntimeInformation.ProcessArchitecture;

        if (OperatingSystem.IsMacOS() && architecture == Architecture.Arm64)
        {
            yield return classifier.Replace("natives-macos", "natives-macos-arm64", StringComparison.OrdinalIgnoreCase);
            yield return classifier.Replace("natives-osx", "natives-osx-arm64", StringComparison.OrdinalIgnoreCase);
        }
        else if (OperatingSystem.IsLinux() && architecture == Architecture.Arm64)
        {
            yield return classifier.Replace("natives-linux", "natives-linux-arm64", StringComparison.OrdinalIgnoreCase);
            yield return classifier.Replace("natives-linux", "natives-linux-aarch64", StringComparison.OrdinalIgnoreCase);
        }
        else if (OperatingSystem.IsWindows() && architecture == Architecture.Arm64)
        {
            yield return classifier.Replace("natives-windows", "natives-windows-arm64", StringComparison.OrdinalIgnoreCase);
        }

        yield return classifier;
    }

    private static string? BuildLibraryPathFromName(JsonElement library, string classifier)
    {
        var basePath = BuildLibraryPathFromName(library);
        if (basePath is null)
        {
            return null;
        }

        var directory = Path.GetDirectoryName(basePath)?.Replace('\\', '/');
        var fileName = Path.GetFileNameWithoutExtension(basePath);
        return string.IsNullOrWhiteSpace(directory)
            ? $"{fileName}-{classifier}.jar"
            : $"{directory}/{fileName}-{classifier}.jar";
    }

    private static IReadOnlyList<string> ReadNativeExtractExcludes(JsonElement library)
    {
        if (!library.TryGetProperty("extract", out var extract) ||
            !extract.TryGetProperty("exclude", out var excludes) ||
            excludes.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return excludes
            .EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToArray();
    }

    private static void ExtractNativeArchive(
        string nativeLibraryPath,
        string nativesDirectory,
        IReadOnlyList<string> excludes,
        CancellationToken cancellationToken)
    {
        var destinationRoot = Path.GetFullPath(nativesDirectory);
        Directory.CreateDirectory(destinationRoot);

        using var archive = ZipFile.OpenRead(nativeLibraryPath);
        foreach (var entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
                entry.FullName.EndsWith("\\", StringComparison.Ordinal) ||
                IsNativeEntryExcluded(entry.FullName, excludes))
            {
                continue;
            }

            var targetPath = GetSafeNativeEntryPath(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            entry.ExtractToFile(targetPath, overwrite: true);
        }
    }

    private static bool IsNativeEntryExcluded(string entryName, IReadOnlyList<string> excludes)
    {
        var normalizedEntry = entryName.Replace('\\', '/').TrimStart('/');
        return excludes.Any(exclude =>
        {
            var normalizedExclude = exclude.Replace('\\', '/').TrimStart('/');
            return normalizedEntry.StartsWith(normalizedExclude, StringComparison.OrdinalIgnoreCase);
        });
    }

    private static string GetSafeNativeEntryPath(string destinationRoot, string entryName)
    {
        var normalizedEntry = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntry));

        var rootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!targetPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException("natives 压缩包包含不安全的路径，已停止解压。");
        }

        return targetPath;
    }

    private static bool IsCurrentOsMatch(JsonElement os)
    {
        if (ReadString(os, "name") is { } osName &&
            !osName.Equals(GetMinecraftOsName(), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (ReadString(os, "arch") is { } architecture &&
            !IsCurrentArchitectureMatch(architecture))
        {
            return false;
        }

        if (ReadString(os, "version") is { } versionPattern &&
            !Regex.IsMatch(Environment.OSVersion.VersionString, versionPattern, RegexOptions.IgnoreCase))
        {
            return false;
        }

        return true;
    }

    private static bool IsCurrentArchitectureMatch(string architecture)
    {
        return architecture.ToLowerInvariant() switch
        {
            "x86" => RuntimeInformation.ProcessArchitecture == Architecture.X86,
            "x86_64" or "amd64" => RuntimeInformation.ProcessArchitecture == Architecture.X64,
            "arm64" or "aarch64" => RuntimeInformation.ProcessArchitecture == Architecture.Arm64,
            "arm" => RuntimeInformation.ProcessArchitecture == Architecture.Arm,
            _ => true
        };
    }

    private static string GetMinecraftOsName()
    {
        if (OperatingSystem.IsWindows())
        {
            return "windows";
        }

        if (OperatingSystem.IsMacOS())
        {
            return "osx";
        }

        return "linux";
    }

    private static string GetNativeRuntimeId()
    {
        var osName = GetMinecraftOsName();
        var architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm64 => "arm64",
            Architecture.Arm => "arm",
            _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant()
        };

        return $"{osName}-{architecture}";
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
        string fallbackAuthlibInjectorJarPath,
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
            throw new InvalidOperationException($"缺少 authlib-injector.jar，请把文件放到：{fallbackAuthlibInjectorJarPath}");
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
