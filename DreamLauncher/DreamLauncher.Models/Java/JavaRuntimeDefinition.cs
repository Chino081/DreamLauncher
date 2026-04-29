namespace DreamLauncher.Models.Java;

public sealed class JavaRuntimeDefinition
{
    public int Version { get; set; }

    public string Name { get; set; } = "";

    public string WindowsX64Url { get; set; } = "";

    public string WindowsX64Sha256 { get; set; } = "";

    public long Size { get; set; }
}
