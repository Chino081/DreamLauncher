namespace DreamLauncher.Models.Clients;

public sealed class ClientInstallation
{
    public required ClientDefinition Definition { get; init; }

    public LocalClientConfig? LocalConfig { get; init; }

    public ClientInstallStatus Status { get; set; } = ClientInstallStatus.Unknown;

    public string InstallPath { get; set; } = "";

    public int MemoryMb { get; set; }

    public string? JavaPath { get; set; }
}
