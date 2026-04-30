using System.Diagnostics;
using System.Security.Cryptography;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Downloads;

public sealed class HttpDownloadService
{
    private readonly HttpClient _httpClient;

    public HttpDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient();
        _httpClient.Timeout = TimeSpan.FromMinutes(30);
    }

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string expectedSha256,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = UrlSecurity.RequireHttps(url, nameof(url));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var retryCount = Math.Max(1, maxRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(source, destinationPath, progress, cancellationToken);

                progress?.Report(new LauncherOperationProgress
                {
                    Stage = "verify",
                    Message = "正在校验 SHA256",
                    Progress = null
                });

                await Sha256Hasher.VerifyFileAsync(destinationPath, expectedSha256, cancellationToken);
                return;
            }
            catch when (attempt < retryCount && !cancellationToken.IsCancellationRequested)
            {
                TryDelete(destinationPath);
                TryDelete(destinationPath + ".download");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    public async Task DownloadFileWithSha1Async(
        string url,
        string destinationPath,
        string expectedSha1,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var source = UrlSecurity.RequireHttps(url, nameof(url));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var retryCount = Math.Max(1, maxRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(source, destinationPath, progress, cancellationToken);

                progress?.Report(new LauncherOperationProgress
                {
                    Stage = "verify",
                    Message = "正在校验 SHA1",
                    Progress = null
                });

                await VerifySha1FileAsync(destinationPath, expectedSha1, cancellationToken);
                return;
            }
            catch when (attempt < retryCount && !cancellationToken.IsCancellationRequested)
            {
                TryDelete(destinationPath);
                TryDelete(destinationPath + ".download");
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken);
            }
        }
    }

    private async Task DownloadOnceAsync(
        Uri source,
        string destinationPath,
        IProgress<LauncherOperationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var tempPath = destinationPath + ".download";
        TryDelete(tempPath);

        using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var stopwatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long completed = 0;
        var buffer = new byte[128 * 1024];

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            completed += read;

            if (stopwatch.Elapsed - lastReport > TimeSpan.FromMilliseconds(250))
            {
                lastReport = stopwatch.Elapsed;
                ReportProgress(progress, completed, totalBytes, stopwatch.Elapsed);
            }
        }

        ReportProgress(progress, completed, totalBytes, stopwatch.Elapsed);
        output.Close();

        TryDelete(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    private static void ReportProgress(
        IProgress<LauncherOperationProgress>? progress,
        long completed,
        long? totalBytes,
        TimeSpan elapsed)
    {
        var speed = elapsed.TotalSeconds <= 0 ? 0 : completed / elapsed.TotalSeconds;
        var remaining = totalBytes.HasValue && speed > 0
            ? TimeSpan.FromSeconds(Math.Max(0, (totalBytes.Value - completed) / speed))
            : (TimeSpan?)null;

        progress?.Report(new LauncherOperationProgress
        {
            Stage = "download",
            Message = "正在下载文件",
            Progress = totalBytes.HasValue && totalBytes.Value > 0 ? (double)completed / totalBytes.Value : null,
            BytesCompleted = completed,
            TotalBytes = totalBytes,
            SpeedBytesPerSecond = speed,
            EstimatedRemaining = remaining
        });
    }

    private static async Task VerifySha1FileAsync(
        string filePath,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(expectedSha1))
        {
            throw new InvalidOperationException("缺少 SHA1 校验值。");
        }

        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha1.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(filePath);
            throw new InvalidDataException("文件 SHA1 校验失败，下载文件已删除。");
        }
    }

    private static void TryDelete(string path)
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
            // Best-effort cleanup. The next write will surface a useful error if the file is locked.
        }
    }
}
