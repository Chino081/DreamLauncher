namespace DreamLauncher.Core.Accounts;

public sealed class MicrosoftDeviceCodeInfo
{
    public string DeviceCode { get; init; } = "";

    public string UserCode { get; init; } = "";

    public string VerificationUri { get; init; } = "";

    public int ExpiresIn { get; init; }

    public int Interval { get; init; }
}
