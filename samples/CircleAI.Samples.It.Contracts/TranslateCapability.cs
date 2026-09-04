namespace CircleAI.Samples.It;

/// <summary>
/// Translating what was said, rather than opening the screen that translates.
/// </summary>
/// <remarks>
/// THE FIRST CAPABILITY THAT ACTUALLY DOES ITS JOB, and the reason the interface
/// exists. Everything else reachable by voice today is a
/// <see cref="NavigateCapability"/> - it opens a screen and leaves the work to
/// the person, which is better than a menu and is still a lobby.
///
/// <para>
/// It answers two different sentences, and the difference matters:
/// </para>
/// <para>
/// "how do you say hello in isiZulu" carries everything needed - the words and
/// the target - so it is ANSWERED. Nothing opens, nothing is set up, the reply
/// is the translation. This is the sentence the app has always been worst at:
/// the general model would produce a paragraph about isiZulu, which is a
/// confident non-answer.
/// </para>
/// <para>
/// "I need translation" carries no text and no target, so there is genuinely
/// nothing to do yet and it opens the interpreter. Returning a route here is the
/// weaker answer the interface warns about, and it is the correct one - guessing
/// what somebody wanted translated would be worse than asking.
/// </para>
/// <para>
/// COST IS FREE BECAUSE NOTHING LEAVES THE PHONE. The model is on the device and
/// the answer is shown and spoken. If a translation ever routes through a
/// network service this must become <see cref="Cost.Costly"/> on the same day -
/// the cost is a claim about where the words go, not about how it feels.
/// </para>
/// </remarks>
public sealed class TranslateCapability : ICapability
{
    private readonly IBrain _brain;
    private readonly VoiceDestination _screen;

    /// <param name="brain">What does the translating. On-device.</param>
    public TranslateCapability(IBrain brain)
    {
        _brain = brain;

        // The screen's own entry, so the words that mean "translate" and the
        // route it lives at are read from the one table that already owns them.
        _screen = VoiceDestinations.All.First(d => d.Route == "translate");
    }

    public string Id => "translate";

    public string Title => _screen.Title;

    public IReadOnlyList<string> Phrases => _screen.Words;

    public Cost Cost => Cost.Free;

    /// <summary>Recognises a full request even when it reads as a question.</summary>
    public bool Claims(string normalised) => TranslationRequest.Parse(normalised) is not null;

    /// <summary>
    /// Whether there is a model to translate WITH, and what to say when there is not.
    /// </summary>
    /// <remarks>
    /// The whole point of asking: this app spent weeks offering translation with
    /// no phonemizer wired, so it could translate and could not speak, and
    /// nothing on any screen said so. Offering something that cannot run is the
    /// broken promise; this is where it gets refused honestly instead.
    /// </remarks>
    public async Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default)
    {
        var state = await _brain.StateAsync(ct).ConfigureAwait(false);
        return state.Ready
            ? (true, string.Empty)
            : (false, string.IsNullOrWhiteSpace(state.Detail)
                ? "There is no model on this phone to translate with yet."
                : state.Detail);
    }

    public async Task<Did> DoAsync(Ask ask, CancellationToken ct = default)
    {
        var request = TranslationRequest.Parse(ask.Heard);

        // Nothing to translate yet - that is the screen's job, not a guess.
        if (request is null)
            return new Did(true, $"Opening {_screen.Title}", _screen.Route);

        var (ready, why) = await ReadyAsync(ct).ConfigureAwait(false);
        if (!ready) return new Did(false, why);

        try
        {
            // ASK FOR THE WORDS AND NOTHING ELSE. A model told to "translate
            // this" cheerfully returns "Sure! In Zulu you would say ..." and the
            // phone then SPEAKS that preamble in a Zulu voice, which is both
            // wrong and slower than the answer.
            var answer = await _brain.AskAsync(
                $"Translate into {request.Language.Name}. Reply with the translation only, "
                + $"no explanation and no quotation marks.\n\n{request.Text}",
                token: null,
                ct).ConfigureAwait(false);

            answer = answer?.Trim() ?? string.Empty;

            return answer.Length == 0
                ? new Did(false, "That came back empty. Try saying it again.")
                : new Did(true, answer);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            // A SENTENCE A PERSON CAN ACT ON, NEVER A STACK TRACE. Did.Say is
            // spoken out loud - the point of asking by voice is that the phone
            // is across the room - and an exception type read aloud is noise.
            return new Did(false,
                $"I could not translate that into {request.Language.Name} just now. "
                + $"({ex.GetType().Name})");
        }
    }
}
