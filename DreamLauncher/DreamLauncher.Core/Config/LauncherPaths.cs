using System.Security;
using DreamLauncher.Models.Clients;

namespace DreamLauncher.Core.Config;

public sealed class LauncherPaths
{
    public LauncherPaths(string? rootPath = null)
    {
        RootPath = string.IsNullOrWhiteSpace(rootPath)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DreamLauncher")
            : Path.GetFullPath(rootPath);

        ConfigPath = Path.Combine(RootPath, "config.json");
        ClientsPath = Path.Combine(RootPath, "clients");
        RuntimePath = Path.Combine(RootPath, "runtime");
        JavaRuntimePath = Path.Combine(RuntimePath, "java");
        CachePath = Path.Combine(RootPath, "cache");
        DownloadsPath = Path.Combine(CachePath, "downloads");
        PackDownloadsPath = Path.Combine(DownloadsPath, "packs");
        JavaDownloadsPath = Path.Combine(DownloadsPath, "java");
        ImagesCachePath = Path.Combine(CachePath, "images");
        LogsPath = Path.Combine(RootPath, "logs");
    }

    public string RootPath { get; }

    public string ConfigPath { get; }

    public string ClientsPath { get; }

    public string RuntimePath { get; }

    public string JavaRuntimePath { get; }

    public string CachePath { get; }

    public string DownloadsPath { get; }

    public string PackDownloadsPath { get; }

    public string JavaDownloadsPath { get; }

    public string ImagesCachePath { get; }

    public string LogsPath { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootPath);
        Directory.CreateDirectory(ClientsPath);
        Directory.CreateDirectory(JavaRuntimePath);
        Directory.CreateDirectory(PackDownloadsPath);
        Directory.CreateDirectory(JavaDownloadsPath);
        Directory.CreateDirectory(ImagesCachePath);
        Directory.CreateDirectory(LogsPath);
    }

    public string GetClientDirectory(ClientDefinition client)
    {
        var installDir = string.IsNullOrWhiteSpace(client.InstallDir) ? client.Id : client.InstallDir;
        return EnsureChildPath(ClientsPath, installDir);
    }

    public string GetClientConfigPath(ClientDefinition client)
    {
        return Path.Combine(GetClientDirectory(client), "client.json");
    }

    public string GetPrivateJavaDirectory(int majorVersion)
    {
        return EnsureChildPath(JavaRuntimePath, $"jre{majorVersion}");
    }

    public static string EnsureChildPath(string rootDirectory, string childPath)
    {
        if (string.IsNullOrWhiteSpace(childPath))
        {
            throw new ArgumentException("路径不能为空。", nameof(childPath));
        }

        var root = Path.GetFullPath(rootDirectory);
        var combined = Path.IsPathRooted(childPath)
            ? Path.GetFullPath(childPath)
            : Path.GetFullPath(Path.Combine(root, childPath));

        var rootWithSeparator = root.EndsWith(Path.DirectorySeparatorChar)
            ? root
            : root + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!combined.StartsWith(rootWithSeparator, comparison))
        {
            throw new SecurityException("目标路径越过了启动器允许管理的目录。");
        }

        return combined;
    }
}
