namespace CircleAI.Samples.It;

/// <summary>A capability and how well it matched what was said.</summary>
/// <param name="Capability">What would be done.</param>
/// <param name="Score">Longest matched phrase, in characters. Higher is surer.</param>
/// <param name="Matched">The phrase that matched, for the log.</param>
public sealed record Candidate(ICapability Capability, int Score, string Matched);

/// <summary>Everything this build can be asked to do, and what matches a sentence.</summary>
/// <remarks>
/// IT ONLY COLLECTS. The knowledge lives in the capabilities - their own words,
/// their own cost, their own readiness - because a registry that knew about all
/// two hundred would be the central table this exists to replace.
/// <para>
/// The matching rules are the ones measured on the phone: whole words only, a
/// length floor so a common word cannot hijack a question, and an instruction
/// test so a long sentence ABOUT something is not a request to do it. They live
/// here rather than in each capability because they are a property of listening,
/// not of any one feature.
/// </para>
/// </remarks>
/// <remarks>
/// NOT the existing <see cref="Capabilities"/>, which is the browse catalogue
/// Services renders - titles, icons and openers. That one stays: a grid is a
/// fine way to look around. This is the doing side, and the two will want to
/// converge once every browsable thing is also askable.
/// </remarks>
public sealed class CapabilityRegistry
{
    private readonly IReadOnlyList<ICapability> _all;

    public CapabilityRegistry(IEnumerable<ICapability> all) => _all = all.ToList();

    /// <summary>Everything this build can be asked to do.</summary>
    /// <remarks>
    /// THE ONE PLACE THAT KNOWS WHICH CAPABILITIES EXIST, which is a different
    /// job from knowing what any of them can do - that stays in the capability.
    /// <para>
    /// A DOING CAPABILITY REPLACES THE NAVIGATE ONE FOR ITS ROUTE. Otherwise the
    /// words that mean "translate" would be claimed by two entries, they would
    /// score identically, and <see cref="Best"/> would correctly refuse to
    /// choose - so adding the capability that actually translates would have
    /// stopped translation being reachable at all. Two owners for one fact, in
    /// its most embarrassing form.
    /// </para>
    /// </remarks>
    /// <param name="brain">
    /// What the doing capabilities work with. Null builds a browse-and-navigate
    /// registry, which is the honest shape for a head with no model on it.
    /// </param>
    /// <param name="settings">
    /// What the app remembers, for the capabilities that change it. Null leaves
    /// mode switching to the Settings screen.
    /// </param>
    public static CapabilityRegistry For(IBrain? brain, ISettings? settings = null)
    {
        var doing = new List<ICapability>();
        var replaced = new HashSet<string>(StringComparer.Ordinal);

        if (brain is not null)
        {
            doing.Add(new TranslateCapability(brain));
            replaced.Add("translate");
        }

        if (settings is not null)
        {
            // Both directions. A switch you can only throw one way is a trap,
            // and "stop translating" has to work by voice for the same reason
            // starting had to: the phone is being held up between two people.
            doing.Add(new SwitchModeCapability(settings, AppMode.Translator));
            doing.Add(new SwitchModeCapability(settings, AppMode.Assistant));
        }

        var navigating = VoiceDestinations.All
            .Where(d => !replaced.Contains(d.Route))
            .Select(d => (ICapability)new NavigateCapability(d));

        return new CapabilityRegistry(doing.Concat(navigating));
    }

    /// <summary>Everything, for Services to browse.</summary>
    public IReadOnlyList<ICapability> All => _all;

    /// <summary>
    /// What this sentence is asking for, surest first, or nothing.
    /// </summary>
    /// <remarks>
    /// NOTHING IS THE COMMON ANSWER AND THAT IS CORRECT. An unmatched turn costs
    /// the answer somebody would have got anyway; a wrong match throws them off
    /// what they were doing. The bar is set for that asymmetry, not for recall.
    /// </remarks>
    public IReadOnlyList<Candidate> Match(string? heard)
    {
        if (string.IsNullOrWhiteSpace(heard)) return [];

        var text = VoiceDestinations.Normalise(heard);
        if (text.Length == 0) return [];

        // A CLAIM BEATS A WORD MATCH, AND SKIPS THE INSTRUCTION TEST.
        //
        // Claiming means the capability recognised the whole shape of the
        // sentence - "how do you say X in Y" names its text AND its target
        // language - which is stronger evidence than having found one of its
        // words somewhere in a phrase. It also has to skip the instruction test,
        // because that test exists to stop a QUESTION being turned into a
        // navigation, and a capability that answers the question is not doing
        // that.
        var claimed = _all.Where(c => c.Claims(text)).ToList();
        if (claimed.Count > 0)
            return claimed
                .Select(c => new Candidate(c, text.Length, text))
                .ToList();

        if (!VoiceDestinations.SoundsLikeAnInstruction(text)) return [];

        return _all
            .SelectMany(c => c.Phrases.Select(p => (Cap: c, Phrase: p)))
            .Where(x => x.Phrase.Length >= 4 && VoiceDestinations.HasWord(text, x.Phrase))
            .GroupBy(x => x.Cap.Id)
            .Select(g =>
            {
                var best = g.OrderByDescending(x => x.Phrase.Length).First();
                return new Candidate(best.Cap, best.Phrase.Length, best.Phrase);
            })
            .OrderByDescending(c => c.Score)
            .ToList();
    }

    /// <summary>
    /// The one thing to do, when the sentence is unambiguous enough to act on.
    /// </summary>
    /// <remarks>
    /// TWO CAPABILITIES MATCHING EQUALLY WELL IS NOT A DECISION TO MAKE QUIETLY.
    /// Picking one on a coin toss is how a dispatcher does the wrong thing
    /// confidently, so a tie returns nothing and the turn answers normally - the
    /// person can say which they meant, which is cheaper than being wrong.
    /// </remarks>
    public ICapability? Best(string? heard)
    {
        var found = Match(heard);
        if (found.Count == 0) return null;
        if (found.Count > 1 && found[0].Score == found[1].Score) return null;
        return found[0].Capability;
    }
}
