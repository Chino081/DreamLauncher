using DreamLauncher.Models.Clients;

namespace DreamLauncher.Windows.ViewModels;

public sealed class ClientInstallationViewModel : ObservableObject
{
    private ClientInstallation _installation;

    public ClientInstallationViewModel(ClientInstallation installation)
    {
        _installation = installation;
    }

    public ClientInstallation Installation => _installation;

    public string Id => _installation.Definition.Id;

    public string Name => _installation.Definition.Name;

    public string Description => _installation.Definition.Description;

    public string Version => _installation.Definition.Version;

    public string MinecraftVersion => _installation.Definition.MinecraftVersion;

    public string LoaderText => string.IsNullOrWhiteSpace(_installation.Definition.LoaderVersion)
        ? _installation.Definition.Loader
        : $"{_installation.Definition.Loader} {_installation.Definition.LoaderVersion}";

    public string JavaText => $"Java {_installation.Definition.JavaVersion}";

    public string MemoryText => $"{_installation.MemoryMb} MB";

    public string ServerAddress => string.IsNullOrWhiteSpace(_installation.Definition.ServerAddress)
        ? "未配置服务器地址"
        : _installation.Definition.ServerAddress;

    public string? CoverUrl => string.IsNullOrWhiteSpace(_installation.Definition.CoverUrl)
        ? null
        : _installation.Definition.CoverUrl;

    public ClientInstallStatus Status => _installation.Status;

    public string StatusText => Status switch
    {
        ClientInstallStatus.NotInstalled => "未安装",
        ClientInstallStatus.Ready => "已就绪",
        ClientInstallStatus.UpdateRequired => "需要更新",
        ClientInstallStatus.Downloading => "正在下载",
        ClientInstallStatus.Extracting => "正在解压",
        ClientInstallStatus.Verifying => "正在校验",
        ClientInstallStatus.VerificationFailed => "校验失败",
        ClientInstallStatus.JavaMissing => "Java 缺失",
        ClientInstallStatus.LaunchFailed => "启动失败",
        ClientInstallStatus.Disabled => "已禁用",
        _ => "未知"
    };

    public string StatusBrush => Status switch
    {
        ClientInstallStatus.Ready => "#2ED47A",
        ClientInstallStatus.UpdateRequired => "#F2C94C",
        ClientInstallStatus.NotInstalled => "#70B8FF",
        ClientInstallStatus.Downloading or ClientInstallStatus.Extracting or ClientInstallStatus.Verifying => "#67E8F9",
        ClientInstallStatus.VerificationFailed or ClientInstallStatus.JavaMissing or ClientInstallStatus.LaunchFailed => "#FF6B6B",
        ClientInstallStatus.Disabled => "#77818F",
        _ => "#A7B0BF"
    };

    public string PrimaryActionText => Status switch
    {
        ClientInstallStatus.NotInstalled => "下载客户端",
        ClientInstallStatus.UpdateRequired => "更新客户端",
        ClientInstallStatus.Ready => "启动游戏",
        ClientInstallStatus.VerificationFailed => "修复客户端",
        ClientInstallStatus.JavaMissing => "安装 Java",
        ClientInstallStatus.Downloading => "正在下载",
        ClientInstallStatus.Extracting => "正在解压",
        ClientInstallStatus.Verifying => "正在校验",
        ClientInstallStatus.Disabled => "不可用",
        _ => "刷新状态"
    };

    public bool CanRunPrimaryAction =>
        Status is ClientInstallStatus.NotInstalled
            or ClientInstallStatus.UpdateRequired
            or ClientInstallStatus.Ready
            or ClientInstallStatus.VerificationFailed
            or ClientInstallStatus.JavaMissing;

    public void Update(ClientInstallation installation)
    {
        _installation = installation;
        RaiseAll();
    }

    public void SetStatus(ClientInstallStatus status)
    {
        _installation.Status = status;
        RaiseAll();
    }

    private void RaiseAll()
    {
        OnPropertyChanged(nameof(Installation));
        OnPropertyChanged(nameof(Id));
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Description));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(MinecraftVersion));
        OnPropertyChanged(nameof(LoaderText));
        OnPropertyChanged(nameof(JavaText));
        OnPropertyChanged(nameof(MemoryText));
        OnPropertyChanged(nameof(ServerAddress));
        OnPropertyChanged(nameof(CoverUrl));
        OnPropertyChanged(nameof(Status));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusBrush));
        OnPropertyChanged(nameof(PrimaryActionText));
        OnPropertyChanged(nameof(CanRunPrimaryAction));
    }
}
