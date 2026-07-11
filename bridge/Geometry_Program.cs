// Program.cs — HiveMind.Geometry Phase 0
// 1. RFC-007C saturation → B_G^class seed table
// 2. Commutation test suite (Part B §13.4)
// 3. Decode latency benchmark placeholder
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using HiveMind.Geometry;

var outDir = args.Length > 0 ? args[0] : ".";

// ── Stage 1: B_G^class seed table ────────────────────────────────────────────
Console.WriteLine(RelationAlgebra.Report());
var json = RelationAlgebra.ToJson();
var seedPath = Path.Combine(outDir, "bg-class-seed.json");
File.WriteAllText(seedPath, json);
Console.WriteLine($"Written: {seedPath}");
Console.WriteLine();

// ── Stage 2: Commutation tests ───────────────────────────────────────────────
// Test across multiple head dimensions (gemma4-12B uses d_h=256)
foreach (int dh in new[] { 32, 64, 128, 256 })
{
    Console.WriteLine(CommutationTest.RunAll(dh));
}

// ── Stage 3: Latency benchmark ───────────────────────────────────────────────
// Measure overhead of Option 3 (WholePlaneGating) per token at d_h=256
Console.WriteLine("=== Latency Benchmark (Option 3, d_h=256) ===");
const int DH    = 256;
const int ITERS = 100_000;

var rng   = new Random(42);
var gates = new float[DH / 2];
for (int k = 0; k < DH / 2; k++) gates[k] = 0.8f;

var proj = new WholePlaneGating(gates, DH);
var q    = new float[DH];
for (int i = 0; i < DH; i++) q[i] = (float)(rng.NextDouble() * 2 - 1);

// Warmup
for (int i = 0; i < 1000; i++)
{
    proj.Apply(q);
    Rope.Rotate(q, i % 512, DH);
}

// Benchmark: gate + rotate (full Option 3 path)
var sw = Stopwatch.StartNew();
for (int i = 0; i < ITERS; i++)
{
    var gated   = proj.Apply(q);
    var rotated = Rope.Rotate(gated, i % 512, DH);
    _ = rotated;
}
sw.Stop();
double gateRotateNs = sw.Elapsed.TotalMicroseconds * 1000.0 / ITERS;

// Baseline: rotate only (no governance)
sw.Restart();
for (int i = 0; i < ITERS; i++)
{
    var rotated = Rope.Rotate(q, i % 512, DH);
    _ = rotated;
}
sw.Stop();
double rotateOnlyNs = sw.Elapsed.TotalMicroseconds * 1000.0 / ITERS;

double overheadNs  = gateRotateNs - rotateOnlyNs;
double overheadPct = overheadNs / rotateOnlyNs * 100.0;

Console.WriteLine($"  Rotate-only:      {rotateOnlyNs,8:F1} ns/token");
Console.WriteLine($"  Gate + Rotate:    {gateRotateNs,8:F1} ns/token");
Console.WriteLine($"  Overhead:         {overheadNs,8:F1} ns/token ({overheadPct:F1}%)");
Console.WriteLine();
Console.WriteLine("Note: benchmark is CPU-side overhead only.");
Console.WriteLine("      GPU-side cost depends on shader dispatch — measure");
Console.WriteLine("      via llama-server token/s comparison (next step).");
Console.WriteLine();

// Write results JSON
var results = new
{
    schema = "phase0-results-v1",
    bg_class_seed = seedPath,
    commutation = new[]
    {
        new { option = "Option3", dh = 256, result = CommutationTest.TestOption3(256).Passes ? "PASS" : "FAIL" },
        new { option = "Option2", dh = 256, result = CommutationTest.TestOption2(256).Passes ? "PASS" : "FAIL" },
        new { option = "Negative", dh = 256, result = CommutationTest.TestArbitrary(256).Passes ? "PASS" : "FAIL" },
    },
    latency = new
    {
        dh              = DH,
        iterations      = ITERS,
        rotate_only_ns  = rotateOnlyNs,
        gate_rotate_ns  = gateRotateNs,
        overhead_ns     = overheadNs,
        overhead_pct    = overheadPct,
        note            = "CPU only; GPU latency requires llama-server benchmark"
    }
};

var outPath = Path.Combine(outDir, "phase0-results.json");
File.WriteAllText(outPath, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Written: {outPath}");
