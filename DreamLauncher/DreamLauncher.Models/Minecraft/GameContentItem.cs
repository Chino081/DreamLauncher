namespace DreamLauncher.Models.Minecraft;

public sealed class GameContentItem
{
    public GameContentKind Kind { get; init; }

    public string Name { get; init; } = "";

    public string FileName { get; init; } = "";

    public string FullPath { get; init; } = "";

    public bool IsEnabled { get; init; }

    public bool IsDirectory { get; init; }

    public long SizeBytes { get; init; }

    public DateTimeOffset LastWriteTime { get; init; }
}
