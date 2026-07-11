// W4K5.cs — Genesis.Geometry Phase 1
// W4 Hadamard matrix, K5 graph, S5 group action
// RFC-TEMIN-W4-FORMAL-PROOF-001: TEMIN completeness forces K=4 → W4 → W(A4)=S5
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace HiveMind.Geometry;

// ── W4: Walsh-Hadamard basis ───────────────────────────────────────────────────
// Rows = four irreducible characters of Z2×Z2, indexed P0..P3
// P0=[+1+1+1+1] P1=[+1-1+1-1] P2=[+1+1-1-1] P3=[+1-1-1+1]
// Source: w4qr-evaluator.js W4_BASIS / RFC-TEMIN-W4-FORMAL-PROOF-001 §1.2

public static class W4
{
    public const int K = 4;

    public static readonly int[,] Basis = {
        { 1,  1,  1,  1 },  // P0
        { 1, -1,  1, -1 },  // P1
        { 1,  1, -1, -1 },  // P2
        { 1, -1, -1,  1 },  // P3
    };

    /// <summary>
    /// Project a 4D feature vector φ = (φ₀,φ₁,φ₂,φ₃) onto W4 basis.
    /// score_i = Σⱼ Basis[i,j] × φ[j]
    /// </summary>
    public static float[] Score(float[] phi)
    {
        if (phi.Length != K) throw new ArgumentException($"φ must be length {K}");
        var scores = new float[K];
        for (int i = 0; i < K; i++)
        for (int j = 0; j < K; j++)
            scores[i] += Basis[i, j] * phi[j];
        return scores;
    }

    /// <summary>
    /// Assign mode P0..P3 by argmax score (tie-break: lowest index).
    /// Returns 0..3.
    /// </summary>
    public static int AssignMode(float[] scores)
    {
        int best = 0;
        for (int i = 1; i < K; i++)
            if (scores[i] > scores[best]) best = i;
        return best;
    }

    public static string ModeName(int mode) => $"P{mode}";

    /// <summary>
    /// Verify W4 is a Hadamard matrix: W4 × W4ᵀ = 4I
    /// </summary>
    public static VerificationResult VerifyHadamard()
    {
        // W4 × W4ᵀ should equal 4×I
        for (int i = 0; i < K; i++)
        for (int j = 0; j < K; j++)
        {
            int dot = 0;
            for (int k = 0; k < K; k++)
                dot += Basis[i, k] * Basis[j, k];
            int expected = i == j ? K : 0;
            if (dot != expected)
                return VerificationResult.Fail($"W4×W4ᵀ[{i},{j}]={dot} ≠ {expected}");
        }
        return VerificationResult.Pass("W4×W4ᵀ = 4I ✓");
    }

    /// <summary>
    /// Verify character orthogonality: rows of W4 are orthogonal characters of Z2×Z2.
    /// Each pair of distinct rows has dot product 0.
    /// </summary>
    public static VerificationResult VerifyCharacterOrthogonality()
    {
        for (int i = 0; i < K; i++)
        for (int j = i + 1; j < K; j++)
        {
            int dot = 0;
            for (int k = 0; k < K; k++)
                dot += Basis[i, k] * Basis[j, k];
            if (dot != 0)
                return VerificationResult.Fail($"Rows {i},{j} not orthogonal: dot={dot}");
        }
        return VerificationResult.Pass("All row pairs orthogonal — Z2×Z2 characters ✓");
    }

    /// <summary>
    /// Verify score-mode consistency: for each basis row, projecting it yields
    /// a unique mode equal to its own index (self-recognition).
    /// </summary>
    public static VerificationResult VerifySelfRecognition()
    {
        for (int i = 0; i < K; i++)
        {
            var phi = new float[] { Basis[i,0], Basis[i,1], Basis[i,2], Basis[i,3] };
            var scores = Score(phi);
            int mode = AssignMode(scores);
            if (mode != i)
                return VerificationResult.Fail($"Basis row {i} (P{i}) maps to P{mode}, not P{i}");
        }
        return VerificationResult.Pass("Each basis row self-identifies as its own mode ✓");
    }

