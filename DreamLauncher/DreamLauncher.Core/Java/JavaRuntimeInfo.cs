namespace DreamLauncher.Core.Java;

public sealed class JavaRuntimeInfo
{
    public required string JavaPath { get; init; }

    public required string Source { get; init; }

    public int MajorVersion { get; init; }
}
