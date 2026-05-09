using System.Diagnostics;
using System.Text;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Remote;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Updates;

public sealed class LauncherUpdateService
{
    public const string FixedLauncherUpdateManifestUrl =
        "https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/launcher-update.json";

    private readonly LauncherPaths _paths;
    private readonly RemoteConfigClient _remoteConfigClient;
    private readonly HttpDownloadService _downloadService;

    public LauncherUpdateService(
        LauncherPaths paths,
        RemoteConfigClient remoteConfigClient,
        HttpDownloadService downloadService)
    {
        _paths = paths;
        _remoteConfigClient = remoteConfigClient;
        _downloadService = downloadService;
    }

    public async Task<LauncherUpdateCheckResult?> CheckAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _remoteConfigClient.GetLauncherUpdateManifestAsync(
            FixedLauncherUpdateManifestUrl,
            cancellationToken);

        if (!manifest.Enabled ||
            string.IsNullOrWhiteSpace(manifest.Version) ||
            !IsNewerVersion(manifest.Version, currentVersion))
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(manifest.WindowsX64Url) ||
            string.IsNullOrWhiteSpace(manifest.WindowsX64Sha256))
        {
            throw new InvalidDataException("启动器更新配置缺少 Windows x64 下载地址或 SHA256。");
        }

        return new LauncherUpdateCheckResult
        {
            CurrentVersion = currentVersion,
            Manifest = manifest
        };
    }

    public async Task<string> DownloadWindowsPackageAsync(
        LauncherUpdateCheckResult update,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? speedLimitKbPerSecond = null)
    {
        var downloadsDirectory = Path.Combine(_paths.DownloadsPath, "launcher");
        Directory.CreateDirectory(downloadsDirectory);

        var packagePath = Path.Combine(
            downloadsDirectory,
            $"DreamLauncher-{SanitizeFileName(update.Manifest.Version)}-win-x64.zip");

        await _downloadService.DownloadFileAsync(
            update.Manifest.WindowsX64Url,
            packagePath,
            update.Manifest.WindowsX64Sha256,
            maxRetryCount,
            progress,
            cancellationToken,
            speedLimitKbPerSecond);

        return packagePath;
    }

    public async Task StartWindowsApplyAndRestartAsync(
        string packagePath,
        string targetDirectory,
        string executablePath,
        int processId,
        CancellationToken cancellationToken = default)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("启动器自动替换目前只支持 Windows。");
        }

        packagePath = Path.GetFullPath(packagePath);
        targetDirectory = Path.GetFullPath(targetDirectory);
        executablePath = Path.GetFullPath(executablePath);

        if (!File.Exists(packagePath))
        {
            throw new FileNotFoundException("启动器更新包不存在。", packagePath);
        }

        if (!File.Exists(executablePath))
        {
            throw new FileNotFoundException("找不到当前启动器可执行文件。", executablePath);
        }

        var updateDirectory = Path.Combine(_paths.CachePath, "updates");
        Directory.CreateDirectory(updateDirectory);

        var scriptPath = Path.Combine(updateDirectory, "apply-launcher-update.ps1");
        var stagingDirectory = Path.Combine(updateDirectory, "staging");
        var logPath = Path.Combine(_paths.LogsPath, "launcher-update.log");

        await File.WriteAllTextAsync(
            scriptPath,
            CreateWindowsUpdateScript(),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);

        var systemDirectory = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var powerShellPath = Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

        var startInfo = new ProcessStartInfo
        {
            FileName = File.Exists(powerShellPath) ? powerShellPath : "powershell.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        };

        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-PackagePath");
        startInfo.ArgumentList.Add(packagePath);
        startInfo.ArgumentList.Add("-TargetDirectory");
        startInfo.ArgumentList.Add(targetDirectory);
        startInfo.ArgumentList.Add("-ExecutablePath");
        startInfo.ArgumentList.Add(executablePath);
        startInfo.ArgumentList.Add("-ProcessId");
        startInfo.ArgumentList.Add(processId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("-StagingDirectory");
        startInfo.ArgumentList.Add(stagingDirectory);
        startInfo.ArgumentList.Add("-LogPath");
        startInfo.ArgumentList.Add(logPath);

        if (Process.Start(startInfo) is null)
        {
            throw new InvalidOperationException("无法启动更新替换进程。");
        }
    }

    private static bool IsNewerVersion(string candidateVersion, string currentVersion)
    {
        var candidate = NormalizeVersion(candidateVersion);
        var current = NormalizeVersion(currentVersion);

        if (Version.TryParse(candidate, out var candidateParsed) &&
            Version.TryParse(current, out var currentParsed))
        {
            return candidateParsed > currentParsed;
        }

        return string.Compare(candidateVersion, currentVersion, StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static string NormalizeVersion(string version)
    {
        var value = version.Trim().TrimStart('v', 'V');
        var metadataIndex = value.IndexOf('+', StringComparison.Ordinal);
        if (metadataIndex >= 0)
        {
            value = value[..metadataIndex];
        }

        var preReleaseIndex = value.IndexOf('-', StringComparison.Ordinal);
        if (preReleaseIndex >= 0)
        {
            value = value[..preReleaseIndex];
        }

        return value;
    }

    private static string SanitizeFileName(string value)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        var builder = new StringBuilder(value.Length);
        foreach (var character in value)
        {
            builder.Append(invalidChars.Contains(character) ? '_' : character);
        }

        return builder.Length == 0 ? "update" : builder.ToString();
    }

    private static string CreateWindowsUpdateScript()
    {
        return """
param(
    [Parameter(Mandatory = $true)][string]$PackagePath,
    [Parameter(Mandatory = $true)][string]$TargetDirectory,
    [Parameter(Mandatory = $true)][string]$ExecutablePath,
    [Parameter(Mandatory = $true)][int]$ProcessId,
    [Parameter(Mandatory = $true)][string]$StagingDirectory,
    [Parameter(Mandatory = $true)][string]$LogPath
)

$ErrorActionPreference = 'Stop'

function Write-UpdateLog {
    param([string]$Message)
    $directory = Split-Path -Parent $LogPath
    if ($directory -and !(Test-Path -LiteralPath $directory)) {
        New-Item -ItemType Directory -Path $directory -Force | Out-Null
    }
    Add-Content -LiteralPath $LogPath -Value "[$(Get-Date -Format o)] $Message"
}

function Copy-DirectoryContent {
    param(
        [Parameter(Mandatory = $true)][string]$Source,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    if (!(Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }

    Get-ChildItem -LiteralPath $Source -Force | ForEach-Object {
        $target = Join-Path $Destination $_.Name
        if ($_.PSIsContainer) {
            Copy-DirectoryContent -Source $_.FullName -Destination $target
        }
        else {
            Copy-Item -LiteralPath $_.FullName -Destination $target -Force
        }
    }
}

try {
    Write-UpdateLog 'Waiting launcher process to exit.'
    if ($ProcessId -gt 0) {
        Wait-Process -Id $ProcessId -ErrorAction SilentlyContinue
    }

    Start-Sleep -Milliseconds 800

    if (Test-Path -LiteralPath $StagingDirectory) {
        Remove-Item -LiteralPath $StagingDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $StagingDirectory -Force | Out-Null

    Write-UpdateLog 'Extracting update package.'
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $StagingDirectory -Force

    $source = $StagingDirectory
    $children = @(Get-ChildItem -LiteralPath $StagingDirectory -Force)
    if ($children.Count -eq 1 -and $children[0].PSIsContainer) {
        $source = $children[0].FullName
    }

    Write-UpdateLog "Copying files from $source to $TargetDirectory."
    Copy-DirectoryContent -Source $source -Destination $TargetDirectory

    Remove-Item -LiteralPath $StagingDirectory -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue

    Write-UpdateLog 'Restarting launcher.'
    Start-Process -FilePath $ExecutablePath -WorkingDirectory $TargetDirectory
}
catch {
    Write-UpdateLog "Update failed: $($_.Exception.Message)"
    try {
        if (Test-Path -LiteralPath $ExecutablePath) {
            Start-Process -FilePath $ExecutablePath -WorkingDirectory $TargetDirectory
        }
    }
    catch {
        Write-UpdateLog "Restart failed: $($_.Exception.Message)"
    }
    exit 1
}
""";
    }
}
