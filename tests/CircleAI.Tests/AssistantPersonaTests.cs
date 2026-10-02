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
            Root(), "src", "CircleAI.Assistant.Runtime", "CircleAISession.cs"));

        Assert.Contains("AssistantPersona.Prompt", source, StringComparison.Ordinal);
        Assert.DoesNotContain("You are Circle AI -", source, StringComparison.Ordinal);
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
