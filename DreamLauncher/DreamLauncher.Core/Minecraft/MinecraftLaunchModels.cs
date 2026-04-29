namespace DreamLauncher.Core.Minecraft;

public sealed class MinecraftLaunchResult
{
    public int ProcessId { get; init; }

    public string LogPath { get; init; } = "";
}
