using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DreamLauncher.Avalonia.Accounts;
using DreamLauncher.Avalonia.Dialogs;
using DreamLauncher.Avalonia.Security;
using DreamLauncher.Avalonia.ViewModels;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Archives;
using DreamLauncher.Core.Clients;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Downloads;
using DreamLauncher.Core.Java;
using DreamLauncher.Core.Minecraft;
using DreamLauncher.Core.Remote;
using DreamLauncher.Core.Security;
using System.Globalization;

namespace DreamLauncher.Avalonia;

public partial class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();

        var paths = new LauncherPaths();
        paths.EnsureCreated();

        var configStore = new LauncherConfigStore(paths);
        var downloadService = new HttpDownloadService();
        var extractor = new SafeZipExtractor();
        var remoteConfigClient = new RemoteConfigClient();
        var tokenStore = new CrossPlatformTokenStore(paths);
        var accountProfileStore = new AccountProfileStore(paths);
        var browserLauncher = new AvaloniaBrowserLauncher();
        var deviceCodePresenter = new AvaloniaMicrosoftDeviceCodePresenter();
        var authService = new MicrosoftAuthService(browserLauncher, deviceCodePresenter);
        var thirdPartyAuthService = new ThirdPartyAuthService();
        var accountManager = new AccountManager(configStore, accountProfileStore, tokenStore, authService, thirdPartyAuthService);
        var clientManager = new ClientManager(paths, downloadService, extractor);
        var javaRuntimeManager = new JavaRuntimeManager(paths, downloadService, extractor);
        var minecraftLaunchService = new MinecraftLaunchService(paths);

        _viewModel = new MainWindowViewModel(
            paths,
            configStore,
            remoteConfigClient,
            clientManager,
            javaRuntimeManager,
            accountManager,
            tokenStore,
            minecraftLaunchService);

        _viewModel.MessageRequested += async (title, message) => await MessageDialog.ShowAsync(this, title, message);
        DataContext = _viewModel;
        Opened += async (_, _) =>
        {
            SetActivePage("launch");
            await _viewModel.InitializeAsync();
        };
    }

    private void LaunchNav_Click(object? sender, RoutedEventArgs e)
    {
        SetActivePage("launch");
    }

    private void DownloadNav_Click(object? sender, RoutedEventArgs e)
    {
        SetActivePage("download");
    }

    private async void SettingsNav_Click(object? sender, RoutedEventArgs e)
    {
        SetActivePage("settings");
        await _viewModel.RefreshSettingsOptionsAsync();
    }

    private async void SwitchAccount_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AccountSwitchWindow(_viewModel);
        await dialog.ShowDialog<bool?>(this);
    }

    private async void LoginAccount_Click(object? sender, RoutedEventArgs e)
    {
        var dialog = new AuthTypeSelectionWindow();
        var result = await dialog.ShowDialog<bool?>(this);
        if (result == true &&
            dialog.SelectedAuthType == AuthTypeChoice.Microsoft &&
            _viewModel.AddAccountCommand.CanExecute(null))
        {
            _viewModel.AddAccountCommand.Execute(null);
        }
    }

    private void ClientAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is Control { DataContext: ClientInstallationViewModel client })
        {
            _viewModel.SelectedClient = client;
        }

        if (_viewModel.PrimaryActionCommand.CanExecute(null))
        {
            _viewModel.PrimaryActionCommand.Execute(null);
        }
    }

    private void DownloadClientAction_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ClientInstallationViewModel client })
        {
            return;
        }

        _viewModel.SelectedClient = client;
        if (!client.CanDownloadFromDownloadCenter || !_viewModel.PrimaryActionCommand.CanExecute(null))
        {
            return;
        }

        _viewModel.PrimaryActionCommand.Execute(null);
    }

    private async void ChooseJava_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 Java 可执行文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("Java 可执行文件")
                {
                    Patterns = OperatingSystem.IsWindows() ? ["*.exe"] : ["*"]
                },
                FilePickerFileTypes.All
            ]
        });

        if (files.Count > 0)
        {
            _viewModel.JavaPathText = files[0].Path.LocalPath;
        }
    }

    private void MemoryAuto_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.MemoryMbText = "";
    }

    private void Memory2048_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.MemoryMbText = "2048";
    }

    private void Memory4096_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.MemoryMbText = "4096";
    }

    private void Memory8192_Click(object? sender, RoutedEventArgs e)
    {
        _viewModel.MemoryMbText = "8192";
    }

    private async void ChooseHashFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择需要计算 SHA256 的文件",
            AllowMultiple = false
        });

        if (files.Count > 0)
        {
            HashToolPathTextBox.Text = files[0].Path.LocalPath;
            await CalculateSelectedFileHashAsync();
        }
    }

    private async void CalculateHash_Click(object? sender, RoutedEventArgs e)
    {
        await CalculateSelectedFileHashAsync();
    }

    private async void CopyHashResult_Click(object? sender, RoutedEventArgs e)
    {
        var text = HashToolJsonTextBox.Text;
        if (string.IsNullOrWhiteSpace(text))
        {
            text = $"sha256={HashToolSha256TextBox.Text}{Environment.NewLine}size={HashToolSizeTextBox.Text}";
        }

        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(HashToolSha256TextBox.Text))
        {
            await MessageDialog.ShowAsync(this, "文件校验工具", "请先选择文件并计算。");
            return;
        }

        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
            await MessageDialog.ShowAsync(this, "文件校验工具", "结果已复制。");
        }
    }

    private async Task CalculateSelectedFileHashAsync()
    {
        var path = HashToolPathTextBox.Text?.Trim() ?? "";
        if (!File.Exists(path))
        {
            await MessageDialog.ShowAsync(this, "文件校验工具", "请选择一个存在的本地文件。");
            return;
        }

        try
        {
            HashToolSha256TextBox.Text = "计算中...";
            HashToolSizeTextBox.Text = "";
            HashToolJsonTextBox.Text = "";

            var sha256 = await Sha256Hasher.ComputeFileAsync(path);
            var size = new FileInfo(path).Length.ToString(CultureInfo.InvariantCulture);
            HashToolSha256TextBox.Text = sha256;
            HashToolSizeTextBox.Text = size;
            HashToolJsonTextBox.Text =
                $"\"packSha256\": \"{sha256}\",{Environment.NewLine}" +
                $"\"packSize\": {size},{Environment.NewLine}" +
                $"\"windowsX64Sha256\": \"{sha256}\",{Environment.NewLine}" +
                $"\"size\": {size}";
        }
        catch (Exception ex)
        {
            HashToolSha256TextBox.Text = "";
            HashToolSizeTextBox.Text = "";
            HashToolJsonTextBox.Text = "";
            await MessageDialog.ShowAsync(this, "文件校验工具", ex.Message);
        }
    }

    private void TitleBar_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void Minimize_Click(object? sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Close_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SetActivePage(string page)
    {
        LaunchPage.IsVisible = page == "launch";
        DownloadPage.IsVisible = page == "download";
        SettingsPage.IsVisible = page == "settings";

        SetNavState(LaunchNavButton, page == "launch");
        SetNavState(DownloadNavButton, page == "download");
        SetNavState(SettingsNavButton, page == "settings");
    }

    private static void SetNavState(Button button, bool active)
    {
        button.Background = active
            ? new SolidColorBrush(Color.Parse("#555B72"), 0.55)
            : Brushes.Transparent;
        button.Foreground = active
            ? Brushes.White
            : new SolidColorBrush(Color.Parse("#B8B2D8"));
    }
}
