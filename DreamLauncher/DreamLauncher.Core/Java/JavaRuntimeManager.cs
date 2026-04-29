using System.Diagnostics;
using System.Text.RegularExpressions;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Models.Java;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Java;

public sealed partial class JavaRuntimeManager
{
    private readonly LauncherPaths _paths;
    private readonly HttpDownloadService _downloadService;
    private readonly SafeZipExtractor _extractor;

    public JavaRuntimeManager(
        LauncherPaths paths,
        HttpDownloadService downloadService,
        SafeZipExtractor extractor)
    {
        _paths = paths;
        _downloadService = downloadService;
        _extractor = extractor;
    }

    public async Task<JavaRuntimeInfo?> ResolveAsync(
        int requiredMajorVersion,
        string? manualJavaPath = null,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(manualJavaPath))
        {
            var manual = NormalizeJavaPath(manualJavaPath);
            var manualVersion = await ProbeJavaMajorVersionAsync(manual, cancellationToken);
            if (manualVersion >= requiredMajorVersion)
            {
                return new JavaRuntimeInfo
                {
                    JavaPath = manual,
                    MajorVersion = manualVersion,
                    Source = "manual"
                };
            }
        }

        var privateJava = GetPrivateJavaExecutable(requiredMajorVersion);
        if (File.Exists(privateJava))
        {
            var privateVersion = await ProbeJavaMajorVersionAsync(privateJava, cancellationToken);
            if (privateVersion >= requiredMajorVersion)
            {
                return new JavaRuntimeInfo
                {
                    JavaPath = privateJava,
                    MajorVersion = privateVersion,
                    Source = "private"
                };
            }
        }

        var systemVersion = await ProbeJavaMajorVersionAsync("java", cancellationToken);
        if (systemVersion >= requiredMajorVersion)
        {
            return new JavaRuntimeInfo
            {
                JavaPath = "java",
                MajorVersion = systemVersion,
                Source = "system"
            };
        }

        return null;
    }

    public async Task<JavaRuntimeInfo> InstallPrivateRuntimeAsync(
        JavaRuntimeDefinition runtime,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("当前 Java 运行时配置只包含 Windows x64 下载地址。");
        }

        var downloadPath = Path.Combine(_paths.JavaDownloadsPath, $"jre{runtime.Version}.zip");
        await _downloadService.DownloadFileAsync(
            runtime.WindowsX64Url,
            downloadPath,
            runtime.WindowsX64Sha256,
            maxRetryCount,
            progress,
            cancellationToken);

        var stagingDirectory = Path.Combine(_paths.JavaRuntimePath, $"jre{runtime.Version}.staging-{Guid.NewGuid():N}");
        var finalDirectory = _paths.GetPrivateJavaDirectory(runtime.Version);

        try
        {
            await _extractor.ExtractAsync(downloadPath, stagingDirectory, progress, cancellationToken);
            var javaHome = FindJavaHome(stagingDirectory);

            SafeDirectory.DeleteChildDirectory(_paths.JavaRuntimePath, finalDirectory);
            if (string.Equals(Path.GetFullPath(javaHome), Path.GetFullPath(stagingDirectory), PathComparison))
            {
                Directory.Move(stagingDirectory, finalDirectory);
            }
            else
            {
                Directory.Move(javaHome, finalDirectory);
                SafeDirectory.DeleteChildDirectory(_paths.JavaRuntimePath, stagingDirectory);
            }
        }
        catch
        {
            if (Directory.Exists(stagingDirectory))
            {
                SafeDirectory.DeleteChildDirectory(_paths.JavaRuntimePath, stagingDirectory);
            }

            throw;
        }

        var javaPath = GetPrivateJavaExecutable(runtime.Version);
        var version = await ProbeJavaMajorVersionAsync(javaPath, cancellationToken);
        if (version < runtime.Version)
        {
            throw new InvalidOperationException("下载的 Java 运行时版本不符合客户端要求。");
        }

        return new JavaRuntimeInfo
        {
            JavaPath = javaPath,
            MajorVersion = version,
            Source = "private"
        };
    }

    private string GetPrivateJavaExecutable(int majorVersion)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        return Path.Combine(_paths.GetPrivateJavaDirectory(majorVersion), "bin", executableName);
    }

    private static string NormalizeJavaPath(string value)
    {
        var path = Environment.ExpandEnvironmentVariables(value.Trim('"', ' '));
        if (File.Exists(path))
        {
            return path;
        }

        if (Directory.Exists(path))
        {
            var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
            return Path.Combine(path, "bin", executableName);
        }

        return path;
    }

    private static async Task<int> ProbeJavaMajorVersionAsync(
        string javaPath,
        CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                ArgumentList = { "-version" },
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            var versionText = await outputTask + await errorTask;
            return ParseJavaMajorVersion(versionText);
        }
        catch
        {
            return 0;
        }
    }

    private static int ParseJavaMajorVersion(string versionText)
    {
        var match = JavaVersionRegex().Match(versionText);
        if (!match.Success)
        {
            return 0;
        }

        var version = match.Groups["version"].Value;
        if (version.StartsWith("1.8", StringComparison.Ordinal))
        {
            return 8;
        }

        var majorText = version.Split('.')[0];
        return int.TryParse(majorText, out var major) ? major : 0;
    }

    private static string FindJavaHome(string rootDirectory)
    {
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        var javaExecutable = Directory
            .EnumerateFiles(rootDirectory, executableName, SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));

        if (javaExecutable is null)
        {
            throw new InvalidDataException("Java 压缩包中没有找到 bin/java。");
        }

        return Directory.GetParent(javaExecutable)!.Parent!.FullName;
    }

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    [GeneratedRegex(@"version\s+""(?<version>[^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();
}
