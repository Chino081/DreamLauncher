namespace DreamLauncher.Models.Updates;

public sealed class LauncherUpdateManifest
{
    public bool Enabled { get; set; } = true;

    public string Version { get; set; } = "";

    public string Title { get; set; } = "启动器更新";

    public string Notes { get; set; } = "";

    public bool Mandatory { get; set; }

    public string WindowsX64Url { get; set; } = "";

    public string WindowsX64Sha256 { get; set; } = "";

    public long Size { get; set; }
}
