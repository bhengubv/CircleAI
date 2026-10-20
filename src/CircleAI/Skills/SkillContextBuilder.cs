using System.Text;
using System.Text.RegularExpressions;

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

        var terms = CircleAI.Core.SearchTerms.Significant(userQuery);
        var subject = SubjectOf(terms, matches);
        var candidates = AboutTheSubject(terms, subject, HeadFirst(terms, subject, matches).ToList()).Take(_maxSkills).ToList();

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
        // '\n', NOT AppendLine: the budget below counts the characters it adds, and a
        // two-character Windows newline put the block over its ceiling on the desktop.
        const string Header = "## What may help";
        var sb = new StringBuilder(Header).Append('\n');
        var entries = 0;

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

            // THE ID, NOT THE DESCRIPTION, heads the entry. The description is what the skill
            // is ABOUT, not a fact for the question: "Eating well on a small budget..., and
            // feeding babies and small children safely - with the danger signs that mean see
            // the clinic" came back on 2026-10-20 as a meal plan "while ensuring safety". The
            // id stays because asking by it is how the body is looked back up. A skill with
            // no body has only its description to give, so it keeps it - within the budget.
            var hasBody = !string.IsNullOrWhiteSpace(detail.Instructions);
            var room = _maxChars - sb.Length - detail.Id.Length - 2;
            var part = hasBody ? RelevantPart(detail.Instructions, terms, subject, Math.Max(0, room)) : string.Empty;
            var described = hasBody ? string.Empty : Within(Plain(detail.Description).Trim(), room - 3);

            // A HEADING WITH NOTHING UNDER IT IS NOISE: the second skill's line arrived alone
            // once the first had used the budget, and it is prefill that informs nothing. The
            // NEXT skill may still have something that fits, so this one is passed over.
            if (part.Length == 0 && described.Length == 0 && entries > 0) continue;

            var heading = described.Length > 0 ? $"{detail.Id} — {described}" : detail.Id;
            sb.Append('\n').Append(heading).Append('\n');
            if (part.Length > 0) sb.Append(part).Append('\n');
            entries++;
        }

        return entries == 0 ? string.Empty : sb.ToString().TrimEnd();
    }

    /// <summary>The parts of a skill's body that are about the question, as plain words, within <paramref name="budget"/>.</summary>
    /// <remarks>
    /// THE TOP OF A SKILL IS NOT THE ANSWER. Measured on the P30 on 2026-10-20 by dumping the
    /// exact text the model was handed: "How do I plant maize?" got the first 600 characters
    /// of "Growing food at home" - spinach, compost, watering - while its "When to plant"
    /// (maize goes in with the first spring rains) and "Maize (mielies)" sections started at
    /// characters 458 and 727, past the cut. The model answered maize from notes about
    /// spinach. So the body is read as sections; the ones mentioning the question's words go
    /// in first, then the rest while room is left, all in the skill's own order; markdown
    /// becomes plain words, and a section that does not fit ends on a whole sentence.
    /// </remarks>
    internal static string RelevantPart(string instructions, IReadOnlyList<string> terms, string? subject, int budget)
    {
        if (budget <= 0 || string.IsNullOrWhiteSpace(instructions)) return string.Empty;

        // A CAPABILITY'S STATUS TRAVELS WITH IT. CapabilityManifestSkillStore leads every
        // body with "Status: planned" and, for anything not shipping, "Do NOT claim you can do
        // this". Cut down to the sentences naming the subject that paragraph was gone, and
        // once the heading was only the id (2026-10-20) nothing else carried "[planned]".
        var source = instructions.Replace("\r", string.Empty, StringComparison.Ordinal);
        if (source.StartsWith(CapabilityManifestSkillStore.StatusLead, StringComparison.Ordinal))
        {
            var end = source.IndexOf("\n\n", StringComparison.Ordinal);
            var status = Within(Plain(end < 0 ? source : source[..end]).Replace('\n', ' ').Trim(), budget);
            if (end < 0 || status.Length == 0) return status;
            var rest = RelevantPart(source[(end + 2)..], terms, subject, budget - status.Length - 1);
            return rest.Length == 0 ? status : status + "\n" + rest;
        }

        var sections = Sections(source);

        // FIRST, ONLY WHAT IS ABOUT THE QUESTION'S SUBJECT. With whole sections the maize
        // answer on 2026-10-20 read "plant in late spring OR EARLY FALL": the "When to plant"
        // section also says the Western Cape plants in autumn, and the 0.8B merged the two.
        // The subject is the question's word the skills are about ("maize", "meals" - see
        // SubjectOf); a section headed by it goes in whole, elsewhere only the sentences
        // naming it, each with the sentence before it so "go in then" still says when.
        // Shorter notes are also less to read: every token is 37 ms on the P30.
        if (subject is not null)
        {
            var about = new List<string>();
            foreach (var (heading, body) in sections)
            {
                if (heading is not null && MentionsWord(heading, subject)) { about.Add(heading + ": " + body); continue; }

                var sentences = SentencesOf(body);
                var keep = new SortedSet<int>();
                for (var i = 0; i < sentences.Count; i++)
                    if (MentionsWord(sentences[i], subject)) { if (i > 0) keep.Add(i - 1); keep.Add(i); }
                if (keep.Count > 0)
                    about.Add((heading is null ? string.Empty : heading + ": ") + string.Join(" ", keep.Select(i => sentences[i])));
            }
            if (about.Count > 0) return Fit(about.Select((text, index) => (index, text)).ToList(), budget);
        }

        // OTHERWISE, WHOLE SECTIONS: those mentioning any of the question's words first,
        // then the rest while room is left, all in the skill's own order.
        var order = sections
            .Select((s, index) => (text: s.Heading is null ? s.Body : s.Heading + ": " + s.Body, index))
            .Select(s => (s.text, s.index, score: terms.Count(t => MentionsWord(s.text, t))))
            .OrderByDescending(s => s.score > 0 ? 1 : 0)
            .ThenByDescending(s => s.score)
            .ThenBy(s => s.index)
            .Select(s => (s.index, s.text))
            .ToList();
        return Fit(order, budget);
    }

    /// <summary>The pieces, in the order given, while they fit; then put back in the skill's order.</summary>
    private static string Fit(List<(int Index, string Text)> pieces, int budget)
    {
        var kept = new List<(int Index, string Text)>();
        var left = budget;
        foreach (var (index, text) in pieces)
        {
            if (left <= 0) break;
            var piece = text.Length <= left ? text : WholeSentencesWithin(text, left);
            if (piece.Length == 0) continue;
            kept.Add((index, piece));
            left -= piece.Length + 1;
        }
        return string.Join("\n", kept.OrderBy(k => k.Index).Select(k => k.Text));
    }

    /// <summary>The sentences of a plain-text passage.</summary>
    /// <remarks>The "2." of a list is not the end of a sentence; "...school. 2. Apply..." is two, not three.</remarks>
    private static List<string> SentencesOf(string text) =>
        Regex.Split(text.Trim(), @"(?<=[.!?])(?<!(?:^|\s)\d{1,2}\.)\s+(?=[A-Z0-9])")
             .Select(s => s.Trim())
             .Where(s => s.Length > 0)
             .ToList();

    /// <summary><paramref name="text"/> whole when it fits in <paramref name="room"/>, else as many whole sentences as do.</summary>
    private static string Within(string text, int room) =>
        room <= 0 ? string.Empty : text.Length <= room ? text : WholeSentencesWithin(text, room);

    /// <summary>A skill body as plain-text sections: an optional heading and what it says.</summary>
    private static List<(string? Heading, string Body)> Sections(string instructions)
    {
        var sections = new List<(string? Heading, string Body)>();
        string? heading = null;
        var words = new StringBuilder();

        void Close()
        {
            // A heading with nothing under it says nothing, so it is not a section.
            var said = words.ToString().Trim();
            if (said.Length > 0) sections.Add((heading, said));
            words.Clear();
        }

        var lines = instructions.Replace("\r", string.Empty).Split('\n');
        for (var n = 0; n < lines.Length; n++)
        {
            // AN ASIDE IN ITALICS IS A PROMPT TO THE READER, NOT A FACT: "*Tell me your budget
            // or who you're feeding and I'll help you plan cheap, healthy meals.*" put "plan"
            // and "meals" into the clinic-warning section, so on 2026-10-20 "Help me plan meals"
            // was handed the danger signs instead of the meals. Most of the consumer skills end
            // on one that runs over two lines, and those went in as facts too.
            var aside = AsideLength(lines, n);
            if (aside > 0) { n += aside - 1; continue; }

            var line = Plain(lines[n]).Trim();
            if (line.StartsWith('#'))
            {
                Close();
                heading = line.TrimStart('#').Trim();
                continue;
            }
            if (line.Length == 0) continue;
            if (line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal)) line = line[2..];
            // "1. Register the birth": the number is the list's, not a sentence ending in "1."
            line = Regex.Replace(line, @"^\d{1,2}[.)]\s+", string.Empty);
            if (words.Length > 0) words.Append(' ');
            words.Append(line);
        }
        Close();
        return sections;
    }

    /// <summary>How many lines from <paramref name="lines"/>[<paramref name="n"/>] make one italic aside; 0 when it opens none.</summary>
    /// <remarks>
    /// An aside opens with a single '*' and closes on the first line ending in one, within a
    /// few lines and the same paragraph. "*Note:* keep the slip" closes its own emphasis, and
    /// "* item" is a list, so neither is one.
    /// </remarks>
    private static int AsideLength(string[] lines, int n)
    {
        var first = lines[n].Trim();
        if (first.Length < 3 || first[0] != '*' || first[1] is '*' or ' ') return 0;
        if (first[^1] == '*' && first[^2] != '*') return 1;
        if (first.IndexOf('*', 1) >= 0) return 0;

        for (var k = n + 1; k < lines.Length && k <= n + 3; k++)
        {
            var line = lines[k].Trim();
            if (line.Length == 0 || line.StartsWith('#')) return 0;
            var star = line.IndexOf('*');
            if (star < 0) continue;
            return star == line.Length - 1 && (line.Length < 2 || line[^2] != '*') ? k - n + 1 : 0;
        }
        return 0;
    }

    /// <summary>As much of <paramref name="text"/> as fits in <paramref name="room"/>, ending on a whole sentence.</summary>
    /// <remarks>Never on the point in "3.5" or the "1." of a list: "sort out: 1." was a line once.</remarks>
    private static string WholeSentencesWithin(string text, int room)
    {
        if (room <= 0) return string.Empty;
        for (var end = Math.Min(room, text.Length) - 1; end > 0; end--)
        {
            if (text[end] is not ('.' or '!' or '?')) continue;
            if (end + 1 < text.Length && !char.IsWhiteSpace(text[end + 1])) continue;
            if (text[end] == '.' && IsListNumber(text, end)) continue;
            return text[..(end + 1)];
        }
        return string.Empty;
    }

    /// <summary>Whether the '.' at <paramref name="dot"/> ends a list number: one or two digits after a space or the start.</summary>
    private static bool IsListNumber(string text, int dot)
    {
        var i = dot - 1;
        var digits = 0;
        while (i >= 0 && char.IsDigit(text[i])) { i--; digits++; }
        return digits is 1 or 2 && (i < 0 || char.IsWhiteSpace(text[i]));
    }

    /// <summary>Whether <paramref name="text"/> carries <paramref name="term"/>, allowing an ending ("plant", "planting").</summary>
    private static bool MentionsWord(string text, string term)
    {
        foreach (var word in text.Split(WordBreaks, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.Equals(word, term, StringComparison.OrdinalIgnoreCase)) return true;
            if (word.Length < 4 || term.Length < 4 || Math.Abs(word.Length - term.Length) > 3) continue;
            if (word.StartsWith(term, StringComparison.OrdinalIgnoreCase) || term.StartsWith(word, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary><paramref name="text"/> without the markdown emphasis a skill was written in.</summary>
    /// <remarks>
    /// THE SKILL BODIES ARE MARKDOWN - "your **clinic checks your..." in the meal skill -
    /// and what is put in front of a model is what comes back out of it. A spoken answer
    /// must never carry "**", and the asterisks are prefill that buys nothing.
    /// </remarks>
    private static string Plain(string? text) =>
        (text ?? string.Empty).Replace("**", string.Empty, StringComparison.Ordinal)
                              .Replace("__", string.Empty, StringComparison.Ordinal)
                              .Replace("*", string.Empty, StringComparison.Ordinal);

    /// <summary>The matches, with those about the question's subject (<see cref="SubjectOf"/>) first.</summary>
    /// <remarks>
    /// THE SUBJECT, NOT THE STORE'S RANK, DECIDES WHAT LEADS. "Help me plan meals" reduces
    /// to {plan, meals}; the store ranked "Plan your money this month" first, because
    /// "plan" is in its name and its tags, and on the P30 on 2026-10-17 the 0.8B answered a
    /// meal question with a budget - 22 sentences in 43 s. "Feeding your family well" was
    /// in the same hits, matched on "meals". Only the 600-character budget's first skill
    /// reaches a phone, so the order is the answer.
    /// <para>
    /// A STABLE PARTITION, NOT A RE-RANK: when no match mentions the subject ("deal with a
    /// government office" - no skill mentions an office), the store's order stands.
    /// </para>
    /// </remarks>
    internal static IEnumerable<SkillSummary> HeadFirst(IReadOnlyList<string> terms, string? subject, IReadOnlyList<SkillSummary> matches)
    {
        if (terms.Count < 2 || matches.Count < 2 || subject is null) return matches;

        var about = matches.Where(m => Mentions(m, subject)).ToList();
        return about.Count == 0 ? matches : about.Concat(matches.Where(m => !about.Contains(m)));
    }

    /// <summary>The question's word the matched skills are most particularly about.</summary>
    /// <remarks>
    /// THE LAST WORD IS USUALLY THE SUBJECT, NOT ALWAYS. "How do I plant maize seeds?" ends
    /// on a word no skill mentions, and "Help me plan meals today" on one that says when, not
    /// what; taking the last word, both fell back to whole sections - the Western Cape's
    /// autumn line for maize, a budget for meals (review, 2026-10-21). So the subject is the
    /// word the FEWEST matched skills mention, and at least one: "meals" (one skill) over
    /// "plan" (two) and "today" (none). On a tie the later word wins, which is the old rule
    /// wherever the old rule was right.
    /// </remarks>
    internal static string? SubjectOf(IReadOnlyList<string> terms, IReadOnlyList<SkillSummary> matches)
    {
        if (terms.Count == 0) return null;

        string? subject = null;
        var fewest = int.MaxValue;
        for (var i = terms.Count - 1; i >= 0; i--)
        {
            var mentioning = matches.Count(m => Mentions(m, terms[i]));
            if (mentioning > 0 && mentioning < fewest) { subject = terms[i]; fewest = mentioning; }
        }
        return subject ?? terms[^1];
    }

    /// <summary>The ordered matches, less those not about the subject once the first one is.</summary>
    /// <remarks>
    /// A SECOND SKILL MUST BE ABOUT THE SUBJECT TOO. "Help me plan meals" also matches "Plan
    /// your money this month", on "plan" alone. While the meal skill's heading carried its
    /// whole description the 600 characters ran out before the budget skill; on 2026-10-20
    /// the heading shrank to the id and the budget went in under the meal plan. Room left
    /// over is not a reason to hand the model something the question is not about.
    /// </remarks>
    internal static List<SkillSummary> AboutTheSubject(IReadOnlyList<string> terms, string? subject, List<SkillSummary> ordered)
    {
        if (terms.Count < 2 || ordered.Count < 2 || subject is null) return ordered;

        return Mentions(ordered[0], subject)
            ? ordered.Where((m, i) => i == 0 || Mentions(m, subject)).ToList()
            : ordered;
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
