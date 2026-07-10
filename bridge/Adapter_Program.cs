// Program.cs — HiveMind.Adapter entry point
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.IO;
using HiveMind.Adapter;

var stateDir = Environment.GetEnvironmentVariable("GENESIS_STATE_DIR")
            ?? Path.Combine(AppContext.BaseDirectory, "..", "state");
var dryRun   = args.Contains("--dry-run");
var verbose  = args.Contains("--verbose") || args.Contains("-v");

Console.WriteLine("[adapter] HiveMind.Adapter v1.0.0");
Console.WriteLine($"[adapter] state: {stateDir}");
Console.WriteLine($"[adapter] dry-run: {dryRun}");
Console.WriteLine();

var added = HookAdapter.Run(stateDir, dryRun, verbose);
Environment.Exit(added < 0 ? 1 : 0);
