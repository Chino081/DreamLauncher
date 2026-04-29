namespace DreamLauncher.Models.Clients;

public sealed class RemoteClientsManifest
{
    public string LauncherVersion { get; set; } = "1.0.0";

    public string AnnouncementUrl { get; set; } = "";

    public string JavaRuntimesUrl { get; set; } = "";

    public List<ClientDefinition> Clients { get; set; } = [];
}
