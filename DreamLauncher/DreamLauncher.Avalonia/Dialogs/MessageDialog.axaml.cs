using Avalonia.Controls;
using Avalonia.Interactivity;

namespace DreamLauncher.Avalonia.Dialogs;

public partial class MessageDialog : Window
{
    public MessageDialog()
    {
        InitializeComponent();
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, bool showCancel = false)
    {
        var dialog = new MessageDialog();
        dialog.TitleTextBlock.Text = title;
        dialog.MessageTextBlock.Text = message;
        dialog.CancelButton.IsVisible = showCancel;
        return dialog.ShowDialog<bool>(owner);
    }

    private void Ok_Click(object? sender, RoutedEventArgs e)
    {
        Close(true);
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Close(false);
    }
}
