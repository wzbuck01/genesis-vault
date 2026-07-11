// FrontierLoop.cs — HiveMind.Orchestration
// Continuous loop: read frontier → invoke bridge → receipt-derived progress
// Completion rule: processed == successful receipt exists
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using HiveMind.Bridge;

namespace HiveMind.Orchestration;

public class FrontierLoop
{
    readonly BridgeRunner _runner;
    readonly BridgeConfig _cfg;
    readonly int          _pollMs;
    readonly bool         _watch;

    public FrontierLoop(BridgeConfig cfg, int pollMs = 30_000, bool watch = false, HttpClient? http = null)
    {
        _cfg    = cfg;
        _runner = new BridgeRunner(cfg, http);
        _pollMs = pollMs;
        _watch  = watch;
    }

    /// <summary>
    /// Run until frontier is exhausted or cancellation is requested.
    /// Each cycle: load substrate → derive processed from receipts →
    ///             rank frontier → invoke bridge → emit receipt → advance.
    /// </summary>
    public async Task RunAsync(int? limit = null, CancellationToken ct = default)
    {
        int totalSuccess = 0, totalFailed = 0, cycles = 0;

        while (!ct.IsCancellationRequested)
        {
            cycles++;

            // Completion rule: processed is derived, never stored separately.
            var nodes     = BridgeRunner.LoadSubstrate(_cfg.SubstratePath);
            var processed = BridgeRunner.LoadProcessed(_cfg.ReceiptsPath);
            var frontier  = BridgeRunner.RankFrontier(nodes, processed);

            Console.WriteLine($"[orch] cycle {cycles} — nodes: {nodes.Count}  " +
                              $"processed: {processed.Count}  frontier: {frontier.Count}");

            if (frontier.Count == 0)
            {
                if (!_watch)
                {
                    Console.WriteLine("[orch] frontier exhausted — loop complete");
                    break;
                }
                Console.WriteLine($"[orch] frontier empty — polling in {_pollMs/1000}s...");
                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
                continue;
            }

            // Apply optional per-run limit
            var batch = limit.HasValue ? frontier.Take(limit.Value).ToList() : frontier;

            foreach (var node in batch)
            {
                if (ct.IsCancellationRequested) break;

                var title = node["canonical_form"]?["title"]?.GetValue<string>() ?? "(untitled)";
                var score = node["frontier_score"]?.GetValue<double>() ?? 0;

                Console.WriteLine($"[orch]   → {title[..Math.Min(60,title.Length)]} (score {score:F3})");

                var result = await _runner.RunOnceAsync(node, ct);

                Console.WriteLine($"[orch]     {result.Outcome}  " +
                                  $"{result.ReceiptHash[..Math.Min(32,result.ReceiptHash.Length)]}...");

                if (result.Outcome == "SUCCESS")
                    totalSuccess++;
                else
                {
                    totalFailed++;
                    Console.Error.WriteLine($"[orch]     error: {result.ErrorMessage}");
                }
            }

            // If all frontier nodes in this cycle were processed, next cycle
            // will see empty frontier and break cleanly.
            // If there are more nodes (limit was applied), poll before next cycle.
            if (limit.HasValue && frontier.Count > limit.Value && !ct.IsCancellationRequested)
            {
                Console.WriteLine($"[orch] polling in {_pollMs / 1000}s...");
                await Task.Delay(_pollMs, ct).ConfigureAwait(false);
            }
        }

        Console.WriteLine($"[orch] finished — cycles: {cycles}  " +
                          $"success: {totalSuccess}  failed: {totalFailed}");
    }
}
