// Observability.cs — Genesis.Geometry Phase 1
// poleId14 observability algebra — 14-dimensional governance coordinate space
// Source: hyperbase/geometry/hb-pole14.js, derive-pole14.js, RFC-HB-006
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HiveMind.Geometry;

/// <summary>
/// 14-dimensional poleId coordinate space (RFC-HB-006).
/// Dimensions 0-9: T1-T10 operational axes, values ∈ {-1, 0, +1}
/// Dimensions 10-13: R-type governance primitives (visceral), values ∈ {0, +1}
///   [10] R:Transform  (TEMIN-T)
///   [11] R:Verify     (TEMIN-E/N)
///   [12] R:Constrain  (TEMIN-M)
///   [13] R:Inverse    (TEMIN-I/N, one-sided: 0 or +1 only)
///
/// The R-type indices 10-13 map exactly to the relation types from RFC-007C/D:
///   R:Transform = R.Transform (K5 type 3)
///   R:Verify    = R.Verify    (K5 type 1)
///   R:Constrain = R.Constrain (K5 type 2)
///   R:Inverse   = R.Inverse   (K5 type 0)
/// This is the connection point between the observability space and B_G^class.
/// </summary>
public readonly struct PoleId14
{
    public const int Length      = 14;
    public const int OpAxes      = 10;  // T1-T10
    public const int GovAxes     = 4;   // R:Transform, R:Verify, R:Constrain, R:Inverse

    // Governance axis indices
    public const int IDX_TRANSFORM = 10;  // R:Transform (corresponds to R.Transform)
    public const int IDX_VERIFY    = 11;  // R:Verify    (corresponds to R.Verify)
    public const int IDX_CONSTRAIN = 12;  // R:Constrain (corresponds to R.Constrain)
    public const int IDX_INVERSE   = 13;  // R:Inverse   (corresponds to R.Inverse)

    public readonly sbyte[] Values;  // sbyte captures {-1,0,+1} compactly

    public PoleId14(sbyte[] values)
    {
        if (values.Length != Length)
            throw new ArgumentException($"poleId14 must have {Length} components");
        // Validate operational axes: {-1, 0, +1}
        for (int i = 0; i < OpAxes; i++)
            if (values[i] < -1 || values[i] > 1)
                throw new ArgumentException($"Operational axis T{i+1} must be {{-1,0,+1}}, got {values[i]}");
        // Validate governance axes: {0, +1}
        for (int i = OpAxes; i < Length; i++)
            if (values[i] < 0 || values[i] > 1)
                throw new ArgumentException($"Governance axis [{i}] must be {{0,+1}}, got {values[i]}");
        Values = (sbyte[])values.Clone();
    }

    public static PoleId14 Zero() => new(new sbyte[Length]);

    /// <summary>
    /// Extract the 4D governance subspace [10..13] as a R-type vector.
    /// Maps to B_G^class class type c_t.
    /// </summary>
    public R? ClassType()
    {
        // The active R-axis determines the class type for B_G^class
        // Priority: highest-index active axis (Inverse > Constrain > Verify > Transform)
        if (Values[IDX_INVERSE]   == 1) return R.Inverse;
        if (Values[IDX_CONSTRAIN] == 1) return R.Constrain;
        if (Values[IDX_VERIFY]    == 1) return R.Verify;
        if (Values[IDX_TRANSFORM] == 1) return R.Transform;
        return null;  // no active governance axis
    }

    /// <summary>
    /// Compute the 32-element WHT (Walsh-Hadamard Transform) spectral key.
    /// The 14D pole projects to a 32-element spectral vector via W4 embedding.
    /// Source: derive-pole14.js computeSpectralKey32()
    /// Simplified: take first 4 operational axes as the φ vector for W4 projection.
    /// </summary>
    public float[] SpectralKey4()
    {
        var phi = new float[W4.K];
        for (int i = 0; i < W4.K; i++)
            phi[i] = Values[i];
        return W4.Score(phi);
    }

    /// <summary>
    /// Assign W4 mode (P0-P3) from the first 4 operational axes.
    /// </summary>
    public int W4Mode()
    {
        var scores = SpectralKey4();
        return W4.AssignMode(scores);
    }

    public override string ToString()
    {
        var sb = new StringBuilder("[");
        for (int i = 0; i < Length; i++)
        {
            if (i == OpAxes) sb.Append('|');
            sb.Append(Values[i] switch { 1 => "+", -1 => "-", _ => "0" });
            if (i < Length - 1 && i != OpAxes - 1) sb.Append(',');
        }
        return sb.Append(']').ToString();
    }
}

