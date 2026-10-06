// FailureAnalyst.cs
//
// The default IFailureAnalyst: a thin capability over the brain (IAIService).
//
// It asks the model to categorise the failure and recommend quick-fix / patch / defer
// as a compact JSON object, then parses it. Two rules keep it honest and safe:
//   1. It NEVER throws for an analysis it cannot make. A null/blank/garbled reply, or
//      JSON it cannot parse, becomes HealingVerdict.NeedsAHuman — so a consumer's
//      pipeline never loses an error to an exception in here.
//   2. It NEVER invents. If the model does not give a field, it is left null; if the
//      model is unsure, the honest answer is Defer. Circle AI does not fill a gap with
//      a confident-sounding fix.
//
// It uses ChatAsync with its OWN system message, which the brain treats as authoritative
// and does NOT wrap in the persona/RAG enrichment (see AIService.PrepareMessagesAsync) —
// so the analysis prompt reaches the model clean.

using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;   // ChatMessage

namespace CircleAI.Hosting.SelfHealing;

/// <summary>Circle AI's self-healing sense, backed by the brain.</summary>
public sealed class FailureAnalyst : IFailureAnalyst
{
    private readonly Func<string, string, CancellationToken, Task<string>> _chat;

    /// <param name="brain">The Circle AI brain. The analyst is a thin layer over it.</param>
    public FailureAnalyst(IAIService brain)
    {
        ArgumentNullException.ThrowIfNull(brain);
        // The brain treats a supplied system message as authoritative and skips the
        // persona/RAG enrichment (AIService.PrepareMessagesAsync), so the analysis
        // prompt reaches the model clean — which is what keeps the 0.6B's JSON honest.
        _chat = (system, user, ct) => brain.ChatAsync(
            new List<ChatMessage> { new("system", system), new("user", user) }, options: null, ct);
    }

    /// <summary>
    /// Construct over a raw chat delegate — <c>(systemPrompt, userPrompt, ct) =&gt; reply</c>.
    /// This lets a host back the analyst with a brain it already runs (e.g. the app's
    /// resident <c>IBrain</c>) instead of standing up a second <see cref="IAIService"/>,
    /// which would load a second copy of the model. The delegate returns the model's raw
    /// text; a persona-wrapped reply still works, it just parses less reliably — and an
    /// unparseable reply safely degrades to "needs a human", never a wrong fix.
    /// </summary>
    /// <param name="chat">The brain call: a system prompt, a user prompt, a token → a reply.</param>
    public FailureAnalyst(Func<string, string, CancellationToken, Task<string>> chat)
        => _chat = chat ?? throw new ArgumentNullException(nameof(chat));

    // Kept short and explicit: a 0.6B follows a tight schema far better than prose, and
    // the "if unsure, defer" line is what stops it guessing a fix for an error it does
    // not understand.
    private const string Instruction =
        "You are Circle AI's failure analyst. You are given one software failure. " +
        "Reply with ONE JSON object and nothing else:\n" +
        "{\"kind\":\"quick-fix|patch|defer\",\"category\":\"short-label\",\"summary\":\"one sentence\"," +
        "\"action\":\"the concrete next step\",\"fix\":\"a drafted code change, or empty\",\"confidence\":0.0}\n" +
        "kind: \"quick-fix\" = safe and reversible to do now (retry, reset a connection, " +
        "refresh a cache); \"patch\" = needs a code change a human should review; " +
        "\"defer\" = not urgent, not safe to touch automatically, or you are not sure. " +
        "If you are not sure, use \"defer\". Do not invent a cause or a fix you are not sure of. " +
        "confidence is 0 to 1.";

    /// <inheritdoc />
    public async Task<HealingVerdict> AnalyseAsync(FailureContext failure, CancellationToken ct = default)
    {
        if (failure is null || string.IsNullOrWhiteSpace(failure.Message))
            return HealingVerdict.NeedsAHuman("No failure detail to analyse.");

        string reply;
        try
        {
            reply = await _chat(Instruction, Describe(failure), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;   // cancellation is the caller's intent, not an analysis failure.
        }
        catch (Exception ex)
        {
            // The brain was unreachable/failed. That is not something to invent around.
            return HealingVerdict.NeedsAHuman($"The analyst could not reach the brain: {ex.GetType().Name}.");
        }

        return Parse(reply);
    }

    /// <summary>Lay the failure out for the model, skipping empty parts.</summary>
    private static string Describe(FailureContext f)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(f.Source)) sb.Append("Source: ").AppendLine(f.Source);
        sb.Append("Error: ").AppendLine(f.Message);
        if (!string.IsNullOrWhiteSpace(f.Details)) sb.Append("Context: ").AppendLine(f.Details);
        if (!string.IsNullOrWhiteSpace(f.StackTrace))
            sb.AppendLine("Stack trace:").AppendLine(f.StackTrace);
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Pull the verdict out of the model's reply. Lenient — a model often wraps JSON in
    /// prose — but never a false success: anything it cannot read becomes NeedsAHuman.
    /// </summary>
    private static HealingVerdict Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
            return HealingVerdict.NeedsAHuman("The brain returned nothing to analyse the failure with.");

        var start = reply.IndexOf('{');
        var end = reply.LastIndexOf('}');
        if (start < 0 || end <= start)
            return HealingVerdict.NeedsAHuman("The analysis did not come back in a form I could read.");

        JsonDocument doc;
        try { doc = JsonDocument.Parse(reply[start..(end + 1)]); }
        catch (JsonException)
        {
            return HealingVerdict.NeedsAHuman("The analysis did not come back in a form I could read.");
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return HealingVerdict.NeedsAHuman("The analysis did not come back in a form I could read.");

            var kindText = Str(root, "kind");
            if (kindText is null)
                return HealingVerdict.NeedsAHuman("The analysis did not say what kind of fix this needs.");

            var summary = Str(root, "summary")
                          ?? "The brain analysed this failure but gave no summary.";

            return new HealingVerdict(
                Kind:              MapKind(kindText),
                Category:          Str(root, "category") ?? "unclassified",
                Summary:           summary,
                RecommendedAction: Blank(Str(root, "action")),
                DraftFix:          Blank(Str(root, "fix")),
                Confidence:        Confidence(root));
        }
    }

    // Unknown or in-between wording is treated as Defer — the safe side. We only claim
    // quick-fix or patch when the model says so plainly.
    private static HealingKind MapKind(string kind) => kind.Trim().ToLowerInvariant() switch
    {
        "quick-fix" or "quickfix" or "quick_fix" or "quick" => HealingKind.QuickFix,
        "patch" or "fix" or "code-fix" => HealingKind.Patch,
        _ => HealingKind.Defer,
    };

    private static string? Str(JsonElement e, string name)
        => e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString()
            : null;

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static double Confidence(JsonElement root)
    {
        if (!root.TryGetProperty("confidence", out var c)) return 0.0;
        if (c.ValueKind == JsonValueKind.Number && c.TryGetDouble(out var d))
            return Math.Clamp(d, 0.0, 1.0);
        if (c.ValueKind == JsonValueKind.String && double.TryParse(c.GetString(), out var s))
            return Math.Clamp(s, 0.0, 1.0);
        return 0.0;
    }
}
