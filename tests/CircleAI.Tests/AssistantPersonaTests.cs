// AssistantPersonaTests.cs
//
// The defect these exist for: THE PATH THAT ANSWERS HAD NO PERSONA.
//
// The persona was a private constant inside CircleAISession - the hybrid app's
// own session - and that app stopped holding a model. Every question now goes
// app -> link -> CircleNeuronService, built from CircleAIService's
// OptionsFactory, and that factory never set AIOptions.SystemPrompt. So the
// brain that replies ran on the SDK default, "You are B!, a helpful on-device
// assistant", while every rule anybody tuned sat in a file nothing reached.
//
// Nothing failed. Both halves built, every suite stayed green, and the only
// symptom was the product answering in somebody else's name on a phone. So the
// guard is a SOURCE check: a unit test cannot new up an Android Application, and
// the thing worth asserting is that the wiring exists at all.

using System;
using System.IO;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public sealed class AssistantPersonaTests
{
    [Fact]
    public void The_persona_carries_what_it_can_do()
    {
        // ONE SENTENCE, NOT A RETRIEVAL. "What can you do?" is every word a
        // stopword, so nothing can be searched for it; this is the answer.
        Assert.Contains(AssistantPersona.CanDo, AssistantPersona.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void What_it_can_do_names_only_what_the_app_already_offers()
    {
        // THE THREE TABS AND THE OFFLINE PROMISE, in the words the screens use.
        // A capability sentence that oversells is worse than none: somebody goes
        // looking for the thing and it is not there.
        foreach (var claim in new[] { "answer questions", "translate", "transcribe", "out loud", "remember" })
            Assert.Contains(claim, AssistantPersona.CanDo, StringComparison.OrdinalIgnoreCase);

        Assert.Contains("no signal", AssistantPersona.CanDo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void What_it_can_do_does_not_claim_vision()
    {
        // DELIBERATELY ABSENT. The manifest can still answer a question that names
        // images, which is what retrieval is good for; a blanket claim in the
        // persona would be made on every turn whether or not it is true on this
        // phone. Pinned so adding it is a decision somebody makes on purpose.
        foreach (var notClaimed in new[] { "image", "photo", "picture", "see " })
            Assert.DoesNotContain(notClaimed, AssistantPersona.CanDo, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_persona_still_says_it_is_a_tool_and_not_a_person()
    {
        // CAPABILITY-HONEST, NOT SELF-AWARE. Carried over with the text; the one
        // line it must not cross outlives any reshuffle of where the text lives.
        Assert.Contains("not a person", AssistantPersona.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Never invent", AssistantPersona.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Circle AI", AssistantPersona.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_persona_is_one_block_of_plain_sentences_not_a_list_of_rules()
    {
        // MEASURED, AND IT IS WHY THIS IS A SENTENCE AND NOT A DIRECTIVE BLOCK.
        // An earlier attempt added bulleted directives for small models on top of
        // this text. Over eight runs on a P30 with Qwen3.5-0.8B the same question
        // went from a clean 128-character answer to "I can't tell jokes." to a
        // 273-character refusal citing rules that forbade nothing of the sort:
        // given prohibitions and a capability list together, the model read the
        // capability list as a list of permissions. Deleted rather than retried.
        // No newlines at all, so there is nothing for a bullet to sit on. (A
        // hyphen inside a sentence is fine and the persona has one: "Circle AI -
        // a dry, competent assistant". What it must not grow is a LIST.)
        Assert.DoesNotContain("\n", AssistantPersona.Prompt, StringComparison.Ordinal);
    }

    [Fact]
    public void The_service_head_sets_the_system_prompt_from_the_persona()
    {
        // THE ONE THAT WOULD HAVE CAUGHT IT. CircleAIService's OptionsFactory
        // builds the AIOptions for the brain every question actually reaches. It
        // had no SystemPrompt, so the SDK default applied and nothing anywhere
        // failed. Source-level because the type is an Android Application.
        var source = File.ReadAllText(
            Path.Combine(Root(), "src", "CircleAIService", "ServiceApplication.cs"));

        Assert.Contains("SystemPrompt", source, StringComparison.Ordinal);
        Assert.Contains("AssistantPersona.Prompt", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_session_does_not_keep_a_second_copy_of_the_persona()
    {
        // ONE FACT, ONE OWNER - the repo's own standing rule, and the reason the
        // two paths disagreed in the first place. CircleAISession must point at
        // the persona, not restate it.
        var source = File.ReadAllText(Path.Combine(
            Root(), "src", "CircleAI", "Assistant.Runtime", "CircleAISession.cs"));

        Assert.Contains("AssistantPersona.Prompt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Circle AI -", source, StringComparison.Ordinal);
    }

    [Fact]
    public void The_spoken_form_claims_exactly_what_the_prompt_form_claims()
    {
        // TWO COPIES OF ONE FACT, AND THIS IS WHAT STOPS THEM DRIFTING. CanDo is
        // addressed to the model as "you"; CanDoSpoken is what the person hears,
        // so it is first person. Neither may quietly grow a claim the other does
        // not make - which is the only real cost of not doing pronoun surgery at
        // runtime.
        foreach (var claim in new[] { "no signal", "answer questions", "write and summarise",
                                      "translate", "transcribe", "out loud", "remember" })
        {
            Assert.Contains(claim, AssistantPersona.CanDo, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(claim, AssistantPersona.CanDoSpoken, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_spoken_form_speaks_as_itself()
    {
        // It is returned verbatim to the person and read aloud by the wake turn,
        // so it must not address them as the thing that runs on the phone.
        Assert.StartsWith("I run on this phone", AssistantPersona.CanDoSpoken, StringComparison.Ordinal);
        Assert.DoesNotContain("you answer", AssistantPersona.CanDoSpoken, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_spoken_form_is_sayable()
    {
        // NO MARKDOWN, NO LIST, NO IDS. The screen-off turn hands this straight to
        // the voice, and PlainReply is not in that path - this string is already
        // the final text.
        foreach (var unsayable in new[] { "*", "#", "`", "\n", "_" })
            Assert.DoesNotContain(unsayable, AssistantPersona.CanDoSpoken, StringComparison.Ordinal);
    }

    [Fact]
    public void Both_heads_answer_the_question_from_the_same_sentence()
    {
        // ONE FACT, ONE OWNER - the failure this whole change exists for. A
        // service answering from the sentence while the app answered from the
        // model is two products wearing one name. Source-level for the same
        // reason as the SystemPrompt check: neither type can be newed up here.
        foreach (var head in new[]
                 {
                     Path.Combine(Root(), "src", "CircleAIService", "ServiceApplication.cs"),
                     Path.Combine(Root(), "src", "CircleAI", "Assistant.Runtime", "CircleAISession.cs"),
                 })
        {
            var source = File.ReadAllText(head);
            Assert.Contains("OverviewAnswer", source, StringComparison.Ordinal);
            Assert.Contains("AssistantPersona.CanDoSpoken", source, StringComparison.Ordinal);
        }
    }


    [Fact]
    public void It_answers_its_name_out_loud()
    {
        // A TONE IS NOT AN ANSWER, measured on a P30 on 2026-10-03: the beep played
        // for 345 ms, the person waited - correctly, because that is how every
        // assistant they have used behaves - and the turn sat silent for fifteen
        // seconds waiting for a question it had never asked for.
        Assert.False(string.IsNullOrWhiteSpace(AssistantPersona.Greeting));
        Assert.False(string.IsNullOrWhiteSpace(AssistantPersona.NothingHeard));
    }

    [Theory]
    [InlineData("greeting")]
    [InlineData("nothing-heard")]
    public void What_it_says_on_waking_is_short_and_sayable(string which)
    {
        // PAID FOR IN LATENCY ON EVERY WAKE. These are synthesised on the device
        // while somebody waits, so a sentence here is a second of silence there.
        // And they are only ever spoken - the screen is off - so a character that
        // is not a word is a noise.
        var line = which == "greeting" ? AssistantPersona.Greeting : AssistantPersona.NothingHeard;

        Assert.InRange(line.Length, 2, 40);
        foreach (var unsayable in new[] { "*", "#", "`", "_" })
            Assert.DoesNotContain(unsayable, line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_turn_greets_before_it_opens_the_microphone()
    {
        // ORDER IS THE WHOLE FIX. Greeting after the capture starts would put our own
        // voice at the front of their question AND leave them talking into silence.
        // Source-level: the turn is Android-only and cannot be driven from here.
        var source = File.ReadAllText(Path.Combine(
            Root(), "src", "CircleAI", "Assistant.Device", "WakeTurn.cs"));

        var greets = source.IndexOf("AssistantPersona.Greeting", StringComparison.Ordinal);
        var hears  = source.IndexOf("await HearAsync(ct)", StringComparison.Ordinal);

        Assert.True(greets > 0, "the turn does not greet at all");
        Assert.True(hears > 0, "the turn does not capture at all");
        Assert.True(greets < hears, "the turn opens the microphone before it says hello");
    }

    [Fact]
    public void The_turn_does_not_wait_the_full_deadline_for_a_first_word()
    {
        // TWO DIFFERENT QUESTIONS, TWO DIFFERENT NUMBERS. Longest bounds a question
        // already under way; FirstWord bounds the silence before any question at all.
        // Sharing one number is how a quiet room cost fifteen seconds of apparent
        // deadness after the phone had just spoken.
        var source = File.ReadAllText(Path.Combine(
            Root(), "src", "CircleAI", "Assistant.Device", "WakeTurn.cs"));

        Assert.Contains("FirstWord", source, StringComparison.Ordinal);
        Assert.Contains("AssistantPersona.NothingHeard", source, StringComparison.Ordinal);
    }


    /// <summary>The repo root, found by walking up from the test assembly.</summary>
    private static string Root()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "src")))
            dir = Path.GetDirectoryName(dir);

        Assert.NotNull(dir);
        return dir!;
    }
}
