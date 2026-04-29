using System.Security.Cryptography;

namespace DreamLauncher.Core.Security;

public static class Sha256Hasher
{
    public static async Task<string> ComputeFileAsync(string filePath, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public static async Task VerifyFileAsync(
        string filePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256))
        {
            throw new InvalidOperationException("缺少 SHA256 校验值。");
        }

        var actual = await ComputeFileAsync(filePath, cancellationToken);
        if (!string.Equals(actual, expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(filePath);
            throw new InvalidDataException("文件 SHA256 校验失败，下载文件已删除。");
        }
    }
}
