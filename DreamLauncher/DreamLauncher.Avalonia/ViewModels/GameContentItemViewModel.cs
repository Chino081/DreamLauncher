using DreamLauncher.Models.Minecraft;

namespace DreamLauncher.Avalonia.ViewModels;

public sealed class GameContentItemViewModel : ObservableObject
{
    public GameContentItemViewModel(GameContentItem item)
    {
        Item = item;
    }

    public GameContentItem Item { get; }

    public GameContentKind Kind => Item.Kind;

    public string Name => Item.Name;

    public string FileName => Item.FileName;

    public bool IsEnabled => Item.IsEnabled;

    public string DetailText => $"{FormatKind(Kind)} · {FormatBytes(Item.SizeBytes)}";

    public string StatusText => IsEnabled ? "已启用" : "未启用";

    public string StatusBrush => IsEnabled ? "#2ED47A" : "#A7B0BF";

    public string ActionText => Kind switch
    {
        GameContentKind.ShaderPack => IsEnabled ? "关闭" : "选择",
        _ => IsEnabled ? "停用" : "启用"
    };

    private static string FormatKind(GameContentKind kind)
    {
        return kind switch
        {
            GameContentKind.ResourcePack => "资源包",
            GameContentKind.ShaderPack => "光影包",
            GameContentKind.Mod => "Mod",
            _ => "文件"
        };
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }
}