    public static IEnumerable<VerificationResult> Verify()
    {
        yield return VerifyHadamard();
        yield return VerifyCharacterOrthogonality();
        yield return VerifySelfRecognition();
    }
}

// ── K5: Complete graph on 5 vertices ─────────────────────────────────────────
// 5 nodes {0,1,2,3,4}, 10 edges (i,j) for i<j
// S5 = W(A4) acts by permuting the 5 nodes
// K5 is the fundamental domain for the A4 root system

public static class K5
{
    public const int N = 5;  // nodes
    public const int E = 10; // edges

    /// <summary>All 10 edges of K5 as (i,j) pairs with i < j.</summary>
    public static readonly (int i, int j)[] Edges =
        (from i in Enumerable.Range(0, N)
         from j in Enumerable.Range(i + 1, N - i - 1)
         select (i, j)).ToArray();

    /// <summary>
    /// Apply a permutation σ ∈ S5 to a K5 edge.
    /// σ maps nodes; edge (i,j) → (σ(i), σ(j)) normalized to i' < j'.
    /// </summary>
    public static (int i, int j) ApplyPermutation(int[] sigma, (int i, int j) edge)
    {
        int a = sigma[edge.i], b = sigma[edge.j];
        return a < b ? (a, b) : (b, a);
    }

    /// <summary>
    /// Verify K5 has exactly C(5,2) = 10 distinct edges.
    /// </summary>
    public static VerificationResult VerifyEdgeCount()
    {
        if (Edges.Length != E)
            return VerificationResult.Fail($"|E(K5)|={Edges.Length} ≠ 10");
        var distinct = Edges.Distinct().Count();
        if (distinct != E)
            return VerificationResult.Fail($"Duplicate edges detected: {distinct} distinct of {E}");
        return VerificationResult.Pass($"|E(K5)| = {E} ✓");
    }

    /// <summary>
    /// Verify S5 acts faithfully on K5 edges.
    /// Each non-identity permutation moves at least one edge.
    /// </summary>
    public static VerificationResult VerifyS5Action()
    {
        // Test all 120 permutations of {0,1,2,3,4}
        var identity = new[] { 0, 1, 2, 3, 4 };
        var allPerms = Permutations(identity).ToList();

        if (allPerms.Count != 120)
            return VerificationResult.Fail($"|S5|={allPerms.Count} ≠ 120");

        int trivial = 0;
        foreach (var sigma in allPerms)
        {
            if (sigma.SequenceEqual(identity)) { trivial++; continue; }
            // Non-identity: must move at least one edge
            bool moved = Edges.Any(e => ApplyPermutation(sigma, e) != e);
            if (!moved)
                return VerificationResult.Fail($"Non-identity perm {string.Join("",sigma)} fixes all edges");
        }

        if (trivial != 1)
            return VerificationResult.Fail($"{trivial} identity permutations found (expected 1)");

        return VerificationResult.Pass($"S5 acts faithfully on K5 — |S5|=120, |K5|={E} ✓");
    }

    static IEnumerable<int[]> Permutations(int[] arr)
    {
        if (arr.Length == 1) { yield return arr; yield break; }
        for (int i = 0; i < arr.Length; i++)
        {
            var rest = arr.Where((_, idx) => idx != i).ToArray();
            foreach (var perm in Permutations(rest))
                yield return new[] { arr[i] }.Concat(perm).ToArray();
        }
    }

    public static IEnumerable<VerificationResult> Verify()
    {
        yield return VerifyEdgeCount();
        yield return VerifyS5Action();
    }
}

// ── Shared result type ─────────────────────────────────────────────────────────

public record VerificationResult(bool Passes, string Message)
{
    public static VerificationResult Pass(string msg) => new(true, msg);
    public static VerificationResult Fail(string msg) => new(false, msg);
    public override string ToString() => $"[{(Passes ? "PASS" : "FAIL")}] {Message}";
}
