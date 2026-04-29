using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DreamLauncher.Avalonia.Accounts;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Avalonia.Dialogs;

public partial class MicrosoftDeviceCodeWindow : Window
{
    private MicrosoftDeviceCodeInfo? _deviceCode;

    public MicrosoftDeviceCodeWindow()
    {
        InitializeComponent();
    }

    public MicrosoftDeviceCodeWindow(MicrosoftDeviceCodeInfo deviceCode)
        : this()
    {
        _deviceCode = deviceCode;
        CodeTextBlock.Text = deviceCode.UserCode;
        Opened += async (_, _) =>
        {
            await CopyCodeAsync();
            AvaloniaBrowserLauncher.Open(deviceCode.VerificationUri);
        };
    }

    private void OpenBrowser_Click(object? sender, RoutedEventArgs e)
    {
        if (_deviceCode is not null)
        {
            AvaloniaBrowserLauncher.Open(_deviceCode.VerificationUri);
        }
    }

    private async void CopyCode_Click(object? sender, RoutedEventArgs e)
    {
        await CopyCodeAsync();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }

    private void Dialog_PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            BeginMoveDrag(e);
        }
    }

    private async Task CopyCodeAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(_deviceCode?.UserCode ?? "");
        }
    }
}
