namespace DreamLauncher.Models.Clients;

public sealed class ClientFileManifest
{
    public string Id { get; set; } = "";

    public string Version { get; set; } = "";

    public string BaseUrl { get; set; } = "";

    public List<ClientFileManifestEntry> Files { get; set; } = [];

    public List<string> Delete { get; set; } = [];
}
