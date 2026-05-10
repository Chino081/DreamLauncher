using System.IO.Compression;
using System.Diagnostics;
using System.Text;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Archives;

public sealed class SafeZipExtractor
{
    private static readonly Lazy<Encoding> ChineseZipEncoding = new(CreateChineseZipEncoding);

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
        using var archive = OpenReadArchive(archivePath);

        var entries = archive.Entries.Where(entry => !IsDirectoryEntry(entry)).ToArray();
        var totalBytes = entries.Sum(entry => Math.Max(0L, entry.Length));
        EnsureDiskSpace(destinationRoot, totalBytes);

        long completedBytes = 0;
        var completedEntries = 0;
        var progressReporter = new ExtractionProgressReporter(progress);

        foreach (var entry in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = GetSafeEntryPath(destinationRoot, entry.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var entryLength = Math.Max(1L, entry.Length);
            long entryRead = 0;

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
                entryRead += read;

                var entryFraction = Math.Min(1.0, (double)entryRead / entryLength);
                double? fileProgress = entries.Length == 0
                    ? null
                    : (double)(completedEntries + entryFraction) / entries.Length;

                progressReporter.Report(
                    $"正在解压 {completedEntries + 1}/{entries.Length} 个文件",
                    fileProgress,
                    completedBytes,
                    totalBytes,
                    force: false);
            }

            completedEntries++;
            progressReporter.Report(
                $"已解压 {completedEntries}/{entries.Length} 个文件",
                entries.Length == 0 ? 1 : (double)completedEntries / entries.Length,
                completedBytes,
                totalBytes,
                force: completedEntries == entries.Length);
        }
    }

    public async Task ExtractMinecraftContentAsync(
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
        using var archive = OpenReadArchive(archivePath);

        var entries = archive.Entries
            .Where(entry => !IsDirectoryEntry(entry))
            .Select(entry => new
            {
                Entry = entry,
                RelativePath = TryGetMinecraftRelativePath(entry.FullName)
            })
            .Where(item => !string.IsNullOrWhiteSpace(item.RelativePath))
            .ToArray();

        if (entries.Length == 0)
        {
            throw new InvalidDataException("压缩包中没有找到 .minecraft 文件夹。");
        }

        var totalBytes = entries.Sum(item => Math.Max(0L, item.Entry.Length));
        EnsureDiskSpace(destinationRoot, totalBytes);

        long completedBytes = 0;
        var completedEntries = 0;
        var progressReporter = new ExtractionProgressReporter(progress);

        foreach (var item in entries)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var targetPath = GetSafeEntryPath(destinationRoot, item.RelativePath!);
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            var entryLength = Math.Max(1L, item.Entry.Length);
            long entryRead = 0;

            await using var source = item.Entry.Open();
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
                entryRead += read;

                var entryFraction = Math.Min(1.0, (double)entryRead / entryLength);
                double? fileProgress = entries.Length == 0
                    ? null
                    : (double)(completedEntries + entryFraction) / entries.Length;

                progressReporter.Report(
                    $"正在解压 {completedEntries + 1}/{entries.Length} 个文件",
                    fileProgress,
                    completedBytes,
                    totalBytes,
                    force: false);
            }

            completedEntries++;
            progressReporter.Report(
                $"已解压 {completedEntries}/{entries.Length} 个文件",
                entries.Length == 0 ? 1 : (double)completedEntries / entries.Length,
                completedBytes,
                totalBytes,
                force: completedEntries == entries.Length);
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

    private static string? TryGetMinecraftRelativePath(string entryName)
    {
        var normalized = entryName.Replace('\\', '/').Trim('/');
        var parts = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var minecraftIndex = Array.FindIndex(parts, part =>
            string.Equals(part, ".minecraft", StringComparison.OrdinalIgnoreCase));

        if (minecraftIndex < 0 || minecraftIndex >= parts.Length - 1)
        {
            return null;
        }

        return string.Join(Path.DirectorySeparatorChar, parts.Skip(minecraftIndex + 1));
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

    private static ZipArchive OpenReadArchive(string archivePath)
    {
        // Many Chinese ZIP tools store non-UTF-8 entry names as GBK/GB18030.
        // ZipArchive still respects the UTF-8 flag, so UTF-8 archives remain safe.
        return ZipFile.Open(archivePath, ZipArchiveMode.Read, ChineseZipEncoding.Value);
    }

    private static Encoding CreateChineseZipEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("GB18030");
    }

    private sealed class ExtractionProgressReporter
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(120);

        private readonly IProgress<LauncherOperationProgress>? _progress;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private TimeSpan _lastReportedElapsed;

        public ExtractionProgressReporter(IProgress<LauncherOperationProgress>? progress)
        {
            _progress = progress;
        }

        public void Report(
            string message,
            double? progress,
            long completedBytes,
            long totalBytes,
            bool force)
        {
            var elapsed = _stopwatch.Elapsed;
            if (!force && elapsed - _lastReportedElapsed < ReportInterval)
            {
                return;
            }

            _lastReportedElapsed = elapsed;
            _progress?.Report(new LauncherOperationProgress
            {
                Stage = "extract",
                Message = message,
                Progress = progress,
                BytesCompleted = completedBytes,
                TotalBytes = totalBytes
            });
        }
    }
}
