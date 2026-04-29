using Avalonia.Controls;
using Avalonia.Interactivity;
using DreamLauncher.Avalonia.ViewModels;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Avalonia.Dialogs;

public partial class AccountSwitchWindow : Window
{
    private MainWindowViewModel? _viewModel;

    public AccountSwitchWindow()
    {
        InitializeComponent();
    }

    public AccountSwitchWindow(MainWindowViewModel viewModel)
        : this()
    {
        _viewModel = viewModel;
        AccountsListBox.ItemsSource = viewModel.Accounts;
        AccountsListBox.SelectedItem = viewModel.CurrentAccount ?? viewModel.Accounts.FirstOrDefault();
    }

    private async void Delete_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is null || AccountsListBox.SelectedItem is not AccountMetadata account)
        {
            return;
        }

        var confirmed = await MessageDialog.ShowAsync(this, "删除账号", $"确定删除账号 {account.PlayerName} 吗？", showCancel: true);
        if (!confirmed)
        {
            return;
        }

        await _viewModel.RemoveAccountAsync(account);
        AccountsListBox.ItemsSource = null;
        AccountsListBox.ItemsSource = _viewModel.Accounts;
        AccountsListBox.SelectedItem = _viewModel.CurrentAccount ?? _viewModel.Accounts.FirstOrDefault();
    }

    private async void Switch_Click(object? sender, RoutedEventArgs e)
    {
        if (_viewModel is not null && AccountsListBox.SelectedItem is AccountMetadata account)
        {
            await _viewModel.SetDefaultAccountAsync(account);
            Close(true);
        }
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
