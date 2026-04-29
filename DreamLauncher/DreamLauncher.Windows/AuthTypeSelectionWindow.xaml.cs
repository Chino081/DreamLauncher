using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace DreamLauncher.Windows;

public enum AuthTypeChoice
{
    Microsoft,
    ThirdParty,
    Offline
}

public partial class AuthTypeSelectionWindow : Window
{
    public AuthTypeSelectionWindow()
    {
        InitializeComponent();
    }

    public AuthTypeChoice? SelectedAuthType { get; private set; }

    private void AuthRadio_Checked(object sender, RoutedEventArgs e)
    {
        SelectedAuthType = sender switch
        {
            RadioButton radio when radio == MicrosoftRadio => AuthTypeChoice.Microsoft,
            RadioButton radio when radio == ThirdPartyRadio && radio.IsEnabled => AuthTypeChoice.ThirdParty,
            RadioButton radio when radio == OfflineRadio && radio.IsEnabled => AuthTypeChoice.Offline,
            _ => null
        };

        ContinueButton.IsEnabled = SelectedAuthType is not null;
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedAuthType is null)
        {
            return;
        }

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Dialog_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }
}
