// ReasoningLoopInnerMonologue.cs
//
// (Phase E1) Replaces TemplateInnerMonologue with a real o1 / DeepSeek-R1
// style reasoning loop. Drives the LLM via IChatGenerator.StreamFragmentsAsync
// and captures the Reasoning-kind fragments as the inner monologue, with
// the Content fragments forming the externally-visible conclusion.

using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Companion.HerJarvis;
using CircleAI.Inference;

namespace CircleAI.Companion;

/// <summary>(Phase E1) Inner-monologue powered by a reasoning-capable LLM.</summary>
public sealed class ReasoningLoopInnerMonologue : IInnerMonologue
{
    private const string ReasoningSystemPrompt =
        "You are this user's inner monologue. Reason carefully before responding. " +
        "Use <think>...</think> blocks for chain-of-thought. The visible answer " +
        "afterwards should be short and reflective — not a solution, an observation.";

    private readonly IChatGenerator _llm;

    public ReasoningLoopInnerMonologue(IChatGenerator llm)
        => _llm = llm ?? throw new ArgumentNullException(nameof(llm));

    public async ValueTask<SelfReflection> ReflectAsync(string contextJson, CancellationToken ct = default)
    {
        if (contextJson is null) throw new ArgumentNullException(nameof(contextJson));

        var messages = new[]
        {
            new ChatMessage("system", ReasoningSystemPrompt),
            new ChatMessage("user",   $"Context (raw JSON):\n{contextJson}\n\nReflect on this in 2-3 sentences."),
        };
        var options = new GenerationOptions { MaxTokens = 256, Temperature = 0.5f, IncludeReasoning = true };

        var reasoning = new StringBuilder();
        var content   = new StringBuilder();
        try
        {
            await foreach (var frag in _llm.StreamFragmentsAsync(messages, options, ct).ConfigureAwait(false))
            {
                ct.ThrowIfCancellationRequested();
                if (frag.Kind == ChatFragmentKind.Reasoning) reasoning.Append(frag.Text);
                else                                          content.Append(frag.Text);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ReasoningLoopInnerMonologue] LLM stream failed: {ex.Message}");
        }

        // Prefer the reasoning trace as the "thought"; fall back to visible content.
        var thought = reasoning.Length > 0 ? reasoning.ToString().Trim() : content.ToString().Trim();
        if (string.IsNullOrEmpty(thought)) thought = "(no inner state)";
        return new SelfReflection(thought, DateTimeOffset.UtcNow);
    }
}
