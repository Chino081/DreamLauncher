using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Config;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Avalonia.Security;

public sealed class CrossPlatformTokenStore : ISecureTokenStore
{
    private const string Magic = "DLTK1";
    private const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly string _tokenDirectory;
    private readonly string _keyPath;

    public CrossPlatformTokenStore(LauncherPaths paths)
    {
        _tokenDirectory = Path.Combine(paths.AccountDataRootPath, "tokens");
        _keyPath = Path.Combine(paths.AccountDataRootPath, "token-key.bin");
    }

    public Task SaveAsync(string accountId, SecureAccountTokens tokens, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(_tokenDirectory);
        var key = LoadOrCreateKey();
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plain = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(tokens, LauncherJson.Options));
        var cipher = new byte[plain.Length];
        var tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
        {
            aes.Encrypt(nonce, plain, cipher, tag);
        }

        using var output = File.Create(GetTokenPath(accountId));
        output.Write(Encoding.ASCII.GetBytes(Magic));
        output.Write(nonce);
        output.Write(tag);
        output.Write(cipher);
        return Task.CompletedTask;
    }

    public Task<SecureAccountTokens?> ReadAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetTokenPath(accountId);
        if (!File.Exists(path))
        {
            return Task.FromResult<SecureAccountTokens?>(null);
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length <= Magic.Length + NonceSize + TagSize ||
                Encoding.ASCII.GetString(bytes, 0, Magic.Length) != Magic)
            {
                return Task.FromResult<SecureAccountTokens?>(null);
            }

            var key = LoadOrCreateKey();
            var nonce = bytes.AsSpan(Magic.Length, NonceSize).ToArray();
            var tag = bytes.AsSpan(Magic.Length + NonceSize, TagSize).ToArray();
            var cipher = bytes.AsSpan(Magic.Length + NonceSize + TagSize).ToArray();
            var plain = new byte[cipher.Length];

            using (var aes = new AesGcm(key, TagSize))
            {
                aes.Decrypt(nonce, cipher, tag, plain);
            }

            var json = Encoding.UTF8.GetString(plain);
            return Task.FromResult(JsonSerializer.Deserialize<SecureAccountTokens>(json, LauncherJson.Options));
        }
        catch
        {
            return Task.FromResult<SecureAccountTokens?>(null);
        }
    }

    public Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = GetTokenPath(accountId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private byte[] LoadOrCreateKey()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_keyPath)!);
        if (File.Exists(_keyPath))
        {
            var key = File.ReadAllBytes(_keyPath);
            if (key.Length == KeySize)
            {
                return key;
            }
        }

        var created = RandomNumberGenerator.GetBytes(KeySize);
        File.WriteAllBytes(_keyPath, created);
        return created;
    }

    private string GetTokenPath(string accountId)
    {
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)));
        return Path.Combine(_tokenDirectory, hash + ".bin");
    }
}
