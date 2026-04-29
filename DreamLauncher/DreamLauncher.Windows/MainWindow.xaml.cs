using System.IO;
using System.Globalization;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Clients;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Java;
using DreamLauncher.Core.Minecraft;
using DreamLauncher.Core.Remote;
using DreamLauncher.Models.Clients;
using DreamLauncher.Models.Config;
using DreamLauncher.Windows.Accounts;
using DreamLauncher.Windows.Security;
using DreamLauncher.Windows.ViewModels;
using Microsoft.Win32;

namespace DreamLauncher.Windows;

public partial class MainWindow : Window
{
    private static readonly int[] MemoryPresets = [2048, 4096, 6144, 8192, 12288, 16384];
    private readonly LauncherPaths _paths;
    private readonly LauncherConfigStore _configStore;
    private readonly AccountManager _accountManager;
    private readonly JavaRuntimeManager _javaRuntimeManager;
    private readonly MainWindowViewModel _viewModel;
    private CancellationTokenSource? _javaRuntimeRefreshCancellation;
    private bool _isUpdatingJavaRuntimeOptions;
    private bool _isUpdatingMemoryOptions;
    private int _javaRuntimeRefreshRequestId;

    public MainWindow()
    {
        InitializeComponent();

        _paths = new LauncherPaths();
        _paths.EnsureCreated();

        _configStore = new LauncherConfigStore(_paths);
        var downloadService = new HttpDownloadService();
        var extractor = new SafeZipExtractor();
        var remoteConfigClient = new RemoteConfigClient();
        var tokenStore = new WindowsCredentialTokenStore();
        var accountProfileStore = new AccountProfileStore(_paths);
        var browserLauncher = new WindowsBrowserLauncher();
        var deviceCodePresenter = new WindowsMicrosoftDeviceCodePresenter();
        var authService = new MicrosoftAuthService(browserLauncher, deviceCodePresenter);
        var thirdPartyAuthService = new ThirdPartyAuthService();
        _accountManager = new AccountManager(_configStore, accountProfileStore, tokenStore, authService, thirdPartyAuthService);
        var clientManager = new ClientManager(_paths, downloadService, extractor);
        _javaRuntimeManager = new JavaRuntimeManager(_paths, downloadService, extractor);
        var minecraftLaunchService = new MinecraftLaunchService(_paths);

        _viewModel = new MainWindowViewModel(
            _configStore,
            remoteConfigClient,
            clientManager,
            _javaRuntimeManager,
            _accountManager,
            tokenStore,
            minecraftLaunchService);

        _viewModel.MessageRequested += message =>
            LauncherMessageBox.Show(this, message, "DreamLauncher", LauncherMessageKind.Info);

        DataContext = _viewModel;
        _viewModel.PropertyChanged += async (_, args) =>
        {
            if (args.PropertyName == nameof(MainWindowViewModel.SelectedClient) &&
                SettingsPage?.Visibility == Visibility.Visible)
            {
                await RefreshJavaRuntimeOptionsAsync();
                await RefreshMemoryOptionsAsync();
            }
        };
        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= MainWindow_Loaded;
        await _viewModel.InitializeAsync();
        await LoadInlineSettingsAsync();
        ShowPage("launch");
    }

