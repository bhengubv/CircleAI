namespace CircleAI.Samples.It;

/// <summary>A capability whose whole job is to open a screen.</summary>
/// <remarks>
/// THE WEAKEST KIND, ON PURPOSE, AND STILL WORTH HAVING. <see cref="ICapability"/>
/// says that returning a route instead of acting is legitimate but should be the
/// exception; most of the ten places a voice can reach genuinely are screens,
/// and pretending otherwise would mean inventing an action for "Settings".
///
/// <para>
/// IT TAKES ITS WORDS FROM THE DESTINATION RATHER THAN REPEATING THEM. Every
/// phrase here is already written down in <see cref="VoiceDestinations"/>, which
/// the bar and the links read too. Typing them again would give the app two
/// lists of the words that mean "translate", and this codebase has been bitten
/// by exactly that three times in a week - two MarkState fields, two keyword
/// files, two hard-coded language pairs. A capability that wraps the destination
/// cannot drift from it.
/// </para>
/// <para>
/// So the migration is not "move the table"; it is "let the table be asked a
/// second way". Anything that later learns to DO its thing replaces its own
/// entry here and nothing else changes.
/// </para>
/// </remarks>
public sealed class NavigateCapability : ICapability
{
    private readonly VoiceDestination _where;

    public NavigateCapability(VoiceDestination where) => _where = where;

    /// <summary>Prefixed, so it can never collide with a doing capability's id.</summary>
    public string Id => "go:" + _where.Route;

    public string Title => _where.Title;

    public IReadOnlyList<string> Phrases => _where.Words;

    /// <summary>Opening a screen is undone by going back.</summary>
    public Cost Cost => Cost.Free;

    /// <summary>A screen this build ships is always available.</summary>
    /// <remarks>
    /// Deliberately not a check on whether the screen WORKS. A capability
    /// reports what it knows, and "the route exists" is what this one knows;
    /// claiming more would be the broken promise ReadyAsync exists to prevent.
    /// </remarks>
    public Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default)
        => Task.FromResult((true, string.Empty));

    public Task<Did> DoAsync(Ask ask, CancellationToken ct = default)
        => Task.FromResult(new Did(true, $"Opening {_where.Title}", _where.Route));

    /// <summary>One of these for every place a voice can reach.</summary>
    public static IReadOnlyList<ICapability> ForEveryDestination() =>
        VoiceDestinations.All.Select(d => (ICapability)new NavigateCapability(d)).ToList();
}
