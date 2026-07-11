using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
// Program.cs — HiveMind.Orchestration entry point
// Runs FrontierLoop until exhausted or Ctrl+C.
// (c) 2026 Brandon Clark / Genesis Systems

using HiveMind.Bridge;
using HiveMind.Orchestration;

var cfg      = BridgeConfig.FromEnvironment();
var pollArg  = Array.IndexOf(args, "--poll");
var pollMs   = pollArg >= 0 ? int.Parse(args[pollArg + 1]) * 1000 : 30_000;
var limitArg = Array.IndexOf(args, "--limit");
var limit    = limitArg >= 0 ? (int?)int.Parse(args[limitArg + 1]) : null;
var once     = args.Contains("--once");
var watch    = args.Contains("--watch");

Console.WriteLine("[orch] HiveMind.Orchestration v1.0.0");
Console.WriteLine($"[orch] state:    {cfg.StateDir}");
Console.WriteLine($"[orch] provider: {cfg.Provider}");
Console.WriteLine($"[orch] poll:     {pollMs / 1000}s");
Console.WriteLine($"[orch] limit:    {limit?.ToString() ?? "all"}");
Console.WriteLine($"[orch] once:     {once}");
Console.WriteLine($"[orch] watch:    {watch}");
Console.WriteLine();

if (!File.Exists(cfg.SubstratePath))
{
    Console.Error.WriteLine($"[orch] FAIL: {cfg.SubstratePath} not found");
    Environment.Exit(1);
}

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    Console.WriteLine("\n[orch] shutdown requested");
    cts.Cancel();
};

var loop = new FrontierLoop(cfg, pollMs, watch);

if (once)
{
    // Single pass: process one node and exit (mirrors Bridge one-shot behavior)
    await loop.RunAsync(limit: 1, ct: cts.Token);
}
else
{
    // Continuous: run until frontier exhausted or Ctrl+C
    await loop.RunAsync(limit: limit, ct: cts.Token);
}
