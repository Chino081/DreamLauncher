using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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
using DreamLauncher.Models.Minecraft;
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
    private string _contentStatusMessage = "请选择一个已安装客户端。";
    private string _contentGameDirectory = "";
    private double _progressValue;
    private bool _hasProgress;
    private bool _isBusy;
    private CancellationTokenSource? _operationCancellation;
    private ImageSource? _currentAccountAvatar;
    private int _avatarLoadVersion;
    private static readonly HttpClient AvatarHttpClient = CreateAvatarHttpClient();

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

    public ObservableCollection<GameContentItemViewModel> ResourcePacks { get; } = [];

    public ObservableCollection<GameContentItemViewModel> ShaderPacks { get; } = [];

    public ObservableCollection<GameContentItemViewModel> Mods { get; } = [];

    public string ResourcePacksTabText => $"🖼 资源包（{ResourcePacks.Count}）";

    public string ShaderPacksTabText => $"☀ 光影包（{ShaderPacks.Count}）";

    public string ModsTabText => $"🔧 Mod（{Mods.Count}）";

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
                OnPropertyChanged(nameof(CurrentAccountInitial));
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

    public ImageSource? CurrentAccountAvatar
    {
        get => _currentAccountAvatar;
        private set
        {
            if (ReferenceEquals(_currentAccountAvatar, value))
            {
                return;
            }

            _currentAccountAvatar = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCurrentAccountAvatar));
        }
    }

    public bool HasCurrentAccountAvatar => CurrentAccountAvatar is not null;

    private async void RefreshCurrentAccountAvatar()
    {
        var version = unchecked(++_avatarLoadVersion);
        CurrentAccountAvatar = null;
        var account = CurrentAccount;
        if (account is null)
        {
            return;
        }

        var officialSkinAvatar = await LoadOfficialSkinAvatarAsync(account);
        if (officialSkinAvatar is not null)
        {
            if (version == _avatarLoadVersion)
            {
                CurrentAccountAvatar = officialSkinAvatar;
            }

            return;
        }

        foreach (var avatarUrl in AccountAvatarUrl.FromAccountCandidates(account))
        {
            try
            {
                var avatar = await LoadBitmapImageAsync(avatarUrl, 96);

                if (version == _avatarLoadVersion)
                {
                    CurrentAccountAvatar = avatar;
                }

                return;
            }
            catch
            {
                // Try the next avatar provider, then fall back to the account initial.
            }
        }
    }

    private static async Task<ImageSource?> LoadOfficialSkinAvatarAsync(AccountMetadata account)
    {
        if (account.Type != AccountType.Microsoft ||
            string.IsNullOrWhiteSpace(account.Uuid))
        {
            return null;
        }

        var uuid = account.Uuid.Trim().Replace("-", "", StringComparison.Ordinal);
        if (uuid.Length == 0)
        {
            return null;
        }

        try
        {
            var profileUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{Uri.EscapeDataString(uuid)}";
            using var response = await AvatarHttpClient.GetAsync(profileUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();

            var profileJson = await response.Content.ReadAsStringAsync();
            var skinUrl = ReadSkinUrl(profileJson);
            if (string.IsNullOrWhiteSpace(skinUrl))
            {
                return null;
            }

            var skin = await LoadBitmapImageAsync(skinUrl, null);
            return CreateMinecraftHeadAvatar(skin);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<BitmapImage> LoadBitmapImageAsync(string url, int? decodePixelWidth)
    {
        using var response = await AvatarHttpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var remoteStream = await response.Content.ReadAsStreamAsync();
        using var memoryStream = new MemoryStream();
        await remoteStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;

        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        if (decodePixelWidth is > 0)
        {
            image.DecodePixelWidth = decodePixelWidth.Value;
        }

        image.StreamSource = memoryStream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static string? ReadSkinUrl(string profileJson)
    {
        using var profileDocument = JsonDocument.Parse(profileJson);
        if (!profileDocument.RootElement.TryGetProperty("properties", out var properties) ||
            properties.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var property in properties.EnumerateArray())
        {
            if (!property.TryGetProperty("name", out var name) ||
                !string.Equals(name.GetString(), "textures", StringComparison.OrdinalIgnoreCase) ||
                !property.TryGetProperty("value", out var value) ||
                string.IsNullOrWhiteSpace(value.GetString()))
            {
                continue;
            }

            var textureJson = Encoding.UTF8.GetString(Convert.FromBase64String(value.GetString()!));
            using var textureDocument = JsonDocument.Parse(textureJson);
            if (textureDocument.RootElement.TryGetProperty("textures", out var textures) &&
                textures.TryGetProperty("SKIN", out var skin) &&
                skin.TryGetProperty("url", out var url))
            {
                return url.GetString();
            }
        }

        return null;
    }

    private static ImageSource CreateMinecraftHeadAvatar(BitmapSource skin)
    {
        const int sourceHeadSize = 8;
        const int targetSize = 64;
        const int blockSize = targetSize / sourceHeadSize;

        var converted = skin.Format == PixelFormats.Bgra32
            ? skin
            : new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);

        var stride = converted.PixelWidth * 4;
        var pixels = new byte[stride * converted.PixelHeight];
        converted.CopyPixels(pixels, stride, 0);

        var scaleX = Math.Max(1, converted.PixelWidth / 64);
        var scaleY = Math.Max(1, converted.PixelHeight / 64);
        var targetStride = targetSize * 4;
        var targetPixels = new byte[targetStride * targetSize];

        for (var y = 0; y < sourceHeadSize; y++)
        {
            for (var x = 0; x < sourceHeadSize; x++)
            {
                var baseColor = ReadPixel(pixels, stride, (8 + x) * scaleX, (8 + y) * scaleY);
                var overlayColor = ReadPixel(pixels, stride, (40 + x) * scaleX, (8 + y) * scaleY);
                var color = BlendOver(baseColor, overlayColor);

                for (var py = y * blockSize; py < (y + 1) * blockSize; py++)
                {
                    for (var px = x * blockSize; px < (x + 1) * blockSize; px++)
                    {
                        WritePixel(targetPixels, targetStride, px, py, color);
                    }
                }
            }
        }

        var avatar = BitmapSource.Create(
            targetSize,
            targetSize,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            targetPixels,
            targetStride);
        avatar.Freeze();
        return avatar;
    }

    private static BgraColor ReadPixel(byte[] pixels, int stride, int x, int y)
    {
        var index = (y * stride) + (x * 4);
        return new BgraColor(
            pixels[index],
            pixels[index + 1],
            pixels[index + 2],
            pixels[index + 3]);
    }

    private static void WritePixel(byte[] pixels, int stride, int x, int y, BgraColor color)
    {
        var index = (y * stride) + (x * 4);
        pixels[index] = color.B;
        pixels[index + 1] = color.G;
        pixels[index + 2] = color.R;
        pixels[index + 3] = color.A;
    }

    private static BgraColor BlendOver(BgraColor background, BgraColor foreground)
    {
        if (foreground.A == 0)
        {
            return background;
        }

        if (foreground.A == 255)
        {
            return foreground;
        }

        var alpha = foreground.A / 255d;
        return new BgraColor(
            (byte)Math.Round((foreground.B * alpha) + (background.B * (1d - alpha))),
            (byte)Math.Round((foreground.G * alpha) + (background.G * (1d - alpha))),
            (byte)Math.Round((foreground.R * alpha) + (background.R * (1d - alpha))),
            255);
    }

    private readonly record struct BgraColor(byte B, byte G, byte R, byte A);

    private static HttpClient CreateAvatarHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("DreamLauncher/0.1");
        return client;
    }

    public string CurrentAccountStatusText => CurrentAccount is null
        ? "请添加账号"
        : CurrentAccount.Status == AccountLoginStatus.Invalid
            ? "需要重新登录"
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

            var progress = CreateProgressReporter(selectedClient);
            await _clientManager.InstallOrUpdateAsync(selectedClient.Installation.Definition, config, progress, cancellationToken);
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

            var progress = CreateProgressReporter(selectedClient);
            await _clientManager.RepairAsync(selectedClient.Installation.Definition, config, progress, cancellationToken);
            selectedClient.SetStatus(ClientInstallStatus.Ready);
            StatusMessage = $"{selectedClient.Name} \u4fee\u590d\u5b8c\u6210";
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
