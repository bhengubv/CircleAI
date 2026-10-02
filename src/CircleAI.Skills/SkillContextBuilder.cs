using System.Text;

namespace CircleAI.Skills;

/// <summary>
/// Selects the most relevant skills for a user query and formats them as a
/// system-prompt context block. Drop this into the B! system prompt enrichment
/// pipeline to give the model knowledge of available skills before each call.
/// </summary>
public sealed class SkillContextBuilder
{
    private readonly ISkillStore _store;
    private readonly int _maxSkills;
    private readonly int _maxChars;

    /// <summary>
    /// Initialises the builder.
    /// </summary>
    /// <param name="store">Source of available skills.</param>
    /// <param name="maxSkills">
    /// Maximum number of skills to include in the context block. Default 5.
    /// </param>
    /// <param name="maxChars">
    /// Hard ceiling on the emitted block. Default 1500 chars (~400 tokens) so
    /// skill context cannot crowd out the conversation on a small on-device
    /// model — measured: an unbounded block took a Huawei sweep from 4m33s to
    /// over 20 minutes.
    /// </param>
    public SkillContextBuilder(ISkillStore store, int maxSkills = 5, int maxChars = 1500)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maxSkills < 1) throw new ArgumentOutOfRangeException(nameof(maxSkills), "Must be at least 1.");
        if (maxChars < 100) throw new ArgumentOutOfRangeException(nameof(maxChars), "Must be at least 100.");
        _store = store;
        _maxSkills = maxSkills;
        _maxChars = maxChars;
    }

    /// <summary>
    /// Returns a formatted system-prompt block listing the most relevant
    /// skills for <paramref name="userQuery"/>. Returns an empty string when
    /// the store is empty or no skills match.
    /// </summary>
    /// <param name="userQuery">The user's current message or intent.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task<string> BuildContextAsync(
        string userQuery,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
            return string.Empty;

        // AN OVERVIEW QUESTION IS ANSWERED BY THE PERSONA, NOT BY A SEARCH, and it
        // is decided BEFORE the search rather than after it.
        //
        // "What can you do?" reduces to zero significant terms - what, can, you, do
        // are all stopwords - so there is no subject to retrieve on and no index can
        // answer it. AssistantPersona.CanDo is one fixed sentence saying what this
        // phone does, in front of the model on every turn, and it cannot be
        // mis-ranked.
        //
        // THIS USED TO FALL OUT OF THE NO-MATCH BRANCH, which is why it had to move.
        // Compact mode - a names-only listing - lived in the else below, so it only
        // ran when nothing matched. Measured against the shipping manifest on
        // 2026-10-02: "What can you do?" DID match, once, because one of the nineteen
        // entries (self.knowledge) contains that exact phrase in its description and
        // the manifest matches the whole query as a substring. An accident beat the
        // rule. And self.knowledge is the meta-entry ABOUT being honest, four fifths
        // Limits - so on a P30 the 0.8B summarised the caveats and answered "there
        // isn't much useful information available for me due to technical
        // constraints".
        //
        // The names-only listing goes with it, and was never the answer anyway: it
        // emitted ids - "model.selection, model.catalogue, model.download" - which is
        // the engine room talking, not the product.
        if (CircleAI.Core.SearchTerms.Significant(userQuery).Count == 0)
            return string.Empty;

        var matches = await _store.SearchAsync(userQuery, cancellationToken).ConfigureAwait(false);

        // A TOPICAL MISS INJECTS NOTHING. "What is the capital of France" has real
        // terms ({capital, France}) that simply matched no skill. Listing capability
        // names at a geography question is the noise that made a P30's 0.6B answer
        // "I need clarification" instead of "Paris" - measured 2026-09-14,
        // enrichment=131 and still wrong. Nothing is the right amount here.
        if (matches.Count == 0) return string.Empty;

        var candidates = matches.Take(_maxSkills).ToList();

        Console.WriteLine($"CIRCLEAI-SKILLS full q=\"{userQuery}\" hits={matches.Count} "
                        + $"chosen={string.Join(",", candidates.Select(c => c.Id))}");

        // Load full detail so we can include instructions.
        var sb = new StringBuilder();
        sb.AppendLine("## Available Skills");

        foreach (var summary in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hard character budget. On a 4096-token window a few verbose skills
            // can crowd out the actual conversation, which is how this block
            // silently degraded answer quality AND speed on-device.
            if (sb.Length >= _maxChars)
            {
                sb.AppendLine();
                sb.AppendLine("(further skills omitted to preserve context budget)");
                break;
            }

            var detail = await _store.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            if (detail is null) continue;

            sb.AppendLine();
            sb.AppendLine($"**{detail.Id}** — {detail.Description}");
            if (!string.IsNullOrWhiteSpace(detail.Instructions))
            {
                var remaining = Math.Max(0, _maxChars - sb.Length);
                var text = detail.Instructions.Length > remaining
                    ? detail.Instructions[..remaining] + " …"
                    : detail.Instructions;
                foreach (var line in text.Split('\n'))
                    sb.AppendLine($"  {line}");
            }
        }

        return sb.ToString().TrimEnd();
    }
}
