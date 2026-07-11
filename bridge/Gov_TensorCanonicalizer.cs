using System.Buffers.Binary;

namespace Genesis.Governance;

/// <summary>
/// Canonical Phase 3 tensor encoding:
///   uint32 element count, little-endian
///   followed by IEEE-754 binary32 values, little-endian.
///
/// NaN values are normalized to one quiet-NaN bit pattern and negative zero is
/// preserved. This makes hashes deterministic across supported .NET targets.
/// </summary>
public static class TensorCanonicalizer
{
    private const int CanonicalNaNBits = unchecked((int)0x7FC00000);

    public static byte[] Serialize<TTensor>(TTensor tensor)
        where TTensor : IReadOnlyList<float>
    {
        ArgumentNullException.ThrowIfNull(tensor);

        checked
        {
            var output = GC.AllocateUninitializedArray<byte>(
                sizeof(uint) + tensor.Count * sizeof(float));

            BinaryPrimitives.WriteUInt32LittleEndian(
                output.AsSpan(0, sizeof(uint)),
                (uint)tensor.Count);

            var destination = output.AsSpan(sizeof(uint));

            for (var i = 0; i < tensor.Count; i++)
            {
                var value = tensor[i];
                var bits = float.IsNaN(value)
                    ? CanonicalNaNBits
                    : BitConverter.SingleToInt32Bits(value);

                BinaryPrimitives.WriteInt32LittleEndian(
                    destination.Slice(i * sizeof(float), sizeof(float)),
                    bits);
            }

            return output;
        }
    }
}
