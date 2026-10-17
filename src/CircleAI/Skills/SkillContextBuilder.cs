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
    public SkillContextBuilder(ISkillStore store, int maxSkills = 5, int maxChars = 0)
    {
        ArgumentNullException.ThrowIfNull(store);
        if (maxSkills < 1) throw new ArgumentOutOfRangeException(nameof(maxSkills), "Must be at least 1.");
        if (maxChars != 0 && maxChars < 100) throw new ArgumentOutOfRangeException(nameof(maxChars), "Must be at least 100.");
        _store = store;
        _maxSkills = maxSkills;
        _maxChars = maxChars > 0 ? maxChars : DeviceBudget();
    }

    /// <summary>The skills block's size for this device, when the caller names none.</summary>
    /// <remarks>
    /// EVERY CHARACTER IS PREFILL. Measured on the P30 on 2026-10-17 after the template
    /// fix: a capability-tile question carried 561 prompt tokens and spent 15.3 s
    /// reading them, most of it this block at its 1,500-character ceiling. 600 on a
    /// phone of 4.5 GB or less keeps the most relevant skill and its first lines;
    /// larger devices keep the old ceiling.
    /// </remarks>
    public static int DeviceBudget()
    {
        try
        {
            var total = CircleAI.Core.DeviceProbe.Snapshot().RamTotalBytes;
            return total > 0 && total <= 4.5 * 1024 * 1024 * 1024 ? 600 : 1500;
        }
        catch { return 1500; }
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

        var candidates = HeadFirst(userQuery, matches).Take(_maxSkills).ToList();

        // NEVER THE QUESTION ITSELF: this line used to print it whole, and a composed
        // question carries a person's remembered facts. Its length is enough to debug.
        Console.WriteLine($"CIRCLEAI-SKILLS q={userQuery.Length} chars hits={matches.Count} budget={_maxChars} "
                        + $"chosen={string.Join(",", candidates.Select(c => c.Id))}");

        // Load full detail so we can include instructions.
        //
        // NOT "AVAILABLE SKILLS", AND NO MARKDOWN. Under that heading the P30's 0.8B
        // read a skill as something it could do - "I can provide basic animal care" -
        // and "**" is a thing a spoken answer must never contain. The id stays, plain:
        // asking by it is how the body is looked back up.
        var sb = new StringBuilder();
        sb.AppendLine("## What may help");

        foreach (var summary in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Hard character budget. On a 4096-token window a few verbose skills
            // can crowd out the actual conversation, which is how this block
            // silently degraded answer quality AND speed on-device. Nothing is said
            // about what was left out: every character here is prefill.
            if (sb.Length >= _maxChars) break;

            var detail = await _store.GetAsync(summary.Id, cancellationToken).ConfigureAwait(false);
            if (detail is null) continue;

            sb.AppendLine();
            sb.AppendLine($"{detail.Id} — {Plain(detail.Description)}");
            if (!string.IsNullOrWhiteSpace(detail.Instructions))
            {
                var remaining = Math.Max(0, _maxChars - sb.Length);
                var body = Plain(detail.Instructions);
                var text = body.Length > remaining
                    ? body[..remaining] + " …"
                    : body;
                foreach (var line in text.Split('\n'))
                    sb.AppendLine($"  {line}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary><paramref name="text"/> without the markdown emphasis a skill was written in.</summary>
    /// <remarks>
    /// THE SKILL BODIES ARE MARKDOWN - "your **clinic checks your..." in the meal skill -
    /// and what is put in front of a model is what comes back out of it. A spoken answer
    /// must never carry "**", and the asterisks are prefill that buys nothing.
    /// </remarks>
    private static string Plain(string? text) =>
        (text ?? string.Empty).Replace("**", string.Empty, StringComparison.Ordinal)
                              .Replace("__", string.Empty, StringComparison.Ordinal);

    /// <summary>The matches, with those about the question's last word first.</summary>
    /// <remarks>
    /// THE LAST WORD OF A REQUEST IS USUALLY WHAT IT IS ABOUT. "Help me plan meals" reduces
    /// to {plan, meals}; the store ranked "Plan your money this month" first, because
    /// "plan" is in its name and its tags, and on the P30 on 2026-10-17 the 0.8B answered a
    /// meal question with a budget - 22 sentences in 43 s. "Feeding your family well" was
    /// in the same hits, matched on "meals". Only the 600-character budget's first skill
    /// reaches a phone, so the order is the answer.
    /// <para>
    /// A STABLE PARTITION, NOT A RE-RANK: when nothing matches the last word ("deal with a
    /// government office" - no skill mentions an office), the store's order stands.
    /// </para>
    /// </remarks>
    internal static IEnumerable<SkillSummary> HeadFirst(string query, IReadOnlyList<SkillSummary> matches)
    {
        var terms = CircleAI.Core.SearchTerms.Significant(query);
        if (terms.Count < 2 || matches.Count < 2) return matches;

        var head = terms[^1];
        var about = matches.Where(m => Mentions(m, head)).ToList();
        return about.Count == 0 ? matches : about.Concat(matches.Where(m => !about.Contains(m)));
    }

    /// <summary>Whether a skill's id, name, tags or description carry <paramref name="term"/>.</summary>
    /// <remarks>
    /// "meal" finds "meals", "plant" finds "planting": one word may start the other, when
    /// both are four letters or more and differ by three at most.
    /// </remarks>
    private static bool Mentions(SkillSummary skill, string term)
    {
        var text = string.Join(' ', skill.Id, skill.Name, skill.Description, string.Join(' ', skill.Tags ?? Array.Empty<string>()));
        foreach (var word in text.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, term, StringComparison.OrdinalIgnoreCase)) return true;
            if (word.Length < 4 || term.Length < 4 || Math.Abs(word.Length - term.Length) > 3) continue;
            if (word.StartsWith(term, StringComparison.OrdinalIgnoreCase) || term.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    private static readonly char[] WordBreaks =
        [' ', '-', '_', '.', ',', ';', ':', '(', ')', '/', '\'', '"', '!', '?', '—', '–'];
}
