// OverviewAnswerTests.cs
//
// AIService answering "what can you do?" itself, without the model.
//
// THE SYMPTOM THIS IS FOR. On a P30 with Qwen3.5-0.8B, with the capability
// sentence sitting in its system prompt, the question came back "I am here to
// help you! What can I do for you today?" - correct, harmless, and not one of
// the things it can actually do. The same persona asks for one or two short
// sentences, so it gave a short one. The sentence was in front of the model
// rather than said by it.
//
// So the assertions that matter are not about the text. They are that the
// generator was never called, and that nothing else was diverted to it.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class OverviewAnswerTests
{
    private const string Answer = "I run on this phone and answer questions.";

    private static AIOptions Options(string modelPath, string? overview = Answer)
        => new() { ModelPath = modelPath, WarmOnStart = false, OverviewAnswer = overview };

    private static List<ChatMessage> Ask(string question) => [new("user", question)];

    [Fact]
    public async Task The_host_answers_it_and_the_model_is_never_asked()
    {
        var generator = new FakeChatGenerator("THE MODEL ANSWERED");
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);

            Assert.Equal(Answer, await svc.AskAsync("what can you do?"));

            // THE POINT OF THE WHOLE CHANGE. Not that the words are right - that
            // nothing was rolled for them.
            Assert.Equal(0, generator.GenerateCallCount);
            Assert.Equal(0, generator.StreamCallCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_does_not_wait_for_a_cold_model()
    {
        // DELIBERATELY WITHOUT StartAsync. The guard sits in front of
        // EnsureStartedAsync, because a known answer must not cost the thirteen to
        // twenty-three seconds a cold load takes on a P30. If this ever starts
        // needing a started service, that property has been lost.
        var generator = new FakeChatGenerator();
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);
            Assert.Equal(Answer, await svc.AskAsync("what can you do"));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Streaming_gets_the_same_answer_in_one_chunk()
    {
        var generator = new FakeChatGenerator();
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);

            var chunks = new List<string>();
            await foreach (var chunk in svc.StreamAsync(Ask("what can you do?")))
                chunks.Add(chunk);

            Assert.Equal([Answer], chunks);
            Assert.Equal(0, generator.StreamCallCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Fragment_streaming_gets_it_as_content_not_reasoning()
    {
        var generator = new FakeChatGenerator();
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);

            var fragments = new List<ChatFragment>();
            await foreach (var fragment in svc.StreamFragmentsAsync(Ask("what do you do")))
                fragments.Add(fragment);

            // There was no thinking to show, so a caller that renders Reasoning
            // separately must not be handed an empty thought bubble.
            Assert.Equal([ChatFragmentKind.Content], fragments.Select(f => f.Kind));
            Assert.Equal(Answer, fragments[0].Text);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task A_real_question_still_goes_to_the_model()
    {
        // THE OTHER HALF, and the one that would catch an over-eager rule. An app
        // that answers everything from a constant is worse than one that answers
        // nothing from one.
        var generator = new FakeChatGenerator("THE MODEL ANSWERED");
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);
            await svc.StartAsync();

            foreach (var question in new[] { "what is the capital of France", "who are you", "how are you", "tell me a joke" })
                Assert.Equal("THE MODEL ANSWERED", await svc.AskAsync(question));

            Assert.Equal(4, generator.GenerateCallCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task Unset_changes_nothing()
    {
        // A HOST THAT DOES NOT SET IT IS A HOST THAT NEVER SEES THIS FEATURE. The
        // SDK does not know what the product around it can do, so silence is the
        // only honest default.
        var generator = new FakeChatGenerator("THE MODEL ANSWERED");
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path, overview: null), generatorFactory: _ => generator);
            await svc.StartAsync();

            Assert.Equal("THE MODEL ANSWERED", await svc.AskAsync("what can you do?"));
            Assert.Equal(1, generator.GenerateCallCount);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task It_reads_the_last_user_turn_not_the_first()
    {
        // A conversation that OPENS with "what can you do" and then asks something
        // real must get the real answer, and the other way round.
        var generator = new FakeChatGenerator("THE MODEL ANSWERED");
        var path = Path.GetTempFileName();
        try
        {
            await using var svc = new AIService(Options(path), generatorFactory: _ => generator);
            await svc.StartAsync();

            List<ChatMessage> openedWithIt =
            [
                new("user", "what can you do?"),
                new("assistant", Answer),
                new("user", "what is the capital of France"),
            ];
            Assert.Equal("THE MODEL ANSWERED", await svc.ChatAsync(openedWithIt));

            List<ChatMessage> endsWithIt =
            [
                new("user", "what is the capital of France"),
                new("assistant", "Paris."),
                new("user", "and what can you do?"),
            ];
            Assert.Equal(Answer, await svc.ChatAsync(endsWithIt));
        }
        finally { File.Delete(path); }
    }
}
