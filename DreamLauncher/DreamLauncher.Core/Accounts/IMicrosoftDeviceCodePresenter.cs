namespace DreamLauncher.Core.Accounts;

public interface IMicrosoftDeviceCodePresenter
{
    Task<bool> ShowAsync(
        MicrosoftDeviceCodeInfo deviceCode,
        Task authenticationTask,
        Action cancelAction,
        CancellationToken cancellationToken = default);
}
