namespace DreamLauncher.Models.Clients;

public enum ClientInstallStatus
{
    Unknown,
    NotInstalled,
    Ready,
    UpdateRequired,
    Downloading,
    Extracting,
    Verifying,
    VerificationFailed,
    JavaMissing,
    LaunchFailed,
    Disabled
}
