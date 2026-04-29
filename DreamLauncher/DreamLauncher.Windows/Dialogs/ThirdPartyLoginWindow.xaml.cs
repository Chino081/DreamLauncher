using System.Windows;
using System.Windows.Input;

namespace DreamLauncher.Windows.Dialogs;

public partial class ThirdPartyLoginWindow : Window
{
    public ThirdPartyLoginWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => UsernameTextBox.Focus();
    }

    public string ApiRoot => ApiRootTextBox.Text.Trim();

    public string Username => UsernameTextBox.Text.Trim();

    public string Password => PasswordBox.Password;

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ApiRoot) ||
            string.IsNullOrWhiteSpace(Username) ||
            string.IsNullOrWhiteSpace(Password))
        {
            LauncherMessageBox.Show(this, "请填写皮肤站地址、账号和密码。", "皮肤站登录", LauncherMessageKind.Info);
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
