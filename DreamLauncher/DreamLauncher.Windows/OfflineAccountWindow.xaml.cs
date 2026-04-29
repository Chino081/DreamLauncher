using System.Windows;
using System.Windows.Input;

namespace DreamLauncher.Windows;

public partial class OfflineAccountWindow : Window
{
    public OfflineAccountWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            PlayerNameTextBox.Focus();
            PlayerNameTextBox.SelectAll();
        };
    }

    public string PlayerName => PlayerNameTextBox.Text.Trim();

    private void Create_Click(object sender, RoutedEventArgs e)
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
