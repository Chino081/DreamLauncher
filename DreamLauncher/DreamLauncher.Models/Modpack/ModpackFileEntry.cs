namespace DreamLauncher.Models.Modpack;

public sealed class ModpackFileEntry
{
    public string RelativePath { get; init; } = "";

    public IReadOnlyList<string> DownloadUrls { get; init; } = [];

    public long FileSize { get; init; }

    public string Sha1 { get; init; } = "";

    public bool Required { get; init; } = true;
}
