// HookAdapter.cs — HiveMind.Adapter
// Reads state/seam-registry.json, emits ACTIVE_DISCOVERY nodes
// for missing hook entries into state/intent-substrate.jsonl (idempotent)
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace HiveMind.Adapter;

public static class HookAdapter
{
    // Governance density map — used to set priority
    static readonly Dictionary<string, int> GovDensity = new()
    {
        ["ad_hoc_github_write_detected"]  = 9,   // very high — write authority
        ["false_verification_claim"]      = 9,   // very high — receipt semantics
        ["credential_slot_mismatch"]      = 8,   // high — authentication
        ["bootstrap_stub_detected"]       = 7,   // medium-high — integrity
        ["repeated_manual_step"]          = 6,   // medium — operational pattern
        ["doctrine_assess"]               = 8,
        ["genesis_analyze"]               = 7,
        ["sgir_learning_cycle"]           = 8,
        ["turn_completed_audit"]          = 7,
        ["leverage_record"]               = 6,
        ["doctrine_inject"]               = 7,
    };

    public static int Run(string stateDir, bool dryRun = false, bool verbose = false)
    {
        var registryPath  = Path.Combine(stateDir, "seam-registry.json");
        var substratePath = Path.Combine(stateDir, "intent-substrate.jsonl");

        if (!File.Exists(registryPath))
        {
            Console.Error.WriteLine($"[adapter] FAIL: {registryPath} not found");
            return -1;
        }

        // Load registry
        var registry = JsonNode.Parse(File.ReadAllText(registryPath));
        var seams    = registry?["seams"]?.AsArray() ?? new JsonArray();

        // Load existing substrate neuron_ids (idempotency)
        var existing = new HashSet<string>();
        if (File.Exists(substratePath))
            foreach (var line in File.ReadLines(substratePath))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    var n = JsonNode.Parse(line)?["neuron_id"]?.GetValue<string>();
                    if (n != null) existing.Add(n);
                }
                catch { }
            }

        // Filter: missing status, hooks.json origin
        var targets = seams
            .Where(s => s?["status"]?.GetValue<string>() == "missing")
            .Where(s =>
            {
                var loc  = s?["host_location"]?.GetValue<string>() ?? "";
                var cap  = s?["capability_id"]?.GetValue<string>()  ?? "";
                return loc.Contains("hooks.json") || loc.Contains("hooks/") ||
                       cap.Contains("hook") || cap.Contains("Hook");
            })
            .ToList();

        Console.WriteLine($"[adapter] registry:  {seams.Count} seams");
        Console.WriteLine($"[adapter] targets:   {targets.Count} missing hooks");
        Console.WriteLine($"[adapter] substrate: {existing.Count} existing nodes");

        if (targets.Count == 0)
        {
            Console.WriteLine("[adapter] nothing to emit");
            return 0;
        }

        var now    = DateTime.UtcNow.ToString("o");
        int added  = 0;
        int skipped = 0;

        foreach (var seam in targets)
        {
            var location   = seam?["host_location"]?.GetValue<string>()    ?? "";
            var capability = seam?["capability_id"]?.GetValue<string>()    ?? "";
            var method     = seam?["interface_or_method"]?.GetValue<string>() ?? "";
            var seamStatus = seam?["status"]?.GetValue<string>()           ?? "missing";

            // Derive hook name from capability_id or location
            var hookName = DeriveHookName(capability, location, method);
            var title    = $"{hookName} — implement {hookName.Replace("_","-")}";
            var desc     = BuildDescription(hookName, capability, location, method, seam);

            var cf = new JsonObject
            {
                ["title"]       = title,
                ["description"] = desc,
                ["domain"]      = "governance",
                ["origin_conv"] = "seam-registry",
            };

            var nid = NeuronId("ACTIVE_DISCOVERY", cf);

            if (existing.Contains(nid))
            {
                if (verbose) Console.WriteLine($"[adapter] skip (exists): {hookName}");
                skipped++;
                continue;
            }

            var priority = GovDensity
                .Where(kv => hookName.Contains(kv.Key) || kv.Key.Contains(hookName))
                .Select(kv => kv.Value)
                .DefaultIfEmpty(6)
                .First();

            var node = new JsonObject
            {
                ["neuron_id"]       = nid,
                ["schema"]          = new JsonObject { ["name"] = "intent-node", ["version"] = "1.0.0" },
                ["kind"]            = "INTENT_NODE",
                ["particle_type"]   = "ACTIVE_DISCOVERY",
                ["canonical_form"]  = cf,
                ["status"]          = "in_progress",
                ["priority"]        = priority,
                ["domain"]          = "governance",
                ["domain_tags"]     = new JsonArray("governance", "hooks", "seam"),
                ["artifacts"]       = new JsonArray(
                    $"tools/hooks.json:{hookName}",
                    $"tools/implement-{hookName.Replace("_","-")}.cs (missing)"
                ),
                ["completion_pct"]  = 0,
                ["blocking_titles"] = new JsonArray(),
                ["origin_conv_id"]  = "seam-registry",
                ["seam_id"]         = $"{location}:{capability}",
                ["seam_status"]     = seamStatus,
                ["gov_density"]     = priority,
                ["visits"]          = 1,
                ["last_visited"]    = now,
                ["created_at"]      = now,
            };

            if (verbose)
                Console.WriteLine($"[adapter] emit: {hookName} (priority {priority}, nid {nid[..16]}...)");

            if (!dryRun)
            {
                File.AppendAllText(substratePath, node.ToJsonString() + "\n");
                existing.Add(nid);
            }
            else
            {
                Console.WriteLine($"[adapter] [dry-run] would emit: {title}");
            }

            added++;
        }

        Console.WriteLine($"[adapter] done — added: {added}  skipped: {skipped}");
        return added;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static string DeriveHookName(string capability, string location, string method)
    {
        if (!string.IsNullOrEmpty(capability)) return capability.ToLowerInvariant().Replace("-","_");
        if (!string.IsNullOrEmpty(method))     return method.ToLowerInvariant().Replace("-","_");
        var loc = Path.GetFileNameWithoutExtension(location);
        return loc.ToLowerInvariant().Replace("-","_");
    }

    static string BuildDescription(string hookName, string capability, string location,
                                   string method, JsonNode? seam)
    {
        var phase   = seam?["phase"]?.GetValue<string>();
        var trigger = seam?["trigger"]?.GetValue<string>();
        var sb      = new StringBuilder();
        sb.Append($"hooks.json declares '{hookName}' ");
        if (phase   != null) sb.Append($"({phase}) ");
        if (trigger != null) sb.Append($"[trigger: {trigger}] ");
        sb.Append("with no backing tool file. ");
        sb.Append($"Source: {location}. ");
        sb.Append("Behavioral contract exists but no implementation. ");
        sb.Append("Emit ACTIVE_DISCOVERY node for bridge to generate design proposal.");
        return sb.ToString();
    }

    static string NeuronId(string particleType, JsonObject canonicalForm)
    {
        var content = new JsonObject
        {
            ["schema"]         = new JsonObject { ["name"] = "intent-node", ["version"] = "1.0.0" },
            ["kind"]           = "INTENT_NODE",
            ["particle_type"]  = particleType,
            ["canonical_form"] = JsonNode.Parse(canonicalForm.ToJsonString()),
        };
        // RFC-013: JCS — sort keys alphabetically at every level
        var jcs = Jcs(content);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(jcs)))
                      .ToLowerInvariant();
    }

    static string Jcs(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            var pairs = obj.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                          .Select(kv => $"{JsonSerializer.Serialize(kv.Key)}:{Jcs(kv.Value)}");
            return "{" + string.Join(",", pairs) + "}";
        }
        if (node is JsonArray arr)
            return "[" + string.Join(",", arr.Select(Jcs)) + "]";
        return node?.ToJsonString() ?? "null";
    }
}
