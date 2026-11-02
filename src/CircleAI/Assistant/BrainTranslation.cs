// BrainTranslation.cs
//
// TRANSLATION ON WHATEVER BRAIN A HEAD HAS, THROUGH THE ONE ENGINE.
//
// LlmTranslationEngine owns the prompt - the language names, "give only the translation", the
// text last - and every head reaches it here: a head that holds the model in-process gets the
// engine over its brain (BrainAsGenerator), and the linked app's brain overrides
// IBrain.TranslateAsync with the service's own translate verb, which runs the same engine on the
// service's brain. What no head does any more is write a translation prompt of its own:
// TranslateCapability did, beside the engine, and that is the defect this closes.

using System;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Languages.Translation;

namespace CircleAI.Assistant;

/// <summary>The translation engine over an <see cref="IBrain"/>.</summary>
public static class BrainTranslation
{
    /// <summary>
    /// <paramref name="text"/> in <paramref name="toTag"/>'s language, by the translation engine on
    /// <paramref name="brain"/>: the translation only.
    /// </summary>
    /// <param name="brain">The head's brain.</param>
    /// <param name="text">What was said.</param>
    /// <param name="fromTag">BCP-47 of its language; empty or "auto" when not known.</param>
    /// <param name="toTag">BCP-47 of the language it should come back in.</param>
    /// <param name="ct">Cancels the translation.</param>
    public static async Task<string> TranslateAsync(IBrain brain, string text, string? fromTag, string toTag, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(brain);
        ArgumentException.ThrowIfNullOrWhiteSpace(toTag);
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        var engine = new LlmTranslationEngine(new BrainAsGenerator(brain), Name);
        var result = await engine.TranslateAsync(
            new CircleAI.Languages.Translation.TranslationRequest(text, fromTag ?? string.Empty, toTag, TranslationMode.Conversational), ct)
            .ConfigureAwait(false);
        return result.TranslatedText;
    }

    /// <summary>A language's name for the prompt, from the catalogue the app offers; the tag when it is not there.</summary>
    /// <remarks>
    /// THE FULLER TABLE (SampleLanguages, the languages the app offers), as every head that
    /// translates already passes it - "from English to Japanese", never "from English to ja".
    /// </remarks>
    public static string Name(string tag) => SampleLanguages.Find(tag)?.Name ?? tag;
}
