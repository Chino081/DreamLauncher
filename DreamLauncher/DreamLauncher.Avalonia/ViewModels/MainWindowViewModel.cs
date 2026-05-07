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
using DreamLauncher.Models.Minecraft;
using DreamLauncher.Models.Operations;
using Avalonia.Media.Imaging;
using System.Runtime.InteropServices;

namespace DreamLauncher.Avalonia.ViewModels;

public sealed class MainWindowViewModel : ObservableObject
{
    private readonly LauncherConfigStore _configStore;
    private readonly LauncherPaths _paths;
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
    private string _contentStatusMessage = "请选择一个已安装客户端。";
    private string _contentGameDirectory = "";
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
    private Bitmap? _currentAccountAvatar;
    private int _avatarLoadVersion;
    private static readonly HttpClient AvatarHttpClient = CreateAvatarHttpClient();

    public MainWindowViewModel(
        LauncherPaths paths,
        LauncherConfigStore configStore,
        RemoteConfigClient remoteConfigClient,
        ClientManager clientManager,
        JavaRuntimeManager javaRuntimeManager,
        AccountManager accountManager,
        ISecureTokenStore tokenStore,
        MinecraftLaunchService minecraftLaunchService)
    {
        _paths = paths;
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

        RebuildMemoryPresetOptions(null);
    }

    public ObservableCollection<ClientInstallationViewModel> Clients { get; } = [];

    public ObservableCollection<AnnouncementItem> Announcements { get; } = [];

    public ObservableCollection<AccountMetadata> Accounts { get; } = [];

    public ObservableCollection<GameContentItemViewModel> ResourcePacks { get; } = [];

    public ObservableCollection<GameContentItemViewModel> ShaderPacks { get; } = [];

    public ObservableCollection<GameContentItemViewModel> Mods { get; } = [];

    public string ResourcePacksTabText => $"▣ 资源包（{ResourcePacks.Count}）";

    public string ShaderPacksTabText => $"☀ 光影包（{ShaderPacks.Count}）";

    public string ModsTabText => $"🔧 Mod（{Mods.Count}）";

    public ObservableCollection<JavaRuntimeOption> JavaRuntimeOptions { get; } = [];

    public ObservableCollection<MemoryPresetOption> MemoryPresetOptions { get; } = [];

    public AsyncRelayCommand RefreshCommand { get; }

    public AsyncRelayCommand PrimaryActionCommand { get; }

    public AsyncRelayCommand AddAccountCommand { get; }

    public AsyncRelayCommand DetectJavaCommand { get; }

    public AsyncRelayCommand SaveSettingsCommand { get; }

    public RelayCommand CancelCommand { get; }

    public event Action<string, string>? MessageRequested;

