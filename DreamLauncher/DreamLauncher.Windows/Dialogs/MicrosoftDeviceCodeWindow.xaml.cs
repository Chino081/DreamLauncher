using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Windows.Dialogs;

public partial class MicrosoftDeviceCodeWindow : Window
{
    private readonly MicrosoftDeviceCodeInfo _deviceCode;

    public MicrosoftDeviceCodeWindow(MicrosoftDeviceCodeInfo deviceCode)
    {
        _deviceCode = deviceCode;
        InitializeComponent();
        CodeTextBlock.Text = _deviceCode.UserCode;
        Loaded += MicrosoftDeviceCodeWindow_Loaded;
    }

    private void MicrosoftDeviceCodeWindow_Loaded(object sender, RoutedEventArgs e)
    {
        CopyCode();
        OpenBrowser();
    }

    private void OpenBrowser_Click(object sender, RoutedEventArgs e)
    {
        OpenBrowser();
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        CopyCode();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void CopyCode()
    {
        Clipboard.SetText(_deviceCode.UserCode);
    }

    private void OpenBrowser()
    {
        Process.Start(new ProcessStartInfo(_deviceCode.VerificationUri)
        {
            UseShellExecute = true
        });
    }

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
