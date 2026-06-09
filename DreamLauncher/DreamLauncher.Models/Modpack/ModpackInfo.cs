namespace DreamLauncher.Models.Modpack;

public sealed class ModpackInfo
{
    public ModpackType Type { get; init; }

    public string Name { get; init; } = "";

    public string MinecraftVersion { get; init; } = "";

    public string Loader { get; init; } = "";

    public string LoaderVersion { get; init; } = "";

    public string OverridesFolder { get; init; } = "";

    public string ClientOverridesFolder { get; init; } = "";

    public IReadOnlyList<ModpackFileEntry> Files { get; init; } = [];
}
