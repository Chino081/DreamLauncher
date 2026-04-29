using DreamLauncher.Models.Accounts;
using DreamLauncher.Models.Clients;

namespace DreamLauncher.Models.Config;

public sealed class LauncherConfig
{
    public string LauncherVersion { get; set; } = "0.1.0";

    public string? ClientsManifestUrl { get; set; }

    public string? JavaRuntimesManifestUrl { get; set; }

    public string? AnnouncementUrl { get; set; }

    public string? MicrosoftClientId { get; set; }

    public string? AuthlibInjectorJarPath { get; set; }

    public string? DefaultAccountId { get; set; }

    public string? DefaultClientId { get; set; }

    public List<AccountMetadata> Accounts { get; set; } = [];

    public Dictionary<string, ClientUserSettings> ClientSettings { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public DownloadSettings Download { get; set; } = new();
}
