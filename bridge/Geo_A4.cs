// A4.cs — Genesis.Geometry Phase 1
// A4 root system: 10 positive roots, W(A4) = S5, T10 receipt root
// Source: WITNESS_OBS_4_HILBERT.py, RFC-TEMIN-W4-FORMAL-PROOF-001
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Linq;

namespace HiveMind.Geometry;

/// <summary>
/// A4 root system — C(5,2) = 10 positive roots.
/// Root (i,j) with 0 ≤ i &lt; j ≤ 4, height = j-i.
/// W(A4) = S5 acts by permuting the 5 coordinates.
/// T10 = (0,4) is the receipt root (height 4, unique maximum-height root).
/// </summary>
public static class A4
{
    public const int N = 5;               // coordinates
    public const int PositiveRootCount = 10;   // C(5,2)

    /// <summary>T10 = (0,4): the receipt root. Maximum height, fixed by Z2 reversal.</summary>
    public static readonly (int i, int j) T10 = (0, 4);

    /// <summary>All 10 positive roots of A4, ordered by (i,j).</summary>
    public static readonly (int i, int j)[] PositiveRoots =
        (from i in Enumerable.Range(0, N)
         from j in Enumerable.Range(i + 1, N - i - 1)
         select (i, j)).ToArray();

    /// <summary>Height of root (i,j) = j - i ∈ {1,2,3,4}.</summary>
    public static int Height((int i, int j) root) => root.j - root.i;

    /// <summary>
    /// Z2 reversal: (i,j) → (4-j, 4-i), normalized so first coord &lt; second.
    /// Fixes T10=(0,4), preserves heights. Source: WITNESS_OBS_4_HILBERT.py.
    /// </summary>
    public static (int i, int j) Reversal((int i, int j) root)
    {
        int ni = N - 1 - root.j;
        int nj = N - 1 - root.i;
        return ni < nj ? (ni, nj) : (nj, ni);
    }

    /// <summary>
    /// 9 non-receipt roots (all except T10).
    /// S3 = Stab_{S5}({0,4}) pointwise acts on {1,2,3} → permutes these.
    /// </summary>
    public static readonly (int i, int j)[] NonReceiptRoots =
        PositiveRoots.Where(r => r != T10).ToArray();

    /// <summary>Roots grouped by height.</summary>
    public static IGrouping<int,(int,int)>[] ByHeight() =>
        PositiveRoots.GroupBy(Height).OrderBy(g => g.Key).ToArray();

    // ── Verifications ─────────────────────────────────────────────────────────

    public static VerificationResult VerifyRootCount()
    {
        if (PositiveRoots.Length != PositiveRootCount)
            return VerificationResult.Fail($"|Roots|={PositiveRoots.Length} ≠ {PositiveRootCount}");
        return VerificationResult.Pass($"|A4⁺| = {PositiveRootCount} ✓  (C(5,2) = 10)");
    }

    public static VerificationResult VerifyHeightDistribution()
    {
        // Expected: h=1 → 4 roots, h=2 → 3, h=3 → 2, h=4 → 1
        var expected = new Dictionary<int,int> { {1,4},{2,3},{3,2},{4,1} };
        foreach (var g in ByHeight())
        {
            if (!expected.TryGetValue(g.Key, out int exp))
                return VerificationResult.Fail($"Unexpected height {g.Key}");
            if (g.Count() != exp)
                return VerificationResult.Fail($"h={g.Key}: {g.Count()} roots, expected {exp}");
        }
        return VerificationResult.Pass("Height distribution h:{4,3,2,1} ✓");
    }

    public static VerificationResult VerifyT10IsReceiptRoot()
    {
        // T10 must have maximum height (4), be unique at that height, and be in PositiveRoots
        if (!PositiveRoots.Contains(T10))
            return VerificationResult.Fail("T10=(0,4) not in positive roots");
        if (Height(T10) != 4)
            return VerificationResult.Fail($"h(T10) = {Height(T10)} ≠ 4");
        var h4 = PositiveRoots.Where(r => Height(r) == 4).ToArray();
        if (h4.Length != 1)
            return VerificationResult.Fail($"Expected 1 root at h=4, got {h4.Length}");
        return VerificationResult.Pass("T10=(0,4) is unique maximum-height (receipt) root ✓");
    }

    public static VerificationResult VerifyZ2Reversal()
    {
        // 1. Reversal fixes T10
        if (Reversal(T10) != T10)
            return VerificationResult.Fail($"Reversal does not fix T10: {Reversal(T10)} ≠ (0,4)");

        // 2. Reversal preserves heights
        foreach (var r in PositiveRoots)
            if (Height(Reversal(r)) != Height(r))
                return VerificationResult.Fail($"Reversal changes height of {r}: {Height(r)} → {Height(Reversal(r))}");

        // 3. Reversal is an involution (R²=id)
        foreach (var r in PositiveRoots)
            if (Reversal(Reversal(r)) != r)
                return VerificationResult.Fail($"Reversal not involutory at {r}");

        // 4. Reversal is not the identity (it moves roots)
        int fixed_ = PositiveRoots.Count(r => Reversal(r) == r);
        if (fixed_ == PositiveRoots.Length)
            return VerificationResult.Fail("Reversal is the identity — expected a non-trivial involution");

        return VerificationResult.Pass($"Z2 reversal: fixes T10, preserves heights, involutory, fixes {fixed_}/10 roots ✓");
    }

    public static VerificationResult VerifyW_A4_is_S5()
    {
        // W(A4) = S5 acts on 5 coordinates, |W(A4)| = 5! = 120
        // Verify: the S5 action on K5 edges matches A4 root permutation
        // Count S5 permutations that fix all roots (should be only identity)
        var allPerms = GenerateS5();
        if (allPerms.Count != 120)
            return VerificationResult.Fail($"|S5|={allPerms.Count} ≠ 120");

        // Each permutation acts on roots by permuting coordinates
        // σ · (i,j) = (σ(i), σ(j)) normalized
        bool faithful = allPerms
            .Where(s => !s.SequenceEqual(new[]{0,1,2,3,4}))
            .All(s => PositiveRoots.Any(r =>
            {
                int a = s[r.i], b = s[r.j];
                return (a < b ? (a,b) : (b,a)) != r;
            }));

        if (!faithful)
            return VerificationResult.Fail("W(A4) does not act faithfully on positive roots");

        return VerificationResult.Pass("W(A4) = S5, |W(A4)| = 120, faithful action on 10 positive roots ✓");
    }

    static List<int[]> GenerateS5()
    {
        var result = new List<int[]>();
        Permute(new[] {0,1,2,3,4}, 0, result);
        return result;
    }

    static void Permute(int[] arr, int start, List<int[]> result)
    {
        if (start == arr.Length) { result.Add((int[])arr.Clone()); return; }
        for (int i = start; i < arr.Length; i++)
        {
            (arr[start], arr[i]) = (arr[i], arr[start]);
            Permute(arr, start + 1, result);
            (arr[start], arr[i]) = (arr[i], arr[start]);
        }
    }

    public static IEnumerable<VerificationResult> Verify()
    {
        yield return VerifyRootCount();
        yield return VerifyHeightDistribution();
        yield return VerifyT10IsReceiptRoot();
        yield return VerifyZ2Reversal();
        yield return VerifyW_A4_is_S5();
    }
}
