namespace DreamLauncher.Models.Minecraft;

public sealed class GameContentInventory
{
    public string GameDirectory { get; init; } = "";

    public string ResourcePacksDirectory { get; init; } = "";

    public string ShaderPacksDirectory { get; init; } = "";

    public string ModsDirectory { get; init; } = "";

    public IReadOnlyList<GameContentItem> ResourcePacks { get; init; } = [];

    public IReadOnlyList<GameContentItem> ShaderPacks { get; init; } = [];

    public IReadOnlyList<GameContentItem> Mods { get; init; } = [];
}
