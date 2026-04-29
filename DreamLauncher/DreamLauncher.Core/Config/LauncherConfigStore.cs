using System.Text.Json;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Config;

namespace DreamLauncher.Core.Config;

public sealed class LauncherConfigStore
{
    public const string FixedClientsManifestUrl =
        "https://raw.giteeusercontent.com/Chino7/DreamLauncher/raw/master/local-cdn/clients.json";

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
                ApplyFixedSources(created);
                await SaveCoreAsync(created, cancellationToken);
                return created;
            }

            try
            {
                LauncherConfig config;
                await using (var stream = File.OpenRead(_paths.ConfigPath))
                {
                    config = await JsonSerializer.DeserializeAsync<LauncherConfig>(
                        stream,
                        LauncherJson.Options,
                        cancellationToken) ?? new LauncherConfig();
                }

                if (ApplyFixedSources(config))
                {
                    await SaveCoreAsync(config, cancellationToken);
                }

                return config;
            }
            catch (JsonException)
            {
                var backupPath = _paths.ConfigPath + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
                File.Copy(_paths.ConfigPath, backupPath, overwrite: false);

                var rebuilt = new LauncherConfig();
                ApplyFixedSources(rebuilt);
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
            ApplyFixedSources(config);
            await SaveCoreAsync(config, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static bool ApplyFixedSources(LauncherConfig config)
    {
        var changed = false;
        changed |= SetIfDifferent(config.ClientsManifestUrl, FixedClientsManifestUrl, value => config.ClientsManifestUrl = value);
        return changed;
    }

    private static bool SetIfDifferent(string? current, string value, Action<string> setValue)
    {
        if (string.Equals(current, value, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        setValue(value);
        return true;
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
