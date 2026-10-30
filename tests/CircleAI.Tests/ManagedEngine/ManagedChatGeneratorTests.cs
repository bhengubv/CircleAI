// ManagedChatGeneratorTests.cs
//
// The C# engine through IChatGenerator, against the pinned fork. templates.json holds each
// conversation and the fork's rendering of it; logits.json the fork's greedy reply (32 tokens,
// or fewer where the model ended its turn). Through the interface, at temperature 0:
//   - the prompt the generator builds is the fork's, character for character and token for token;
//   - the reply is the fork's, with the token counts and the finish reason that implies.
// Then what the interface promises beyond one reply: the streams add up to it; a stop sequence
// ends it at the earliest match and never reaches the caller; system messages merge into the one
// system turn; cancellation throws, and a stream left early gives the engine back; the next turn
// reuses the first, and a saved session and a cold start answer it exactly as the live state
// does; and with thinking on, the reasoning and the answer come apart.
//
// Needs the 2B on this machine (says SKIPPED otherwise). Three conversations run unless
// BONSAI2_GENERATOR_CASES=all. The kernels are unoptimised in Debug: run these in Release.

using System.Text;
using System.Text.Json;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

/// <summary>One 2B for the whole class: loading it is the slow part.</summary>
public sealed class ManagedGeneratorFixture : IDisposable
{
    public ManagedGeneratorFixture()
    {
        if (Bonsai2Model.TwoB() is { } path) Generator = ManagedChatGenerator.Load(path, new ManagedGeneratorOptions { ContextLength = 1024 });
    }

    public ManagedChatGenerator? Generator { get; }

    public void Dispose() => Generator?.Dispose();
}

public sealed class ManagedChatGeneratorTests(ManagedGeneratorFixture fixture, ITestOutputHelper output) : IClassFixture<ManagedGeneratorFixture>
{
    private static readonly string[] Everyday = ["greet", "chinese", "system"];

    private static readonly ChatMessage[] Greet = [new("user", "Hello! Who are you?")];

    private static GenerationOptions Greedy(int maxTokens, TurnTimingRecorder? timing = null, string[]? stops = null) =>
        new() { Temperature = 0, MaxTokens = maxTokens, Budget = PowerBudget.None, Timing = timing, StopSequences = stops };

    private ManagedChatGenerator? Generator()
    {
        if (fixture.Generator is null) output.WriteLine(Bonsai2Model.Missing);
        return fixture.Generator;
    }

