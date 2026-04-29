using System.Windows;
using System.Windows.Input;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Config;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Windows;

public partial class SettingsWindow : Window
{
    private readonly LauncherConfigStore _configStore;
    private readonly AccountManager _accountManager;
    private readonly LauncherPaths _paths;

    public SettingsWindow(
        LauncherConfigStore configStore,
        AccountManager accountManager,
        LauncherPaths paths)
    {
        _configStore = configStore;
        _accountManager = accountManager;
        _paths = paths;
        InitializeComponent();
        Loaded += SettingsWindow_Loaded;
    }

    private async void SettingsWindow_Loaded(object sender, RoutedEventArgs e)
    {
        await LoadConfigAsync();
    }

    private async Task LoadConfigAsync()
    {
        var config = await _configStore.LoadAsync();
        ClientsManifestUrlTextBox.Text = config.ClientsManifestUrl ?? "";
        JavaRuntimesUrlTextBox.Text = config.JavaRuntimesManifestUrl ?? "";
        AnnouncementUrlTextBox.Text = config.AnnouncementUrl ?? "";
        MicrosoftClientIdTextBox.Text = config.MicrosoftClientId ?? "";
        AuthlibInjectorJarPathTextBox.Text = config.AuthlibInjectorJarPath ?? "";
        MaxRetryCountTextBox.Text = config.Download.MaxRetryCount.ToString();
        SpeedLimitTextBox.Text = config.Download.SpeedLimitKbPerSecond?.ToString() ?? "";
        RootPathTextBlock.Text = _paths.RootPath;
        AccountsListBox.ItemsSource = config.Accounts;
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var config = await _configStore.LoadAsync();
            config.ClientsManifestUrl = EmptyToNull(ClientsManifestUrlTextBox.Text);
            config.JavaRuntimesManifestUrl = EmptyToNull(JavaRuntimesUrlTextBox.Text);
            config.AnnouncementUrl = EmptyToNull(AnnouncementUrlTextBox.Text);
            config.MicrosoftClientId = EmptyToNull(MicrosoftClientIdTextBox.Text);
            config.AuthlibInjectorJarPath = EmptyToNull(AuthlibInjectorJarPathTextBox.Text);

            if (int.TryParse(MaxRetryCountTextBox.Text, out var retryCount))
            {
                config.Download.MaxRetryCount = Math.Clamp(retryCount, 1, 10);
            }

            config.Download.SpeedLimitKbPerSecond = int.TryParse(SpeedLimitTextBox.Text, out var speedLimit)
                ? Math.Max(1, speedLimit)
                : null;

            await _configStore.SaveAsync(config);
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "保存失败", LauncherMessageKind.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private async void SetDefaultAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountMetadata account)
        {
            return;
        }

        try
        {
            await _accountManager.SetDefaultAccountAsync(account.Id);
            await LoadConfigAsync();
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "账号设置失败", LauncherMessageKind.Warning);
        }
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (AccountsListBox.SelectedItem is not AccountMetadata account)
        {
            return;
        }

        var confirm = LauncherMessageBox.Show(
            this,
            $"删除账号 {account.PlayerName}？",
            "删除账号",
            LauncherMessageKind.Warning,
            showCancel: true);

        if (!confirm)
        {
            return;
        }

        try
        {
            await _accountManager.RemoveAccountAsync(account.Id);
            await LoadConfigAsync();
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "删除失败", LauncherMessageKind.Warning);
        }
    }

    private static string? EmptyToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
