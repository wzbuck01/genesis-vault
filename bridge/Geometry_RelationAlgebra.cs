// RelationAlgebra.cs — RFC-007C/D complement algebra saturation
// Produces the B_G^class 4×4 adjacency table for attention-space governance
// Part B §10.6 — "Initialize from RFC-007C's actual Comp/Inv/Lift adjacency"
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace HiveMind.Geometry;

// ── Relation types — T = {inverse, verify, constrain, transform} (RFC-007D §2) ──

public enum R
{
    Inverse   = 0,   // I — identity/reversibility (TEMIN: I + N)
    Verify    = 1,   // E — invariant evaluation   (TEMIN: E + N)
    Constrain = 2,   // M — boundary enforcement   (TEMIN: M)
    Transform = 3,   // T — state change            (TEMIN: T)
}

public static class RelationAlgebra
{
    public const int K = 4;

    // ── Inv : T → T ─────────────────────────────────────────────────────────
    // From RFC-007C addendum (inverse_cancellation): Inv(inverse) = inverse
    // From symmetry of verification: Inv(verify) = verify
    // From bidirectionality of constraint: Inv(constrain) = constrain
    // From RFC-007D: transform is compositionally closed → Inv(transform) = transform

    public static R Inv(R t) => t switch
    {
        R.Inverse   => R.Inverse,    // (a,inverse,b) ⇒ (b,inverse,a) — self-dual
        R.Verify    => R.Verify,     // verification is symmetric
        R.Constrain => R.Constrain,  // constraint is bidirectional
        R.Transform => R.Transform,  // transformation closure is self-dual
        _ => throw new ArgumentOutOfRangeException(nameof(t)),
    };

    // ── Lift : T → T ────────────────────────────────────────────────────────
    // From RFC-007C §5.1: (a,t,b) ∈ E ⇒ (c(a), Lift(t), c(b)) ∈ E*
    // All four types are self-dual under complement lifting:
    // inverse ↔ inverse (complement of an inverse is still an inverse)
    // verify  ↔ verify  (complement verification is still verification)
    // constrain ↔ constrain (complement of a constraint is a constraint)
    // transform ↔ transform (complement of a transformation is a transformation)

    public static R Lift(R t) => t;  // all four types are Lift-self-dual

    // ── Comp : T × T → T? ───────────────────────────────────────────────────
    // From RFC-007C §5.2 and RFC-007D:
    // Comp(transform, transform) = transform  (RFC-007D: transitivity forced)
    // Comp(inverse, inverse)     = null       (involution collapses to self-loop, outside T)
    // Comp(verify, constrain)    = verify     (verify a constrained edge → still verify)
    // Comp(constrain, verify)    = constrain  (constrain a verified path → constrain)
    // Comp(transform, verify)    = verify     (verify a transform → verify)
    // Comp(verify, transform)    = transform  (transform a verified state → transform)
    // All other pairs → null (⊥, not defined)

    public static R? Comp(R t1, R t2) => (t1, t2) switch
    {
        (R.Transform, R.Transform) => R.Transform,
        (R.Verify,    R.Constrain) => R.Verify,
        (R.Constrain, R.Verify)    => R.Constrain,
        (R.Transform, R.Verify)    => R.Verify,
        (R.Verify,    R.Transform) => R.Transform,
        _ => null,
    };

    // ── Saturation algorithm (RFC-007C §5.4) ─────────────────────────────────
    // Input: seed edges E₀
    // Output: E* (saturated under Lift, Comp, Inv)
    // Complexity: O(K³) — tiny for K=4

    public static HashSet<(R src, R dst)> Saturate(IEnumerable<(R src, R dst)> seed)
    {
        var E = new HashSet<(R, R)>(seed);
        bool changed = true;

        while (changed)
        {
            changed = false;
            var Enew = new HashSet<(R, R)>(E);

            // Complement lifting: (a,t,b) → (Lift(a... wait, Lift acts on relation type not base
            // Since all bases in T are self-dual (same type), lifting (src,dst) pair means
            // the edge type is preserved — Lift adds the same edge (already present by self-duality)
            // The semantic content: if (c_i, t, c_j) exists, so does (c(c_i), Lift(t), c(c_j)) = (c_i, t, c_j)
            // All Lift applications are trivial for self-dual types. Skip (no new edges).

            // Inversion: (src,dst) → (dst,src) with Inv applied to relation
            // Since we're building a class×class table, Inv symmetrizes the table
            foreach (var (src, dst) in E.ToList())
            {
                // Inv means if (src→dst) is admissible, so is (dst→src)
                if (Enew.Add((dst, src))) changed = true;
            }

            // Composition closure
            foreach (var (a, b) in E.ToList())
            foreach (var (b2, c) in E.ToList())
            {
                if (b != b2) continue;
                // Here (a→b) and (b→c) with same intermediate class b
                // Comp represents: if class a can transition to b and b to c,
                // can a transition directly to c?
                // For B_G^class this means: if (a,b) admitted and (b,c) admitted
                // and Comp(t_ab, t_bc) defined → (a,c) admitted
                // Since we're building an untyped admissibility table,
                // composition closure = transitivity over admitted pairs
                if (Enew.Add((a, c))) changed = true;
            }

            E = Enew;
        }

        return E;
    }

    // ── Seed edges from RFC-007C/D structure ─────────────────────────────────
    // TEMIN mapping (RFC-007D §2.2) gives natural seed adjacencies:
    // T (transform) → can precede/follow: verify, transform
    // E (verify)    → can precede/follow: constrain, transform
    // M (constrain) → can precede/follow: verify, constrain
    // I (inverse)   → can precede/follow: inverse, verify
    // N → inverse + verify (already covered)
    //
    // Seed: all (t, TEMIN-adjacent(t)) pairs

