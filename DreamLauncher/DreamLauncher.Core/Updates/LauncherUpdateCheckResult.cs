using DreamLauncher.Models.Updates;

namespace DreamLauncher.Core.Updates;

public sealed class LauncherUpdateCheckResult
{
    public required string CurrentVersion { get; init; }

    public required LauncherUpdateManifest Manifest { get; init; }
}
