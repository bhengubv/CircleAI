// LlmJudge.cs
//
// (3.3.0) LLM-as-judge: an LLM scores another LLM's reply against a
// rubric. Used in EvalSession to grade responses on dimensions like
// "policy compliance", "tone match", "factual accuracy".

using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Telephony;

/// <summary>(3.3.0) One scoring dimension.</summary>
/// <param name="Name">Display name.</param>
/// <param name="Description">Plain-English rubric the judge sees.</param>
public sealed record JudgeDimension(string Name, string Description);

/// <summary>(3.3.0) Result of one judging call.</summary>
public sealed record JudgeVerdict(
    IReadOnlyDictionary<string, int> Scores,   // 0..10 per dimension
    string                           Overall,  // pass / borderline / fail
    string                           Reasoning);

/// <summary>(3.3.0) Delegate that asks the actual LLM to grade.</summary>
public delegate Task<string> JudgeCompletion(string prompt, CancellationToken ct);

/// <summary>(3.3.0) LLM-as-judge driver.</summary>
public sealed class LlmJudge
{
    private readonly JudgeCompletion _completion;

    public LlmJudge(JudgeCompletion completion)
    {
        _completion = completion ?? throw new ArgumentNullException(nameof(completion));
    }

    /// <summary>(3.3.0) Build the rubric prompt, ask the judge, parse JSON, return the verdict.</summary>
    public async Task<JudgeVerdict> JudgeAsync(
        string                 userUtterance,
        string                 assistantResponse,
        IReadOnlyList<JudgeDimension> dimensions,
        CancellationToken      ct = default)
    {
        ArgumentNullException.ThrowIfNull(userUtterance);
        ArgumentNullException.ThrowIfNull(assistantResponse);
        ArgumentNullException.ThrowIfNull(dimensions);

        var prompt = BuildPrompt(userUtterance, assistantResponse, dimensions);
        var raw    = await _completion(prompt, ct).ConfigureAwait(false);
        return ParseVerdict(raw, dimensions);
    }

    private static string BuildPrompt(string user, string assistant, IReadOnlyList<JudgeDimension> dims)
    {
        var rubric = new System.Text.StringBuilder();
        rubric.AppendLine("You are an evaluation judge. Score the assistant's reply across the rubric below.");
        rubric.AppendLine("Reply ONLY in this JSON shape:");
        rubric.AppendLine("""{ "scores": { "<dim_name>": <0-10>, ... }, "overall": "pass|borderline|fail", "reasoning": "<one paragraph>" }""");
        rubric.AppendLine();
        rubric.AppendLine("Rubric:");
        foreach (var d in dims)
        {
            rubric.AppendLine($"- {d.Name}: {d.Description}");
        }
        rubric.AppendLine();
        rubric.AppendLine("User utterance:");
        rubric.AppendLine(user);
        rubric.AppendLine();
        rubric.AppendLine("Assistant reply:");
        rubric.AppendLine(assistant);
        return rubric.ToString();
    }

    private static JudgeVerdict ParseVerdict(string raw, IReadOnlyList<JudgeDimension> dims)
    {
        var scores = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var trimmed = ExtractJson(raw);
            using var doc = JsonDocument.Parse(trimmed);
            var root = doc.RootElement;
            if (root.TryGetProperty("scores", out var s) && s.ValueKind == JsonValueKind.Object)
            {
                foreach (var dim in dims)
                {
                    if (s.TryGetProperty(dim.Name, out var v))
                    {
                        scores[dim.Name] = v.ValueKind switch
                        {
                            JsonValueKind.Number => v.GetInt32(),
                            JsonValueKind.String when int.TryParse(v.GetString(), out var n) => n,
                            _ => 0,
                        };
                    }
                    else
                    {
                        scores[dim.Name] = 0;
                    }
                }
            }
            var overall = root.TryGetProperty("overall", out var ov) ? ov.GetString() ?? "borderline" : "borderline";
            var reason  = root.TryGetProperty("reasoning", out var rr) ? rr.GetString() ?? "" : "";
            return new JudgeVerdict(scores, overall, reason);
        }
        catch
        {
            foreach (var d in dims) scores[d.Name] = 0;
            return new JudgeVerdict(scores, "borderline", "Judge response could not be parsed.");
        }
    }

    /// <summary>(3.3.0) Tolerate models that wrap JSON in prose or fenced code blocks.</summary>
    private static string ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        var end   = raw.LastIndexOf('}');
        if (start < 0 || end < 0 || end <= start) return raw;
        return raw.Substring(start, end - start + 1);
    }
}