    public event Action? GameLaunchSucceeded;

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
                OnPropertyChanged(nameof(CurrentAccountAvatarUrl));
                RefreshCurrentAccountAvatar();
            }
        }
    }

    public string CurrentAccountName => CurrentAccount?.PlayerName ?? "未登录";

    public string CurrentAccountInitial => string.IsNullOrWhiteSpace(CurrentAccount?.PlayerName)
        ? "\u5495"
        : CurrentAccount.PlayerName[..1].ToUpperInvariant();

    public string? CurrentAccountAvatarUrl => AccountAvatarUrl.FromAccount(CurrentAccount);

    public Bitmap? CurrentAccountAvatar
    {
        get => _currentAccountAvatar;
        private set
        {
            if (ReferenceEquals(_currentAccountAvatar, value))
            {
                return;
            }

            var previous = _currentAccountAvatar;
            _currentAccountAvatar = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCurrentAccountAvatar));
            previous?.Dispose();
        }
    }

    public bool HasCurrentAccountAvatar => CurrentAccountAvatar is not null;

    public string CurrentAccountStatusText => CurrentAccount is null
        ? "请添加账号"
        : CurrentAccount.Status == AccountLoginStatus.Invalid
            ? "需要重新登录"
            : AccountManager.IsOfflineAccount(CurrentAccount)
                ? "离线账号"
                : AccountManager.IsThirdPartyAccount(CurrentAccount)
                    ? "第三方账号"
                    : "Microsoft 正版账号";

    public string CurrentAccountOnlineText => CurrentAccount is null
        ? "状态：未登录"
        : CurrentAccount.Status == AccountLoginStatus.Invalid
            ? "状态：已失效"
            : "状态：在线";

    private async void RefreshCurrentAccountAvatar()
    {
        var version = unchecked(++_avatarLoadVersion);
        CurrentAccountAvatar = null;

        foreach (var avatarUrl in AccountAvatarUrl.FromAccountCandidates(CurrentAccount))
        {
            try
            {
                using var response = await AvatarHttpClient.GetAsync(
                    avatarUrl,
                    HttpCompletionOption.ResponseHeadersRead);
                response.EnsureSuccessStatusCode();

                await using var remoteStream = await response.Content.ReadAsStreamAsync();
                using var memoryStream = new MemoryStream();
                await remoteStream.CopyToAsync(memoryStream);
                memoryStream.Position = 0;

                var avatar = new Bitmap(memoryStream);
                if (version == _avatarLoadVersion)
                {
                    CurrentAccountAvatar = avatar;
                }
                else
                {
                    avatar.Dispose();
                }

                return;
            }
            catch
            {
                // Try the next avatar provider, then fall back to the account initial.
            }
        }

        if (version == _avatarLoadVersion)
        {
            CurrentAccountAvatar = null;
        }
    }

    private static HttpClient CreateAvatarHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DreamLauncher/0.1");
        return client;
    }

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

    public string ContentStatusMessage
    {
        get => _contentStatusMessage;
        set => SetProperty(ref _contentStatusMessage, value);
    }

    public string ContentGameDirectory
    {
        get => _contentGameDirectory;
        set => SetProperty(ref _contentGameDirectory, value);
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
        set => SetMemoryMbText(value);
    }

    public string JavaPathText
    {
        get => _javaPathText;
        set => SetJavaPathText(value, rebuildOptions: true);
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

            SetJavaPathText(value.JavaPath ?? "", rebuildOptions: false);
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

            if (value.IsAutomatic)
            {
                SetMemoryMbText(GetDefaultMemoryMb().ToString());
            }
            else if (!value.IsCustom && value.MemoryMb.HasValue)
            {
                SetMemoryMbText(value.MemoryMb.Value.ToString());
            }

            OnPropertyChanged(nameof(IsCustomMemorySelected));
            OnPropertyChanged(nameof(CustomMemoryInputOpacity));
            UpdateMemoryStatusText();
        }
    }

    public bool IsCustomMemorySelected => SelectedMemoryPreset?.IsCustom == true;

    public double CustomMemoryInputOpacity => IsCustomMemorySelected ? 1 : 0.55;

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

    public void ApplyContentInventory(GameContentInventory inventory)
    {
        ReplaceContentItems(ResourcePacks, inventory.ResourcePacks);
        ReplaceContentItems(ShaderPacks, inventory.ShaderPacks);
        ReplaceContentItems(Mods, inventory.Mods);

        ContentGameDirectory = inventory.GameDirectory;
        ContentStatusMessage =
            $"当前客户端：{SelectedClient?.Name ?? "未选择"}，资源包 {ResourcePacks.Count} 个，光影包 {ShaderPacks.Count} 个，Mod {Mods.Count} 个。";
        RaiseContentTabTextChanged();
    }

    public void ClearContentInventory(string message)
    {
        ResourcePacks.Clear();
        ShaderPacks.Clear();
        Mods.Clear();
        ContentGameDirectory = "";
        ContentStatusMessage = message;
        RaiseContentTabTextChanged();
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

                settings.MemoryMb = ReadSelectedMemoryMb();
                settings.JavaPath = string.IsNullOrWhiteSpace(JavaPathText) ? null : JavaPathText.Trim();
                SelectedClient.Installation.MemoryMb = settings.MemoryMb ?? SelectedClient.Installation.Definition.DefaultMemoryMb;
                SelectedClient.Installation.JavaPath = settings.JavaPath;
                SelectedClient.Update(SelectedClient.Installation);
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

            var selectedClient = SelectedClient;
            var requiredVersion = selectedClient.Installation.Definition.JavaVersion;
            StatusMessage = $"正在检测 Java {requiredVersion}";
            JavaRuntimeStatusText = "正在后台检测 Java 环境...";

            var config = await _configStore.LoadAsync(cancellationToken);
            _currentConfig = config;
            var manualJavaPath = config.ClientSettings.GetValueOrDefault(selectedClient.Id)?.JavaPath;
            var options = await BuildJavaRuntimeOptionsAsync(requiredVersion, manualJavaPath, cancellationToken);
            ReplaceJavaRuntimeOptions(options, manualJavaPath);
            SetJavaPathText(manualJavaPath ?? "", rebuildOptions: false);

            var compatibleCount = options.Count(option => !option.IsAutomatic && option.IsCompatible);
            if (compatibleCount == 0)
            {
                JavaRuntimeStatusText = $"未检测到 Java {requiredVersion} 或更高版本，启动时会提示安装。";
                StatusMessage = $"未找到 Java {requiredVersion}";
                MessageRequested?.Invoke("Java 环境", $"未检测到 Java {requiredVersion} 或更高版本，可以留空让启动器在启动时自动下载。");
                return;
            }

            var recommended = options.FirstOrDefault(option => option.IsAutomatic)?.RecommendedPath;
            JavaRuntimeStatusText = $"当前客户端：{selectedClient.Name}，需要 Java {requiredVersion}。已检测到 {compatibleCount} 个可用 Java。";
            StatusMessage = string.IsNullOrWhiteSpace(recommended)
                ? $"已检测到 {compatibleCount} 个 Java"
                : $"当前推荐 Java：{recommended}";
        });
    }

    public async Task RefreshSettingsOptionsAsync()
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            var config = await _configStore.LoadAsync(cancellationToken);
            _currentConfig = config;
            ApplyDownloadSettings(config);

            var selectedClient = SelectedClient;
            if (selectedClient is null)
            {
                RebuildMemoryPresetOptions(null);
                SetJavaPathText("", rebuildOptions: true);
                JavaRuntimeStatusText = "当前未选择客户端，自动选择会按 Java 17 检测。";
                return;
            }

            var settings = config.ClientSettings.GetValueOrDefault(selectedClient.Id);
            RebuildMemoryPresetOptions(settings?.MemoryMb);

            var requiredVersion = selectedClient.Installation.Definition.JavaVersion;
            JavaRuntimeStatusText = "正在后台检测 Java 环境...";
            var options = await BuildJavaRuntimeOptionsAsync(requiredVersion, settings?.JavaPath, cancellationToken);
            ReplaceJavaRuntimeOptions(options, settings?.JavaPath);
            SetJavaPathText(settings?.JavaPath ?? "", rebuildOptions: false);

            var compatibleCount = options.Count(option => !option.IsAutomatic && option.IsCompatible);
            JavaRuntimeStatusText = compatibleCount == 0
                ? $"当前客户端：{selectedClient.Name}，需要 Java {requiredVersion}。未检测到可用 Java。"
                : $"当前客户端：{selectedClient.Name}，需要 Java {requiredVersion}。已检测到 {compatibleCount} 个可用 Java。";
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
                await InstallSelectedClientAsync(SelectedClient);
                break;
            case ClientInstallStatus.VerificationFailed:
                await RepairSelectedClientAsync(SelectedClient);
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

    private async Task RepairSelectedClientAsync(ClientInstallationViewModel selectedClient)
    {
        await RunGuardedAsync(async cancellationToken =>
        {
            var config = await _configStore.LoadAsync(cancellationToken);
            selectedClient.SetStatus(ClientInstallStatus.Downloading);
            StatusMessage = $"\u6b63\u5728\u4fee\u590d {selectedClient.Name}";

            await _clientManager.RepairAsync(
                selectedClient.Installation.Definition,
                config,
                CreateProgressReporter(selectedClient),
                cancellationToken);

            selectedClient.SetStatus(ClientInstallStatus.Ready);
            StatusMessage = $"{selectedClient.Name} \u4fee\u590d\u5b8c\u6210";
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

            if (account.Status == AccountLoginStatus.Invalid)
            {
                throw new InvalidOperationException("账号已失效，请删除后重新登录。");
            }

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
                authlibInjectorJarPath: config.AuthlibInjectorJarPath,
                maxRetryCount: config.Download.MaxRetryCount,
                progress: CreateProgressReporter(selectedClient),
                cancellationToken: cancellationToken);

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

    private static void ReplaceContentItems(
        ObservableCollection<GameContentItemViewModel> target,
        IEnumerable<GameContentItem> source)
    {
        target.Clear();
        foreach (var item in source)
        {
            target.Add(new GameContentItemViewModel(item));
        }
    }

    private void RaiseContentTabTextChanged()
    {
        OnPropertyChanged(nameof(ResourcePacksTabText));
        OnPropertyChanged(nameof(ShaderPacksTabText));
        OnPropertyChanged(nameof(ModsTabText));
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
        RebuildMemoryPresetOptions(settings?.MemoryMb);
        JavaPathText = settings?.JavaPath ?? "";
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

    private void ReplaceJavaRuntimeOptions(IReadOnlyList<JavaRuntimeOption> options, string? selectedPath)
    {
        try
        {
            _syncingJavaOptions = true;
            JavaRuntimeOptions.Clear();
            foreach (var option in options)
            {
                JavaRuntimeOptions.Add(option);
            }

            var normalized = string.IsNullOrWhiteSpace(selectedPath) ? null : selectedPath.Trim();
            SelectedJavaRuntimeOption = normalized is null
                ? JavaRuntimeOptions.FirstOrDefault()
                : JavaRuntimeOptions.FirstOrDefault(option =>
                      !option.IsAutomatic &&
                      string.Equals(option.JavaPath, normalized, StringComparison.OrdinalIgnoreCase))
                  ?? JavaRuntimeOptions.FirstOrDefault();
        }
        finally
        {
            _syncingJavaOptions = false;
        }
    }

    private void SetJavaPathText(string value, bool rebuildOptions)
    {
        if (!SetProperty(ref _javaPathText, value, nameof(JavaPathText)))
        {
            return;
        }

        if (rebuildOptions)
        {
            RebuildJavaRuntimeOptions(value);
        }

        UpdateJavaRuntimeStatusText(value);
    }

    private void SetMemoryMbText(string value)
    {
        if (!SetProperty(ref _memoryMbText, value, nameof(MemoryMbText)))
        {
            return;
        }

        UpdateMemoryStatusText();
    }

    private void RebuildMemoryPresetOptions(int? savedMemory)
    {
        try
        {
            _syncingMemoryPreset = true;
            var defaultMemory = GetDefaultMemoryMb();
            MemoryPresetOptions.Clear();
            MemoryPresetOptions.Add(MemoryPresetOption.Auto(defaultMemory));
            foreach (var memory in new[] { 2048, 4096, 6144, 8192, 12288, 16384 })
            {
                MemoryPresetOptions.Add(MemoryPresetOption.Preset(memory));
            }

            MemoryPresetOptions.Add(MemoryPresetOption.Custom());

            var selected = savedMemory.HasValue
                ? MemoryPresetOptions.FirstOrDefault(item => item.MemoryMb == savedMemory.Value)
                  ?? MemoryPresetOptions.First(item => item.IsCustom)
                : MemoryPresetOptions.First(item => item.IsAutomatic);

            SelectedMemoryPreset = selected;
            _memoryMbText = savedMemory?.ToString() ?? defaultMemory.ToString();
            OnPropertyChanged(nameof(MemoryMbText));
        }
        finally
        {
            _syncingMemoryPreset = false;
        }

        OnPropertyChanged(nameof(IsCustomMemorySelected));
        OnPropertyChanged(nameof(CustomMemoryInputOpacity));
        UpdateMemoryStatusText();
    }

    private async Task<IReadOnlyList<JavaRuntimeOption>> BuildJavaRuntimeOptionsAsync(
        int requiredVersion,
        string? manualJavaPath,
        CancellationToken cancellationToken)
    {
        var recommendedTask = _javaRuntimeManager.ResolveAsync(requiredVersion, null, cancellationToken);
        var discoveredTask = DiscoverJavaRuntimeOptionsAsync(requiredVersion, cancellationToken);
        var recommended = await recommendedTask;

        var options = new List<JavaRuntimeOption>
        {
            JavaRuntimeOption.Auto(
                recommended is null
                    ? $"未找到 Java {requiredVersion} 或更高版本"
                    : recommended.JavaPath,
                recommended?.JavaPath)
        };

        foreach (var option in await discoveredTask)
        {
            if (options.Any(item =>
                    !item.IsAutomatic &&
                    string.Equals(item.JavaPath, option.JavaPath, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            options.Add(option);
        }

        if (!string.IsNullOrWhiteSpace(manualJavaPath) &&
            options.All(item => !string.Equals(item.JavaPath, manualJavaPath, StringComparison.OrdinalIgnoreCase)))
        {
            var manualVersion = await _javaRuntimeManager.ProbeJavaMajorVersionForPathAsync(manualJavaPath, cancellationToken);
            var isCompatible = manualVersion >= requiredVersion;
            options.Add(JavaRuntimeOption.Manual(
                $"手动指定 | {FormatJavaVersion(manualVersion)} | {manualJavaPath}{FormatCompatibilitySuffix(isCompatible, requiredVersion)}",
                manualJavaPath,
                manualVersion,
                isCompatible));
        }

        return options;
    }

    private Task<IReadOnlyList<JavaRuntimeOption>> DiscoverJavaRuntimeOptionsAsync(
        int requiredVersion,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<JavaRuntimeOption>>(async () =>
        {
            var paths = DiscoverJavaCandidatePaths(requiredVersion);
            var options = new List<JavaRuntimeOption>();
            foreach (var path in paths.Take(80))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var version = await _javaRuntimeManager
                    .ProbeJavaMajorVersionForPathAsync(path, cancellationToken)
                    .ConfigureAwait(false);
                if (version <= 0)
                {
                    continue;
                }

                var isCompatible = version >= requiredVersion;
                options.Add(JavaRuntimeOption.Manual(
                    $"{GetJavaDisplayName(path, version)} | {RuntimeInformation.ProcessArchitecture} | {path}{FormatCompatibilitySuffix(isCompatible, requiredVersion)}",
                    path,
                    version,
                    isCompatible));
            }

            return options
                .OrderByDescending(option => option.IsCompatible)
                .ThenByDescending(option => option.MajorVersion)
                .ThenBy(option => option.JavaPath, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken);
    }

    private string[] DiscoverJavaCandidatePaths(int requiredVersion)
    {
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddJavaCandidate(paths, Environment.GetEnvironmentVariable("JAVA_HOME"));
        AddJavaCandidate(paths, Environment.GetEnvironmentVariable("JDK_HOME"));

        foreach (var version in new[] { requiredVersion, 8, 17, 21, 25 })
        {
            AddJavaCandidate(paths, _javaRuntimeManager.GetPrivateJavaExecutablePath(version));
        }

        if (Directory.Exists(_paths.JavaRuntimePath))
        {
            AddJavaCandidate(paths, _paths.JavaRuntimePath);
            foreach (var javaHome in SafeEnumerateDirectories(_paths.JavaRuntimePath).Take(80))
            {
                AddJavaCandidate(paths, javaHome);
            }
        }

        if (OperatingSystem.IsWindows())
        {
            AddProgramFilesJavaCandidates(paths);
        }
        else
        {
            AddUnixJavaCandidates(paths);
        }

        return paths.ToArray();
    }

    private static void AddJavaCandidate(ISet<string> paths, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        var path = Environment.ExpandEnvironmentVariables(value.Trim('"', ' '));
        if (Directory.Exists(path))
        {
            path = Path.Combine(path, "bin", OperatingSystem.IsWindows() ? "java.exe" : "java");
        }

        if (File.Exists(path))
        {
            paths.Add(Path.GetFullPath(path));
        }
    }

    private static void AddProgramFilesJavaCandidates(ISet<string> paths)
    {
        foreach (var programFiles in new[]
                 {
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                     Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)
                 })
        {
            if (string.IsNullOrWhiteSpace(programFiles))
            {
                continue;
            }

            foreach (var vendorDirectory in new[] { "Zulu", "Java", "Eclipse Adoptium", "Microsoft", "BellSoft", "Amazon Corretto" })
            {
                var directory = Path.Combine(programFiles, vendorDirectory);
                if (!Directory.Exists(directory))
                {
                    continue;
                }

                AddJavaCandidate(paths, directory);
                foreach (var javaHome in SafeEnumerateDirectories(directory).Take(80))
                {
                    AddJavaCandidate(paths, javaHome);
                }
            }
        }
    }

    private static void AddUnixJavaCandidates(ISet<string> paths)
    {
        foreach (var directory in new[]
                 {
                     "/Library/Java/JavaVirtualMachines",
                     "/usr/lib/jvm",
                     "/usr/java",
                     "/opt/java",
                     "/opt/homebrew/opt",
                     "/usr/local/opt"
                 })
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            AddJavaCandidate(paths, directory);
            foreach (var javaHome in SafeEnumerateDirectories(directory).Take(100))
            {
                AddJavaCandidate(paths, javaHome);
                var contentsHome = Path.Combine(javaHome, "Contents", "Home");
                AddJavaCandidate(paths, contentsHome);
            }
        }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string directory)
    {
        try
        {
            return Directory.EnumerateDirectories(directory).ToArray();
        }
        catch
        {
            return [];
        }
    }

    private int GetDefaultMemoryMb()
    {
        return Math.Max(1024, SelectedClient?.Installation.Definition.DefaultMemoryMb ?? 4096);
    }

    private int? ReadSelectedMemoryMb()
    {
        if (SelectedMemoryPreset is not { } option || option.IsAutomatic)
        {
            return null;
        }

        if (!option.IsCustom)
        {
            return option.MemoryMb;
        }

        if (!int.TryParse(MemoryMbText.Trim(), out var memoryMb))
        {
            throw new InvalidOperationException("最大内存需要填写数字，单位是 MB。");
        }

        if (memoryMb is < 1024 or > 65536)
        {
            throw new InvalidOperationException("最大内存建议填写 1024 到 65536 MB。");
        }

        return memoryMb;
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
        MemoryStatusText = SelectedMemoryPreset is { IsAutomatic: false }
            ? $"当前客户端：{selectedClient.Name}，最大内存 {FormatMemory(int.TryParse(MemoryMbText, out var memory) ? memory : defaultMemory)}。"
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

    private static string GetJavaDisplayName(string path, int version)
    {
        var lowerPath = path.ToLowerInvariant();
        if (lowerPath.Contains("zulu"))
        {
            return $"Zulu JDK {version}";
        }

        if (lowerPath.Contains("temurin") || lowerPath.Contains("adoptium"))
        {
            return $"Temurin JDK {version}";
        }

        if (lowerPath.Contains("corretto"))
        {
            return $"Corretto JDK {version}";
        }

        if (lowerPath.Contains("microsoft"))
        {
            return $"Microsoft JDK {version}";
        }

        if (lowerPath.Contains("bellsoft") || lowerPath.Contains("liberica"))
        {
            return $"Liberica JDK {version}";
        }

        return $"Java {version}";
    }

    private static string FormatJavaVersion(int majorVersion)
    {
        return majorVersion > 0 ? $"Java {majorVersion}" : "未知版本";
    }

    private static string FormatCompatibilitySuffix(bool isCompatible, int requiredVersion)
    {
        return isCompatible ? "" : $"（不兼容，需 Java {requiredVersion}+）";
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
    public int MajorVersion { get; init; }

    public bool IsAutomatic { get; init; }

    public bool IsCompatible { get; init; } = true;

    public string? RecommendedPath { get; init; }

    public static JavaRuntimeOption Auto(string recommended, string? recommendedPath = null)
    {
        return new JavaRuntimeOption($"自动选择（当前推荐：{recommended}）", null)
        {
            IsAutomatic = true,
            RecommendedPath = recommendedPath
        };
    }

    public static JavaRuntimeOption Manual(
        string displayName,
        string javaPath,
        int majorVersion = 0,
        bool isCompatible = true)
    {
        return new JavaRuntimeOption(displayName, javaPath)
        {
            MajorVersion = majorVersion,
            IsCompatible = isCompatible
        };
    }

    public override string ToString()
    {
        return DisplayName;
    }
}

public sealed record MemoryPresetOption(string DisplayName, int? MemoryMb)
{
    public bool IsAutomatic { get; init; }

    public bool IsCustom { get; init; }

    public static MemoryPresetOption Auto(int memoryMb)
    {
        return new MemoryPresetOption($"自动选择（当前推荐：{FormatMemory(memoryMb)}）", null)
        {
            IsAutomatic = true
        };
    }

    public static MemoryPresetOption Preset(int memoryMb)
    {
        return new MemoryPresetOption(FormatMemory(memoryMb), memoryMb);
    }

    public static MemoryPresetOption Custom()
    {
        return new MemoryPresetOption("自定义", null)
        {
            IsCustom = true
        };
    }

    public override string ToString()
    {
        return DisplayName;
    }

    private static string FormatMemory(int memoryMb)
    {
        return memoryMb >= 1024 && memoryMb % 1024 == 0
            ? $"{memoryMb / 1024} GB"
            : $"{memoryMb} MB";
    }
}
