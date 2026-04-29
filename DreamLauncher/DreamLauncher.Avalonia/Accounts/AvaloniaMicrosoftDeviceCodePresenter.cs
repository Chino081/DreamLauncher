using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using DreamLauncher.Avalonia.Dialogs;
using DreamLauncher.Core.Accounts;

namespace DreamLauncher.Avalonia.Accounts;

public sealed class AvaloniaMicrosoftDeviceCodePresenter : IMicrosoftDeviceCodePresenter
{
    public Task<bool> ShowAsync(
        MicrosoftDeviceCodeInfo deviceCode,
        Task authenticationTask,
        Action cancelAction,
        CancellationToken cancellationToken = default)
    {
        return Dispatcher.UIThread.InvokeAsync(() => ShowCoreAsync(deviceCode, authenticationTask, cancelAction, cancellationToken));
    }

    private static async Task<bool> ShowCoreAsync(
        MicrosoftDeviceCodeInfo deviceCode,
        Task authenticationTask,
        Action cancelAction,
        CancellationToken cancellationToken)
    {
        var owner = (global::Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow
            ?? throw new InvalidOperationException("无法打开登录窗口。");
        var window = new MicrosoftDeviceCodeWindow(deviceCode);
        var closedByAuthentication = false;

        using var registration = cancellationToken.Register(() =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                cancelAction();
                window.Close(false);
            });
        });

        _ = authenticationTask.ContinueWith(task =>
        {
            Dispatcher.UIThread.Post(() =>
            {
                closedByAuthentication = true;
                window.Close(!task.IsCanceled && task.Exception is null);
            });
        }, TaskScheduler.Default);

        var result = await window.ShowDialog<bool?>(owner);

        if (result != true && !closedByAuthentication)
        {
            cancelAction();
        }

        return result == true;
    }
}
