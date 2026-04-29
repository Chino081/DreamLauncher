using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Windows;

public partial class AccountSwitchWindow : Window, INotifyPropertyChanged
{
    private readonly AccountManager _accountManager;
    private AccountSwitchItem? _selectedAccount;

    public AccountSwitchWindow(AccountManager accountManager)
    {
        _accountManager = accountManager;
        InitializeComponent();
        DataContext = this;
        Loaded += AccountSwitchWindow_Loaded;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AccountSwitchItem> Accounts { get; } = [];

    public AccountSwitchItem? SelectedAccount
    {
        get => _selectedAccount;
        set
        {
            if (_selectedAccount == value)
            {
                return;
            }

            _selectedAccount = value;
            OnPropertyChanged();
            UpdateButtonState();
        }
    }

    public bool AccountChanged { get; private set; }

    private async void AccountSwitchWindow_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= AccountSwitchWindow_Loaded;
        await ReloadAccountsAsync();
    }

    private async Task ReloadAccountsAsync()
    {
        var accounts = await _accountManager.GetAccountsAsync();
        var defaultAccount = await _accountManager.GetDefaultAccountAsync();
        var defaultAccountId = defaultAccount?.Id;

        Accounts.Clear();
        foreach (var account in accounts)
        {
            Accounts.Add(new AccountSwitchItem(account, string.Equals(account.Id, defaultAccountId, StringComparison.Ordinal)));
        }

        SelectedAccount = Accounts.FirstOrDefault(item => item.IsDefault) ?? Accounts.FirstOrDefault();
        EmptyTextBlock.Visibility = Accounts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateButtonState();
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        try
        {
            await _accountManager.SetDefaultAccountAsync(SelectedAccount.Id);
            AccountChanged = true;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "切换失败", LauncherMessageKind.Warning);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is null)
        {
            return;
        }

        var confirm = LauncherMessageBox.Show(
            this,
            $"删除账号 {SelectedAccount.PlayerName}？",
            "删除账号",
            LauncherMessageKind.Warning,
            showCancel: true);

        if (!confirm)
        {
            return;
        }

        try
        {
            await _accountManager.RemoveAccountAsync(SelectedAccount.Id);
            AccountChanged = true;
            await ReloadAccountsAsync();
        }
        catch (Exception ex)
        {
            LauncherMessageBox.Show(this, ex.Message, "删除失败", LauncherMessageKind.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = AccountChanged;
    }

    private void UpdateButtonState()
    {
        var hasSelection = SelectedAccount is not null;
        if (SwitchButton is not null)
        {
            SwitchButton.IsEnabled = hasSelection;
        }

        if (DeleteButton is not null)
        {
            DeleteButton.IsEnabled = hasSelection;
        }
    }

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class AccountSwitchItem
    {
        public AccountSwitchItem(AccountMetadata account, bool isDefault)
        {
            Id = account.Id;
            PlayerName = string.IsNullOrWhiteSpace(account.PlayerName) ? "未知玩家" : account.PlayerName;
            UuidText = string.IsNullOrWhiteSpace(account.Uuid) ? "UUID：未知" : $"UUID：{account.Uuid}";
            IsDefault = isDefault;
            StatusText = FormatStatus(account.Status);
            DetailText = $"{FormatType(account)} · 上次登录 {account.LastLoginUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
            AvatarText = PlayerName.Length > 0 ? PlayerName[..1].ToUpperInvariant() : "?";
        }

        public string Id { get; }

        public string PlayerName { get; }

        public string UuidText { get; }

        public string DetailText { get; }

        public string StatusText { get; }

        public string AvatarText { get; }

        public bool IsDefault { get; }

        public Visibility DefaultBadgeVisibility => IsDefault ? Visibility.Visible : Visibility.Collapsed;

        private static string FormatStatus(AccountLoginStatus status)
        {
            return status switch
            {
                AccountLoginStatus.Available => "可用",
                AccountLoginStatus.Expired => "已过期",
                AccountLoginStatus.RefreshRequired => "需重登",
                AccountLoginStatus.Invalid => "无效",
                _ => "未知"
            };
        }

        private static string FormatType(AccountMetadata account)
        {
            return account.Type switch
            {
                AccountType.Microsoft => "正版账号",
                AccountType.ThirdParty => string.IsNullOrWhiteSpace(account.AuthServerName)
                    ? "第三方账号"
                    : account.AuthServerName,
                AccountType.Offline => "离线账号",
                _ => "未知账号"
            };
        }
    }
}
