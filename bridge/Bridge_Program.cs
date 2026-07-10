using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
// Program.cs — HiveMind.Bridge CLI entry point
// One-shot: read frontier, invoke bridge, emit receipt.
// (c) 2026 Brandon Clark / Genesis Systems

using HiveMind.Bridge;

var cfg      = BridgeConfig.FromEnvironment();
var dryRun   = args.Contains("--dry-run");
var limitArg = Array.IndexOf(args, "--limit");
var limit    = limitArg >= 0 ? int.Parse(args[limitArg + 1]) : 1;

Console.WriteLine("[bridge] HiveMind.Bridge v1.0.0");
Console.WriteLine($"[bridge] state:    {cfg.StateDir}");
Console.WriteLine($"[bridge] provider: {cfg.Provider}");
Console.WriteLine($"[bridge] limit:    {limit}");
Console.WriteLine($"[bridge] dry-run:  {dryRun}");
Console.WriteLine();

if (!File.Exists(cfg.SubstratePath))
{
    Console.Error.WriteLine($"[bridge] FAIL: {cfg.SubstratePath} not found");
    Environment.Exit(1);
}

var nodes     = BridgeRunner.LoadSubstrate(cfg.SubstratePath);
var processed = BridgeRunner.LoadProcessed(cfg.ReceiptsPath);
var frontier  = BridgeRunner.RankFrontier(nodes, processed);

Console.WriteLine($"[bridge] nodes: {nodes.Count}  processed: {processed.Count}  frontier: {frontier.Count}");

if (frontier.Count == 0)
{
    Console.WriteLine("[bridge] frontier empty — nothing to do");
    Environment.Exit(0);
}

var batch   = frontier.Take(limit).ToList();
var runner  = new BridgeRunner(cfg);
int success = 0, failed = 0;

foreach (var node in batch)
{
    var title = node["canonical_form"]?["title"]?.GetValue<string>() ?? "(untitled)";
    var score = node["frontier_score"]?.GetValue<double>() ?? 0;
    Console.WriteLine($"[bridge] → {title[..Math.Min(60,title.Length)]} (score {score:F3})");

    if (dryRun) { Console.WriteLine("         [dry-run]"); continue; }

    var result = await runner.RunOnceAsync(node);
    Console.WriteLine($"         {result.Outcome}  {result.ReceiptHash[..Math.Min(32,result.ReceiptHash.Length)]}...");
    if (result.Outcome == "SUCCESS") success++; else { failed++; Console.Error.WriteLine($"         error: {result.ErrorMessage}"); }
}

Console.WriteLine($"\n[bridge] done — success: {success}  failed: {failed}");
if (failed > 0 && success == 0) Environment.Exit(1);
