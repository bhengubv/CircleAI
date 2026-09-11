namespace CircleAI.Samples.It;

/// <summary>Changing what the app is for, by saying so.</summary>
/// <remarks>
/// FLIPPING MODE TOOK FOUR STEPS THROUGH SETTINGS, and it is the switch that
/// changes what the whole app IS - assistant or interpreter. Somebody sitting
/// across from a person they cannot understand has to open Settings, find the
/// mode, choose it and come back, which is a menu answer to a conversational
/// problem. Saying "switch to translator" is one step and needs no screen.
///
/// <para>
/// DRAFT, NOT FREE. Every other capability today either opens a screen or shows
/// an answer; this one WRITES something that persists and changes how the next
/// turn behaves. That is exactly the distinction <see cref="Cost"/> exists to
/// carry, and the first capability to earn a cost above Free is worth pointing
/// at: the transcript that reaches it may be wrong, and the cost says how much
/// that matters. It is undone by saying the other one, which is why it is Draft
/// and not Costly.
/// </para>
/// <para>
/// IT SAYS WHICH MODE IT IS NOW, always. A silent switch is indistinguishable
/// from a misheard sentence, and the failure mode of this app has never been
/// doing the wrong thing loudly - it has been doing nothing quietly.
/// </para>
/// </remarks>
public sealed class SwitchModeCapability : ICapability
{
    private readonly ISettings _settings;
    private readonly AppMode _mode;

    public SwitchModeCapability(ISettings settings, AppMode mode)
    {
        _settings = settings;
        _mode = mode;
    }

    public string Id => "mode:" + _mode.ToString().ToLowerInvariant();

    public string Title => _mode == AppMode.Translator ? "Translator mode" : "Assistant mode";

    /// <summary>
    /// The words for this mode, and deliberately not the bare ones.
    /// </summary>
    /// <remarks>
    /// "translator" alone is not here: it is one letter from "translation",
    /// which means the SCREEN, and a sentence that changes what the app is
    /// should not be one syllable from a sentence that opens a page. Every
    /// phrase carries the word "mode" or is unambiguous on its own.
    /// </remarks>
    public IReadOnlyList<string> Phrases => _mode == AppMode.Translator
        ? ["translator mode", "translation mode", "interpreter mode", "start interpreting"]
        : ["assistant mode", "answer mode", "stop interpreting", "stop translating"];

    /// <summary>It writes a setting that outlives the turn.</summary>
    public Cost Cost => Cost.Draft;

    /// <summary>A mode is always available; it is the app's own state.</summary>
    public Task<(bool Ready, string Why)> ReadyAsync(CancellationToken ct = default)
        => Task.FromResult((true, string.Empty));

    public async Task<Did> DoAsync(Ask ask, CancellationToken ct = default)
    {
        try
        {
            var current = await _settings.LoadAsync(ct).ConfigureAwait(false);

            // ALREADY THERE IS NOT A FAILURE, and saying so is better than
            // silently doing nothing - which is what "it did not hear me" feels
            // like from the other side of a room.
            if (current.Mode == _mode)
                return new Did(true, $"Already in {Title.ToLowerInvariant()}.");

            await _settings.SaveAsync(current with { Mode = _mode }, ct).ConfigureAwait(false);

            return new Did(true, _mode == AppMode.Translator
                ? "Translator mode. Say something and I will translate it."
                : "Assistant mode. Ask me anything.");
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new Did(false, $"I could not change the mode just now. ({ex.GetType().Name})");
        }
    }
}