    [Fact]
    public async Task Each_conversation_gets_the_forks_prompt_and_the_forks_greedy_reply()
    {
        var g = Generator();
        if (g is null) return;

        using var templates = Bonsai2Model.Fixture("templates.json");
        using var logits = Bonsai2Model.Fixture("logits.json");
        var steps = logits.RootElement.GetProperty("greedy_steps").GetInt32();
        var conversations = templates.RootElement.GetProperty("cases").EnumerateArray().ToDictionary(c => c.GetProperty("name").GetString()!);
        var everything = Environment.GetEnvironmentVariable("BONSAI2_GENERATOR_CASES") == "all";
        var endOfTurn = new HashSet<int>();
        foreach (var text in new[] { "<|im_end|>", "<|endoftext|>" })
        {
            if (g.Tokenizer.TryGetSpecial(text, out var id)) endOfTurn.Add(id);
        }

        var ran = 0;
        foreach (var c in logits.RootElement.GetProperty("cases").EnumerateArray())
        {
            var name = c.GetProperty("name").GetString()!;
            if (!everything && !Everyday.Contains(name)) continue;
            var conversation = conversations[name];
            // Tools reach this engine as words in the system turn, as they reach every engine today.
            if (conversation.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array) continue;
            var messages = conversation.GetProperty("messages").EnumerateArray()
                .Select(m => new ChatMessage(m.GetProperty("role").GetString()!, m.GetProperty("content").GetString()!))
                .ToArray();

            var prompt = g.RenderPrompt(messages);
            Assert.Equal(conversation.GetProperty("rendered").GetString(), prompt);
            var promptIds = c.GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32()).ToArray();
            Assert.Equal(promptIds, g.Tokenizer.Encode(prompt));

            // The fork's reply: its greedy tokens up to the end of its turn.
            var forkTokens = c.GetProperty("steps").EnumerateArray().Select(s => s.GetProperty("token").GetInt32()).ToList();
            var ended = forkTokens.FindIndex(endOfTurn.Contains);
            var replyTokens = ended < 0 ? forkTokens : forkTokens[..ended];
            var bytes = new List<byte>();
            foreach (var token in replyTokens) bytes.AddRange(g.Tokenizer.Piece(token, special: false).ToArray());
            var expected = Encoding.UTF8.GetString(bytes.ToArray()).TrimStart();

            var response = await g.GenerateResponseAsync(messages, Greedy(steps));
            output.WriteLine($"{name}: {response.TokensIn} in, {response.TokensOut} out, {response.FinishReason}, {response.Latency.TotalSeconds:F1} s: {response.Text}");
            Assert.Equal(expected, response.Text);
            Assert.Equal(promptIds.Length, response.TokensIn);
            Assert.Equal(replyTokens.Count, response.TokensOut);
            Assert.Equal(ended < 0 ? FinishReason.Length : FinishReason.Stop, response.FinishReason);
            Assert.Null(response.ReasoningContent);
            ran++;
        }
        Assert.True(ran >= Everyday.Length, $"only {ran} conversations ran");
    }

    [Fact]
    public async Task The_streams_add_up_to_the_reply()
    {
        var g = Generator();
        if (g is null) return;

        var reply = await g.GenerateAsync(Greet, Greedy(12));
        var fragments = new List<ChatFragment>();
        await foreach (var fragment in g.StreamFragmentsAsync(Greet, Greedy(12))) fragments.Add(fragment);
        var chunks = new List<string>();
        await foreach (var chunk in g.StreamAsync(Greet, Greedy(12))) chunks.Add(chunk);

        Assert.StartsWith("Hello! I am Qwen3.5", reply);
        Assert.All(fragments, f => Assert.Equal(ChatFragmentKind.Content, f.Kind));
        Assert.True(fragments.Count > 1, "the reply came in one piece, not as a stream");
        Assert.Equal(reply, string.Concat(fragments.Select(f => f.Text)));
        Assert.Equal(reply, string.Concat(chunks));
    }

    [Fact]
    public async Task A_stop_sequence_ends_the_reply_at_the_earliest_match_and_never_reaches_the_caller()
    {
        var g = Generator();
        if (g is null) return;

        // "Hello! I am Qwen3.5": " am" completes both stops at once; "I am" starts first.
        var chunks = new List<string>();
        await foreach (var chunk in g.StreamAsync(Greet, Greedy(32, stops: ["am", "I am"]))) chunks.Add(chunk);
        Assert.Equal("Hello! ", string.Concat(chunks));

        var response = await g.GenerateResponseAsync(Greet, Greedy(32, stops: ["Qwen"]));
        Assert.Equal("Hello! I am ", response.Text);
        Assert.Equal(FinishReason.Stop, response.FinishReason);
    }

    [Fact]
    public void System_messages_merge_into_the_one_system_turn_and_images_are_refused()
    {
        var g = Generator();
        if (g is null) return;

        var prompt = g.RenderPrompt([new("user", "Hi"), new("system", "One."), new("system", "  "), new("SYSTEM", "Two.")]);
        Assert.Equal("<|im_start|>system\nOne.\n\nTwo.<|im_end|>\n<|im_start|>user\nHi<|im_end|>\n" +
                     "<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);

        Assert.Throws<NotSupportedException>(() => g.RenderPrompt([new("user", "What is this?") { ImageBytes = [1, 2, 3] }]));
    }

    [Fact]
    public async Task Cancellation_throws_and_a_stream_left_early_gives_the_engine_back()
    {
        var g = Generator();
        if (g is null) return;

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => g.GenerateAsync(Greet, Greedy(8), new CancellationToken(canceled: true)));

        using var cancel = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var fragment in g.StreamFragmentsAsync(Greet, Greedy(32), cancel.Token)) cancel.Cancel();
        });

        // Leaving the loop stops the run before the loop is left, not 31 tokens later.
        var abandoned = new TurnTimingRecorder();
        await foreach (var fragment in g.StreamFragmentsAsync(Greet, Greedy(32, abandoned))) break;
        Assert.Equal("cancel", abandoned.Finish);

        Assert.False(string.IsNullOrEmpty(await g.GenerateAsync(Greet, Greedy(2)).WaitAsync(TimeSpan.FromMinutes(2))));
    }

    [Fact]
    public async Task The_next_turn_reuses_the_first_and_a_saved_session_and_a_cold_start_answer_it_the_same()
    {
        var g = Generator();
        if (g is null) return;

        ChatMessage[] first = [new("user", "My name is Thandi.")];
        var reply = await g.GenerateAsync(first, Greedy(8));
        ChatMessage[] second = [.. first, new("assistant", reply), new("user", "What is my name?")];
        var session = Path.Combine(Path.GetTempPath(), $"circleai-session-{Guid.NewGuid():N}.bin");
        try
        {
            Assert.True(await g.SaveSessionAsync(session));

            var live = new TurnTimingRecorder();
            var answer = await g.GenerateAsync(second, Greedy(6, live));
            Assert.Equal("prefix-hit", live.KvPath);

            Assert.True(await g.LoadSessionAsync(session));
            var restored = new TurnTimingRecorder();
            Assert.Equal(answer, await g.GenerateAsync(second, Greedy(6, restored)));
            Assert.Equal("continued", restored.KvPath);

            await g.GenerateAsync([new("user", "Hi")], Greedy(1));
            var cold = new TurnTimingRecorder();
            Assert.Equal(answer, await g.GenerateAsync(second, Greedy(6, cold)));
            Assert.Equal("reset", cold.KvPath);

            output.WriteLine($"first reply: {reply}");
            output.WriteLine($"second turn ({live.PromptTokens} prompt tokens): {answer}");
            output.WriteLine($"prefill: live {live.PrefillMs} ms, from the session {restored.PrefillMs} ms, cold {cold.PrefillMs} ms");
        }
        finally
        {
            File.Delete(session);
        }

        Assert.False(await g.LoadSessionAsync(session));
    }

    [Fact]
    public async Task With_thinking_on_the_reasoning_and_the_answer_come_apart()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var g = ManagedChatGenerator.Load(path, new ManagedGeneratorOptions { ContextLength = 512, Thinking = true });
        ChatMessage[] hello = [new("user", "Hello!")];
        using var templates = Bonsai2Model.Fixture("templates.json");
        var rendered = templates.RootElement.GetProperty("cases").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "thinking_on").GetProperty("rendered").GetString();
        Assert.Equal(rendered, g.RenderPrompt(hello));

        GenerationOptions Seeded(bool reasoning) =>
            new() { Temperature = 1f, Seed = 7, MaxTokens = 24, Budget = PowerBudget.None, IncludeReasoning = reasoning };

        var fragments = new List<ChatFragment>();
        await foreach (var fragment in g.StreamFragmentsAsync(hello, Seeded(true))) fragments.Add(fragment);
        Assert.NotEmpty(fragments);
        Assert.Equal(ChatFragmentKind.Reasoning, fragments[0].Kind);
        Assert.DoesNotContain(fragments, f => f.Text.Contains("<think>") || f.Text.Contains("</think>"));

        var full = await g.GenerateResponseAsync(hello, Seeded(true));
        Assert.Equal(string.Concat(fragments.Where(f => f.Kind == ChatFragmentKind.Reasoning).Select(f => f.Text)).Trim(), full.ReasoningContent);
        Assert.Equal(string.Concat(fragments.Where(f => f.Kind == ChatFragmentKind.Content).Select(f => f.Text)), full.Text);

        var quiet = new List<ChatFragment>();
        await foreach (var fragment in g.StreamFragmentsAsync(hello, Seeded(false))) quiet.Add(fragment);
        Assert.All(quiet, f => Assert.Equal(ChatFragmentKind.Content, f.Kind));
        Assert.Null((await g.GenerateResponseAsync(hello, Seeded(false))).ReasoningContent);

        output.WriteLine($"reasoning: {full.ReasoningContent}");
        output.WriteLine($"answer: {full.Text}");
    }
}
