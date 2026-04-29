using System.IO.Compression;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Archives;

public sealed class SafeZipExtractor
{
    public async Task ExtractAsync(
        string archivePath,
        string destinationDirectory,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("客户端压缩包不存在。", archivePath);
        }

        Directory.CreateDirectory(destinationDirectory);
        var destinationRoot = Path.GetFullPath(destinationDirectory);
        using var archive = ZipFile.OpenRead(archivePath);

        var entries = archive.Entries.Where(entry => !IsDirectoryEntry(entry)).ToArray();
        var totalBytes = entries.Sum(entry => Math.Max(0L, entry.Length));
        EnsureDiskSpace(destinationRoot, totalBytes);

        long completedBytes = 0;
        var completedEntries = 0;

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = GetSafeEntryPath(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            await using var source = entry.Open();
            await using var target = new FileStream(
                targetPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            var buffer = new byte[128 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                completedBytes += read;

                progress?.Report(new LauncherOperationProgress
                {
                    Stage = "extract",
                    Message = $"正在解压 {entry.FullName}",
                    Progress = totalBytes == 0 ? null : (double)completedBytes / totalBytes,
                    BytesCompleted = completedBytes,
                    TotalBytes = totalBytes
                });
            }

            completedEntries++;
            progress?.Report(new LauncherOperationProgress
            {
                Stage = "extract",
                Message = $"已解压 {completedEntries}/{entries.Length} 个文件",
                Progress = entries.Length == 0 ? 1 : (double)completedEntries / entries.Length,
                BytesCompleted = completedBytes,
                TotalBytes = totalBytes
            });
        }
    }

    private static string GetSafeEntryPath(string destinationRoot, string entryName)
    {
        var normalizedEntry = entryName.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var targetPath = Path.GetFullPath(Path.Combine(destinationRoot, normalizedEntry));

        var rootWithSeparator = destinationRoot.EndsWith(Path.DirectorySeparatorChar)
            ? destinationRoot
            : destinationRoot + Path.DirectorySeparatorChar;

        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!targetPath.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException("压缩包包含不安全的路径，已停止解压。");
        }

        return targetPath;
    }

    private static bool IsDirectoryEntry(ZipArchiveEntry entry)
    {
        return entry.FullName.EndsWith("/", StringComparison.Ordinal) ||
               entry.FullName.EndsWith("\\", StringComparison.Ordinal);
    }

    private static void EnsureDiskSpace(string destinationRoot, long requiredBytes)
    {
        if (requiredBytes <= 0)
        {
            return;
        }

        var root = Path.GetPathRoot(destinationRoot);
        if (string.IsNullOrWhiteSpace(root))
        {
            return;
        }

        var drive = new DriveInfo(root);
        var safetyMargin = Math.Max(512L * 1024 * 1024, requiredBytes / 10);
        if (drive.AvailableFreeSpace < requiredBytes + safetyMargin)
        {
            throw new IOException("磁盘空间不足，无法解压客户端。");
        }
    }
}
