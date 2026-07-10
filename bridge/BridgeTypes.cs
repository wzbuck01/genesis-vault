using System;
using System.IO;

// BridgeTypes.cs — shared types for HiveMind.Bridge
// (c) 2026 Brandon Clark / Genesis Systems

using System.Text.Json.Nodes;

namespace HiveMind.Bridge;

public record BridgeConfig
{
    public string StateDir   { get; init; } = "";
    public string Provider   { get; init; } = "http://localhost:18080";
    public string Model      { get; init; } = "gemma4-v2";
    public string ExecNode   { get; init; } = "gmktec-01";

    public string SubstratePath  => Path.Combine(StateDir, "intent-substrate.jsonl");
    public string ResultsPath    => Path.Combine(StateDir, "bridge-results.jsonl");
    public string ReceiptsPath   => Path.Combine(StateDir, "bridge-receipts.jsonl");

    public static BridgeConfig FromEnvironment()
    {
        var stateDir = Environment.GetEnvironmentVariable("GENESIS_STATE_DIR")
                    ?? Path.Combine(AppContext.BaseDirectory, "..", "state");
        return new BridgeConfig
        {
            StateDir = stateDir,
            Provider = Environment.GetEnvironmentVariable("BRIDGE_PROVIDER") ?? "http://localhost:18080",
            Model    = Environment.GetEnvironmentVariable("BRIDGE_MODEL")    ?? "gemma4-v2",
            ExecNode = Environment.GetEnvironmentVariable("EXEC_NODE")       ?? "gmktec-01",
        };
    }
}

public record BridgeResult
{
    public required string NeuronId      { get; init; }
    public required string Title         { get; init; }
    public required string Outcome       { get; init; }  // SUCCESS | FAILED | SKIPPED
    public required string ReceiptHash   { get; init; }
    public          double FrontierScore { get; init; }
    public          string? ErrorMessage { get; init; }
}
