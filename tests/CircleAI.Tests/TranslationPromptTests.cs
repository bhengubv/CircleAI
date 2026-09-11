// TranslationPromptTests.cs
//
// What the translation engine actually asks the model.
//
// THE ENGINE HAD ZERO CONSUMERS AND SO NOTHING EVER READ ITS PROMPT. It was
// written, it compiled, it was never called - the Translate screen built its own
// against the raw brain - so the one thing that decides whether a translation is
// any good went unexamined for as long as the class existed. When the screen was
// finally pointed at the engine, the engine's prompt turned out to be the WORSE
// of the two.
//
// These pin the two things that were wrong, and the one thing that must not be
// lost from the version that worked.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using CircleAI.Languages.Translation;
using Xunit;

namespace CircleAI.Tests;

public class TranslationPromptTests
{
    [Fact]
    public async Task The_languages_are_named_not_tagged()
    {
        // THE DEFECT. "Translate from en to ja" asks a 0.6B model to reason
        // about two tokens it has mostly seen as ordinary words - "ja" is the
        // German for yes - where "from English to Japanese" is a sentence it has
        // seen a great many times. The screen got this right before the engine
        // did, which is why the engine now uses the screen's wording.
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy, Names).TranslateAsync(
            new TranslationRequest("hello", "en", "ja"));

        Assert.Contains("English", spy.Last);
        Assert.Contains("Japanese", spy.Last);
        Assert.DoesNotContain("from en to", spy.Last);
    }

    [Fact]
    public async Task The_engines_own_table_is_too_small_and_the_caller_supplies_a_better_one()
    {
        // THIS IS WHY THE LOOKUP IS AN ARGUMENT. The first version of this
        // reached for CircleAI.Languages.KnownLanguages because that is the
        // table this assembly can see. It lists TWENTY languages; the app ships
        // SEVENTY-FIVE. Japanese is not among the twenty - on an app with a
        // whole Open JTalk prosody stack for Japanese - so the prompt came out
        // as "from English to ja" and nothing said so.
        var withDefault = new PromptSpy();
        await new LlmTranslationEngine(withDefault).TranslateAsync(
            new TranslationRequest("hello", "en", "ja"));

        var withCallers = new PromptSpy();
        await new LlmTranslationEngine(withCallers, Names).TranslateAsync(
            new TranslationRequest("hello", "en", "ja"));

        Assert.DoesNotContain("Japanese", withDefault.Last);
        Assert.Contains("Japanese", withCallers.Last);
    }

    /// <summary>Stands in for the app's own language table.</summary>
    private static string Names(string tag) => tag switch
    {
        "en" or "en-GB" => "English",
        "ja" => "Japanese",
        "pt" or "pt-BR" => "Portuguese",
        "zu" => "isiZulu",
        "fr" => "French",
        _ => tag,
    };

    [Fact]
    public async Task A_regional_tag_still_finds_its_language()
    {
        // "pt-BR" and "pt" are the same language to a model being asked to
        // translate into it, and a lookup that misses would silently fall back
        // to printing the raw tag.
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy, Names).TranslateAsync(
            new TranslationRequest("hello", "en-GB", "pt-BR"));

        Assert.Contains("English", spy.Last);
        Assert.Contains("Portuguese", spy.Last);
    }

    [Fact]
    public async Task A_language_we_do_not_ship_falls_back_to_its_tag()
    {
        // Better than nothing, and honest about what is known. Silently
        // dropping the language would ask for a translation into nowhere.
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy).TranslateAsync(
            new TranslationRequest("hello", "en", "qq"));

        Assert.Contains("qq", spy.Last);
    }

    [Fact]
    public async Task It_still_says_to_give_only_the_translation()
    {
        // THE CLAUSE THAT MUST SURVIVE EVERY REWRITE. Without it the model
        // ANSWERS the sentence rather than translating it - so "where is the
        // toilet" comes back as directions, in front of somebody at a hospital
        // desk who needed the words to say.
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy).TranslateAsync(
            new TranslationRequest("where is the toilet", "en", "zu"));

        Assert.Contains("only the translation", spy.Last, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_text_is_last_so_nothing_after_it_can_be_read_as_instruction()
    {
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy).TranslateAsync(
            new TranslationRequest("the sentence itself", "en", "fr"));

        Assert.EndsWith("the sentence itself", spy.Last, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_African_language_is_named_the_way_its_speakers_name_it()
    {
        // isiZulu, not "Zulu". The table already spells these properly and a
        // prompt that does not is the app telling somebody their language has a
        // shorter name than it does.
        var spy = new PromptSpy();
        await new LlmTranslationEngine(spy, Names).TranslateAsync(
            new TranslationRequest("hello", "en", "zu"));

        Assert.Contains("isiZulu", spy.Last);
    }

    [Fact]
    public async Task The_translation_comes_back_trimmed_and_paired_with_its_original()
    {
        var spy = new PromptSpy { Reply = "  Sawubona  " };
        var result = await new LlmTranslationEngine(spy).TranslateAsync(
            new TranslationRequest("hello", "en", "zu"));

        Assert.Equal("Sawubona", result.TranslatedText);
        Assert.Equal("hello", result.OriginalText);
        Assert.Equal("zu", result.TargetBcpTag);
    }

    /// <summary>Records the prompt instead of running a model.</summary>
    private sealed class PromptSpy : IChatGenerator
    {
        public string Last { get; private set; } = string.Empty;
        public string Reply { get; init; } = "translated";

        public Task<string> GenerateAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            CancellationToken ct = default)
        {
            Last = messages[^1].Content;
            return Task.FromResult(Reply);
        }

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> messages,
            GenerationOptions? options = null,
            [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
        {
            Last = messages[^1].Content;
            await Task.CompletedTask;
            yield return Reply;
        }

        public void Dispose() { }
    }
}
