namespace DreamLauncher.Models.Operations;

public sealed class LauncherOperationProgress
{
    public string Stage { get; init; } = "";

    public string Message { get; init; } = "";

    public double? Progress { get; init; }

    public long? BytesCompleted { get; init; }

    public long? TotalBytes { get; init; }

    public double? SpeedBytesPerSecond { get; init; }

    public TimeSpan? EstimatedRemaining { get; init; }
}
