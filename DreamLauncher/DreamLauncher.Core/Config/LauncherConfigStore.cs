using System.Text.Json;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Config;

namespace DreamLauncher.Core.Config;

public sealed class LauncherConfigStore
{
    private readonly LauncherPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public LauncherConfigStore(LauncherPaths paths)
    {
        _paths = paths;
    }

    public async Task<LauncherConfig> LoadAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureCreated();

            if (!File.Exists(_paths.ConfigPath))
            {
                var created = new LauncherConfig();
                await SaveCoreAsync(created, cancellationToken);
                return created;
            }

            try
            {
                await using var stream = File.OpenRead(_paths.ConfigPath);
                return await JsonSerializer.DeserializeAsync<LauncherConfig>(
                    stream,
                    LauncherJson.Options,
                    cancellationToken) ?? new LauncherConfig();
            }
            catch (JsonException)
            {
                var backupPath = _paths.ConfigPath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_paths.ConfigPath, backupPath, overwrite: false);

                var rebuilt = new LauncherConfig();
                await SaveCoreAsync(rebuilt, cancellationToken);
                return rebuilt;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LauncherConfig config, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            _paths.EnsureCreated();
            await SaveCoreAsync(config, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task SaveCoreAsync(LauncherConfig config, CancellationToken cancellationToken)
    {
        var tempPath = _paths.ConfigPath + ".tmp";
        await using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, config, LauncherJson.Options, cancellationToken);
        }

        File.Copy(tempPath, _paths.ConfigPath, overwrite: true);
        File.Delete(tempPath);
    }
}
