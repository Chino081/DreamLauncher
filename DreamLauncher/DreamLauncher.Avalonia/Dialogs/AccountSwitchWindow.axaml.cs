using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DreamLauncher.Avalonia.ViewModels;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Avalonia.Dialogs;

public partial class AccountSwitchWindow : Window, INotifyPropertyChanged
{
    private MainWindowViewModel? _viewModel;
    private AccountSwitchItem? _selectedAccount;

    public AccountSwitchWindow()
    {
        InitializeComponent();
        DataContext = this;
        UpdateButtonState();
    }

    public AccountSwitchWindow(MainWindowViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        ReloadAccounts();
    }

    public new event PropertyChangedEventHandler? PropertyChanged;

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
            foreach (var account in Accounts)
            {
                account.IsSelected = ReferenceEquals(account, value);
            }

            OnPropertyChanged();
            UpdateButtonState();
        }
    }

    private void ReloadAccounts()
    {
        if (_viewModel is null)
        {
            return;
        }

        var defaultAccountId = _viewModel.CurrentAccount?.Id;
        Accounts.Clear();
        foreach (var account in _viewModel.Accounts)
        {
            Accounts.Add(new AccountSwitchItem(
                account,
                string.Equals(account.Id, defaultAccountId, StringComparison.Ordinal)));
        }

        SelectedAccount = Accounts.FirstOrDefault(item => item.IsDefault) ?? Accounts.FirstOrDefault();
        EmptyTextBlock.IsVisible = Accounts.Count == 0;
        UpdateButtonState();
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || SelectedAccount is null)
        {
            return;
        }

        var confirmed = await MessageDialog.ShowAsync(
            this,
            "删除账号",
            $"确定删除账号 {SelectedAccount.PlayerName} 吗？",
            showCancel: true);
        if (!confirmed)
        {
            return;
        }

        await _viewModel.RemoveAccountAsync(SelectedAccount.Account);
        ReloadAccounts();
    }

    private async void Switch_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && SelectedAccount is not null)
        {
            await _viewModel.SetDefaultAccountAsync(SelectedAccount.Account);
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
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

    private void Dialog_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public sealed class AccountSwitchItem : INotifyPropertyChanged
    {
        private bool _isSelected;

        public AccountSwitchItem(AccountMetadata account, bool isDefault)
        {
            Account = account;
            PlayerName = string.IsNullOrWhiteSpace(account.PlayerName) ? "未知玩家" : account.PlayerName;
            UuidText = string.IsNullOrWhiteSpace(account.Uuid) ? "UUID：未知" : $"UUID：{account.Uuid}";
            DetailText = $"{FormatType(account)} · 上次登录 {account.LastLoginUtc.LocalDateTime:yyyy-MM-dd HH:mm}";
            StatusText = FormatStatus(account.Status);
            AvatarText = PlayerName.Length > 0 ? PlayerName[..1].ToUpperInvariant() : "?";
            IsDefault = isDefault;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        public AccountMetadata Account { get; }

        public string PlayerName { get; }

        public string UuidText { get; }

        public string DetailText { get; }

        public string StatusText { get; }

        public string AvatarText { get; }

        public bool IsDefault { get; }

        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected == value)
                {
                    return;
                }

                _isSelected = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CardBackground));
                OnPropertyChanged(nameof(CardBorderBrush));
            }
        }

        public string CardBackground => IsSelected ? "#3E2558" : "#3A4059";

        public string CardBorderBrush => IsSelected ? "#8A45FF" : "#515970";

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

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }
    }
}
