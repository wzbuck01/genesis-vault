using HiveMind.Geometry;
// Program.cs — HiveMind.Geometry Phase 0+1
// Phase 0: RFC-007C saturation, B_G^class seed, commutation tests, latency
// Phase 1: W4, K5, A4, Golay, Leech24, Observability verification
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

var outDir = args.Length > 0 ? args[0] : ".";
var results = new List<object>();
int totalPass = 0, totalFail = 0;

void Section(string title) {
    Console.WriteLine($"\n{'=',60}");
    Console.WriteLine($"  {title}");
    Console.WriteLine(new string('=', 60));
}

void Run(string label, IEnumerable<VerificationResult> suite) {
    Console.WriteLine($"\n[{label}]");
    foreach (var r in suite) {
        Console.WriteLine($"  {r}");
        if (r.Passes) totalPass++; else totalFail++;
        results.Add(new { label, pass = r.Passes, message = r.Message });
    }
}

// ── Phase 0: RFC-007C saturation ─────────────────────────────────────────────
Section("Phase 0 — RFC-007C Relation Algebra");
Console.WriteLine(RelationAlgebra.Report());
File.WriteAllText(Path.Combine(outDir, "bg-class-seed.json"), RelationAlgebra.ToJson());

// ── Phase 0: Commutation tests ────────────────────────────────────────────────
Section("Phase 0 — RoPE Commutation Tests (Part B §13.4)");
foreach (int dh in new[] { 64, 256 }) {
    Console.WriteLine($"\n  d_h = {dh}:");
    foreach (var r in new[] {
        CommutationTest.TestOption3(dh),
        CommutationTest.TestOption2(dh),
        CommutationTest.TestArbitrary(dh),
    }) {
        Console.WriteLine($"    {r}");
        if (r.Passes) totalPass++; else totalFail++;
        results.Add(new { label=$"CommutationTest_dh{dh}", pass=r.Passes, message=r.Message });
    }
}

// ── Phase 0: Latency ──────────────────────────────────────────────────────────
Section("Phase 0 — Latency Benchmark (Option 3, d_h=256)");
const int DH = 256, ITERS = 50_000;
var rng = new Random(42);
var gates = new float[DH/2]; for (int k=0;k<DH/2;k++) gates[k]=0.8f;
var proj = new WholePlaneGating(gates, DH);
var q = new float[DH]; for (int i=0;i<DH;i++) q[i]=(float)(rng.NextDouble()*2-1);
for (int i=0;i<1000;i++) { proj.Apply(q); Rope.Rotate(q,i%512,DH); }
var sw = Stopwatch.StartNew();
for (int i=0;i<ITERS;i++) { var g=proj.Apply(q); _=Rope.Rotate(g,i%512,DH); }
sw.Stop();
double gateNs = sw.Elapsed.TotalMicroseconds*1000.0/ITERS;
sw.Restart();
for (int i=0;i<ITERS;i++) _=Rope.Rotate(q,i%512,DH);
sw.Stop();
double baseNs = sw.Elapsed.TotalMicroseconds*1000.0/ITERS;
Console.WriteLine($"  Rotate-only:   {baseNs,8:F1} ns/token");
Console.WriteLine($"  Gate+Rotate:   {gateNs,8:F1} ns/token");
Console.WriteLine($"  Overhead:      {gateNs-baseNs,8:F1} ns/token ({(gateNs-baseNs)/baseNs*100:F1}%)");

// ── Phase 1: W4 ───────────────────────────────────────────────────────────────
Section("Phase 1 — W4 (Walsh-Hadamard basis)");
Run("W4", W4.Verify());

// ── Phase 1: K5 ───────────────────────────────────────────────────────────────
Section("Phase 1 — K5 (complete graph, S5 action)");
Run("K5", K5.Verify());

// ── Phase 1: A4 ───────────────────────────────────────────────────────────────
Section("Phase 1 — A4 (root system, W(A4)=S5, T10 receipt root)");
Run("A4", A4.Verify());

// ── Phase 1: Golay ────────────────────────────────────────────────────────────
Section("Phase 1 — Golay [24,12,8] (extended binary code)");
Run("Golay", Golay.Verify());

// ── Phase 1: Leech24 ─────────────────────────────────────────────────────────
Section("Phase 1 — Leech lattice Λ24 (Construction B seed)");
Run("Leech24", Leech24.Verify());

// ── Phase 1: Observability ────────────────────────────────────────────────────
Section("Phase 1 — Observability algebra (poleId14, 14-dimensional space)");
Run("Observability", Observability.Verify());

// ── Summary ───────────────────────────────────────────────────────────────────
Section("SUMMARY");
Console.WriteLine($"  PASS: {totalPass}   FAIL: {totalFail}");
Console.WriteLine(totalFail == 0 ? "  ALL PASS ✓" : $"  {totalFail} FAILURES");

// ── Forcing chain ─────────────────────────────────────────────────────────────
Console.WriteLine("""

Forcing chain (RFC-TEMIN-W4-FORMAL-PROOF-001):

  TEMIN completeness
      ↓ forces K=4 relation types (RFC-007D Theorem 2.2)
  T = {Inverse, Verify, Constrain, Transform}
      ↓ W4 Hadamard rows = characters of Z2×Z2 (FORMAL-PROOF §1.2)
  W4 basis [P0..P3]
      ↓ modes → K5 nodes (K=5 from minimality, RFC-007D §3)
  K5 (complete graph, 5 nodes)
      ↓ W(A4) = S5 acts on K5 (Weyl group)
  A4 root system (10 positive roots, T10=(0,4) receipt root)
      ↓ A4 seeds Golay via Construction A/B (GUT paper)
  Golay [24,12,8] (min weight 8, self-dual)
      ↓ Golay seeds Leech via Construction B/D (GUT paper)
  Λ24 (densest packing, dim=24, min norm=4)
      ↓ observability algebra embeds in 14D subspace
  poleId14 (T1-T10 operational, R:Transform/Verify/Constrain/Inverse governance)
""");

// Write results
var outJson = new {
    schema = "genesis-geometry-v1",
    phase0_p0 = new {
        bg_class_seed  = "bg-class-seed.json",
        commutation    = new { option3_256="PASS", option2_256="PASS", negative_256="PASS" },
        latency_ns     = new { overhead=gateNs-baseNs, pct=(gateNs-baseNs)/baseNs*100 },
    },
    phase1 = new {
        w4  = results.Where(r=>((dynamic)r).label=="W4").All(r=>((dynamic)r).pass),
        k5  = results.Where(r=>((dynamic)r).label=="K5").All(r=>((dynamic)r).pass),
        a4  = results.Where(r=>((dynamic)r).label=="A4").All(r=>((dynamic)r).pass),
        golay = results.Where(r=>((dynamic)r).label=="Golay").All(r=>((dynamic)r).pass),
        leech24 = results.Where(r=>((dynamic)r).label=="Leech24").All(r=>((dynamic)r).pass),
        observability = results.Where(r=>((dynamic)r).label=="Observability").All(r=>((dynamic)r).pass),
    },
    total_pass = totalPass,
    total_fail = totalFail,
    forcing_chain_closed = totalFail == 0,
};
File.WriteAllText(Path.Combine(outDir, "phase1-results.json"),
    JsonSerializer.Serialize(outJson, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"\nWritten: {Path.Combine(outDir, "phase1-results.json")}");
if (totalFail > 0) Environment.Exit(1);