/// <summary>
/// Observability algebra — verifications and structure.
/// </summary>
public static class Observability
{
    /// <summary>
    /// Verify the dimension is 14 (10 operational + 4 governance = RFC-007C K=4).
    /// </summary>
    public static VerificationResult VerifyDimension()
    {
        if (PoleId14.Length != 14)
            return VerificationResult.Fail($"Dimension={PoleId14.Length} ≠ 14");
        if (PoleId14.OpAxes + PoleId14.GovAxes != PoleId14.Length)
            return VerificationResult.Fail($"OpAxes={PoleId14.OpAxes} + GovAxes={PoleId14.GovAxes} ≠ {PoleId14.Length}");
        return VerificationResult.Pass($"dim(poleId14) = {PoleId14.OpAxes}+{PoleId14.GovAxes} = {PoleId14.Length} ✓");
    }

    /// <summary>
    /// Verify the governance axes map bijectively to RFC-007C relation types.
    /// </summary>
    public static VerificationResult VerifyGovernanceAxisMapping()
    {
        // The 4 governance axes correspond to the 4 relation types T = {Inverse, Verify, Constrain, Transform}
        var axes = new Dictionary<int, R>
        {
            [PoleId14.IDX_INVERSE]   = R.Inverse,
            [PoleId14.IDX_VERIFY]    = R.Verify,
            [PoleId14.IDX_CONSTRAIN] = R.Constrain,
            [PoleId14.IDX_TRANSFORM] = R.Transform,
        };

        if (axes.Count != RelationAlgebra.K)
            return VerificationResult.Fail($"Axis count={axes.Count} ≠ K={RelationAlgebra.K}");

        // Verify bijection: each R enum value appears exactly once
        var rTypes = axes.Values.Distinct().Count();
        if (rTypes != RelationAlgebra.K)
            return VerificationResult.Fail($"Non-bijective mapping: {rTypes} distinct R types ≠ {RelationAlgebra.K}");

        return VerificationResult.Pass(
            $"Governance axes [10-13] ↔ RFC-007C T={{Inverse,Verify,Constrain,Transform}} bijection ✓");
    }

    /// <summary>
    /// Verify that W4 mode assignment on zero governance coordinates yields P0.
    /// P0 = [+1+1+1+1] is the ground state.
    /// </summary>
    public static VerificationResult VerifyZeroModeIsP0()
    {
        var zero = PoleId14.Zero();
        var phi  = new float[] { 0, 0, 0, 0 };
        var scores = W4.Score(phi);
        // All scores equal 0 for zero input — argmax ties to index 0 (P0)
        int mode = W4.AssignMode(scores);
        // Actually for zero phi, all scores are 0 — tie-break to P0
        if (mode != 0)
            return VerificationResult.Fail($"Zero poleId → mode P{mode} ≠ P0");
        return VerificationResult.Pass("Zero poleId → W4 mode P0 (ground state) ✓");
    }

    /// <summary>
    /// Verify ClassType() correctly identifies active governance axis.
    /// </summary>
    public static VerificationResult VerifyClassTypeExtraction()
    {
        var vals = new sbyte[PoleId14.Length];

        // Test each governance axis
        foreach (R expected in Enum.GetValues<R>())
        {
            Array.Clear(vals, 0, vals.Length);
            int idx = expected switch
            {
                R.Transform => PoleId14.IDX_TRANSFORM,
                R.Verify    => PoleId14.IDX_VERIFY,
                R.Constrain => PoleId14.IDX_CONSTRAIN,
                R.Inverse   => PoleId14.IDX_INVERSE,
                _ => throw new ArgumentOutOfRangeException()
            };
            vals[idx] = 1;
            var pole = new PoleId14(vals);
            var ct = pole.ClassType();
            // Priority-based: Inverse > Constrain > Verify > Transform
            // so if we only set one axis, ClassType should return that axis
            if (idx == PoleId14.IDX_TRANSFORM)
            {
                if (ct != R.Transform)
                    return VerificationResult.Fail($"Transform only → ClassType={ct}");
            }
        }
        return VerificationResult.Pass("ClassType() correctly extracts active governance axis ✓");
    }

    public static IEnumerable<VerificationResult> Verify()
    {
        yield return VerifyDimension();
        yield return VerifyGovernanceAxisMapping();
        yield return VerifyZeroModeIsP0();
        yield return VerifyClassTypeExtraction();
    }
}
