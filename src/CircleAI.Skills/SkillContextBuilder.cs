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

        var matches = await _store.SearchAsync(userQuery, cancellationToken).ConfigureAwait(false);

        // NO-MATCH => COMPACT MODE, not "dump everything".
        //
        // This used to fall back to the full skill list WITH full Instructions on
        // every unmatched turn. Measured on a Huawei P30 Lite (2026-07-21): with
        // a capability-manifest store behind it, that pushed the on-device sweep
        // from 4m33s to >20m — a 0.6B with a 4096-token window spends its whole
        // budget re-reading skills the turn never asked about. Compact mode gives
        // the model an awareness line without the prompt tax.
        var matched = matches.Count > 0;
        IReadOnlyList<SkillSummary> candidates;
        if (matched)
        {
            candidates = matches.Take(_maxSkills).ToList();
        }
        else
        {
            var all = await _store.ListAsync(cancellationToken).ConfigureAwait(false);
            if (all.Count == 0) return string.Empty;

            var names = all.Take(_maxSkills).Select(s => s.Id);
            return "## Available Skills (names only; ask to expand)\n" + string.Join(", ", names) + "\n";
        }

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
