using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace DreamLauncher.Avalonia.Dialogs;

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

    public AuthTypeChoice? SelectedAuthType { get; private set; } = AuthTypeChoice.Microsoft;

    private void Microsoft_Click(object? sender, RoutedEventArgs e)
    {
        SelectedAuthType = AuthTypeChoice.Microsoft;
    }

    private void Continue_Click(object? sender, RoutedEventArgs e)
    {
        Close(SelectedAuthType is not null);
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
}
