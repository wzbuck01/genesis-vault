// RopeProjection.cs — Part B §9.6 — RoPE-commuting governance projection
// Implements Option 3 (whole-plane gating) and Option 2 (block-diagonal projection)
// with commutation unit tests per §13.4
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Text;

namespace HiveMind.Geometry;

/// <summary>
/// Standard RoPE rotation for one position.
/// R_p: block-diagonal rotation over 2D frequency planes.
/// For head dimension d_h, operates on d_h/2 independent 2D blocks.
/// θ_k = 1 / 10000^(2k / d_h)  (standard frequencies)
/// </summary>
public static class Rope
{
    /// <summary>Apply RoPE rotation to a query/key vector at position pos.</summary>
    public static float[] Rotate(float[] q, int pos, int dh)
    {
        var result = new float[dh];
        int pairs = dh / 2;
        for (int k = 0; k < pairs; k++)
        {
            double theta = pos / Math.Pow(10000.0, 2.0 * k / dh);
            float cos = (float)Math.Cos(theta);
            float sin = (float)Math.Sin(theta);
            float q0  = q[2 * k];
            float q1  = q[2 * k + 1];
            result[2 * k]     = q0 * cos - q1 * sin;
            result[2 * k + 1] = q0 * sin + q1 * cos;
        }
        return result;
    }

    /// <summary>Generate per-plane frequencies for dimension dh.</summary>
    public static double[] Frequencies(int dh)
    {
        int pairs = dh / 2;
        var freqs = new double[pairs];
        for (int k = 0; k < pairs; k++)
            freqs[k] = 1.0 / Math.Pow(10000.0, 2.0 * k / dh);
        return freqs;
    }
}

/// <summary>
/// Option 3: Whole-plane gating.
/// Π_G^(3)(q)_{2k} = γ_k * q_{2k}  (and 2k+1)
/// Commutes with R_p EXACTLY by construction — scalar scaling commutes with rotation.
/// Cost: O(d_h) — element-wise multiply.
/// </summary>
public class WholePlaneGating
{
    readonly float[] _gates;  // γ_k per frequency plane
    readonly int _dh;

    public WholePlaneGating(float[] gates, int dh)
    {
        _gates = gates;
        _dh    = dh;
    }

    /// <summary>
    /// Construct from g_t governance state.
    /// g_t = (class_type, locus_score, authority_stratum, receipt_exists)
    /// Maps to per-plane gates via B_G^class weights.
    /// </summary>
    public static WholePlaneGating FromGt(
        R classType,
        float locusScore,
        int authorityStratum,
        bool receiptExists,
        int dh,
        float[,] bgClass)
    {
        int pairs = dh / 2;
        var gates = new float[pairs];

        // Base gate from B_G^class for this class type (row sum as scalar)
        float classWeight = 0f;
        for (int j = 0; j < RelationAlgebra.K; j++)
            classWeight += bgClass[(int)classType, j];
        classWeight /= RelationAlgebra.K;  // normalize to [0,1] range initially

        // Modulate by locus score (higher locus = stronger governance emphasis)
        float baseGate = classWeight * Math.Clamp(locusScore, 0f, 1f);

        // B_G^authority hard mask: if authority insufficient, gate to 0
        // (represented here as a very low gate — actual hard block is downstream)
        float authorityMod = authorityStratum >= 1 ? 1.0f : 0.1f;

        // B_G^receipt: if no receipt, suppress (soft here — hard block is downstream)
        float receiptMod = receiptExists ? 1.0f : 0.5f;

        // Apply modulation uniformly across all planes for Option 3
        // Fine-tuning will learn per-plane differentiation
        float finalGate = baseGate * authorityMod * receiptMod;

        for (int k = 0; k < pairs; k++)
            gates[k] = finalGate;

        return new WholePlaneGating(gates, dh);
    }

    /// <summary>Apply whole-plane gating to query/key vector.</summary>
    public float[] Apply(float[] q)
    {
        var result = new float[_dh];
        int pairs  = _dh / 2;
        for (int k = 0; k < pairs; k++)
        {
            result[2 * k]     = _gates[k] * q[2 * k];
            result[2 * k + 1] = _gates[k] * q[2 * k + 1];
        }
        return result;
    }

    public float[] Gates => _gates;
}

