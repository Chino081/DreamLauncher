using System.Windows;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Windows.Accounts;

public sealed class WindowsMicrosoftDeviceCodePresenter : IMicrosoftDeviceCodePresenter
{
    public Task<bool> ShowAsync(
        MicrosoftDeviceCodeInfo deviceCode,
        Task authenticationTask,
        Action cancelAction,
        CancellationToken cancellationToken = default)
    {
        var dispatcher = Application.Current.Dispatcher;
        if (!dispatcher.CheckAccess())
        {
            return dispatcher.InvokeAsync(() => ShowCore(deviceCode, authenticationTask, cancelAction, cancellationToken)).Task;
        }

        return Task.FromResult(ShowCore(deviceCode, authenticationTask, cancelAction, cancellationToken));
    }

    private static bool ShowCore(
        MicrosoftDeviceCodeInfo deviceCode,
        Task authenticationTask,
        Action cancelAction,
        CancellationToken cancellationToken)
    {
        var window = new MicrosoftDeviceCodeWindow(deviceCode)
        {
            Owner = Application.Current.MainWindow
        };

        var closedByAuthentication = false;
        using var registration = cancellationToken.Register(() =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                cancelAction();
                CloseWindow(window, false);
            });
        });

        _ = authenticationTask.ContinueWith(task =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                closedByAuthentication = true;
                CloseWindow(window, !task.IsCanceled && task.Exception is null);
            });
        }, TaskScheduler.Default);

        var result = window.ShowDialog() == true;
        if (!result && !closedByAuthentication)
        {
            cancelAction();
        }

        return result;
    }

    private static void CloseWindow(Window window, bool result)
    {
        if (!window.IsVisible)
        {
            return;
        }

        window.DialogResult = result;
        window.Close();
    }
}
