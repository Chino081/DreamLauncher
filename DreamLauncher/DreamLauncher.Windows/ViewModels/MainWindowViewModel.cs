using System.Collections.ObjectModel;
using System.Windows.Input;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Clients;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Java;
using DreamLauncher.Core.Minecraft;
using DreamLauncher.Core.Remote;
using DreamLauncher.Models.Accounts;
using DreamLauncher.Models.Announcements;
using DreamLauncher.Models.Clients;
using DreamLauncher.Models.Config;
using DreamLauncher.Models.Java;
using DreamLauncher.Models.Operations;

namespace DreamLauncher.Windows.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherConfigStore _configStore;
    private readonly RemoteConfigClient _remoteConfigClient;
    private readonly ClientManager _clientManager;
    private readonly JavaRuntimeManager _javaRuntimeManager;
    private readonly AccountManager _accountManager;
    private readonly ISecureTokenStore _tokenStore;
    private readonly MinecraftLaunchService _minecraftLaunchService;
    private ClientInstallationViewModel? _selectedClient;
    private AccountMetadata? _currentAccount;
    private string _statusMessage = "正在准备启动器";
    private string _operationText = "";
    private double _progressValue;
    private bool _hasProgress;
    private bool _isBusy;
    private CancellationTokenSource? _operationCancellation;

    public MainWindowViewModel(
        LauncherConfigStore configStore,
        RemoteConfigClient remoteConfigClient,
        ClientManager clientManager,
        JavaRuntimeManager javaRuntimeManager,
        AccountManager accountManager,
        ISecureTokenStore tokenStore,
        MinecraftLaunchService minecraftLaunchService)
    {
        _configStore = configStore;
        _remoteConfigClient = remoteConfigClient;
        _clientManager = clientManager;
        _javaRuntimeManager = javaRuntimeManager;
        _accountManager = accountManager;
        _tokenStore = tokenStore;
        _minecraftLaunchService = minecraftLaunchService;

        RefreshCommand = new AsyncRelayCommand(InitializeAsync, () => !IsBusy);
        PrimaryActionCommand = new AsyncRelayCommand(ExecutePrimaryActionAsync, () => SelectedClient?.CanRunPrimaryAction == true && !IsBusy);
        AddAccountCommand = new AsyncRelayCommand(AddAccountAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);
    }

    public ObservableCollection<ClientInstallationViewModel> Clients { get; } = [];

    public ObservableCollection<AnnouncementItem> Announcements { get; } = [];

    public ObservableCollection<AccountMetadata> Accounts { get; } = [];

    public ICommand RefreshCommand { get; }

    public ICommand PrimaryActionCommand { get; }

    public ICommand AddAccountCommand { get; }

    public RelayCommand CancelCommand { get; }

    public event Action<string>? MessageRequested;

    public event Action? GameLaunchSucceeded;

    public ClientInstallationViewModel? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (SetProperty(ref _selectedClient, value))
            {
                OnPropertyChanged(nameof(HasSelectedClient));
                OnPropertyChanged(nameof(SelectedClientName));
                RaiseCommandStates();
            }
        }
    }

    public bool HasSelectedClient => SelectedClient is not null;

    public string SelectedClientName => SelectedClient?.Name ?? "未选择客户端";

    public AccountMetadata? CurrentAccount
    {
        get => _currentAccount;
        set
        {
            if (SetProperty(ref _currentAccount, value))
            {
                OnPropertyChanged(nameof(CurrentAccountName));
                OnPropertyChanged(nameof(CurrentAccountStatusText));
            }
        }
    }

    public string CurrentAccountName => CurrentAccount?.PlayerName ?? "未登录";

    public string CurrentAccountStatusText => CurrentAccount is null
        ? "请添加账号"
        : AccountManager.IsOfflineAccount(CurrentAccount)
            ? "离线测试账号"
            : AccountManager.IsThirdPartyAccount(CurrentAccount)
                ? $"皮肤站账号 · {CurrentAccount.AuthServerName ?? CurrentAccount.AuthServerUrl}"
            : "Microsoft 正版账号";

    public string StatusMessage
    {
        get => _statusMessage;
        set => SetProperty(ref _statusMessage, value);
    }

    public string OperationText
    {
        get => _operationText;
        set => SetProperty(ref _operationText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set => SetProperty(ref _progressValue, value);
    }

    public bool HasProgress
    {
        get => _hasProgress;
        set => SetProperty(ref _hasProgress, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseCommandStates();
            }
        }
    }

    public async Task InitializeAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在读取本地配置";
            HasProgress = false;

            var config = await _configStore.LoadAsync(cancellationToken);
            await LoadAccountsAsync(cancellationToken);

            var manifest = await LoadClientsManifestAsync(config, cancellationToken);
            var installations = await _clientManager.GetInstallationsAsync(manifest.Clients, config, cancellationToken);

            Clients.Clear();
            foreach (var installation in installations)
            {
                Clients.Add(new ClientInstallationViewModel(installation));
            }

            if (Clients.Count == 0)
            {
                Clients.Add(new ClientInstallationViewModel(CreatePlaceholderClient()));
            }

            SelectedClient = Clients.FirstOrDefault(client => client.Id == config.DefaultClientId) ?? Clients.FirstOrDefault();
            await LoadAnnouncementsAsync(config, manifest, cancellationToken);
            StatusMessage = "启动器已就绪";
        });
    }

    private async Task ExecutePrimaryActionAsync()
    {
        if (SelectedClient is null)
        {
            return;
        }

        switch (SelectedClient.Status)
        {
            case ClientInstallStatus.NotInstalled:
            case ClientInstallStatus.UpdateRequired:
            case ClientInstallStatus.VerificationFailed:
                await InstallSelectedClientAsync(SelectedClient);
                break;
            case ClientInstallStatus.Ready:
            case ClientInstallStatus.JavaMissing:
                await LaunchSelectedClientAsync(SelectedClient);
                break;
        }
    }

    private async Task InstallSelectedClientAsync(ClientInstallationViewModel selectedClient)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            var config = await _configStore.LoadAsync(cancellationToken);
            selectedClient.SetStatus(ClientInstallStatus.Downloading);
            StatusMessage = $"正在准备 {selectedClient.Name}";

            var progress = CreateProgressReporter(selectedClient);
            await _clientManager.InstallOrUpdateAsync(selectedClient.Installation.Definition, config, progress, cancellationToken);
            selectedClient.SetStatus(ClientInstallStatus.Ready);
            StatusMessage = $"{selectedClient.Name} 已就绪";
        });
    }

    private async Task LaunchSelectedClientAsync(ClientInstallationViewModel selectedClient)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在检查账号";
            var account = CurrentAccount;
            if (account is null)
            {
                throw new InvalidOperationException("请先添加账号。");
            }

            account = await _accountManager.RefreshAccountAsync(account.Id, cancellationToken);
            CurrentAccount = account;

            var tokens = AccountManager.IsOfflineAccount(account)
                ? AccountManager.CreateOfflineTokens(account)
                : await _tokenStore.ReadAsync(account.Id, cancellationToken)
                    ?? throw new InvalidOperationException("账号令牌不存在，请重新登录。");

            var config = await _configStore.LoadAsync(cancellationToken);
            StatusMessage = "正在检查 Java";
            var java = await _javaRuntimeManager.ResolveAsync(
                selectedClient.Installation.Definition.JavaVersion,
                selectedClient.Installation.JavaPath,
                cancellationToken);

            if (java is null)
            {
                selectedClient.SetStatus(ClientInstallStatus.JavaMissing);
                java = await InstallRequiredJavaAsync(selectedClient.Installation.Definition.JavaVersion, config, cancellationToken);
                selectedClient.SetStatus(ClientInstallStatus.Ready);
            }

            StatusMessage = "正在启动 Minecraft";
            var result = await _minecraftLaunchService.LaunchAsync(
                selectedClient.Installation,
                account,
                tokens,
                java,
                config.AuthlibInjectorJarPath,
                cancellationToken);

            StatusMessage = $"游戏已启动，进程 ID：{result.ProcessId}";
            OperationText = $"启动日志：{result.LogPath}";
            GameLaunchSucceeded?.Invoke();
        });
    }

    private async Task<JavaRuntimeInfo> InstallRequiredJavaAsync(
        int requiredVersion,
        LauncherConfig config,
        CancellationToken cancellationToken)
    {
        var manifestUrl = config.JavaRuntimesManifestUrl;
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            throw new InvalidOperationException($"缺少 Java {requiredVersion} 下载配置，请刷新客户端列表后重试。");
        }

        StatusMessage = $"正在下载 Java {requiredVersion}";
        var manifest = await _remoteConfigClient.GetJavaRuntimesAsync(manifestUrl, cancellationToken);
        var runtime = manifest.Runtimes.FirstOrDefault(item => item.Version == requiredVersion)
            ?? throw new InvalidOperationException($"远程配置中没有 Java {requiredVersion} 运行时。");

        return await _javaRuntimeManager.InstallPrivateRuntimeAsync(
            runtime,
            config.Download.MaxRetryCount,
            CreateProgressReporter(SelectedClient),
            cancellationToken);
    }

    private async Task AddAccountAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在打开 Microsoft 登录";
            var account = await _accountManager.AddMicrosoftAccountAsync(cancellationToken);
            await LoadAccountsAsync(cancellationToken);
            CurrentAccount = account;
            StatusMessage = $"已登录 {account.PlayerName}";
        });
    }

    public async Task AddOfflineAccountAsync(string playerName)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在创建离线测试账号";
            var account = await _accountManager.AddOfflineAccountAsync(playerName, cancellationToken);
            await LoadAccountsAsync(cancellationToken);
            CurrentAccount = account;
            StatusMessage = $"已切换到离线测试账号 {account.PlayerName}";
        });
    }

    public async Task AddThirdPartyAccountAsync(
        string apiRoot,
        string username,
        string password)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在登录皮肤站";
            var account = await _accountManager.AddThirdPartyAccountAsync(
                new ThirdPartyLoginInput
                {
                    ApiRoot = apiRoot,
                    Username = username,
                    Password = password
                },
                cancellationToken);

            await LoadAccountsAsync(cancellationToken);
            CurrentAccount = account;
            StatusMessage = $"已登录皮肤站账号 {account.PlayerName}";
        });
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken)
    {
        Accounts.Clear();
        var accounts = await _accountManager.GetAccountsAsync(cancellationToken);
        foreach (var account in accounts)
        {
            Accounts.Add(account);
        }

        CurrentAccount = await _accountManager.GetDefaultAccountAsync(cancellationToken);
    }

    private async Task<RemoteClientsManifest> LoadClientsManifestAsync(
        LauncherConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.ClientsManifestUrl))
        {
            return new RemoteClientsManifest();
        }

        StatusMessage = "正在拉取远程客户端列表";
        var manifest = await _remoteConfigClient.GetClientsManifestAsync(config.ClientsManifestUrl, cancellationToken);

        var changed = false;
        if (!string.IsNullOrWhiteSpace(manifest.JavaRuntimesUrl) &&
            !string.Equals(config.JavaRuntimesManifestUrl, manifest.JavaRuntimesUrl, StringComparison.Ordinal))
        {
            config.JavaRuntimesManifestUrl = manifest.JavaRuntimesUrl;
            changed = true;
        }

        if (!string.IsNullOrWhiteSpace(manifest.AnnouncementUrl) &&
            !string.Equals(config.AnnouncementUrl, manifest.AnnouncementUrl, StringComparison.Ordinal))
        {
            config.AnnouncementUrl = manifest.AnnouncementUrl;
            changed = true;
        }

        if (changed)
        {
            await _configStore.SaveAsync(config, cancellationToken);
        }

        return manifest;
    }

    private async Task LoadAnnouncementsAsync(
        LauncherConfig config,
        RemoteClientsManifest manifest,
        CancellationToken cancellationToken)
    {
        Announcements.Clear();
        var url = !string.IsNullOrWhiteSpace(config.AnnouncementUrl)
            ? config.AnnouncementUrl
            : manifest.AnnouncementUrl;

        if (string.IsNullOrWhiteSpace(url))
        {
            Announcements.Add(new AnnouncementItem
            {
                Title = "公告未配置",
                Content = "远程客户端配置未提供公告地址。",
                Date = DateOnly.FromDateTime(DateTime.Today)
            });
            return;
        }

        try
        {
            var document = await _remoteConfigClient.GetAnnouncementAsync(url, cancellationToken);
            foreach (var item in document.Items)
            {
                Announcements.Add(item);
            }
        }
        catch
        {
            Announcements.Add(new AnnouncementItem
            {
                Title = "公告加载失败",
                Content = "远程公告暂时不可用，稍后可以刷新重试。",
                Date = DateOnly.FromDateTime(DateTime.Today)
            });
        }
    }

    private IProgress<LauncherOperationProgress> CreateProgressReporter(ClientInstallationViewModel? selectedClient)
    {
        return new Progress<LauncherOperationProgress>(progress =>
        {
            HasProgress = true;
            ProgressValue = progress.Progress.HasValue
                ? Math.Clamp(progress.Progress.Value * 100, 0, 100)
                : 0;

            OperationText = FormatProgress(progress);

            if (selectedClient is null)
            {
                return;
            }

            selectedClient.SetStatus(progress.Stage switch
            {
                "download" => ClientInstallStatus.Downloading,
                "verify" => ClientInstallStatus.Verifying,
                "extract" => ClientInstallStatus.Extracting,
                _ => selectedClient.Status
            });
        });
    }

    private static string FormatProgress(LauncherOperationProgress progress)
    {
        var text = progress.Message;
        if (progress.BytesCompleted.HasValue && progress.TotalBytes.HasValue)
        {
            text += $"  {FormatBytes(progress.BytesCompleted.Value)} / {FormatBytes(progress.TotalBytes.Value)}";
        }

        if (progress.SpeedBytesPerSecond.HasValue)
        {
            text += $"  {FormatBytes((long)progress.SpeedBytesPerSecond.Value)}/s";
        }

        if (progress.EstimatedRemaining.HasValue)
        {
            text += $"  剩余 {progress.EstimatedRemaining.Value:mm\\:ss}";
        }

        return text;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return $"{value:0.##} {units[unit]}";
    }

    private async Task RunGuardedAsync(Func<CancellationToken, Task> action)
    {
        if (IsBusy)
        {
            return;
        }

        _operationCancellation = new CancellationTokenSource();
        IsBusy = true;
        HasProgress = false;

        try
        {
            await action(_operationCancellation.Token);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "操作已取消";
        }
        catch (Exception ex)
        {
            StatusMessage = ex.Message;
            MessageRequested?.Invoke(ex.Message);
        }
        finally
        {
            _operationCancellation.Dispose();
            _operationCancellation = null;
            IsBusy = false;
        }
    }

    private void CancelCurrentOperation()
    {
        _operationCancellation?.Cancel();
    }

    private void RaiseCommandStates()
    {
        (RefreshCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (PrimaryActionCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        (AddAccountCommand as AsyncRelayCommand)?.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
    }

    private static ClientInstallation CreatePlaceholderClient()
    {
        return new ClientInstallation
        {
            Definition = new ClientDefinition
            {
                Id = "configure-source",
                Name = "暂无客户端",
                Description = "固定客户端源暂时没有返回可用客户端，请检查网络后刷新。",
                Version = "0.0.0",
                MinecraftVersion = "1.20.1",
                Loader = "forge",
                LoaderVersion = "47.x",
                JavaVersion = 17,
                ServerAddress = "mc.example.com",
                DefaultMemoryMb = 4096,
                Enabled = false
            },
            Status = ClientInstallStatus.Disabled,
            InstallPath = "",
            MemoryMb = 4096
        };
    }
}
