using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using DreamLauncher.Core.Security;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Core.Downloads;

public sealed class HttpDownloadService
{
    private const int BufferSize = 256 * 1024;
    private const int DefaultParallelSegmentCount = 16;
    private const long MinParallelDownloadSize = 8L * 1024 * 1024;
    private const long MinSegmentSize = 2L * 1024 * 1024;

    private static readonly SocketsHttpHandler SharedHandler = new()
    {
        MaxConnectionsPerServer = 200,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
        AllowAutoRedirect = true
    };

    private readonly HttpClient _httpClient;

    public HttpDownloadService(HttpClient? httpClient = null)
    {
        _httpClient = httpClient ?? new HttpClient(SharedHandler, disposeHandler: false);
        _httpClient.Timeout = Timeout.InfiniteTimeSpan;
        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("DreamLauncher/0.1");
        }
    }

    public async Task DownloadFileAsync(
        string url,
        string destinationPath,
        string expectedSha256,
        int maxRetryCount,
        IProgress<LauncherOperationProgress>? progress = null,
        CancellationToken cancellationToken = default,
        int? speedLimitKbPerSecond = null)
    {
        var source = UrlSecurity.RequireHttps(url, nameof(url));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var retryCount = Math.Max(1, maxRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(
                    source,
                    destinationPath,
                    progress,
                    cancellationToken,
                    speedLimitKbPerSecond);

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
        CancellationToken cancellationToken = default,
        int? speedLimitKbPerSecond = null)
    {
        var source = UrlSecurity.RequireHttps(url, nameof(url));
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var retryCount = Math.Max(1, maxRetryCount);

        for (var attempt = 1; attempt <= retryCount; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await DownloadOnceAsync(
                    source,
                    destinationPath,
                    progress,
                    cancellationToken,
                    speedLimitKbPerSecond);

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
        CancellationToken cancellationToken,
        int? speedLimitKbPerSecond)
    {
        var tempPath = destinationPath + ".download";
        TryDelete(tempPath);

        var probe = await ProbeDownloadAsync(source, cancellationToken);
        var rateLimiter = DownloadRateLimiter.Create(speedLimitKbPerSecond);

        if (probe.SupportsRanges && probe.TotalBytes is >= MinParallelDownloadSize)
        {
            try
            {
                await DownloadParallelAsync(
                    source,
                    tempPath,
                    probe.TotalBytes.Value,
                    progress,
                    rateLimiter,
                    cancellationToken);
            }
            catch (RangeDownloadNotSupportedException)
            {
                TryDelete(tempPath);
                await DownloadSingleAsync(
                    source,
                    tempPath,
                    probe.TotalBytes,
                    progress,
                    rateLimiter,
                    cancellationToken);
            }
        }
        else
        {
            await DownloadSingleAsync(
                source,
                tempPath,
                probe.TotalBytes,
                progress,
                rateLimiter,
                cancellationToken);
        }

        TryDelete(destinationPath);
        File.Move(tempPath, destinationPath);
    }

    private async Task<DownloadProbe> ProbeDownloadAsync(
        Uri source,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new RangeHeaderValue(0, 0);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode == HttpStatusCode.PartialContent)
        {
            return new DownloadProbe(
                SupportsRanges: true,
                TotalBytes: response.Content.Headers.ContentRange?.Length ??
                            response.Content.Headers.ContentLength);
        }

        return new DownloadProbe(
            SupportsRanges: response.Headers.AcceptRanges.Any(value =>
                string.Equals(value, "bytes", StringComparison.OrdinalIgnoreCase)),
            TotalBytes: response.Content.Headers.ContentLength);
    }

    private async Task DownloadSingleAsync(
        Uri source,
        string tempPath,
        long? knownTotalBytes,
        IProgress<LauncherOperationProgress>? progress,
        DownloadRateLimiter? rateLimiter,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(source, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength ?? knownTotalBytes;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            tempPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: BufferSize,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        var tracker = new DownloadProgressTracker(progress, totalBytes);
        var buffer = new byte[BufferSize];

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (rateLimiter is not null)
            {
                await rateLimiter.WaitAsync(read, cancellationToken);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            tracker.Add(read);
        }

        tracker.ReportFinal();
    }

    private async Task DownloadParallelAsync(
        Uri source,
        string tempPath,
        long totalBytes,
        IProgress<LauncherOperationProgress>? progress,
        DownloadRateLimiter? rateLimiter,
        CancellationToken cancellationToken)
    {
        var segmentCount = GetSegmentCount(totalBytes);
        var segments = CreateSegments(totalBytes, segmentCount).ToArray();
        var tracker = new DownloadProgressTracker(progress, totalBytes);

        await using (var output = new FileStream(
                         tempPath,
                         FileMode.Create,
                         FileAccess.Write,
                         FileShare.ReadWrite,
                         bufferSize: 1,
                         FileOptions.Asynchronous | FileOptions.RandomAccess))
        {
            output.SetLength(totalBytes);
        }

        await Task.WhenAll(segments.Select(segment =>
            DownloadSegmentAsync(
                source,
                tempPath,
                segment.Start,
                segment.End,
                tracker,
                rateLimiter,
                cancellationToken)));

        tracker.ReportFinal();
    }

    private async Task DownloadSegmentAsync(
        Uri source,
        string tempPath,
        long start,
        long end,
        DownloadProgressTracker tracker,
        DownloadRateLimiter? rateLimiter,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, source);
        request.Headers.Range = new RangeHeaderValue(start, end);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.StatusCode != HttpStatusCode.PartialContent)
        {
            throw new RangeDownloadNotSupportedException();
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            tempPath,
            FileMode.Open,
            FileAccess.Write,
            FileShare.ReadWrite,
            bufferSize: BufferSize,
            FileOptions.Asynchronous | FileOptions.RandomAccess);

        output.Seek(start, SeekOrigin.Begin);

        var remaining = end - start + 1;
        var buffer = new byte[BufferSize];
        while (remaining > 0)
        {
            var read = await input.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken);
            if (read == 0)
            {
                throw new EndOfStreamException("下载连接提前结束。");
            }

            if (rateLimiter is not null)
            {
                await rateLimiter.WaitAsync(read, cancellationToken);
            }

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            remaining -= read;
            tracker.Add(read);
        }
    }

    private static int GetSegmentCount(long totalBytes)
    {
        var countBySize = (int)Math.Max(1, totalBytes / MinSegmentSize);
        return Math.Clamp(countBySize, 1, DefaultParallelSegmentCount);
    }

    private static IEnumerable<DownloadSegment> CreateSegments(long totalBytes, int segmentCount)
    {
        var segmentSize = totalBytes / segmentCount;
        for (var index = 0; index < segmentCount; index++)
        {
            var start = index * segmentSize;
            var end = index == segmentCount - 1
                ? totalBytes - 1
                : start + segmentSize - 1;

            yield return new DownloadSegment(start, end);
        }
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

    private sealed class DownloadProgressTracker
    {
        private static readonly TimeSpan ReportInterval = TimeSpan.FromMilliseconds(250);

        private readonly IProgress<LauncherOperationProgress>? _progress;
        private readonly long? _totalBytes;
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private readonly object _reportLock = new();
        private long _completed;
        private long _lastReportedBytes;
        private TimeSpan _lastReportedElapsed;
        private double _lastSpeed;

        public DownloadProgressTracker(
            IProgress<LauncherOperationProgress>? progress,
            long? totalBytes)
        {
            _progress = progress;
            _totalBytes = totalBytes;
        }

        public void Add(int bytes)
        {
            var completed = Interlocked.Add(ref _completed, bytes);
            Report(completed, force: false);
        }

        public void ReportFinal()
        {
            Report(Interlocked.Read(ref _completed), force: true);
        }

        private void Report(long completed, bool force)
        {
            var elapsed = _stopwatch.Elapsed;
            if (!force && elapsed - _lastReportedElapsed < ReportInterval)
            {
                return;
            }

            lock (_reportLock)
            {
                elapsed = _stopwatch.Elapsed;
                if (!force && elapsed - _lastReportedElapsed < ReportInterval)
                {
                    return;
                }

                var deltaBytes = completed - _lastReportedBytes;
                var deltaSeconds = (elapsed - _lastReportedElapsed).TotalSeconds;
                if (deltaSeconds > 0 && deltaBytes >= 0)
                {
                    _lastSpeed = deltaBytes / deltaSeconds;
                }

                _lastReportedBytes = completed;
                _lastReportedElapsed = elapsed;

                var speed = _lastSpeed > 0
                    ? _lastSpeed
                    : elapsed.TotalSeconds <= 0 ? 0 : completed / elapsed.TotalSeconds;

                var remaining = _totalBytes.HasValue && speed > 0
                    ? TimeSpan.FromSeconds(Math.Max(0, (_totalBytes.Value - completed) / speed))
                    : (TimeSpan?)null;

                _progress?.Report(new LauncherOperationProgress
                {
                    Stage = "download",
                    Message = "正在下载文件",
                    Progress = _totalBytes.HasValue && _totalBytes.Value > 0
                        ? (double)completed / _totalBytes.Value
                        : null,
                    BytesCompleted = completed,
                    TotalBytes = _totalBytes,
                    SpeedBytesPerSecond = speed,
                    EstimatedRemaining = remaining
                });
            }
        }
    }

    private sealed class DownloadRateLimiter
    {
        private readonly double _bytesPerSecond;
        private readonly SemaphoreSlim _gate = new(1, 1);
        private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
        private double _availableBytes;
        private TimeSpan _lastTick;

        private DownloadRateLimiter(double bytesPerSecond)
        {
            _bytesPerSecond = bytesPerSecond;
            _availableBytes = bytesPerSecond;
        }

        public static DownloadRateLimiter? Create(int? speedLimitKbPerSecond)
        {
            return speedLimitKbPerSecond is > 0
                ? new DownloadRateLimiter(speedLimitKbPerSecond.Value * 1024d)
                : null;
        }

        public async Task WaitAsync(int bytes, CancellationToken cancellationToken)
        {
            while (true)
            {
                TimeSpan delay;

                await _gate.WaitAsync(cancellationToken);
                try
                {
                    Refill();
                    if (_availableBytes >= bytes)
                    {
                        _availableBytes -= bytes;
                        return;
                    }

                    var requiredBytes = bytes - _availableBytes;
                    _availableBytes = 0;
                    delay = TimeSpan.FromSeconds(requiredBytes / _bytesPerSecond);
                }
                finally
                {
                    _gate.Release();
                }

                await Task.Delay(delay, cancellationToken);
            }
        }

        private void Refill()
        {
            var now = _stopwatch.Elapsed;
            var elapsedSeconds = (now - _lastTick).TotalSeconds;
            if (elapsedSeconds <= 0)
            {
                return;
            }

            _availableBytes = Math.Min(
                _bytesPerSecond,
                _availableBytes + elapsedSeconds * _bytesPerSecond);
            _lastTick = now;
        }
    }

    private sealed record DownloadProbe(bool SupportsRanges, long? TotalBytes);

    private sealed record DownloadSegment(long Start, long End);

    private sealed class RangeDownloadNotSupportedException : Exception;
}
