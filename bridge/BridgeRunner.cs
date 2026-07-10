// BridgeRunner.cs — HiveMind.Bridge library class
// Governed translation: UMS intent node → GNB verb → provider → receipt
// (c) 2026 Brandon Clark / Genesis Systems

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace HiveMind.Bridge;

public class BridgeRunner
{
    const string VERB     = "bridge.execute.intent";
    const string VERB_VER = "1.0.0";
    const string BROKER   = "1.0.0";

    static readonly Dictionary<string, double> TypeWeight = new()
    {
        ["BLOCKER"]          = 2.0,
        ["ACTIVE_DISCOVERY"] = 1.5,
        ["LATENT_GOAL"]      = 1.2,
        ["FRONTIER"]         = 1.0,
        ["TANGENT"]          = 0.5,
    };
    static readonly HashSet<string> SkipStatus = ["completed", "abandoned"];
    static readonly string[] ValidTypes =
        ["LATENT_GOAL","ACTIVE_DISCOVERY","BLOCKER","TANGENT","FRONTIER"];
    static readonly string[] RequiredFields =
        ["neuron_id","particle_type","canonical_form","frontier_score"];

    readonly BridgeConfig _cfg;
    readonly HttpClient   _http;

    public BridgeRunner(BridgeConfig cfg, HttpClient? http = null)
    {
        _cfg  = cfg;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    // ── Substrate loading ─────────────────────────────────────────────────────

    public static Dictionary<string, JsonObject> LoadSubstrate(string path)
    {
        var nodes = new Dictionary<string, JsonObject>();
        if (!File.Exists(path)) return nodes;
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var n  = JsonNode.Parse(line)?.AsObject();
                var id = n?["neuron_id"]?.GetValue<string>();
                if (id != null) nodes[id] = n!;
            }
            catch { }
        }
        return nodes;
    }

    public static HashSet<string> LoadProcessed(string receiptsPath)
    {
        var processed = new HashSet<string>();
        if (!File.Exists(receiptsPath)) return processed;
        foreach (var line in File.ReadLines(receiptsPath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var r       = JsonNode.Parse(line)?.AsObject();
                var body    = r?["body"]?.AsObject() ?? r;
                var id      = body?["neuron_id"]?.GetValue<string>();
                var outcome = body?["outcome"]?.GetValue<string>();
                if (id != null && outcome == "SUCCESS") processed.Add(id);
            }
            catch { }
        }
        return processed;
    }

    // ── Frontier ranking ──────────────────────────────────────────────────────

    public static List<JsonObject> RankFrontier(
        Dictionary<string, JsonObject> nodes,
        HashSet<string> processed)
    {
        return nodes.Values
            .Where(n => !processed.Contains(n["neuron_id"]?.GetValue<string>() ?? ""))
            .Where(n => !SkipStatus.Contains(n["status"]?.GetValue<string>() ?? ""))
            .Select(n =>
            {
                var ptype = n["particle_type"]?.GetValue<string>() ?? "LATENT_GOAL";
                var pri   = n["priority"]?.GetValue<double>()      ?? 5.0;
                var comp  = n["completion_pct"]?.GetValue<double>() ?? 0.0;
                var tw    = TypeWeight.GetValueOrDefault(ptype, 1.0);
                n["frontier_score"] = (pri / 10.0) * (1.0 - comp / 100.0) * tw;
                return n;
            })
            .OrderByDescending(n => n["frontier_score"]?.GetValue<double>() ?? 0)
            .ToList();
    }

    // ── Main entry point ──────────────────────────────────────────────────────

    /// <summary>
    /// Execute one governed bridge cycle for the given frontier node.
    /// Returns BridgeResult with Outcome = SUCCESS | FAILED.
    /// Never throws — all errors surface as Outcome=FAILED.
    /// </summary>
    public async Task<BridgeResult> RunOnceAsync(
        JsonObject node,
        CancellationToken ct = default)
    {
        var invId     = Guid.NewGuid().ToString();
        var invokedAt = DateTime.UtcNow.ToString("o");
        var neuronId  = node["neuron_id"]?.GetValue<string>() ?? "";
        var title     = node["canonical_form"]?["title"]?.GetValue<string>() ?? "(untitled)";
        var score     = node["frontier_score"]?.GetValue<double>() ?? 0;

        // 1. Validate VerbContract
        if (!ValidateContract(node, out var reason))
            return Fail(neuronId, title, score, $"VerbContract rejected: {reason}",
                        invId, invokedAt);

        // 2. Translate
        var prompt = BuildPrompt(node);

        // 3. Invoke provider
        string content = "", finishReason = "unknown";
        int promptTokens = 0, completionTokens = 0;
        try
        {
            var reqBody = new
            {
                model    = _cfg.Model,
                messages = new[]
                {
                    new { role = "system", content =
                        "You are a governed execution substrate. Analyze the intent node " +
                        "and produce a concrete, numbered execution plan. No preamble." },
                    new { role = "user", content = prompt },
                },
                max_tokens  = 1024,
                temperature = 0.0,
                stream      = false,
            };

            var httpReq = new HttpRequestMessage(HttpMethod.Post,
                $"{_cfg.Provider}/v1/chat/completions")
            {
                Content = new StringContent(
                    JsonSerializer.Serialize(reqBody),
                    Encoding.UTF8, "application/json"),
            };

            var res = await _http.SendAsync(httpReq, ct);
            if (!res.IsSuccessStatusCode)
            {
                var err = await res.Content.ReadAsStringAsync(ct);
                return Fail(neuronId, title, score,
                    $"provider HTTP {(int)res.StatusCode}: {err[..Math.Min(200,err.Length)]}",
                    invId, invokedAt);
            }

            var resp = JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
            content          = resp?["choices"]?[0]?["message"]?["content"]?.GetValue<string>() ?? "";
            promptTokens     = resp?["usage"]?["prompt_tokens"]?.GetValue<int>()     ?? 0;
            completionTokens = resp?["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
            finishReason     = resp?["choices"]?[0]?["finish_reason"]?.GetValue<string>() ?? "unknown";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Fail(neuronId, title, score, ex.Message, invId, invokedAt);
        }

        // 4. Emit receipt + publish result
        var execResult = new { content, model = _cfg.Model,
            promptTokens, completionTokens, finishReason };
        var hash = EmitReceipt(node, invId, invokedAt, "SUCCESS", execResult, neuronId);
        PublishResult(node, invId, score, content, _cfg.Model,
            promptTokens, completionTokens, finishReason, hash);

        return new BridgeResult
        {
            NeuronId      = neuronId,
            Title         = title,
            Outcome       = "SUCCESS",
            ReceiptHash   = hash,
            FrontierScore = score,
        };
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    static bool ValidateContract(JsonObject n, out string reason)
    {
        foreach (var f in RequiredFields)
            if (n[f] == null) { reason = $"missing: {f}"; return false; }
        var pt = n["particle_type"]?.GetValue<string>();
        if (!ValidTypes.Contains(pt)) { reason = $"unknown particle_type: {pt}"; return false; }
        if (n["canonical_form"]?["title"] == null) { reason = "canonical_form.title required"; return false; }
        reason = "OK";
        return true;
    }

    static string BuildPrompt(JsonObject n)
    {
        var cf       = n["canonical_form"]?.AsObject();
        var sb       = new StringBuilder();
        sb.AppendLine("INTENT NODE");
        sb.AppendLine($"Type: {n["particle_type"]?.GetValue<string>()}");
        sb.AppendLine($"Domain: {n["domain"]?.GetValue<string>() ?? "general"}");
        sb.AppendLine($"Priority: {n["priority"]?.GetValue<int>() ?? 5}/10");
        sb.AppendLine($"Status: {n["status"]?.GetValue<string>() ?? "unknown"}");
        sb.AppendLine($"Frontier score: {n["frontier_score"]?.GetValue<double>():F4}");
        sb.AppendLine($"\nTitle: {cf?["title"]?.GetValue<string>()}");
        sb.AppendLine($"Description: {cf?["description"]?.GetValue<string>() ?? ""}");
        var blockers = n["blocking_titles"]?.AsArray()
            .Select(x => x?.GetValue<string>()).Where(x => x != null).ToList() ?? [];
        if (blockers.Count > 0) sb.AppendLine($"Blocked by: {string.Join(", ", blockers)}");
        var artifacts = n["artifacts"]?.AsArray()
            .Select(x => x?.GetValue<string>()).Where(x => x != null).ToList() ?? [];
        if (artifacts.Count > 0) sb.AppendLine($"Artifacts: {string.Join(", ", artifacts)}");
        sb.AppendLine("\nProvide a concrete execution plan. If blocked, identify what must");
        sb.AppendLine("happen first. If a goal, decompose into immediate next steps.");
        sb.AppendLine("Be specific to the Genesis substrate (UMS, Hyperbase, ExeRay, EMIT, RFC corpus).");
        return sb.ToString();
    }

    static string Sha256Hex(string s)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();

    BridgeResult Fail(string neuronId, string title, double score,
                      string error, string invId, string invokedAt)
    {
        var hash = EmitReceipt(null, invId, invokedAt, "FAILED", null, neuronId);
        return new BridgeResult
        {
            NeuronId      = neuronId,
            Title         = title,
            Outcome       = "FAILED",
            ReceiptHash   = hash,
            FrontierScore = score,
            ErrorMessage  = error,
        };
    }

    string EmitReceipt(JsonObject? node, string invId, string invokedAt,
                       string outcome, object? execResult, string neuronId)
    {
        var body = new
        {
            invocation_id        = invId,
            emit_ordinal         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            verb                 = VERB,
            verb_version         = VERB_VER,
            invoked_at           = invokedAt,
            invoked_by           = "genesis-bridge-layer",
            execution_node       = _cfg.ExecNode,
            broker_version       = BROKER,
            locus_at_invoke      = "T1",
            justification        = $"frontier intent: {node?["canonical_form"]?["title"]?.GetValue<string>() ?? neuronId}",
            neuron_id            = neuronId,
            particle_type        = node?["particle_type"]?.GetValue<string>(),
            canonical_form       = node?["canonical_form"],
            frontier_score       = node?["frontier_score"]?.GetValue<double>(),
            before_state         = node != null ? new
            {
                status         = node["status"]?.GetValue<string>(),
                completion_pct = node["completion_pct"]?.GetValue<double>() ?? 0,
            } : null,
            after_state = new
            {
                status           = outcome == "SUCCESS" ? "bridge_processed" : (node?["status"]?.GetValue<string>()),
                bridge_result_id = invId,
            },
            execution_result     = execResult,
            outcome,
            compensation_available = false,
        };
        var json = JsonSerializer.Serialize(body);
        var hash = "sha256:" + Sha256Hex(json);
        File.AppendAllText(_cfg.ReceiptsPath,
            JsonSerializer.Serialize(new { body, content_hash = hash }) + "\n");
        return hash;
    }

    void PublishResult(JsonObject node, string invId, double score,
                       string content, string model, int promptTokens,
                       int completionTokens, string finishReason, string hash)
    {
        var result = new
        {
            invocation_id  = invId,
            neuron_id      = node["neuron_id"]?.GetValue<string>(),
            particle_type  = node["particle_type"]?.GetValue<string>(),
            domain         = node["domain"]?.GetValue<string>(),
            title          = node["canonical_form"]?["title"]?.GetValue<string>(),
            frontier_score = score,
            execution      = new { model, promptTokens, completionTokens, finishReason, content },
            receipt_hash   = hash,
            outcome        = "SUCCESS",
            published_at   = DateTime.UtcNow.ToString("o"),
        };
        File.AppendAllText(_cfg.ResultsPath, JsonSerializer.Serialize(result) + "\n");
    }
}
