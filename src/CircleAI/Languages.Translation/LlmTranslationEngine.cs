using System.Runtime.CompilerServices;
using CircleAI.Inference;
using CircleAI.Languages;

namespace CircleAI.Languages.Translation;

/// <summary>
/// <see cref="ITranslationEngine"/> backed by the on-device LLM via
/// <see cref="IChatGenerator"/>. All processing is on-device — no API calls,
/// no data leaving the device.
/// </summary>
public sealed class LlmTranslationEngine : ILiveTranslator
{
    private readonly IChatGenerator _generator;
    private readonly Func<string, string> _name;

    /// <param name="generator">The on-device model.</param>
    /// <param name="name">
    /// Turns a BCP-47 tag into the language's name, for the prompt.
    /// </param>
    /// <remarks>
    /// THE ENGINE DOES NOT OWN A LANGUAGE TABLE, and the first attempt at this
    /// proved why. Reaching for KnownLanguages - the table this assembly can
    /// see - looked obviously right and quietly downgraded translation for most
    /// of the app: KnownLanguages lists TWENTY languages and the app ships
    /// SEVENTY-FIVE, so Japanese, Korean, Vietnamese, Thai, Russian and fifty
    /// others fell back to printing their raw tag at the model. Caught by a test
    /// asking for Japanese and getting "from English to ja".
    /// <para>
    /// A caller knows which languages its app actually offers; this assembly
    /// cannot. The default is there so the engine still works standalone, and it
    /// is the poorer of the two answers by construction.
    /// </para>
    /// </remarks>
    public LlmTranslationEngine(IChatGenerator generator, Func<string, string>? name = null)
    {
        _generator = generator ?? throw new ArgumentNullException(nameof(generator));
        _name = name ?? DefaultName;
    }

    /// <inheritdoc/>
    public async Task<TranslationResult> TranslateAsync(
        TranslationRequest request, CancellationToken ct = default)
    {
        var messages = new[] { new ChatMessage("user", BuildPrompt(request)) };
        var translated = await _generator
            .GenerateAsync(messages, OptionsFor(request), ct)
            .ConfigureAwait(false);

        return new TranslationResult(
            request.Text,
            translated.Trim(),
            request.SourceBcpTag,
            request.TargetBcpTag,
            0.9f,
            DateTimeOffset.UtcNow);
    }

    /// <inheritdoc/>
    public async IAsyncEnumerable<string> StreamTranslateAsync(
        TranslationRequest request,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var messages = new[] { new ChatMessage("user", BuildPrompt(request)) };
        await foreach (var token in _generator.StreamAsync(messages, OptionsFor(request), ct)
                                              .ConfigureAwait(false))
            yield return token;
    }

    /// <summary>How a translation is asked of the model.</summary>
    /// <remarks>
    /// PLAIN: the prompt below is the whole prompt, and a host that put its persona or its notes
    /// in front of it had them translated too, or the sentence answered rather than translated.
    /// NO THINKING: a translation is the words, not a problem to work - a brain that thinks first
    /// makes the person at the desk wait for nothing. ROOM FOR THE WORDS: an answer cap sized to
    /// the text, so a paragraph is not cut off at a chat answer's length, nor a word given 512.
    /// </remarks>
    internal static GenerationOptions OptionsFor(TranslationRequest request) => new()
    {
        Plain = true,
        Effort = ReasoningEffort.Off,
        MaxTokens = Math.Clamp((request.Text?.Length ?? 0) / 2 + 64, 128, 1024),
    };

    /// <inheritdoc/>
    public Task<bool> IsLanguagePairSupportedAsync(
        string sourceBcpTag, string targetBcpTag, CancellationToken ct = default)
        => Task.FromResult(true); // On-device LLM handles any pair it was trained on.

    /// <inheritdoc/>
    public async IAsyncEnumerable<ConversationTurn> StreamConversationAsync(
        IAsyncEnumerable<ConversationTurn> inputStream,
        string partyABcpTag,
        string partyBBcpTag,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var turn in inputStream.WithCancellation(ct).ConfigureAwait(false))
        {
            var targetTag = turn.SpeakerBcpTag == partyABcpTag ? partyBBcpTag : partyABcpTag;

            var req = new TranslationRequest(
                turn.OriginalText, turn.SpeakerBcpTag, targetTag,
                TranslationMode.Conversational);

            var result = await TranslateAsync(req, ct).ConfigureAwait(false);

            yield return turn with { TranslatedText = result.TranslatedText };
        }
    }

    /// <summary>The prompt handed to the on-device model.</summary>
    /// <remarks>
    /// NAMES, NOT TAGS, AND THE APP KNEW THIS BEFORE THE ENGINE DID. This asked
    /// for a translation "from en to ja", while the Translate screen — which
    /// bypassed this class entirely and built its own prompt — asked "from
    /// English to Japanese". The screen was right: a 0.6B model has seen the
    /// word Japanese a great many times, and the token "ja" mostly as the German
    /// for yes. Tags are for lookups; a model wants the name.
    /// <para>
    /// Falls back to the tag for a language CircleAI does not ship, which is
    /// still better than nothing and is at least honest about what it knows.
    /// </para>
    /// <para>
    /// "GIVE ONLY THE TRANSLATION" IS LICENSED BY A REAL FAILURE, not by
    /// tidiness. Without it the model ANSWERS the sentence instead of
    /// translating it — which, in front of somebody at a hospital desk, is a
    /// very different thing to put on the screen.
    /// </para>
    /// <para>
    /// A SOURCE NOBODY KNOWS IS LEFT UNNAMED, not guessed. "How do you say sawubona in
    /// English" puts isiZulu where the asker's own language would be, so a capability that
    /// only knows the target asks for "into English" - which the model handles from the text
    /// itself - rather than naming a source that may be wrong. An empty tag, "auto" and "und"
    /// (BCP-47's undetermined) all mean not known.
    /// </para>
    /// </remarks>
    private string BuildPrompt(TranslationRequest r) =>
        (IsUnknown(r.SourceBcpTag)
            ? $"Translate the following text into {_name(r.TargetBcpTag)}. "
            : $"Translate the following text from {_name(r.SourceBcpTag)} to {_name(r.TargetBcpTag)}. ") +
        $"Mode: {r.Mode}. Preserve meaning and cultural context, not just literal words. " +
        (r.ContextHint is not null ? $"Context: {r.ContextHint}. " : string.Empty) +
        $"Give only the translation, nothing else.\n\n{r.Text}";

    /// <summary>Whether a source tag says nothing about the language: empty, "auto" or "und".</summary>
    public static bool IsUnknown(string? bcpTag) =>
        string.IsNullOrWhiteSpace(bcpTag)
        || string.Equals(bcpTag, "auto", StringComparison.OrdinalIgnoreCase)
        || string.Equals(bcpTag, "und", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The fallback name lookup: this assembly's own table, which is smaller
    /// than any real app's. See the constructor.
    /// </summary>
    private static string DefaultName(string bcpTag)
    {
        if (string.IsNullOrWhiteSpace(bcpTag)) return bcpTag;

        // The primary subtag: "pt-BR" and "pt" are the same language to a model
        // being asked to translate into it.
        var primary = bcpTag.Split('-')[0];

        foreach (var known in KnownLanguages.All)
            if (string.Equals(known.BcpTag, primary, StringComparison.OrdinalIgnoreCase))
                return known.DisplayName;

        return bcpTag;
    }
}
