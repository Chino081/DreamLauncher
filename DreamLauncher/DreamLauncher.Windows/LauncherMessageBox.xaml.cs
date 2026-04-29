using System.Windows;
using System.Windows.Input;

namespace DreamLauncher.Windows;

public enum LauncherMessageKind
{
    Info,
    Warning,
    Error,
    Success
}

public partial class LauncherMessageBox : Window
{
    private LauncherMessageBox(
        string title,
        string message,
        LauncherMessageKind kind,
        bool showCancel)
    {
        InitializeComponent();
        TitleTextBlock.Text = title;
        MessageTextBlock.Text = message;
        CancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;

        IconTextBlock.Text = kind switch
        {
            LauncherMessageKind.Success => "✓",
            LauncherMessageKind.Error => "×",
            LauncherMessageKind.Warning => "!",
            _ => "i"
        };

        IconTextBlock.Foreground = kind switch
        {
            LauncherMessageKind.Success => (System.Windows.Media.Brush)FindResource("AccentBlue"),
            LauncherMessageKind.Error => (System.Windows.Media.Brush)FindResource("Danger"),
            LauncherMessageKind.Warning => (System.Windows.Media.Brush)FindResource("Accent"),
            _ => (System.Windows.Media.Brush)FindResource("TextPrimary")
        };
    }

    public static bool Show(
        Window owner,
        string message,
        string title = "DreamLauncher",
        LauncherMessageKind kind = LauncherMessageKind.Info,
        bool showCancel = false)
    {
        var dialog = new LauncherMessageBox(title, message, kind, showCancel)
        {
            Owner = owner
        };

        return dialog.ShowDialog() == true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
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
