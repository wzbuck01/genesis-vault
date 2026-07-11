using System.Buffers.Binary;
using System.Security.Cryptography;

namespace Genesis.Governance;

/// <summary>
/// Canonical policy identity over the selected B_G^class row and derived gates.
/// Domain tags prevent accidental equivalence with an untyped float sequence.
/// </summary>
public static class GovernancePolicyCanonicalizer
{
    private static ReadOnlySpan<byte> DomainTag
        => "genesis.phase3.policy.v1"u8;

    public static string Hash(
        ReadOnlySpan<float> policyRow,
        ReadOnlySpan<float> gates)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(DomainTag);

        AppendFloatVector(hash, policyRow);
        AppendFloatVector(hash, gates);

        return Convert.ToHexString(hash.GetHashAndReset())
            .ToLowerInvariant();
    }

    private static void AppendFloatVector(
        IncrementalHash hash,
        ReadOnlySpan<float> values)
    {
        Span<byte> count = stackalloc byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32LittleEndian(count, checked((uint)values.Length));
        hash.AppendData(count);

        Span<byte> encoded = stackalloc byte[sizeof(int)];

        foreach (var value in values)
        {
            var bits = float.IsNaN(value)
                ? unchecked((int)0x7FC00000)
                : BitConverter.SingleToInt32Bits(value);

            BinaryPrimitives.WriteInt32LittleEndian(encoded, bits);
            hash.AppendData(encoded);
        }
    }
}
