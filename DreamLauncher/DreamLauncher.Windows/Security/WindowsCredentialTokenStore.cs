using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DreamLauncher.Core.Accounts;
using DreamLauncher.Core.Serialization;
using DreamLauncher.Models.Accounts;

namespace DreamLauncher.Windows.Security;

public sealed class WindowsCredentialTokenStore : ISecureTokenStore
{
    private const int CredentialTypeGeneric = 1;
    private const int CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const string TargetPrefix = "DreamLauncher:Microsoft:";
    private static readonly byte[] FileEntropy = Encoding.UTF8.GetBytes("DreamLauncher.AccountTokens.v1");
    private readonly string _fallbackDirectory;

    public WindowsCredentialTokenStore()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            ".DreamtcLauncher",
            "tokens"))
    {
    }

    public WindowsCredentialTokenStore(string fallbackDirectory)
    {
        _fallbackDirectory = fallbackDirectory;
    }

    public Task SaveAsync(
        string accountId,
        SecureAccountTokens tokens,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (TrySaveToCredentialManager(accountId, tokens))
        {
            DeleteFallbackFile(accountId);
            return Task.CompletedTask;
        }

        SaveFallbackFile(accountId, tokens);
        return Task.CompletedTask;
    }

    public Task<SecureAccountTokens?> ReadAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var credentialTokens = ReadFromCredentialManager(accountId);
        if (credentialTokens is not null)
        {
            return Task.FromResult<SecureAccountTokens?>(credentialTokens);
        }

        return Task.FromResult(ReadFallbackFile(accountId));
    }

    public Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = CredDelete(GetTargetName(accountId), CredentialTypeGeneric, 0);
        DeleteFallbackFile(accountId);
        return Task.CompletedTask;
    }

    private static bool TrySaveToCredentialManager(string accountId, SecureAccountTokens tokens)
    {
        var json = JsonSerializer.Serialize(tokens, LauncherJson.Options);
        var bytes = Encoding.Unicode.GetBytes(json);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = GetTargetName(accountId),
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = accountId
            };

            if (!CredWrite(ref credential, 0))
            {
                return false;
            }

            return true;
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }
    }

    private static SecureAccountTokens? ReadFromCredentialManager(string accountId)
    {
        if (!CredRead(GetTargetName(accountId), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return null;
            }

            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var json = Encoding.Unicode.GetString(bytes);
            return JsonSerializer.Deserialize<SecureAccountTokens>(json, LauncherJson.Options);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    private void SaveFallbackFile(string accountId, SecureAccountTokens tokens)
    {
        Directory.CreateDirectory(_fallbackDirectory);

        var json = JsonSerializer.Serialize(tokens, LauncherJson.Options);
        var plainBytes = Encoding.UTF8.GetBytes(json);
        var encryptedBytes = ProtectedData.Protect(plainBytes, FileEntropy, DataProtectionScope.CurrentUser);
        File.WriteAllBytes(GetFallbackPath(accountId), encryptedBytes);
    }

    private SecureAccountTokens? ReadFallbackFile(string accountId)
    {
        var path = GetFallbackPath(accountId);
        if (!File.Exists(path))
        {
            return null;
        }

        var encryptedBytes = File.ReadAllBytes(path);
        var plainBytes = ProtectedData.Unprotect(encryptedBytes, FileEntropy, DataProtectionScope.CurrentUser);
        var json = Encoding.UTF8.GetString(plainBytes);
        return JsonSerializer.Deserialize<SecureAccountTokens>(json, LauncherJson.Options);
    }

    private void DeleteFallbackFile(string accountId)
    {
        var path = GetFallbackPath(accountId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private string GetFallbackPath(string accountId)
    {
        return Path.Combine(_fallbackDirectory, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId))) + ".bin");
    }

    private static string GetTargetName(string accountId)
    {
        return TargetPrefix + accountId;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref NativeCredential userCredential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPointer);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, int type, int flags);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr credentialPointer);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }
}
