namespace CircleAI.Samples.It;

/// <summary>What being wrong about this would cost.</summary>
/// <remarks>
/// DECLARED BY THE FEATURE, NOT GUESSED BY THE DISPATCHER. Something has to
/// decide whether a misheard sentence is allowed to happen silently, and the
/// only thing that knows is the feature itself - a dispatcher inferring it from
/// a name would be wrong exactly when it mattered.
/// <para>
/// A router that does the wrong thing confidently is worse than a menu. This is
/// what keeps that from being possible.
/// </para>
/// </remarks>
public enum Cost
{
    /// <summary>Opens something. Undone by going back.</summary>
    Free,

    /// <summary>Writes something on this phone. Undone by deleting it.</summary>
    Draft,

    /// <summary>
    /// Leaves the phone, spends money, or is seen by somebody else.
    /// </summary>
    /// <remarks>
    /// ALWAYS CONFIRMED OUT LOUD FIRST, whatever the transcript said. "Anita
    /// Slation" is what Whisper made of "I need translation" on this very phone;
    /// a sentence that mangled must never be one step from sending anything.
    /// </remarks>
    Costly,
}

/// <summary>What somebody asked for, and anything they said with it.</summary>
/// <param name="Heard">The transcript, as it arrived. Never cleaned up.</param>
/// <param name="Language">What they were speaking, when the turn could tell.</param>
public sealed record Ask(string Heard, string? Language = null);

/// <summary>What happened, in words the circle can say back.</summary>
/// <param name="Done">False when it could not, and Say explains.</param>
/// <param name="Say">One line, spoken and shown. Never a stack trace.</param>
/// <param name="Route">Where to go afterwards, or null to stay put.</param>
public sealed record Did(bool Done, string Say, string? Route = null);

/// <summary>One thing this app can be asked to do.</summary>
/// <remarks>
/// SERVICES IS FOR BROWSING; THE CIRCLE IS FOR DOING. A grid works at twelve
/// features and fails at two hundred - you can only find what you can already
/// name and picture - so the catalogue stays for looking around and asking
/// becomes how anything is actually used.
///
/// <para>
/// EACH FEATURE OWNS ITS OWN PHRASES. The first version of this was one table
/// that had to know about every screen, which is the same one-owner-for-
/// everything that produced two MarkState fields, two keyword files and two
/// hard-coded language pairs in this codebase. At two hundred entries a central
/// table is both unmaintainable and WORSE - more entries means more collisions
/// and more hijacked questions. Nothing central knows them all; the registry
/// only collects.
/// </para>
/// <para>
/// AND IT DOES THE THING, rather than opening the screen that does the thing.
/// Navigating is still making somebody do the work: "I need translation" landing
/// on the interpreter, where two languages must then be set, is an improvement
/// on a menu and still a lobby. Where a capability can act on what it was told,
/// it should.
/// </para>
/// </remarks>
public interface ICapability
{
    /// <summary>Stable id, for settings and logs. Never shown to anybody.</summary>
    string Id { get; }

    /// <summary>What a person calls this. "Your CV", not "CareerInterviewHost".</summary>
    string Title { get; }

    /// <summary>
    /// Things somebody might say to mean this, in their words.
    /// </summary>
    /// <remarks>
    /// DISTINCTIVE ONES ONLY. A phrase common enough to turn up mid-question
    /// will hijack it, and a hijacked question is worse than a missed command -
    /// the miss costs the answer they would have got anyway, the hijack throws
    /// them off the screen they were using. A capability that cannot be named
    /// distinctively gets no phrases and stays browse-only, deliberately.
    /// </remarks>
    IReadOnlyList<string> Phrases { get; }

    /// <summary>What being wrong about this costs.</summary>
    Cost Cost { get; }

    /// <summary>
    /// Whether this recognises the sentence by its own SHAPE, beyond its phrases.
    /// </summary>
    /// <remarks>
    /// A SECOND DOOR, FOR THE CASES A WORD LIST CANNOT DESCRIBE. The registry's
    /// ordinary path asks whether a sentence contains one of this capability's
    /// words and sounds like an instruction. That test is right, and it is why
    /// "how do you say hello in isiZulu" is refused: seven words, no asking
    /// phrase, so it is a question about a subject rather than a command.
    /// <para>
    /// But it IS a job - it names its own text and its own target language, and
    /// the only reason to refuse it was that acting on it meant NAVIGATING
    /// somewhere, which is not an answer to a question. A capability that can
    /// answer it should be able to say so, and only the capability knows.
    /// </para>
    /// <para>
    /// Default false: nothing claims a sentence it cannot describe, so adding
    /// this changed the behaviour of nothing that existed.
    /// </para>
    /// </remarks>
    /// <param name="normalised">
    /// The sentence, already lower-cased and stripped of punctuation by
    /// <see cref="VoiceDestinations.Normalise"/> - so an implementation never
    /// has to think about what a transcriber does with full stops.
    /// </param>
    bool Claims(string normalised) => false;

    /// <summary>
    /// Whether this can actually be done right now, and why not.
    /// </summary>
    /// <remarks>
    /// THE SAME QUESTION THE WIRING PROBE ASKS. A capability that is catalogued,
    /// downloaded and unwired is exactly how this app spent weeks able to
    /// translate and unable to speak. Offering something that cannot run is the
    /// broken promise; saying so is the feature.
    /// </remarks>
    Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default);

    /// <summary>Do it.</summary>
    /// <remarks>
    /// Returning a Route instead of acting is legitimate - some things genuinely
    /// are a screen - but it is the weaker answer and should be the exception.
    /// </remarks>
    Task<Did> DoAsync(Ask ask, CancellationToken ct = default);
}
