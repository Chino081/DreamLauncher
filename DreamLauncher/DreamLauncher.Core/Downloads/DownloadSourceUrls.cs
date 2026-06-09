using DreamLauncher.Models.Config;

namespace DreamLauncher.Core.Downloads;

public static class DownloadSourceUrls
{
    public static string GetManifestUrl(DownloadSource source) => source switch
    {
        DownloadSource.Official =>
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        _ =>
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json"
    };

    public static string GetLibraryBaseUrl(DownloadSource source) => source switch
    {
        DownloadSource.Official => "https://libraries.minecraft.net/",
        _ => "https://bmclapi2.bangbang93.com/maven/"
    };

    public static string GetAssetBaseUrl(DownloadSource source) => source switch
    {
        DownloadSource.Official => "https://resources.download.minecraft.net/",
        _ => "https://bmclapi2.bangbang93.com/assets/"
    };

    public static string GetForgeInstallerUrl(DownloadSource source, string version) => source switch
    {
        DownloadSource.Official =>
            $"https://maven.minecraftforge.net/net/minecraftforge/forge/{version}/forge-{version}-installer.jar",
        _ =>
            $"https://bmclapi2.bangbang93.com/maven/net/minecraftforge/forge/{version}/forge-{version}-installer.jar"
    };

    public static string GetNeoForgeInstallerUrl(DownloadSource source, string version) => source switch
    {
        DownloadSource.Official =>
            $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar",
        _ =>
            $"https://bmclapi2.bangbang93.com/maven/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar"
    };

    public static string GetCurseForgeApiBase(DownloadSource source) => source switch
    {
        DownloadSource.Official => "https://api.curseforge.com",
        _ => "https://mod.mcimirror.top/curseforge"
    };

    /// <summary>
    /// Converts a Mojang/CurseForge/Modrinth URL to the selected download source.
    /// Used for URLs returned in version JSON (libraries, assets, etc.).
    /// </summary>
    public static string ConvertUrl(DownloadSource source, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        return source switch
        {
            DownloadSource.Official => ToOfficialUrl(url),
            _ => ToBmclapiUrl(url)
        };
    }

    /// <summary>
    /// Converts a Modrinth download URL to the selected source.
    /// </summary>
    public static string ConvertModrinthUrl(DownloadSource source, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (source == DownloadSource.Official)
        {
            return url
                .Replace("mod.mcimirror.top", "cdn.modrinth.com");
        }

        return ToBmclapiUrl(url)
            .Replace("cdn.modrinth.com", "mod.mcimirror.top");
    }

    /// <summary>
    /// Converts a CurseForge CDN URL to the selected source.
    /// </summary>
    public static string ConvertCurseForgeUrl(DownloadSource source, string url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        // Always fix overwolf URLs first
        var corrected = url
            .Replace("-service.overwolf.wtf", ".forgecdn.net")
            .Replace("://media.", "://edge.");

        if (source == DownloadSource.Official)
        {
            return corrected
                .Replace("mod.mcimirror.top", "edge.forgecdn.net");
        }

        return corrected
            .Replace("edge.forgecdn.net", "mod.mcimirror.top")
            .Replace("mediafilez.forgecdn.net", "mod.mcimirror.top");
    }

    private static string ToBmclapiUrl(string url)
    {
        return url
            .Replace("https://piston-meta.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://launchermeta.mojang.com", "https://bmclapi2.bangbang93.com")
            .Replace("https://libraries.minecraft.net", "https://bmclapi2.bangbang93.com/maven")
            .Replace("https://resources.download.minecraft.net", "https://bmclapi2.bangbang93.com/assets");
    }

    private static string ToOfficialUrl(string url)
    {
        return url
            .Replace("https://bmclapi2.bangbang93.com/maven", "https://libraries.minecraft.net")
            .Replace("https://bmclapi2.bangbang93.com/assets", "https://resources.download.minecraft.net")
            .Replace("https://bmclapi2.bangbang93.com", "https://piston-meta.mojang.com");
    }
}
