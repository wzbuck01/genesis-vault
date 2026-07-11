using System.Diagnostics;

namespace Genesis.Governance;

/// <summary>
/// Phase 3 composition boundary. Geometry remains outside this type.
///
/// The delegates bind directly to the already-verified implementations:
///   PoleId14.ClassType()
///   RelationAlgebra.BuildBgClass()
///   existing gate derivation
///   WholePlaneGating.Apply(...)
/// </summary>
public sealed class GovernanceAdapter<TPole, TTensor>
    where TTensor : IReadOnlyList<float>
{
    private readonly Func<TPole, int?> _classType;
    private readonly Func<float[,]> _buildBgClass;
    private readonly Func<ReadOnlyMemory<float>, float[]> _derivePlaneGates;
    private readonly Func<TTensor, ReadOnlyMemory<float>, TTensor> _applyGates;
    private readonly int _defaultClassType;

    public GovernanceAdapter(
        Func<TPole, int?> classType,
        Func<float[,]> buildBgClass,
        Func<ReadOnlyMemory<float>, float[]> derivePlaneGates,
        Func<TTensor, ReadOnlyMemory<float>, TTensor> applyGates,
        int defaultClassType = 0)
    {
        _classType = classType ?? throw new ArgumentNullException(nameof(classType));
        _buildBgClass = buildBgClass ?? throw new ArgumentNullException(nameof(buildBgClass));
        _derivePlaneGates = derivePlaneGates ?? throw new ArgumentNullException(nameof(derivePlaneGates));
        _applyGates = applyGates ?? throw new ArgumentNullException(nameof(applyGates));

        if (defaultClassType < 0)
            throw new ArgumentOutOfRangeException(nameof(defaultClassType));

        _defaultClassType = defaultClassType;
    }

    public GovernanceAdapterResult<TTensor> Apply(
        TTensor q,
        TTensor k,
        TPole poleId)
    {
        ArgumentNullException.ThrowIfNull(q);
        ArgumentNullException.ThrowIfNull(k);
        ArgumentNullException.ThrowIfNull(poleId);

        var bgClass = _buildBgClass();
        var classType = _classType(poleId) ?? _defaultClassType;

        if ((uint)classType >= (uint)bgClass.GetLength(0))
            throw new ArgumentOutOfRangeException(
                nameof(poleId),
                classType,
                $"Class type must be in [0,{bgClass.GetLength(0) - 1}].");

        var policyRow = ExtractRow(bgClass, classType);
        var gates = _derivePlaneGates(policyRow);

        var qBytes = TensorCanonicalizer.Serialize(q);
        var kBytes = TensorCanonicalizer.Serialize(k);

        var inputIdentity = Sha256.Concat(qBytes, kBytes);
        var preWitness = Sha256.Hash(qBytes);
        var policyIdentity =
            GovernancePolicyCanonicalizer.Hash(policyRow.Span, gates);

        var started = Stopwatch.GetTimestamp();

        var projectedQ = _applyGates(q, gates);
        var projectedK = _applyGates(k, gates);

        var latencyNs = StopwatchTicks.ToNanoseconds(
            Stopwatch.GetTimestamp() - started);

        var projectedQBytes = TensorCanonicalizer.Serialize(projectedQ);
        var projectedKBytes = TensorCanonicalizer.Serialize(projectedK);

        var receipt = new GovernanceAdapterReceipt(
            InputIdentity: inputIdentity,
            PolicyIdentity: policyIdentity,
            PreWitness: preWitness,
            PostWitness: Sha256.Hash(projectedQBytes),
            LatencyNs: latencyNs,
            OutputIdentity: Sha256.Concat(projectedQBytes, projectedKBytes),
            ClassType: classType);

        return new GovernanceAdapterResult<TTensor>(
            projectedQ,
            projectedK,
            receipt);
    }

    private static ReadOnlyMemory<float> ExtractRow(float[,] matrix, int row)
    {
        var columns = matrix.GetLength(1);
        var values = GC.AllocateUninitializedArray<float>(columns);

        for (var column = 0; column < columns; column++)
            values[column] = matrix[row, column];

        return values;
    }
}