    private void LaunchNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("launch");
    }

    private void DownloadNav_Click(object sender, RoutedEventArgs e)
    {
        ShowPage("download");
    }

    private async void SettingsNav_Click(object sender, RoutedEventArgs e)
    {
        await LoadInlineSettingsAsync();
        ShowPage("settings");
    }

    private async void SettingsButton_Click(object sender, RoutedEventArgs e)
    {
        await LoadInlineSettingsAsync();
        ShowPage("settings");
    }

    private async void ReloadInlineSettings_Click(object sender, RoutedEventArgs e)
    {
        await LoadInlineSettingsAsync();
    }

    private async void SaveInlineSettings_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = await _configStore.LoadAsync();
            ApplySelectedMemorySetting(config);

            if (int.TryParse(MainMaxRetryCountTextBox.Text, out var retryCount))
            {
                config.Download.MaxRetryCount = Math.Clamp(retryCount, 1, 10);
            }

            config.Download.SpeedLimitKbPerSecond = int.TryParse(MainSpeedLimitTextBox.Text, out var speedLimit)
                ? Math.Max(1, speedLimit)
                : null;

            await _configStore.SaveAsync(config);
            await _viewModel.InitializeAsync();
            await LoadInlineSettingsAsync();
            LauncherMessageBox.Show(this, "设置已保存。", "DreamLauncher", LauncherMessageKind.Success);
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "保存失败", LauncherMessageKind.Warning);
        }
    }

    private void ClientActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: ClientInstallationViewModel client })
        {
            return;
        }

        _viewModel.SelectedClient = client;
        if (_viewModel.PrimaryActionCommand.CanExecute(null))
        {
            _viewModel.PrimaryActionCommand.Execute(null);
        }
    }

    private async void LoginAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var selectionWindow = new AuthTypeSelectionWindow
        {
            Owner = this
        };

        if (selectionWindow.ShowDialog() != true || selectionWindow.SelectedAuthType is null)
        {
            return;
        }

        switch (selectionWindow.SelectedAuthType.Value)
        {
            case AuthTypeChoice.Microsoft:
                if (_viewModel.AddAccountCommand.CanExecute(null))
                {
                    _viewModel.AddAccountCommand.Execute(null);
                }

                break;
            case AuthTypeChoice.ThirdParty:
                await ShowThirdPartyLoginAsync();
                break;
            case AuthTypeChoice.Offline:
                await ShowOfflineAccountAsync();
                break;
        }
    }

    private async void SwitchAccountButton_Click(object sender, RoutedEventArgs e)
    {
        var switchWindow = new AccountSwitchWindow(_accountManager)
        {
            Owner = this
        };

        if (switchWindow.ShowDialog() == true || switchWindow.AccountChanged)
        {
            await _viewModel.InitializeAsync();
            await LoadInlineSettingsAsync();
        }
    }

    private async void ChooseHashFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择需要计算 SHA256 的文件",
            CheckFileExists = true,
            Multiselect = false
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        HashToolPathTextBox.Text = dialog.FileName;
        await CalculateSelectedFileHashAsync();
    }

    private async void CalculateHash_Click(object sender, RoutedEventArgs e)
    {
        await CalculateSelectedFileHashAsync();
    }

    private void CopyHashResult_Click(object sender, RoutedEventArgs e)
    {
        var text = HashToolJsonTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"sha256={HashToolSha256TextBox.Text}{Environment.NewLine}size={HashToolSizeTextBox.Text}";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            LauncherMessageBox.Show(this, "请先选择文件并计算。", "文件校验工具", LauncherMessageKind.Info);
            return;
        }

        Clipboard.SetText(text);
        LauncherMessageBox.Show(this, "结果已复制。", "文件校验工具", LauncherMessageKind.Success);
    }

    private async Task LoadInlineSettingsAsync()
    {
        var config = await _configStore.LoadAsync();
        MainMaxRetryCountTextBox.Text = config.Download.MaxRetryCount.ToString();
        MainSpeedLimitTextBox.Text = config.Download.SpeedLimitKbPerSecond?.ToString() ?? "";
        await RefreshJavaRuntimeOptionsAsync(config);
        await RefreshMemoryOptionsAsync(config);
    }

    private async void AutoDetectJava_Click(object sender, RoutedEventArgs e)
    {
        await SaveSelectedClientJavaPathAsync(null);
        await RefreshJavaRuntimeOptionsAsync();
    }

    private async void AddJavaRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (_viewModel.SelectedClient is null)
        {
            LauncherMessageBox.Show(this, "请先选择一个客户端。", "Java 环境", LauncherMessageKind.Info);
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择 Java 可执行文件",
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Java 可执行文件|java.exe;javaw.exe|所有文件|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        var javaPath = _javaRuntimeManager.NormalizeJavaPathForUserInput(dialog.FileName);
        var version = await _javaRuntimeManager.ProbeJavaMajorVersionForPathAsync(javaPath);
        var required = _viewModel.SelectedClient.Installation.Definition.JavaVersion;
        if (version < required)
        {
            LauncherMessageBox.Show(
                this,
                $"这个 Java 是 {FormatJavaVersion(version)}，当前客户端需要 Java {required} 或更高版本。",
                "Java 版本不匹配",
                LauncherMessageKind.Warning);
            return;
        }

        await SaveSelectedClientJavaPathAsync(javaPath);
        await RefreshJavaRuntimeOptionsAsync();
    }

    private async void JavaRuntimeComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingJavaRuntimeOptions ||
            JavaRuntimeComboBox.SelectedItem is not JavaRuntimeOption option)
        {
            return;
        }

        if (!option.IsAutomatic && !option.IsCompatible)
        {
            var required = _viewModel.SelectedClient?.Installation.Definition.JavaVersion ?? 17;
            LauncherMessageBox.Show(
                this,
                $"这个 Java 是 {FormatJavaVersion(option.MajorVersion)}，当前客户端需要 Java {required} 或更高版本。",
                "Java 版本不匹配",
                LauncherMessageKind.Warning);
            await RefreshJavaRuntimeOptionsAsync();
            return;
        }

        await SaveSelectedClientJavaPathAsync(option.IsAutomatic ? null : option.JavaPath);
        await RefreshJavaRuntimeOptionsAsync();
    }

    private async Task RefreshJavaRuntimeOptionsAsync(LauncherConfig? config = null)
    {
        var refreshId = Interlocked.Increment(ref _javaRuntimeRefreshRequestId);
        _javaRuntimeRefreshCancellation?.Cancel();
        var refreshCancellation = new CancellationTokenSource();
        _javaRuntimeRefreshCancellation = refreshCancellation;
        var cancellationToken = refreshCancellation.Token;

        SetJavaDetectionControlsEnabled(false);

        try
        {
            JavaRuntimeStatusTextBlock.Text = "正在后台检测 Java 环境...";
            await Task.Yield();

            config ??= await _configStore.LoadAsync(cancellationToken);
            var selectedClient = _viewModel.SelectedClient;
            var requiredVersion = selectedClient?.Installation.Definition.JavaVersion ?? 17;
            var clientName = selectedClient?.Name ?? "未选择客户端";
            var clientSettings = selectedClient is null
                ? null
                : config.ClientSettings.GetValueOrDefault(selectedClient.Id);
            var manualJavaPath = clientSettings?.JavaPath;

            var recommendedTask = _javaRuntimeManager.ResolveAsync(requiredVersion, null, cancellationToken);
            var discoveredTask = DiscoverJavaRuntimeOptionsAsync(requiredVersion, cancellationToken);

            var recommended = await recommendedTask;
            var options = new List<JavaRuntimeOption>
            {
                new()
                {
                    DisplayName = recommended is null
                        ? $"自动选择（未找到 Java {requiredVersion} 或更高版本）"
                        : $"自动选择（当前推荐：{recommended.JavaPath}）",
                    IsAutomatic = true
                }
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
                options.Add(new JavaRuntimeOption
                {
                    DisplayName = $"手动指定 | {FormatJavaVersion(manualVersion)} | {manualJavaPath}{FormatCompatibilitySuffix(isCompatible, requiredVersion)}",
                    JavaPath = manualJavaPath,
                    MajorVersion = manualVersion,
                    IsCompatible = isCompatible
                });
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (refreshId != _javaRuntimeRefreshRequestId)
            {
                return;
            }

            _isUpdatingJavaRuntimeOptions = true;
            try
            {
                JavaRuntimeComboBox.ItemsSource = options;
                JavaRuntimeComboBox.SelectedItem = string.IsNullOrWhiteSpace(manualJavaPath)
                    ? options.First()
                    : options.FirstOrDefault(item => string.Equals(item.JavaPath, manualJavaPath, StringComparison.OrdinalIgnoreCase))
                      ?? options.First();
            }
            finally
            {
                _isUpdatingJavaRuntimeOptions = false;
            }

            JavaRuntimeStatusTextBlock.Text = selectedClient is null
                ? "当前未选择客户端，自动选择暂按 Java 17 检测。"
                : $"当前客户端：{clientName}，需要 Java {requiredVersion}。自动选择会优先使用启动器私有 Java，再尝试系统 Java。";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            if (refreshId == _javaRuntimeRefreshRequestId)
            {
                SetJavaDetectionControlsEnabled(true);
                _javaRuntimeRefreshCancellation = null;
            }

            refreshCancellation.Dispose();
        }
    }

    private async Task RefreshMemoryOptionsAsync(LauncherConfig? config = null)
    {
        config ??= await _configStore.LoadAsync();
        var selectedClient = _viewModel.SelectedClient;
        var defaultMemory = Math.Max(1024, selectedClient?.Installation.Definition.DefaultMemoryMb ?? 4096);
        var savedMemory = selectedClient is null
            ? null
            : config.ClientSettings.GetValueOrDefault(selectedClient.Id)?.MemoryMb;

        var options = new List<MemoryPresetOption>
        {
            new()
            {
                DisplayName = $"自动选择（当前推荐：{FormatMemory(defaultMemory)}）",
                IsAutomatic = true
            }
        };

        options.AddRange(MemoryPresets.Select(memory => new MemoryPresetOption
        {
            DisplayName = FormatMemory(memory),
            MemoryMb = memory
        }));
        options.Add(new MemoryPresetOption
        {
            DisplayName = "自定义",
            IsCustom = true
        });

        var selectedOption = savedMemory.HasValue
            ? options.FirstOrDefault(item => item.MemoryMb == savedMemory.Value) ??
              options.First(item => item.IsCustom)
            : options.First();

        _isUpdatingMemoryOptions = true;
        try
        {
            MemoryPresetComboBox.ItemsSource = options;
            MemoryPresetComboBox.SelectedItem = selectedOption;
            CustomMemoryTextBox.Text = savedMemory?.ToString(CultureInfo.InvariantCulture) ??
                                       defaultMemory.ToString(CultureInfo.InvariantCulture);
        }
        finally
        {
            _isUpdatingMemoryOptions = false;
        }

        UpdateCustomMemoryInputState();
        MemoryStatusTextBlock.Text = selectedClient is null
            ? "当前未选择客户端。"
            : savedMemory.HasValue
                ? $"当前客户端：{selectedClient.Name}，最大内存 {FormatMemory(savedMemory.Value)}。"
                : $"当前客户端：{selectedClient.Name}，自动使用 {FormatMemory(defaultMemory)}。";
    }

    private void MemoryPresetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isUpdatingMemoryOptions)
        {
            return;
        }

        UpdateCustomMemoryInputState();
    }

    private void UpdateCustomMemoryInputState()
    {
        var isCustom = MemoryPresetComboBox.SelectedItem is MemoryPresetOption { IsCustom: true };
        CustomMemoryTextBox.IsEnabled = isCustom;
        CustomMemoryTextBox.Opacity = isCustom ? 1 : 0.55;
    }

    private void ApplySelectedMemorySetting(LauncherConfig config)
    {
        var selectedClient = _viewModel.SelectedClient;
        if (selectedClient is null)
        {
            return;
        }

        var memoryMb = ReadSelectedMemoryMb();
        if (!config.ClientSettings.TryGetValue(selectedClient.Id, out var settings))
        {
            settings = new ClientUserSettings();
            config.ClientSettings[selectedClient.Id] = settings;
        }

        settings.MemoryMb = memoryMb;
        selectedClient.Installation.MemoryMb = memoryMb ?? selectedClient.Installation.Definition.DefaultMemoryMb;
        selectedClient.Update(selectedClient.Installation);
    }

    private int? ReadSelectedMemoryMb()
    {
        if (MemoryPresetComboBox.SelectedItem is not MemoryPresetOption option || option.IsAutomatic)
        {
            return null;
        }

        if (!option.IsCustom)
        {
            return option.MemoryMb;
        }

        if (!int.TryParse(CustomMemoryTextBox.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var memoryMb))
        {
            throw new InvalidOperationException("最大内存需要填写数字，单位是 MB。");
        }

        if (memoryMb is < 1024 or > 65536)
        {
            throw new InvalidOperationException("最大内存建议填写 1024 到 65536 MB。");
        }

        return memoryMb;
    }

    private void SetJavaDetectionControlsEnabled(bool isEnabled)
    {
        JavaRuntimeComboBox.IsEnabled = isEnabled;
        AutoDetectJavaButton.IsEnabled = isEnabled;
        AddJavaRuntimeButton.IsEnabled = isEnabled;
    }

    private Task<IReadOnlyList<JavaRuntimeOption>> DiscoverJavaRuntimeOptionsAsync(
        int requiredVersion,
        CancellationToken cancellationToken)
    {
        return Task.Run<IReadOnlyList<JavaRuntimeOption>>(async () =>
        {
            var paths = DiscoverJavaCandidatePaths(requiredVersion);
            var options = new List<JavaRuntimeOption>();
            foreach (var path in paths.Take(40))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var version = await _javaRuntimeManager.ProbeJavaMajorVersionForPathAsync(path, cancellationToken).ConfigureAwait(false);
                if (version <= 0)
                {
                    continue;
                }

                var isCompatible = version >= requiredVersion;
                options.Add(new JavaRuntimeOption
                {
                    DisplayName = $"{GetJavaDisplayName(path, version)} | x64 | {path}{FormatCompatibilitySuffix(isCompatible, requiredVersion)}",
                    JavaPath = path,
                    MajorVersion = version,
                    IsCompatible = isCompatible
                });
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
        AddJavaCandidate(paths, _javaRuntimeManager.GetPrivateJavaExecutablePath(requiredVersion));

        if (Directory.Exists(_paths.JavaRuntimePath))
        {
            AddJavaCandidate(paths, _paths.JavaRuntimePath);
            foreach (var javaHome in SafeEnumerateDirectories(_paths.JavaRuntimePath).Take(40))
            {
                AddJavaCandidate(paths, javaHome);
            }
        }

        AddProgramFilesJavaCandidates(paths);
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
            path = Path.Combine(path, "bin", "java.exe");
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

    private async Task SaveSelectedClientJavaPathAsync(string? javaPath)
    {
        var selectedClient = _viewModel.SelectedClient;
        if (selectedClient is null)
        {
            return;
        }

        var config = await _configStore.LoadAsync();
        if (!config.ClientSettings.TryGetValue(selectedClient.Id, out var settings))
        {
            settings = new ClientUserSettings();
            config.ClientSettings[selectedClient.Id] = settings;
        }

        settings.JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : javaPath;
        await _configStore.SaveAsync(config);
        selectedClient.Installation.JavaPath = settings.JavaPath;
    }

    private async Task CalculateSelectedFileHashAsync()
    {
        var path = HashToolPathTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            LauncherMessageBox.Show(this, "请选择一个存在的本地文件。", "文件校验工具", LauncherMessageKind.Info);
            return;
        }

        try
        {
            HashToolSha256TextBox.Text = "计算中...";
            HashToolSizeTextBox.Text = "";
            HashToolJsonTextBox.Text = "";

            var result = await Task.Run(() =>
            {
                var fileInfo = new FileInfo(path);
                using var stream = File.OpenRead(path);
                var hashBytes = SHA256.HashData(stream);
                var sha256 = Convert.ToHexString(hashBytes).ToLowerInvariant();
                return (sha256, fileInfo.Length);
            });

            var sizeText = result.Length.ToString(CultureInfo.InvariantCulture);
            HashToolSha256TextBox.Text = result.sha256;
            HashToolSizeTextBox.Text = sizeText;
            HashToolJsonTextBox.Text =
                $"\"packSha256\": \"{result.sha256}\",{Environment.NewLine}" +
                $"\"packSize\": {sizeText},{Environment.NewLine}" +
                $"\"windowsX64Sha256\": \"{result.sha256}\",{Environment.NewLine}" +
                $"\"size\": {sizeText}";
        }
        catch (Exception ex)
        {
            HashToolSha256TextBox.Text = "";
            HashToolSizeTextBox.Text = "";
            HashToolJsonTextBox.Text = "";
            LauncherMessageBox.Show(this, ex.Message, "计算失败", LauncherMessageKind.Warning);
        }
    }

    private void ShowPage(string page)
    {
        LaunchPage.Visibility = page == "launch" ? Visibility.Visible : Visibility.Collapsed;
        DownloadPage.Visibility = page == "download" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "settings" ? Visibility.Visible : Visibility.Collapsed;

        SetNavState(LaunchNavButton, page == "launch");
        SetNavState(DownloadNavButton, page == "download");
        SetNavState(SettingsNavButton, page == "settings");
    }

    private void SetNavState(Button button, bool isActive)
    {
        button.Background = isActive
            ? (Brush)FindResource("GlassBrushLight")
            : Brushes.Transparent;
        button.BorderBrush = isActive
            ? (Brush)FindResource("GlassBrushLight")
            : Brushes.Transparent;
        button.Foreground = isActive
            ? (Brush)FindResource("TextPrimary")
            : new SolidColorBrush(Color.FromRgb(184, 178, 216));
        button.Opacity = isActive ? 1 : 0.74;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed)
        {
            return;
        }

        DragMove();
    }

    private void ShellContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ShellContent.Clip = new RectangleGeometry(
            new Rect(0, 0, ShellContent.ActualWidth, ShellContent.ActualHeight),
            ShellBorder.CornerRadius.TopLeft,
            ShellBorder.CornerRadius.TopLeft);
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private async void OfflineAccountButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowOfflineAccountAsync();
    }

    private async Task ShowOfflineAccountAsync()
    {
        var accountWindow = new OfflineAccountWindow
        {
            Owner = this
        };

        if (accountWindow.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.AddOfflineAccountAsync(accountWindow.PlayerName);
    }

    private async void ThirdPartyLoginButton_Click(object sender, RoutedEventArgs e)
    {
        await ShowThirdPartyLoginAsync();
    }

    private async Task ShowThirdPartyLoginAsync()
    {
        var loginWindow = new ThirdPartyLoginWindow
        {
            Owner = this
        };

        if (loginWindow.ShowDialog() != true)
        {
            return;
        }

        await _viewModel.AddThirdPartyAccountAsync(
            loginWindow.ApiRoot,
            loginWindow.Username,
            loginWindow.Password);
    }

    private static string GetJavaDisplayName(string path, int version)
    {
        var normalized = path.Replace('\\', '/');
        if (normalized.Contains("/Zulu/", StringComparison.OrdinalIgnoreCase))
        {
            return $"Zulu JDK {version}";
        }

        if (normalized.Contains("/Eclipse Adoptium/", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("/Temurin", StringComparison.OrdinalIgnoreCase))
        {
            return $"Temurin JDK {version}";
        }

        if (normalized.Contains("/Microsoft/", StringComparison.OrdinalIgnoreCase))
        {
            return $"Microsoft JDK {version}";
        }

        return $"Java {version}";
    }

    private static string FormatJavaVersion(int version)
    {
        return version <= 0 ? "未知版本" : $"Java {version}";
    }

    private static string FormatMemory(int memoryMb)
    {
        return memoryMb >= 1024 && memoryMb % 1024 == 0
            ? $"{memoryMb / 1024} GB"
            : $"{memoryMb} MB";
    }

    private static string FormatCompatibilitySuffix(bool isCompatible, int requiredVersion)
    {
        return isCompatible ? "" : $"（不兼容，需 Java {requiredVersion}+）";
    }

    private sealed class JavaRuntimeOption
    {
        public required string DisplayName { get; init; }

        public string? JavaPath { get; init; }

        public int MajorVersion { get; init; }

        public bool IsAutomatic { get; init; }

        public bool IsCompatible { get; init; } = true;
    }

    private sealed class MemoryPresetOption
    {
        public required string DisplayName { get; init; }

        public int? MemoryMb { get; init; }

        public bool IsAutomatic { get; init; }

        public bool IsCustom { get; init; }
    }
}
