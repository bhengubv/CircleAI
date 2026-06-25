// KnowledgeGraphExtractor.cs
//
// (Phase E2) Uses the host's IAIService to ask an LLM to extract
// (subject, predicate, object) triples from a single conversation turn.
// The extraction prompt asks for strict-JSON output; the parser is
// defensive against the LLM emitting extra prose.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Inference;

namespace CircleAI.Companion;

public interface IKnowledgeGraphExtractor
{
    ValueTask<IReadOnlyList<KnowledgeTriple>> ExtractFromTurnAsync(
        string userText, string assistantText, string? sourceEpisodeId, CancellationToken ct = default);
}

public sealed class LlmKnowledgeGraphExtractor : IKnowledgeGraphExtractor
{
    private const string SystemPrompt =
        "You are a knowledge-graph extractor. Read the conversation turn between USER and ASSISTANT. " +
        "Identify entities (people, places, things, concepts) and facts. " +
        "Output a single JSON array of triples like [{\"s\":\"Subject\",\"p\":\"predicate\",\"o\":\"object\",\"c\":0.0-1.0}, ...]. " +
        "Only output the JSON — no prose, no markdown fences.";

    private readonly IAIService _ai;
    public LlmKnowledgeGraphExtractor(IAIService ai) => _ai = ai ?? throw new ArgumentNullException(nameof(ai));

    public async ValueTask<IReadOnlyList<KnowledgeTriple>> ExtractFromTurnAsync(
        string userText, string assistantText, string? sourceEpisodeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(userText) && string.IsNullOrWhiteSpace(assistantText))
            return Array.Empty<KnowledgeTriple>();

        var userMsg = new StringBuilder()
            .AppendLine("USER:")
            .AppendLine(userText)
            .AppendLine("ASSISTANT:")
            .AppendLine(assistantText)
            .ToString();

        string reply;
        try
        {
            reply = await _ai.ChatAsync(new[]
            {
                new ChatMessage("system", SystemPrompt),
                new ChatMessage("user",   userMsg),
            }, ct: ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KnowledgeGraphExtractor] LLM call failed: {ex.Message}");
            return Array.Empty<KnowledgeTriple>();
        }

        return ParseTriples(reply, sourceEpisodeId);
    }

    internal static IReadOnlyList<KnowledgeTriple> ParseTriples(string raw, string? sourceEpisodeId)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<KnowledgeTriple>();
        var firstBracket = raw.IndexOf('[');
        var lastBracket  = raw.LastIndexOf(']');
        if (firstBracket < 0 || lastBracket <= firstBracket) return Array.Empty<KnowledgeTriple>();
        var jsonSlice = raw[firstBracket..(lastBracket + 1)];
        try
        {
            using var doc = JsonDocument.Parse(jsonSlice);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return Array.Empty<KnowledgeTriple>();
            var hits = new List<KnowledgeTriple>(doc.RootElement.GetArrayLength());
            foreach (var entry in doc.RootElement.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object) continue;
                var s = entry.TryGetProperty("s", out var sv) ? sv.GetString() : null;
                var p = entry.TryGetProperty("p", out var pv) ? pv.GetString() : null;
                var o = entry.TryGetProperty("o", out var ov) ? ov.GetString() : null;
                var c = entry.TryGetProperty("c", out var cv) && cv.ValueKind == JsonValueKind.Number
                            ? Math.Clamp((float)cv.GetDouble(), 0f, 1f) : 0.75f;
                if (string.IsNullOrWhiteSpace(s) || string.IsNullOrWhiteSpace(p) || string.IsNullOrWhiteSpace(o)) continue;
                hits.Add(new KnowledgeTriple(s!, p!, o!, sourceEpisodeId, c, DateTimeOffset.UtcNow));
            }
            return hits;
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[KnowledgeGraphExtractor] parse failed: {ex.Message}");
            return Array.Empty<KnowledgeTriple>();
        }
    }
}
