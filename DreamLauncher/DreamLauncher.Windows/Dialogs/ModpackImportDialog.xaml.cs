using System.Windows;
using System.Windows.Input;

namespace DreamLauncher.Windows.Dialogs;

public partial class ModpackImportDialog : Window
{
    public ModpackImportDialog(string defaultName = "")
    {
        InitializeComponent();
        InstanceNameTextBox.Text = defaultName;
        Loaded += (_, _) =>
        {
            InstanceNameTextBox.Focus();
            InstanceNameTextBox.SelectAll();
        };
    }

    public string InstanceName => InstanceNameTextBox.Text.Trim();

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(InstanceNameTextBox.Text.Trim()))
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