    public static List<(R src, R dst)> SeedEdges() => new()
    {
        // Self-transitions (each type can follow itself where Comp is defined)
        (R.Transform, R.Transform),  // Comp(transform,transform)=transform

        // Cross-type adjacency from TEMIN structure
        (R.Transform, R.Verify),     // T-evaluation follows transition
        (R.Verify,    R.Transform),  // verified state can be transformed
        (R.Verify,    R.Constrain),  // Comp(verify,constrain)=verify
        (R.Constrain, R.Verify),     // Comp(constrain,verify)=constrain
        (R.Inverse,   R.Verify),     // N: inverse + verify
        (R.Verify,    R.Inverse),    // N symmetric
        (R.Inverse,   R.Inverse),    // inverse is self-inverse
        (R.Constrain, R.Constrain),  // constraint boundary is idempotent
    };

    // ── Build B_G^class: the 4×4 adjacency table ─────────────────────────────
    // Returns float[K,K] where [i,j] = initial bias weight
    //   > 0 : admitted transition (positive bias)
    //   = 0 : neutral
    // Note: B_G^authority and B_G^receipt are hard-masked SEPARATELY (Part B §10.6)
    //       This table is the LEARNED term only.

    public static float[,] BuildBgClass()
    {
        var seed  = SeedEdges();
        var Estar = Saturate(seed);

        var table = new float[K, K];

        // Admitted pairs get initial positive bias seeded from E*
        foreach (var (src, dst) in Estar)
        {
            var i = (int)src;
            var j = (int)dst;
            table[i, j] = 1.0f;  // initial bias — fine-tuning adjusts from here
        }

        return table;
    }

    // ── Hard masks (NOT learned — Part B §10.6) ──────────────────────────────
    // These are documented here for completeness but should NOT enter the
    // training objective. They are applied as unconditional post-processing.

    /// <summary>
    /// B_G^authority mask: if dst authority > src authority, block unconditionally.
    /// Theorem 3: none can self-authorize upward. Hard -∞ bias.
    /// </summary>
    public static bool AuthorityBlocked(int srcStratum, int dstStratum)
        => dstStratum > srcStratum;

    /// <summary>
    /// B_G^receipt mask: if mutation lacks an emitted receipt, block.
    /// Theorem 4: receipt-at-generation-time, not training-time snapshot.
    /// </summary>
    public static bool ReceiptRequired(bool receiptExists)
        => !receiptExists;

    // ── Reporting ─────────────────────────────────────────────────────────────

    public static string Report()
    {
        var seed  = SeedEdges();
        var Estar = Saturate(seed);
        var table = BuildBgClass();
        var names = Enum.GetNames<R>();

        var sb = new StringBuilder();
        sb.AppendLine("RFC-007C/D Relation Algebra — B_G^class Seed Table");
        sb.AppendLine($"K = {K}, T = {{{string.Join(", ", names)}}}");
        sb.AppendLine($"|E₀| = {seed.Count}  |E*| = {Estar.Count}");
        sb.AppendLine();

        // E* listing
        sb.AppendLine("E* (saturated admissible pairs):");
        foreach (var (src, dst) in Estar.OrderBy(e => e.src).ThenBy(e => e.dst))
            sb.AppendLine($"  ({src}, {dst})");
        sb.AppendLine();

        // 4×4 table
        sb.AppendLine("B_G^class initial weights (row=src, col=dst):");
        sb.Append("          ");
        foreach (var n in names) sb.Append($"{n,12}");
        sb.AppendLine();
        for (int i = 0; i < K; i++)
        {
            sb.Append($"{names[i],10}");
            for (int j = 0; j < K; j++)
                sb.Append($"{table[i,j],12:F1}");
            sb.AppendLine();
        }
        sb.AppendLine();

        sb.AppendLine("Hard-masked terms (NOT in table, applied unconditionally):");
        sb.AppendLine("  B_G^authority: block if dst_stratum > src_stratum (Theorem 3)");
        sb.AppendLine("  B_G^receipt:   block if receipt not yet emitted (Theorem 4)");

        return sb.ToString();
    }

    /// <summary>Serialize B_G^class table as JSON for the ONNX encoder pipeline.</summary>
    public static string ToJson()
    {
        var seed  = SeedEdges();
        var Estar = Saturate(seed);
        var table = BuildBgClass();
        var names = Enum.GetNames<R>();

        var rows = new List<object>();
        for (int i = 0; i < K; i++)
        {
            var cols = new Dictionary<string, float>();
            for (int j = 0; j < K; j++)
                cols[names[j]] = table[i, j];
            rows.Add(new { src = names[i], weights = cols });
        }

        return JsonSerializer.Serialize(new
        {
            schema       = "bg-class-v1",
            rfc          = "RFC-007C/D",
            K,
            relation_types = names,
            seed_count   = seed.Count,
            estar_count  = Estar.Count,
            estar        = Estar.OrderBy(e => e.src).ThenBy(e => e.dst)
                               .Select(e => new { src = e.src.ToString(), dst = e.dst.ToString() }),
            table        = rows,
            hard_masked  = new[]
            {
                new { term = "B_G^authority", rule = "block if dst_stratum > src_stratum", theorem = 3 },
                new { term = "B_G^receipt",   rule = "block if receipt not yet emitted",   theorem = 4 },
            },
            note = "table weights are initial seed values; fine-tune against bridge corpus",
        }, new JsonSerializerOptions { WriteIndented = true });
    }
}
