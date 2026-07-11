using System.Security.Cryptography;

namespace Genesis.Governance;

public static class Sha256
{
    public static string Hash(ReadOnlySpan<byte> data)
        => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static string Concat(
        ReadOnlySpan<byte> first,
        ReadOnlySpan<byte> second)
    {
        using var incremental = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        incremental.AppendData(first);
        incremental.AppendData(second);
        return Convert.ToHexString(incremental.GetHashAndReset())
            .ToLowerInvariant();
    }
}
