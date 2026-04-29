using System.Collections.ObjectModel;
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

namespace DreamLauncher.Avalonia.ViewModels;

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
    private LauncherConfig? _currentConfig;
    private string _statusMessage = "正在准备启动器";
    private string _operationText = "状态：暂无下载";
    private string _currentDownloadStatusText = "状态：暂无下载";
    private string _downloadSpeedText = "下载速度：0 KB/s";
    private string _downloadSizeText = "下载大小：0 MB / 0 MB";
    private double _progressValue;
    private bool _hasProgress;
    private bool _isBusy;
    private string _memoryMbText = "";
    private string _javaPathText = "";
    private string _retryCountText = "3";
    private string _speedLimitText = "";
    private string _javaRuntimeStatusText = "自动选择会在启动前按当前客户端要求检测 Java。";
    private string _memoryStatusText = "自动选择会使用当前客户端建议内存。";
    private JavaRuntimeOption? _selectedJavaRuntimeOption;
    private MemoryPresetOption? _selectedMemoryPreset;
    private bool _syncingJavaOptions;
    private bool _syncingMemoryPreset;
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
        AddAccountCommand = new AsyncRelayCommand(AddMicrosoftAccountAsync, () => !IsBusy);
        DetectJavaCommand = new AsyncRelayCommand(DetectJavaAsync, () => SelectedClient is not null && !IsBusy);
        SaveSettingsCommand = new AsyncRelayCommand(SaveSettingsAsync, () => !IsBusy);
        CancelCommand = new RelayCommand(CancelCurrentOperation, () => IsBusy);

        JavaRuntimeOptions.Add(JavaRuntimeOption.Auto("启动时自动检测"));
        SelectedJavaRuntimeOption = JavaRuntimeOptions[0];

        MemoryPresetOptions.Add(new MemoryPresetOption("自动选择", null));
        MemoryPresetOptions.Add(new MemoryPresetOption("2048 MB", 2048));
        MemoryPresetOptions.Add(new MemoryPresetOption("4096 MB", 4096));
        MemoryPresetOptions.Add(new MemoryPresetOption("8192 MB", 8192));
        MemoryPresetOptions.Add(new MemoryPresetOption("12288 MB", 12288));
        SelectedMemoryPreset = MemoryPresetOptions[0];
    }

    public ObservableCollection<ClientInstallationViewModel> Clients { get; } = [];

    public ObservableCollection<AnnouncementItem> Announcements { get; } = [];

    public ObservableCollection<AccountMetadata> Accounts { get; } = [];

    public ObservableCollection<JavaRuntimeOption> JavaRuntimeOptions { get; } = [];

    public ObservableCollection<MemoryPresetOption> MemoryPresetOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PrimaryActionCommand { get; }

    public AsyncRelayCommand AddAccountCommand { get; }

    public AsyncRelayCommand DetectJavaCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public RelayCommand CancelCommand { get; }

    public event Action<string, string>? MessageRequested;

    public ClientInstallationViewModel? SelectedClient
    {
        get => _selectedClient;
        set
        {
            if (SetProperty(ref _selectedClient, value))
            {
                foreach (var client in Clients)
                {
                    client.IsSelected = ReferenceEquals(client, value);
                }

                OnPropertyChanged(nameof(HasSelectedClient));
                OnPropertyChanged(nameof(SelectedClientName));
                ApplySettingsFromConfig();
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
                OnPropertyChanged(nameof(CurrentAccountInitial));
                OnPropertyChanged(nameof(CurrentAccountOnlineText));
            }
        }
    }

    public string CurrentAccountName => CurrentAccount?.PlayerName ?? "未登录";

    public string CurrentAccountInitial => string.IsNullOrWhiteSpace(CurrentAccount?.PlayerName)
        ? "梦"
        : CurrentAccount.PlayerName[..1].ToUpperInvariant();

    public string CurrentAccountStatusText => CurrentAccount is null
        ? "请添加账号"
        : AccountManager.IsOfflineAccount(CurrentAccount)
            ? "离线账号"
            : AccountManager.IsThirdPartyAccount(CurrentAccount)
                ? "第三方账号"
                : "Microsoft 正版账号";

    public string CurrentAccountOnlineText => CurrentAccount is null ? "状态：未登录" : "状态：在线";

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

    public string CurrentDownloadStatusText
    {
        get => _currentDownloadStatusText;
        set => SetProperty(ref _currentDownloadStatusText, value);
    }

    public string ProgressText => $"总进度：{ProgressValue:0}%";

    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        set => SetProperty(ref _downloadSpeedText, value);
    }

    public string DownloadSizeText
    {
        get => _downloadSizeText;
        set => SetProperty(ref _downloadSizeText, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        set
        {
            if (SetProperty(ref _progressValue, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
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

    public string MemoryMbText
    {
        get => _memoryMbText;
        set
        {
            if (SetProperty(ref _memoryMbText, value))
            {
                SelectMemoryPresetFromText();
                UpdateMemoryStatusText();
            }
        }
    }

    public string JavaPathText
    {
        get => _javaPathText;
        set
        {
            if (SetProperty(ref _javaPathText, value))
            {
                RebuildJavaRuntimeOptions(value);
                UpdateJavaRuntimeStatusText(value);
            }
        }
    }

    public JavaRuntimeOption? SelectedJavaRuntimeOption
    {
        get => _selectedJavaRuntimeOption;
        set
        {
            if (!SetProperty(ref _selectedJavaRuntimeOption, value) || value is null || _syncingJavaOptions)
            {
                return;
            }

            JavaPathText = value.JavaPath ?? "";
        }
    }

    public MemoryPresetOption? SelectedMemoryPreset
    {
        get => _selectedMemoryPreset;
        set
        {
            if (!SetProperty(ref _selectedMemoryPreset, value) || value is null || _syncingMemoryPreset)
            {
                return;
            }

            MemoryMbText = value.MemoryMb?.ToString() ?? "";
        }
    }

    public string RetryCountText
    {
        get => _retryCountText;
        set => SetProperty(ref _retryCountText, value);
    }

    public string SpeedLimitText
    {
        get => _speedLimitText;
        set => SetProperty(ref _speedLimitText, value);
    }

    public string JavaRuntimeStatusText
    {
        get => _javaRuntimeStatusText;
        set => SetProperty(ref _javaRuntimeStatusText, value);
    }

    public string MemoryStatusText
    {
        get => _memoryStatusText;
        set => SetProperty(ref _memoryStatusText, value);
    }

    public async Task InitializeAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在读取本地配置";
            HasProgress = false;
            ProgressValue = 0;
            OperationText = "状态：暂无下载";
            CurrentDownloadStatusText = "状态：暂无下载";
            DownloadSpeedText = "下载速度：0 KB/s";
            DownloadSizeText = "下载大小：0 MB / 0 MB";

            var config = await _configStore.LoadAsync(cancellationToken);
            _currentConfig = config;
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
            ApplyDownloadSettings(config);
            StatusMessage = "启动器已就绪";
        });
    }

    public async Task AddMicrosoftAccountAsync()
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

    public async Task SetDefaultAccountAsync(AccountMetadata account)
    {
        await _accountManager.SetDefaultAccountAsync(account.Id);
        await LoadAccountsAsync(CancellationToken.None);
        CurrentAccount = account;
        StatusMessage = $"已切换到 {account.PlayerName}";
    }

    public async Task RemoveAccountAsync(AccountMetadata account)
    {
        await _accountManager.RemoveAccountAsync(account.Id);
        await LoadAccountsAsync(CancellationToken.None);
        StatusMessage = $"已删除账号 {account.PlayerName}";
    }

    public async Task SaveSettingsAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            var config = await _configStore.LoadAsync(cancellationToken);
            _currentConfig = config;

            if (int.TryParse(RetryCountText, out var retryCount))
            {
                config.Download.MaxRetryCount = Math.Clamp(retryCount, 1, 10);
            }

            config.Download.SpeedLimitKbPerSecond = int.TryParse(SpeedLimitText, out var speedLimit)
                ? Math.Max(1, speedLimit)
                : null;

            if (SelectedClient is not null)
            {
                if (!config.ClientSettings.TryGetValue(SelectedClient.Id, out var settings))
                {
                    settings = new ClientUserSettings();
                    config.ClientSettings[SelectedClient.Id] = settings;
                }

                settings.MemoryMb = int.TryParse(MemoryMbText, out var memory)
                    ? Math.Clamp(memory, 1024, 65536)
                    : null;
                settings.JavaPath = string.IsNullOrWhiteSpace(JavaPathText) ? null : JavaPathText.Trim();
            }

            await _configStore.SaveAsync(config, cancellationToken);
            _currentConfig = config;
            StatusMessage = "设置已保存";
        });
    }

    public async Task DetectJavaAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            if (SelectedClient is null)
            {
                return;
            }

            var requiredVersion = SelectedClient.Installation.Definition.JavaVersion;
            StatusMessage = $"正在检测 Java {requiredVersion}";
            var java = await _javaRuntimeManager.ResolveAsync(requiredVersion, null, cancellationToken);
            if (java is null)
            {
                JavaPathText = "";
                JavaRuntimeStatusText = $"未检测到 Java {requiredVersion} 或更高版本，启动时会提示安装。";
                StatusMessage = $"未找到 Java {requiredVersion}";
                MessageRequested?.Invoke("Java 环境", $"未检测到 Java {requiredVersion} 或更高版本，可以留空让启动器在启动时自动下载。");
                return;
            }

            JavaPathText = java.JavaPath;
            JavaRuntimeStatusText = $"当前推荐：{java.JavaPath}";
            StatusMessage = $"已选择 Java {java.MajorVersion}：{java.JavaPath}";
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

            await _clientManager.InstallOrUpdateAsync(
                selectedClient.Installation.Definition,
                config,
                CreateProgressReporter(selectedClient),
                cancellationToken);

            selectedClient.SetStatus(ClientInstallStatus.Ready);
            StatusMessage = $"{selectedClient.Name} 已就绪";
        });
    }

    private async Task LaunchSelectedClientAsync(ClientInstallationViewModel selectedClient)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            StatusMessage = "正在检查账号";
            var account = CurrentAccount ?? throw new InvalidOperationException("请先添加账号。");
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
        });
    }

    private async Task<JavaRuntimeInfo> InstallRequiredJavaAsync(
        int requiredVersion,
        LauncherConfig config,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(config.JavaRuntimesManifestUrl))
        {
            throw new InvalidOperationException($"缺少 Java {requiredVersion} 下载配置，请刷新客户端列表后重试。");
        }

        StatusMessage = $"正在下载 Java {requiredVersion}";
        var manifest = await _remoteConfigClient.GetJavaRuntimesAsync(config.JavaRuntimesManifestUrl, cancellationToken);
        var runtime = manifest.Runtimes.FirstOrDefault(item => item.Version == requiredVersion)
            ?? throw new InvalidOperationException($"远程配置中没有 Java {requiredVersion} 运行时。");

        return await _javaRuntimeManager.InstallPrivateRuntimeAsync(
            runtime,
            config.Download.MaxRetryCount,
            CreateProgressReporter(SelectedClient),
            cancellationToken);
    }

    private async Task LoadAccountsAsync(CancellationToken cancellationToken)
    {
        Accounts.Clear();
        foreach (var account in await _accountManager.GetAccountsAsync(cancellationToken))
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
            CurrentDownloadStatusText = progress.Stage switch
            {
                "download" => "状态：正在下载",
                "verify" => "状态：正在校验",
                "extract" => "状态：正在解压",
                _ => $"状态：{progress.Message}"
            };
            DownloadSpeedText = progress.SpeedBytesPerSecond.HasValue
                ? $"下载速度：{FormatBytes((long)progress.SpeedBytesPerSecond.Value)}/s"
                : "下载速度：0 KB/s";
            DownloadSizeText = progress.BytesCompleted.HasValue && progress.TotalBytes.HasValue
                ? $"下载大小：{FormatBytes(progress.BytesCompleted.Value)} / {FormatBytes(progress.TotalBytes.Value)}"
                : "下载大小：0 MB / 0 MB";

            selectedClient?.SetStatus(progress.Stage switch
            {
                "download" => ClientInstallStatus.Downloading,
                "verify" => ClientInstallStatus.Verifying,
                "extract" => ClientInstallStatus.Extracting,
                _ => selectedClient.Status
            });
        });
    }

    private void ApplySettingsFromConfig()
    {
        var config = _currentConfig;
        if (config is null)
        {
            return;
        }

        ApplyDownloadSettings(config);
        var selectedClient = SelectedClient;
        if (selectedClient is null)
        {
            MemoryMbText = "";
            JavaPathText = "";
            JavaRuntimeStatusText = "当前未选择客户端，自动选择会按 Java 17 检测。";
            MemoryStatusText = "当前未选择客户端。";
            return;
        }

        var settings = config.ClientSettings.GetValueOrDefault(selectedClient.Id);
        MemoryMbText = settings?.MemoryMb?.ToString() ?? "";
        JavaPathText = settings?.JavaPath ?? "";
        UpdateMemoryStatusText();
        UpdateJavaRuntimeStatusText(JavaPathText);
    }

    private void ApplyDownloadSettings(LauncherConfig config)
    {
        RetryCountText = config.Download.MaxRetryCount.ToString();
        SpeedLimitText = config.Download.SpeedLimitKbPerSecond?.ToString() ?? "";
    }

    private void RebuildJavaRuntimeOptions(string? selectedPath)
    {
        if (_syncingJavaOptions)
        {
            return;
        }

        try
        {
            _syncingJavaOptions = true;
            var normalized = string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath.Trim();
            JavaRuntimeOptions.Clear();
            JavaRuntimeOptions.Add(JavaRuntimeOption.Auto(normalized ?? "启动时自动检测"));
            if (normalized is not null)
            {
                JavaRuntimeOptions.Add(JavaRuntimeOption.Manual(CreateJavaDisplayName(normalized), normalized));
            }

            SelectedJavaRuntimeOption = normalized is null ? JavaRuntimeOptions[0] : JavaRuntimeOptions[^1];
        }
        finally
        {
            _syncingJavaOptions = false;
        }
    }

    private void SelectMemoryPresetFromText()
    {
        if (_syncingMemoryPreset)
        {
            return;
        }

        try
        {
            _syncingMemoryPreset = true;
            var match = int.TryParse(MemoryMbText, out var memory)
                ? MemoryPresetOptions.FirstOrDefault(item => item.MemoryMb == memory)
                : MemoryPresetOptions.FirstOrDefault(item => item.MemoryMb is null);

            SelectedMemoryPreset = match ?? MemoryPresetOptions.FirstOrDefault(item => item.MemoryMb is null);
        }
        finally
        {
            _syncingMemoryPreset = false;
        }
    }

    private void UpdateJavaRuntimeStatusText(string? selectedPath)
    {
        var selectedClient = SelectedClient;
        if (selectedClient is null)
        {
            JavaRuntimeStatusText = "当前未选择客户端，自动选择会按 Java 17 检测。";
            return;
        }

        var requiredVersion = selectedClient.Installation.Definition.JavaVersion;
        JavaRuntimeStatusText = string.IsNullOrWhiteSpace(selectedPath)
            ? $"当前客户端：{selectedClient.Name}，需要 Java {requiredVersion}。自动选择会优先使用启动器私有 Java，再尝试系统 Java。"
            : $"当前客户端：{selectedClient.Name}，已指定 Java：{selectedPath.Trim()}";
    }

    private void UpdateMemoryStatusText()
    {
        var selectedClient = SelectedClient;
        if (selectedClient is null)
        {
            MemoryStatusText = "当前未选择客户端。";
            return;
        }

        var defaultMemory = Math.Max(1024, selectedClient.Installation.Definition.DefaultMemoryMb);
        MemoryStatusText = int.TryParse(MemoryMbText, out var memory)
            ? $"当前客户端：{selectedClient.Name}，最大内存 {FormatMemory(memory)}。"
            : $"当前客户端：{selectedClient.Name}，自动使用 {FormatMemory(defaultMemory)}。";
    }

    private static string CreateJavaDisplayName(string path)
    {
        var version = path.Contains("zulu-25", StringComparison.OrdinalIgnoreCase)
            ? "25"
            : path.Contains("zulu-21", StringComparison.OrdinalIgnoreCase)
                ? "21"
                : path.Contains("zulu-17", StringComparison.OrdinalIgnoreCase)
                    ? "17"
                    : path.Contains("zulu-8", StringComparison.OrdinalIgnoreCase)
                        ? "8"
                        : "";

        var name = string.IsNullOrWhiteSpace(version) ? "Java" : $"Zulu JDK {version}";
        return $"{name} | x64 | {path}";
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
            MessageRequested?.Invoke("DreamLauncher", ex.Message);
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
        RefreshCommand.RaiseCanExecuteChanged();
        PrimaryActionCommand.RaiseCanExecuteChanged();
        AddAccountCommand.RaiseCanExecuteChanged();
        DetectJavaCommand.RaiseCanExecuteChanged();
        SaveSettingsCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
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

    private static string FormatMemory(int memoryMb)
    {
        return memoryMb >= 1024 && memoryMb % 1024 == 0
            ? $"{memoryMb / 1024} GB"
            : $"{memoryMb} MB";
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

public sealed record JavaRuntimeOption(string DisplayName, string? JavaPath)
{
    public static JavaRuntimeOption Auto(string recommended)
    {
        return new JavaRuntimeOption($"自动选择（当前推荐：{recommended}）", null);
    }

    public static JavaRuntimeOption Manual(string displayName, string javaPath)
    {
        return new JavaRuntimeOption(displayName, javaPath);
    }

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed record MemoryPresetOption(string DisplayName, int? MemoryMb)
{
    public override string ToString()
    {
        return DisplayName;
    }
}