/// <summary>
/// Option 2: Block-diagonal pre-RoPE projection.
/// Each 2D block has an independent 2×2 projection matrix.
/// Commutes with R_p when block structure matches RoPE's block structure.
/// Cost: O(d_h) — 4 multiplications per 2D block.
/// </summary>
public class BlockDiagonalProjection
{
    // Per-plane 2×2 matrices: [k, 0,0], [k, 0,1], [k, 1,0], [k, 1,1]
    readonly float[,,] _blocks;
    readonly int _dh;

    public BlockDiagonalProjection(float[,,] blocks, int dh)
    {
        _blocks = blocks;
        _dh     = dh;
    }

    /// <summary>
    /// Initialize as scaled identity per plane (matches Option 3 initially).
    /// Fine-tuning learns per-block structure.
    /// </summary>
    public static BlockDiagonalProjection Identity(int dh, float[] scales)
    {
        int pairs  = dh / 2;
        var blocks = new float[pairs, 2, 2];
        for (int k = 0; k < pairs; k++)
        {
            float s = k < scales.Length ? scales[k] : 1.0f;
            blocks[k, 0, 0] = s;
            blocks[k, 0, 1] = 0f;
            blocks[k, 1, 0] = 0f;
            blocks[k, 1, 1] = s;
        }
        return new BlockDiagonalProjection(blocks, dh);
    }

    /// <summary>Apply block-diagonal projection to query/key vector.</summary>
    public float[] Apply(float[] q)
    {
        var result = new float[_dh];
        int pairs  = _dh / 2;
        for (int k = 0; k < pairs; k++)
        {
            float q0 = q[2 * k];
            float q1 = q[2 * k + 1];
            result[2 * k]     = _blocks[k, 0, 0] * q0 + _blocks[k, 0, 1] * q1;
            result[2 * k + 1] = _blocks[k, 1, 0] * q0 + _blocks[k, 1, 1] * q1;
        }
        return result;
    }
}

/// <summary>
/// Commutation test suite — Part B §13.4
/// Verifies Π_G * R_p = R_p * Π_G (within floating-point tolerance)
/// </summary>
public static class CommutationTest
{
    const float TOLERANCE = 1e-5f;
    const int   TEST_VECTORS = 100;
    const int   TEST_POSITIONS = 10;

    static float[] RandomVector(Random rng, int dh)
    {
        var v = new float[dh];
        for (int i = 0; i < dh; i++)
            v[i] = (float)(rng.NextDouble() * 2 - 1);
        return v;
    }

    static float MaxAbsDiff(float[] a, float[] b)
    {
        float max = 0f;
        for (int i = 0; i < a.Length; i++)
            max = Math.Max(max, Math.Abs(a[i] - b[i]));
        return max;
    }

    /// <summary>
    /// Test Option 3: WholePlaneGating commutes with R_p exactly.
    /// Path A: R_p(Π_G(q))
    /// Path B: Π_G(R_p(q))
    /// Expected: A ≈ B within floating-point tolerance.
    /// </summary>
    public static CommutationResult TestOption3(int dh = 64, ulong seed = 42)
    {
        var rng    = new Random((int)seed);
        var gates  = new float[dh / 2];
        for (int k = 0; k < dh / 2; k++)
            gates[k] = (float)(rng.NextDouble() * 0.9 + 0.1);  // random gates in (0.1, 1.0)

        var proj   = new WholePlaneGating(gates, dh);
        float maxError = 0f;
        int   tested   = 0;

        for (int t = 0; t < TEST_VECTORS; t++)
        {
            var q = RandomVector(rng, dh);
            for (int pos = 0; pos < TEST_POSITIONS; pos++)
            {
                // Path A: gate then rotate
                var pathA = Rope.Rotate(proj.Apply(q), pos, dh);
                // Path B: rotate then gate
                var pathB = proj.Apply(Rope.Rotate(q, pos, dh));

                float err = MaxAbsDiff(pathA, pathB);
                maxError = Math.Max(maxError, err);
                tested++;
            }
        }

        return new CommutationResult
        {
            Option     = "Option 3 (WholePlaneGating)",
            Dh         = dh,
            Tested     = tested,
            MaxError   = maxError,
            Passes     = maxError < TOLERANCE,
            Tolerance  = TOLERANCE,
        };
    }

