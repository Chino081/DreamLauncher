namespace DreamLauncher.Models.Config;

public sealed class DownloadSettings
{
    public int MaxRetryCount { get; set; } = 3;

    public int? SpeedLimitKbPerSecond { get; set; }

    public DownloadSource Source { get; set; } = DownloadSource.Bmclapi;
}
