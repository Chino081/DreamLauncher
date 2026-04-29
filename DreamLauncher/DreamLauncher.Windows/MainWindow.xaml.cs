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
using DreamLauncher.Windows.Accounts;
using DreamLauncher.Windows.Security;
using DreamLauncher.Windows.ViewModels;

namespace DreamLauncher.Windows;

public partial class MainWindow : Window
{
    private const string DefaultMicrosoftClientId = "00000000402b5328";
    private readonly LauncherPaths _paths;
    private readonly LauncherConfigStore _configStore;
    private readonly AccountManager _accountManager;
    private readonly MainWindowViewModel _viewModel;

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
        var browserLauncher = new WindowsBrowserLauncher();
        var deviceCodePresenter = new WindowsMicrosoftDeviceCodePresenter();
        var authService = new MicrosoftAuthService(browserLauncher, deviceCodePresenter);
        var thirdPartyAuthService = new ThirdPartyAuthService();
        _accountManager = new AccountManager(_configStore, tokenStore, authService, thirdPartyAuthService);
        var clientManager = new ClientManager(_paths, downloadService, extractor);
        var javaRuntimeManager = new JavaRuntimeManager(_paths, downloadService, extractor);
        var minecraftLaunchService = new MinecraftLaunchService(_paths);

        _viewModel = new MainWindowViewModel(
            _configStore,
            remoteConfigClient,
            clientManager,
            javaRuntimeManager,
            _accountManager,
            tokenStore,
            minecraftLaunchService);

        _viewModel.MessageRequested += message =>
            MessageBox.Show(this, message, "DreamLauncher", MessageBoxButton.OK, MessageBoxImage.Information);

        DataContext = _viewModel;
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
            config.ClientsManifestUrl = EmptyToNull(MainClientsManifestUrlTextBox.Text);
            config.JavaRuntimesManifestUrl = EmptyToNull(MainJavaRuntimesUrlTextBox.Text);
            config.AnnouncementUrl = EmptyToNull(MainAnnouncementUrlTextBox.Text);
            config.MicrosoftClientId = EmptyToNull(MainMicrosoftClientIdTextBox.Text);
            config.AuthlibInjectorJarPath = EmptyToNull(MainAuthlibInjectorJarPathTextBox.Text);

            if (int.TryParse(MainMaxRetryCountTextBox.Text, out var retryCount))
            {
                config.Download.MaxRetryCount = Math.Clamp(retryCount, 1, 10);
            }

            config.Download.SpeedLimitKbPerSecond = int.TryParse(MainSpeedLimitTextBox.Text, out var speedLimit)
                ? Math.Max(1, speedLimit)
                : null;

            await _configStore.SaveAsync(config);
            await _viewModel.InitializeAsync();
            MessageBox.Show(this, "设置已保存。", "DreamLauncher", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "保存失败", MessageBoxButton.OK, MessageBoxImage.Warning);
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

    private async Task LoadInlineSettingsAsync()
    {
        var config = await _configStore.LoadAsync();
        MainClientsManifestUrlTextBox.Text = config.ClientsManifestUrl ?? "";
        MainJavaRuntimesUrlTextBox.Text = config.JavaRuntimesManifestUrl ?? "";
        MainAnnouncementUrlTextBox.Text = config.AnnouncementUrl ?? "";
        MainMicrosoftClientIdTextBox.Text = config.MicrosoftClientId ?? DefaultMicrosoftClientId;
        MainAuthlibInjectorJarPathTextBox.Text = config.AuthlibInjectorJarPath ?? "";
        MainMaxRetryCountTextBox.Text = config.Download.MaxRetryCount.ToString();
        MainSpeedLimitTextBox.Text = config.Download.SpeedLimitKbPerSecond?.ToString() ?? "";
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

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