    /// <summary>
    /// Test Option 2: BlockDiagonalProjection (initialized as scaled identity).
    /// Block-diagonal in same basis as R_p → commutes exactly.
    /// </summary>
    public static CommutationResult TestOption2(int dh = 64, ulong seed = 42)
    {
        var rng    = new Random((int)seed);
        var scales = new float[dh / 2];
        for (int k = 0; k < dh / 2; k++)
            scales[k] = (float)(rng.NextDouble() * 0.9 + 0.1);

        var proj   = BlockDiagonalProjection.Identity(dh, scales);
        float maxError = 0f;
        int   tested   = 0;

        for (int t = 0; t < TEST_VECTORS; t++)
        {
            var q = RandomVector(rng, dh);
            for (int pos = 0; pos < TEST_POSITIONS; pos++)
            {
                var pathA = Rope.Rotate(proj.Apply(q), pos, dh);
                var pathB = proj.Apply(Rope.Rotate(q, pos, dh));

                float err = MaxAbsDiff(pathA, pathB);
                maxError = Math.Max(maxError, err);
                tested++;
            }
        }

        return new CommutationResult
        {
            Option     = "Option 2 (BlockDiagonal, scaled-identity init)",
            Dh         = dh,
            Tested     = tested,
            MaxError   = maxError,
            Passes     = maxError < TOLERANCE,
            Tolerance  = TOLERANCE,
        };
    }

    /// <summary>
    /// Negative test: arbitrary (non-block-diagonal) projection.
    /// Should NOT commute with R_p — this confirms the test is sensitive.
    /// </summary>
    public static CommutationResult TestArbitrary(int dh = 64, ulong seed = 42)
    {
        var rng    = new Random((int)seed);
        // Build a random projection matrix (not block-diagonal)
        var matrix = new float[dh, dh];
        for (int i = 0; i < dh; i++)
        for (int j = 0; j < dh; j++)
            matrix[i, j] = (float)(rng.NextDouble() * 0.1);  // small off-diagonal entries

        // Make diagonal dominant
        for (int i = 0; i < dh; i++)
            matrix[i, i] = (float)(rng.NextDouble() * 0.5 + 0.5);

        float maxError = 0f;
        int   tested   = 0;

        for (int t = 0; t < 20; t++)  // fewer tests, will fail quickly
        {
            var q    = RandomVector(rng, dh);
            int pos  = rng.Next(1, 100);

            // Apply arbitrary matrix
            var Aq = new float[dh];
            for (int i = 0; i < dh; i++)
            {
                float s = 0f;
                for (int j = 0; j < dh; j++) s += matrix[i, j] * q[j];
                Aq[i] = s;
            }

            var pathA = Rope.Rotate(Aq, pos, dh);    // A then R_p
            var Rq    = Rope.Rotate(q, pos, dh);
            var pathB = new float[dh];
            for (int i = 0; i < dh; i++)
            {
                float s = 0f;
                for (int j = 0; j < dh; j++) s += matrix[i, j] * Rq[j];
                pathB[i] = s;
            }

            float err = MaxAbsDiff(pathA, pathB);
            maxError = Math.Max(maxError, err);
            tested++;
        }

        return new CommutationResult
        {
            Option    = "Negative control (arbitrary matrix, should NOT commute)",
            Dh        = dh,
            Tested    = tested,
            MaxError  = maxError,
            Passes    = maxError >= TOLERANCE,   // passes = confirmed non-commutation
            Tolerance = TOLERANCE,
            IsNegativeControl = true,
        };
    }

    public static string RunAll(int dh = 64)
    {
        var sb = new StringBuilder();
        sb.AppendLine("=== Commutation Test Suite (Part B §13.4) ===");
        sb.AppendLine($"d_h = {dh}, tolerance = {TOLERANCE:E1}");
        sb.AppendLine();

        foreach (var result in new[]
        {
            TestOption3(dh),
            TestOption2(dh),
            TestArbitrary(dh),
        })
        {
            sb.AppendLine(result.ToString());
        }

        return sb.ToString();
    }
}

public class CommutationResult
{
    public string Option          { get; init; } = "";
    public int    Dh              { get; init; }
    public int    Tested          { get; init; }
    public float  MaxError        { get; init; }
    public bool   Passes          { get; init; }
    public float  Tolerance       { get; init; }
    public bool   IsNegativeControl { get; init; }

    public override string ToString()
    {
        var status = Passes ? "PASS" : "FAIL";
        var label  = IsNegativeControl ? "(non-commutation confirmed)" : "";
        return $"[{status}] {Option}\n" +
               $"       tested={Tested}  max_error={MaxError:E3}  tolerance={Tolerance:E1} {label}";
    }
}
