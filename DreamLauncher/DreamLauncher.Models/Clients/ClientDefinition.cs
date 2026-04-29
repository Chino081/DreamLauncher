namespace DreamLauncher.Models.Clients;

public sealed class ClientDefinition
{
    public string Id { get; set; } = "";

    public string Name { get; set; } = "";

    public string Description { get; set; } = "";

    public string Version { get; set; } = "";

    public string MinecraftVersion { get; set; } = "";

    public string Loader { get; set; } = "vanilla";

    public string LoaderVersion { get; set; } = "";

    public int JavaVersion { get; set; } = 17;

    public string ServerAddress { get; set; } = "";

    public int DefaultMemoryMb { get; set; } = 4096;

    public string JvmArgs { get; set; } = "";

    public string GameArgs { get; set; } = "";

    public string PackUrl { get; set; } = "";

    public string PackSha256 { get; set; } = "";

    public long PackSize { get; set; }

    public string InstallDir { get; set; } = "";

    public string CoverUrl { get; set; } = "";

    public string IconUrl { get; set; } = "";

    public bool Enabled { get; set; } = true;
}
