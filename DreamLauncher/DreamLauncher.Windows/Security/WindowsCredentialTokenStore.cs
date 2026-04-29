using System.ComponentModel;
using System.Runtime.InteropServices;
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

    public Task SaveAsync(
        string accountId,
        SecureAccountTokens tokens,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

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
                throw new Win32Exception(Marshal.GetLastWin32Error(), "无法写入 Windows Credential Manager。");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    public Task<SecureAccountTokens?> ReadAsync(
        string accountId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredRead(GetTargetName(accountId), CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound)
            {
                return Task.FromResult<SecureAccountTokens?>(null);
            }

            throw new Win32Exception(error, "无法读取 Windows Credential Manager。");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return Task.FromResult<SecureAccountTokens?>(null);
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            var json = Encoding.Unicode.GetString(bytes);
            var tokens = JsonSerializer.Deserialize<SecureAccountTokens>(json, LauncherJson.Options);
            return Task.FromResult(tokens);
        }
        finally
        {
            CredFree(credentialPointer);
        }
    }

    public Task DeleteAsync(string accountId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!CredDelete(GetTargetName(accountId), CredentialTypeGeneric, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != ErrorNotFound)
            {
                throw new Win32Exception(error, "无法删除 Windows Credential Manager 凭据。");
            }
        }

        return Task.CompletedTask;
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
