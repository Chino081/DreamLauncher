using System.Security;

namespace DreamLauncher.Core.Config;

public static class SafeDirectory
{
    public static void DeleteChildDirectory(string rootDirectory, string targetDirectory)
    {
        var root = Path.GetFullPath(rootDirectory);
        var target = Path.GetFullPath(targetDirectory);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

        if (string.Equals(root.TrimEnd(Path.DirectorySeparatorChar), target.TrimEnd(Path.DirectorySeparatorChar), comparison))
        {
            throw new SecurityException("不能删除根目录。");
        }

        _ = LauncherPaths.EnsureChildPath(root, target);

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }
}
