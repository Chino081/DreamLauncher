namespace DreamLauncher.Models.Clients;

public sealed class ClientFileManifestEntry
{
    public string Path { get; set; } = "";

    public string Url { get; set; } = "";

    public string Sha256 { get; set; } = "";

    public long Size { get; set; }
}
