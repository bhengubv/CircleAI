// ItGenerator.cs
//
// IT's placeholder "brain" — an IChatGenerator that returns canned, personality-
// driven replies so this sample runs with ZERO model download and ZERO native
// libraries. It is deliberately dumb: it demonstrates the *pipeline* (routing +
// warm slot + streaming + in-session memory), not real language modelling.
//
// >>> To make IT actually think: replace `new ItGenerator()` in Program.cs with
//     the real generator, e.g.  new QwenTextGenerator(modelPath, ...)  (MNN).
//     Nothing else in the sample changes — that's the whole point of the seam.

using System.Runtime.CompilerServices;
using CircleAI.Inference;

namespace CircleAI.Samples.It;

/// <summary>
/// A tiny, rule-based stand-in for a real on-device chat model. Implements the
/// two members every generator must (<see cref="GenerateAsync"/> +
/// <see cref="StreamAsync"/>); the session-snapshot and structured-response
/// members come free from <see cref="IChatGenerator"/>'s default methods.
/// </summary>
public sealed class ItGenerator : IChatGenerator
{
    // Same cues the concierge uses, so IT's *style* matches how it was routed.
    private static readonly string[] ReasoningCues =
    {
        "solve", "prove", "debug", "calculate", "derive", "equation",
        "algorithm", "stack trace", "refactor", "step by step", "step-by-step",
    };

    /// <inheritdoc />
    public Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(Compose(messages));
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions? options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // Stream word-by-word so the sample shows genuinely chunked output — the
        // same contract a real token-streaming model fulfils.
        var words = Compose(messages).Split(' ');
        for (int i = 0; i < words.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            yield return i == 0 ? words[i] : " " + words[i];
            await Task.Delay(12, ct).ConfigureAwait(false);
        }
    }

    public void Dispose() { /* no native resources to release */ }

    // ---- IT's "personality": canned replies shaped by the conversation ----

    private static string Compose(IReadOnlyList<ChatMessage> messages)
    {
        var lastUser = messages.LastOrDefault(m =>
            string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase))?.Content?.Trim() ?? "";
        var lower = lastUser.ToLowerInvariant();
        var name  = ExtractName(messages); // demonstrates memory: scans the whole history

        // 1. Greeting.
        if (IsGreeting(lower))
            return name is null
                ? "IT here. No, I won't ask if you've tried turning it off and on again. What do you need?"
                : $"Hello again, {name}. IT is listening.";

        // 2. Memory recall — "what's my name?"
        if (lower.Contains("my name") && (lower.Contains("what") || lower.Contains('?')))
            return name is null
                ? "You haven't told me your name yet. Tell me and I'll remember it."
                : $"You're {name}. I remember what I'm told — that's rather the point of me.";

        // 3. Memory set — "my name is ..." / "call me ..."
        if ((lower.StartsWith("my name is ") || lower.StartsWith("call me ")) && name is not null)
            return $"Good to meet you, {name}. I'll remember that.";

        // 4. A reasoning turn — the concierge routes these to a reasoning specialist.
        if (ReasoningCues.Any(c => lower.Contains(c)))
            return "That one wants a reasoning specialist, and the concierge just routed it there. "
                 + "The method, step by step: (1) state exactly what's asked; (2) split it into the "
                 + "smallest solvable parts; (3) solve each; (4) reassemble and sanity-check. "
                 + "I'm the placeholder brain, so I narrate the shape instead of crunching it — wire in a "
                 + "real reasoning model and this line becomes the actual worked answer.";

        // 5. Sign-off.
        if (lower is "bye" or "quit" or "exit"
            || lower.StartsWith("thanks") || lower.StartsWith("thank you"))
            return name is null ? "Any time. IT out." : $"Any time, {name}. IT out.";

        // 6. Everything else — honest deadpan.
        var prefix = name is null ? "" : $"{name}, ";
        return $"{prefix}noted: \"{Shorten(lastUser)}\". I'm the reference placeholder brain, so I won't "
             + "fake a real answer — but everything around me is real: the concierge chose who answers, the "
             + "warm slot served it, and this text streamed to you piece by piece. Swap in a live model and "
             + "this becomes a genuine reply.";
    }

    private static bool IsGreeting(string lower)
        => lower is "hi" or "hello" or "hey" or "yo"
           || lower.StartsWith("hi ") || lower.StartsWith("hello") || lower.StartsWith("hey ");

    /// <summary>
    /// Scans the whole conversation for "my name is X" / "call me X". This is how
    /// IT "remembers" across turns — the full history is passed on every call.
    /// </summary>
    private static string? ExtractName(IReadOnlyList<ChatMessage> messages)
    {
        foreach (var m in messages)
        {
            if (!string.Equals(m.Role, "user", StringComparison.OrdinalIgnoreCase)) continue;
            var text = m.Content ?? "";
            var lower = text.ToLowerInvariant();
            foreach (var lead in new[] { "my name is ", "call me " })
            {
                var idx = lower.IndexOf(lead, StringComparison.Ordinal);
                if (idx < 0) continue;
                var rest = text.Substring(idx + lead.Length).TrimStart();
                var word = new string(rest.TakeWhile(ch => char.IsLetter(ch) || ch is '-' or '\'').ToArray());
                if (word.Length >= 2)
                    return char.ToUpperInvariant(word[0]) + word.Substring(1);
            }
        }
        return null;
    }

    private static string Shorten(string s) => s.Length <= 60 ? s : s.Substring(0, 57) + "...";
}
