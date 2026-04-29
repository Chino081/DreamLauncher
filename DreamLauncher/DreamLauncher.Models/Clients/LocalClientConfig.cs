namespace DreamLauncher.Models.Clients;

public sealed class LocalClientConfig
{
    public string Id { get; set; } = "";

    public string Version { get; set; } = "";

    public string MinecraftVersion { get; set; } = "";

    public string Loader { get; set; } = "vanilla";

    public string LoaderVersion { get; set; } = "";

    public int JavaVersion { get; set; }

    public string PackSha256 { get; set; } = "";

    public DateTimeOffset InstalledAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastValidatedUtc { get; set; }
}
