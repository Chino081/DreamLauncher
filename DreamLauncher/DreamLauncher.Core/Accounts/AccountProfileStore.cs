using System.Text.Json;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Serialization;

namespace DreamLauncher.Core.Accounts;

public sealed class AccountProfileStore
{
    private readonly LauncherPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AccountProfileStore(LauncherPaths paths)
    {
        _paths = paths;
    }

    public async Task<AccountProfileDocument> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.AccountDataRootPath);
            if (!File.Exists(_paths.AccountDataPath))
            {
                return new AccountProfileDocument();
            }

            try
            {
                await using var stream = File.OpenRead(_paths.AccountDataPath);
                return await LauncherJson.DeserializeAsync<AccountProfileDocument>(stream, cancellationToken)
                    ?? new AccountProfileDocument();
            }
            catch (JsonException)
            {
                var backupPath = _paths.AccountDataPath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_paths.AccountDataPath, backupPath, overwrite: false);
                return new AccountProfileDocument();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(AccountProfileDocument document, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(_paths.AccountDataRootPath);

            var tempPath = _paths.AccountDataPath + ".tmp";
            await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                await LauncherJson.SerializeAsync(stream, document, cancellationToken);
            }

            File.Copy(tempPath, _paths.AccountDataPath, overwrite: true);
            File.Delete(tempPath);
        }
        finally
        {
            _gate.Release();
        }
    }
}
