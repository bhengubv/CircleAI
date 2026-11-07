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
// does; and with thinking on, the reasoning and the answer come apart. And every member the
// interface gives a default body is the engine's own, called through the interface: the default's
// guesses - four characters a token, a text marker for a session, the reply as one kind of
// fragment - never answer for it. That gap is how LlamaGenerator leaked its thinking.
//
// Needs the 2B on this machine (says SKIPPED otherwise). Three conversations run unless
// BONSAI2_GENERATOR_CASES=all. The kernels are unoptimised in Debug: run these in Release.

using System.Text;
using System.Text.Json;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Sampling;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

/// <summary>One 2B for the whole class: loading it is the slow part.</summary>
public sealed class ManagedGeneratorFixture : IDisposable
{
    public ManagedGeneratorFixture()
    {
        Path = Bonsai2Model.TwoB();
        if (Path is not null)
            Generator = ManagedChatGenerator.Load(Path, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = PrimedDirectory });
    }

    public string? Path { get; }

    public ManagedChatGenerator? Generator { get; }

    /// <summary>Where this run's primed starts go: a folder of its own, gone when the run ends.</summary>
    public string PrimedDirectory { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"circleai-primed-{Guid.NewGuid():N}");

    public void Dispose()
    {
        Generator?.Dispose();
        try { Directory.Delete(PrimedDirectory, recursive: true); } catch (DirectoryNotFoundException) { }
    }
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
    public async Task The_persona_reaches_the_model_as_its_system_turn()
    {
        // Under MNN the persona went unread and the P30 answered "I am Qwen3.5"
        // (persona-must-live-where-the-answer-is-built). Here it is the system turn itself.
        // The same question the phone's "engine" broadcast asks, for comparing its answer.
        // Measured 2026-10-31, greedy: "I am an AI assistant designed to provide helpful
        // information and support." - the persona's assistant in one sentence, no longer Qwen;
        // without the persona the same question gets "Hello! I am Qwen3.5, a large language
        // model developed by Alibaba Cloud...". It does not give its name.
        var g = Generator();
        if (g is null) return;

        var response = await g.GenerateResponseAsync([new("system", CircleAI.Assistant.AssistantPersona.Prompt), new("user", "Hello! Who are you?")], Greedy(48));
        output.WriteLine($"{response.TokensIn} in, {response.TokensOut} out, {response.FinishReason}: {response.Text}");
        Assert.DoesNotContain("Qwen", response.Text);
        Assert.DoesNotContain("Alibaba", response.Text);
        Assert.Equal(FinishReason.Stop, response.FinishReason);
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
    public void System_messages_merge_into_the_one_system_turn_and_a_picture_is_written_where_the_template_puts_it()
    {
        var g = Generator();
        if (g is null) return;

        var prompt = g.RenderPrompt([new("user", "Hi"), new("system", "One."), new("system", "  "), new("SYSTEM", "Two.")]);
        Assert.Equal("<|im_start|>system\nOne.\n\nTwo.<|im_end|>\n<|im_start|>user\nHi<|im_end|>\n" +
                     "<|im_start|>assistant\n<think>\n\n</think>\n\n", prompt);

        // The 2B sees when its tower is beside it (ManagedVisionTests holds it to the fork); one that
        // cannot see refuses the picture rather than answering blind.
        ChatMessage[] picture = [new("user", "What is this?") { ImageBytes = [1, 2, 3] }];
        if (g.CanSee)
            Assert.Equal("<|im_start|>user\n<|vision_start|><|image_pad|><|vision_end|>What is this?<|im_end|>\n<|im_start|>assistant\n<think>\n\n</think>\n\n", g.RenderPrompt(picture));
        else
            Assert.Throws<NotSupportedException>(() => g.RenderPrompt(picture));
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

    [Fact]
    public void Every_member_the_interface_gives_a_default_is_the_engines_own()
    {
        // A member the class leaves out maps to the interface's own body.
        var map = typeof(ManagedChatGenerator).GetInterfaceMap(typeof(IChatGenerator));
        var defaults = new List<string>();
        for (var i = 0; i < map.InterfaceMethods.Length; i++)
        {
            var member = map.InterfaceMethods[i];
            if (member.IsStatic || member.IsAbstract) continue;
            defaults.Add(member.Name);
            Assert.True(map.TargetMethods[i].DeclaringType == typeof(ManagedChatGenerator), $"{member.Name} falls to IChatGenerator's default body");
        }
        Assert.Equal(new[] { "GenerateResponseAsync", "LoadSessionAsync", "SaveSessionAsync", "StreamFragmentsAsync" }, defaults.Order().ToArray());
    }

    [Fact]
    public async Task Through_the_interface_each_of_them_does_the_engines_work()
    {
        var engine = Generator();
        if (engine is null) return;
        IChatGenerator g = engine;

        // GenerateResponseAsync: the default guesses four characters a token and always says Stop.
        var response = await g.GenerateResponseAsync(Greet, Greedy(4));
        Assert.Equal(engine.Tokenizer.Encode(engine.RenderPrompt(Greet)).Count, response.TokensIn);
        Assert.Equal(4, response.TokensOut);
        Assert.Equal(FinishReason.Length, response.FinishReason);

        // StreamFragmentsAsync: the default hands StreamAsync's text back as content; the engine parts
        // a thought from the answer, and neither carries a think tag.
        var thinking = new GenerationOptions
        {
            Temperature = 1f, Seed = 7, MaxTokens = 16, Budget = PowerBudget.None, IncludeReasoning = true,
            Effort = ReasoningEffort.Medium, ThinkingBudget = 8,
        };
        var fragments = new List<ChatFragment>();
        await foreach (var fragment in g.StreamFragmentsAsync(Greet, thinking)) fragments.Add(fragment);
        Assert.NotEmpty(fragments);
        Assert.Equal(ChatFragmentKind.Reasoning, fragments[0].Kind);
        Assert.DoesNotContain(fragments, f => f.Text.Contains("<think>") || f.Text.Contains("</think>"));

        // SaveSessionAsync and LoadSessionAsync: the default writes a text marker and takes any marker
        // back; the engine writes its own state and refuses a marker.
        var session = Path.Combine(Path.GetTempPath(), $"circleai-session-{Guid.NewGuid():N}.bin");
        var marker = Path.Combine(Path.GetTempPath(), $"circleai-marker-{Guid.NewGuid():N}.txt");
        try
        {
            await g.GenerateAsync(Greet, Greedy(2));
            Assert.True(await g.SaveSessionAsync(session));
            Assert.Equal("CAIMGEN1", Encoding.ASCII.GetString(File.ReadAllBytes(session), 0, 8));
            Assert.True(await g.LoadSessionAsync(session));
            await File.WriteAllTextAsync(marker, $"circleai-session-marker\ntype:{typeof(ManagedChatGenerator).FullName}\n");
            await Assert.ThrowsAsync<InvalidDataException>(() => g.LoadSessionAsync(marker));
        }
        finally
        {
            File.Delete(session);
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task A_conversation_with_the_same_system_turn_starts_primed_and_answers_as_a_cold_start_does()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        ChatMessage[] earlier = [new("system", persona), new("user", "Hello!")];
        ChatMessage[] next = [new("system", persona), new("user", "What is two plus two?")];
        await g.GenerateAsync(earlier, Greedy(4)); // reads the system turn once, and primes
        var inMemory = new TurnTimingRecorder();
        var answer = await g.GenerateAsync(next, Greedy(10, inMemory));
        Assert.Equal("primed", inMemory.KvPath);

        // A new process finds the start on disk; one that keeps none reads the whole prompt; the
        // three answers are one answer.
        using var fresh = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = fixture.PrimedDirectory });
        var fromDisk = new TurnTimingRecorder();
        Assert.Equal(answer, await fresh.GenerateAsync(next, Greedy(10, fromDisk)));
        Assert.Equal("primed-disk", fromDisk.KvPath);

        using var cold = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        var reset = new TurnTimingRecorder();
        Assert.Equal(answer, await cold.GenerateAsync(next, Greedy(10, reset)));
        Assert.Equal("reset", reset.KvPath);

        output.WriteLine($"{answer} - prefill: primed {inMemory.PrefillMs} ms, from disk {fromDisk.PrefillMs} ms, cold {reset.PrefillMs} ms ({reset.PromptTokens} tokens)");
    }

    [Fact]
    public async Task What_was_read_while_they_talked_is_not_read_again_and_the_answer_is_the_same_answer()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        ChatMessage[] Said(string words) => [new("system", persona), new("user", words)];

        using var cold = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        var answer = await cold.GenerateAsync(Said("What is two plus two?"), Greedy(10));

        await g.GenerateAsync(Said("Hello!"), Greedy(4));

        var read = new TurnTimingRecorder();
        Assert.Empty(await g.GenerateAsync(Said("What is two"), new GenerationOptions { Temperature = 0, MaxTokens = 10, Budget = PowerBudget.None, JustRead = true, Timing = read }));
        Assert.Equal("primed", read.KvPath);

        var whole = new TurnTimingRecorder();
        Assert.Equal(answer, await g.GenerateAsync(Said("What is two plus two?"), Greedy(10, whole)));
        Assert.Equal("continued", whole.KvPath);
    }

    [Fact]
    public async Task A_word_that_changed_costs_the_last_read_and_not_the_ones_before_it()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        ChatMessage[] Said(string words) => [new("system", persona), new("user", words)];
        var reading = new GenerationOptions { Temperature = 0, MaxTokens = 10, Budget = PowerBudget.None, JustRead = true };

        using var cold = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        var answer = await cold.GenerateAsync(Said("Why is the sky blue?"), Greedy(10));

        await g.GenerateAsync(Said("Hello!"), Greedy(4));
        await g.GenerateAsync(Said("Why is"), reading);
        await g.GenerateAsync(Said("Why is this"), reading);

        var whole = new TurnTimingRecorder();
        Assert.Equal(answer, await g.GenerateAsync(Said("Why is the sky blue?"), Greedy(10, whole)));
        Assert.Equal("prefix-hit", whole.KvPath);
    }

    private static GenerationOptions Talking(string talk, int maxTokens, TurnTimingRecorder? timing = null, bool justRead = false) =>
        new() { Temperature = 0, MaxTokens = maxTokens, Budget = PowerBudget.None, Timing = timing, Conversation = talk, JustRead = justRead };

    [Fact]
    public async Task A_kept_conversation_reads_only_the_new_turn_and_remembers_what_was_said()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        var talk = Guid.NewGuid().ToString("N");
        ChatMessage[] Said(string words) => [new("system", persona), new("user", words)];

        var opening = new TurnTimingRecorder();
        await g.GenerateAsync(Said("My sister's name is Thandi."), Talking(talk, 24, opening));

        // NONE OF THE EARLIER TURN IS IN THESE MESSAGES: the brain has it, and nothing has to say it again.
        var next = Said("What is my sister's name?");
        Assert.InRange(g.Unread(next, null, talk), 1, 30);

        var second = new TurnTimingRecorder();
        var answer = await g.GenerateAsync(next, Talking(talk, 24, second));

        Assert.Equal("continued", second.KvPath);
        Assert.True(second.PromptTokens > opening.PromptTokens, $"{second.PromptTokens} after {opening.PromptTokens}");
        Assert.Contains("Thandi", answer, StringComparison.OrdinalIgnoreCase);
        output.WriteLine($"{second.PromptTokens} tokens in all: {answer}");
    }

    [Fact]
    public async Task What_a_table_answered_in_between_reaches_the_brain_with_the_next_turn()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        var talk = Guid.NewGuid().ToString("N");

        ChatMessage[] first = [new("system", persona), new("user", "Name one kind of bird.")];
        var reply = await g.GenerateAsync(first, Talking(talk, 10));

        // The brain never saw the middle exchange: a table answered it.
        ChatMessage[] next =
        [
            new("system", persona), new("user", "Name one kind of bird."), new("assistant", reply),
            new("user", "My sister's name is Thandi."), new("assistant", "I'll remember that."),
            new("user", "What is my sister's name?"),
        ];
        var timing = new TurnTimingRecorder();
        var answer = await g.GenerateAsync(next, Talking(talk, 24, timing));

        Assert.Equal("continued", timing.KvPath);
        Assert.Contains("Thandi", answer, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_false_start_leaves_the_conversation_where_it_was()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        ChatMessage[] Said(string words) => [new("system", persona), new("user", words)];

        using var cold = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        var straight = Guid.NewGuid().ToString("N");
        await cold.GenerateAsync(Said("What is two plus two?"), Talking(straight, 10));
        var expected = await cold.GenerateAsync(Said("And three plus three?"), Talking(straight, 10));

        var talk = Guid.NewGuid().ToString("N");
        await g.GenerateAsync(Said("What is two plus two?"), Talking(talk, 10));

        // Words that turned out wrong, read and then an answer begun and stopped on them.
        await g.GenerateAsync(Said("And four"), Talking(talk, 10, justRead: true));
        await g.GenerateAsync(Said("And four plus"), Talking(talk, 10, justRead: true));
        using (var stop = new CancellationTokenSource())
        {
            var options = Talking(talk, 40);
            var reading = g.StreamAsync(Said("And four plus four?"), options, stop.Token).GetAsyncEnumerator(stop.Token);
            await reading.MoveNextAsync();
            await stop.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => { while (await reading.MoveNextAsync()) { } });
            await reading.DisposeAsync();
        }

        var timing = new TurnTimingRecorder();
        Assert.Equal(expected, await g.GenerateAsync(Said("And three plus three?"), Talking(talk, 10, timing)));
        Assert.Equal("talk", timing.KvPath);
    }

    [Fact]
    public async Task Another_conversation_is_read_from_its_own_messages()
    {
        var g = Generator();
        if (g is null) return;

        const string persona = "You are B!, a helpful assistant on this phone. Answer in one sentence.";
        ChatMessage[] Said(string words) => [new("system", persona), new("user", words)];

        await g.GenerateAsync(Said("My sister's name is Thandi."), Talking(Guid.NewGuid().ToString("N"), 12));

        var timing = new TurnTimingRecorder();
        await g.GenerateAsync(Said("What is two plus two?"), Talking(Guid.NewGuid().ToString("N"), 6, timing));

        Assert.Equal("primed", timing.KvPath);
        Assert.Equal(g.BuildPrompt(Said("What is two plus two?")).Ids.Count, timing.PromptTokens);
    }

    [Fact]
    public async Task What_is_still_to_read_leaves_out_what_the_state_holds_and_touches_nothing()
    {
        var g = Generator();
        if (g is null) return;

        // A system turn no other test uses: nothing of it is held or primed yet.
        var persona = $"You are B!, an assistant on a phone. {Guid.NewGuid():N}";
        ChatMessage[] first = [new("system", persona), new("user", "What is two plus two?")];
        var whole = g.BuildPrompt(first).Ids.Count;
        Assert.Equal(whole, g.Unread(first, null));

        var reply = await g.GenerateAsync(first, Greedy(8));
        ChatMessage[] next = [.. first, new("assistant", reply), new("user", "And three plus three?")];

        // A follow-up reads from where the last turn began, and counting it moved nothing: the
        // call finds the state as it was left.
        var total = g.BuildPrompt(next).Ids.Count;
        var unread = g.Unread(next, null);
        Assert.InRange(unread, 1, total - whole + 8);
        var timing = new TurnTimingRecorder();
        await g.GenerateAsync(next, Greedy(2, timing));
        Assert.Equal("prefix-hit", timing.KvPath);
        output.WriteLine($"{unread} of {total} tokens to read");
    }

    [Fact]
    public async Task A_primed_start_on_disk_is_not_still_to_read_and_a_new_brain_measures_nothing_until_it_reads()
    {
        var g = Generator();
        if (g is null) return;

        var persona = $"You are B!, an assistant on a phone. {Guid.NewGuid():N}";
        ChatMessage[] conversation = [new("system", persona), new("user", "Hello!")];
        await g.GenerateAsync(conversation, Greedy(2)); // reads the system turn once, and primes
        ChatMessage[] next = [new("system", persona), new("user", "What is two plus two?")];
        var whole = g.BuildPrompt(next).Ids.Count;

        using var fresh = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = fixture.PrimedDirectory });
        Assert.Null(fresh.ReadCost);
        var unread = fresh.Unread(next, null);
        Assert.InRange(unread, 1, whole - 1);

        using var keepsNone = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        Assert.Equal(whole, keepsNone.Unread(next, null));

        await fresh.GenerateAsync(next, Greedy(2));
        Assert.True(fresh.ReadCost > TimeSpan.Zero);
        output.WriteLine($"{unread} of {whole} tokens to read; {fresh.ReadCost!.Value.TotalMilliseconds:F1} ms a token");
    }

    [Fact]
    public async Task The_service_hands_the_engine_what_it_can_read_in_time_and_the_engine_answers()
    {
        if (Generator() is null) return;

        const string persona = "You are B!, an assistant on a phone. Answer in one short sentence.";
        var engine = ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = "" });
        await using var service = new CircleAI.Hosting.AIService(
            new CircleAI.Hosting.AIOptions { ModelPath = fixture.Path!, WarmOnStart = false, SystemPrompt = persona },
            generatorFactory: _ => engine);

        // The first question measures the reading.
        await service.ChatAsync([new ChatMessage("user", "Hello!")], Greedy(2));

        var conversation = new List<ChatMessage>();
        foreach (var n in new[] { "one", "two", "three", "four", "five", "six" })
        {
            conversation.Add(new ChatMessage("user", $"Tell me something about the number {n}, in a sentence of a few words please."));
            conversation.Add(new ChatMessage("assistant", $"The number {n} is a small whole number that people count with every day."));
        }
        conversation.Add(new ChatMessage("user", "Name one fruit that is yellow."));
        var whole = engine.BuildPrompt([new ChatMessage("system", persona), .. conversation]).Ids.Count;

        var timing = new TurnTimingRecorder();
        var options = new GenerationOptions { Temperature = 0, MaxTokens = 6, Budget = PowerBudget.None, Timing = timing, ReadWithin = engine.ReadCost!.Value * 100 };
        var answer = new StringBuilder();
        await foreach (var piece in service.StreamAsync(conversation, options)) answer.Append(piece);

        Assert.InRange(timing.PromptTokens, 1, whole - 100);
        Assert.False(string.IsNullOrWhiteSpace(answer.ToString()));
        output.WriteLine($"{timing.PromptTokens} of {whole} tokens read ({timing.KvPath}): {answer}");
    }

    private static readonly CircleAI.Tools.ToolDefinition[] Weather =
    [
        new()
        {
            Name = "get_weather",
            Description = "The weather now in a city",
            Parameters = new Dictionary<string, CircleAI.Tools.ToolParameter> { ["location"] = new() { Type = "string", Description = "The city" } },
            RequiredParameters = ["location"],
        },
    ];

    private static ChatMessage[] Seed(string persona) => [new("system", persona), new("user", ".")];

    private static ChatMessage[] AskedTheWeather(string persona) => [new("system", persona), new("user", "What is the weather in Paris right now?")];

    private static string NewPersona() => $"You are B!, an assistant on a phone. {Guid.NewGuid():N}";

    private ManagedChatGenerator Fresh(string folder) =>
        ManagedChatGenerator.Load(fixture.Path!, new ManagedGeneratorOptions { ContextLength = 1024, PrimedStartDirectory = folder });

    [Fact]
    public async Task A_system_turn_read_in_the_gaps_starts_a_turn_that_answers_as_if_it_had_read_it_all()
    {
        var g = Generator();
        if (g is null) return;

        var persona = NewPersona();
        var asked = AskedTheWeather(persona);
        var whole = g.BuildPrompt(asked, Weather, false, null).Ids.Count;
        Assert.Equal(whole, g.Unread(asked, Weather));

        Assert.True(await g.PrimeAsync(Seed(persona), Weather));

        Assert.InRange(g.Unread(asked, Weather), 1, 40);
        var timing = new TurnTimingRecorder();
        var answer = await g.GenerateAsync(asked, Greedy(12, timing).WithTools(Weather));
        Assert.Equal("primed-disk", timing.KvPath);

        // And read once is read: it is on disk for the next process and nothing was left behind.
        using var next = Fresh(fixture.PrimedDirectory);
        Assert.True(await next.PrimeAsync(Seed(persona), Weather));
        Assert.Empty(Directory.GetFiles(fixture.PrimedDirectory, "*.priming"));

        using var cold = Fresh("");
        var read = new TurnTimingRecorder();
        Assert.Equal(answer, await cold.GenerateAsync(asked, Greedy(12, read).WithTools(Weather)));
        Assert.Equal("reset", read.KvPath);
        output.WriteLine($"{whole} tokens: {timing.PrefillMs} ms primed, {read.PrefillMs} ms cold: {answer}");
    }

    [Fact]
    public async Task A_brain_goes_from_its_tools_to_a_plain_turn_and_back_by_its_starts_and_answers_as_a_cold_one_does()
    {
        var g = Generator();
        if (g is null) return;

        var persona = NewPersona();
        ChatMessage[] tool = AskedTheWeather(persona);
        ChatMessage[] plain = [new("system", persona), new("user", "Name one fruit that is yellow.")];
        Assert.True(await g.PrimeAsync(Seed(persona), null));
        Assert.True(await g.PrimeAsync(Seed(persona), Weather));

        var (toWhole, toPlain, back) = (new TurnTimingRecorder(), new TurnTimingRecorder(), new TurnTimingRecorder());
        var called = await g.GenerateAsync(tool, Greedy(12, toWhole).WithTools(Weather));
        var answered = await g.GenerateAsync(plain, Greedy(12, toPlain));
        var calledAgain = await g.GenerateAsync(tool, Greedy(12, back).WithTools(Weather));

        // Every change of system turn is a file, and neither turn sees what the other left in the state.
        Assert.Equal(["primed-disk", "primed-disk", "primed-disk"], [toWhole.KvPath, toPlain.KvPath, back.KvPath]);
        using var cold = Fresh("");
        Assert.Equal(answered, await cold.GenerateAsync(plain, Greedy(12)));
        Assert.Equal(called, await cold.GenerateAsync(tool, Greedy(12).WithTools(Weather)));
        Assert.Equal(called, calledAgain);
        output.WriteLine($"{toPlain.PrefillMs} ms into the plain turn, {back.PrefillMs} ms back: {answered}");
    }

    [Fact]
    public async Task A_turn_takes_the_engine_from_a_read_in_the_gaps_and_keeps_its_conversation()
    {
        var g = Generator();
        if (g is null) return;

        ChatMessage[] first = [new("system", NewPersona()), new("user", "What is two plus two?")];
        var reply = await g.GenerateAsync(first, Greedy(8));

        var persona = NewPersona();
        var reading = g.PrimeAsync(Seed(persona), Weather);

        // The follow-up does not wait for the read, and the read has not touched what it follows up.
        var timing = new TurnTimingRecorder();
        await g.GenerateAsync([.. first, new("assistant", reply), new("user", "And three plus three?")], Greedy(2, timing));
        Assert.Equal("prefix-hit", timing.KvPath);
        Assert.False(reading.IsCompleted);

        Assert.True(await reading);
        Assert.InRange(g.Unread(AskedTheWeather(persona), Weather), 1, 40);
    }

    [Fact]
    public async Task A_read_stopped_part_way_goes_on_from_what_it_wrote_down()
    {
        var g = Generator();
        if (g is null) return;

        var persona = NewPersona();
        using var stop = new CancellationTokenSource();
        var reading = g.PrimeAsync(Seed(persona), Weather, stop.Token);
        var deadline = DateTime.UtcNow.AddMinutes(5);
        while (!Directory.Exists(fixture.PrimedDirectory) || Directory.GetFiles(fixture.PrimedDirectory, "*.priming").Length == 0)
        {
            Assert.True(DateTime.UtcNow < deadline, "nothing was written down");
            await Task.Delay(20);
        }
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);

        // Another process, as after a kill: it goes on from there, and the start it keeps is the one a
        // brain that read the whole of it would have kept.
        using var next = Fresh(fixture.PrimedDirectory);
        Assert.True(await next.PrimeAsync(Seed(persona), Weather));
        Assert.True(next.PrimedFrom >= 64, $"it started at token {next.PrimedFrom}");
        Assert.Empty(Directory.GetFiles(fixture.PrimedDirectory, "*.priming"));

        var asked = AskedTheWeather(persona);
        var timing = new TurnTimingRecorder();
        var answer = await next.GenerateAsync(asked, Greedy(12, timing).WithTools(Weather));
        Assert.Equal("primed-disk", timing.KvPath);
        using var cold = Fresh("");
        Assert.Equal(answer, await cold.GenerateAsync(asked, Greedy(12).WithTools(Weather)));
    }

    [Fact]
    public async Task A_read_in_the_gaps_costs_no_kept_start_its_place()
    {
        if (Generator() is null) return;

        // Three starts kept, then a fourth read in the gaps: four is what a brain keeps, and what the
        // read wrote down on its way is not one of them.
        var folder = System.IO.Path.Combine(fixture.PrimedDirectory, Guid.NewGuid().ToString("N"));
        using var engine = Fresh(folder);
        for (var i = 0; i < 3; i++) await engine.GenerateAsync([new("system", NewPersona()), new("user", "Hello!")], Greedy(1));

        Assert.True(await engine.PrimeAsync(Seed(NewPersona()), Weather));

        Assert.Equal(4, Directory.GetFiles(folder, "*.primed").Length);
        Assert.Empty(Directory.GetFiles(folder, "*.priming"));
    }

    [Fact]
    public async Task A_brain_let_go_stops_its_read_in_the_gaps_at_the_next_token()
    {
        if (Generator() is null) return;

        var engine = Fresh(fixture.PrimedDirectory);
        var reading = engine.PrimeAsync(Seed(NewPersona()), Weather);
        engine.Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => reading);
    }

    [Fact]
    public async Task A_brain_that_keeps_no_starts_or_a_conversation_with_no_system_turn_reads_nothing_ahead()
    {
        var g = Generator();
        if (g is null) return;

        using var keepsNone = Fresh("");
        Assert.False(await keepsNone.PrimeAsync(Seed(NewPersona()), Weather));
        Assert.False(await g.PrimeAsync([new("user", ".")], Weather));
    }

    [Fact]
    public async Task A_service_reads_its_tools_ahead_and_a_tool_turn_starts_after_them_and_runs_the_tool()
    {
        if (Generator() is null) return;

        const string persona = "You are B!, an assistant on a phone. Answer in one short sentence.";
        var folder = System.IO.Path.Combine(fixture.PrimedDirectory, Guid.NewGuid().ToString("N"));
        var ran = new List<string>();
        await using var service = new CircleAI.Hosting.AIService(
            new CircleAI.Hosting.AIOptions
            {
                ModelPath = fixture.Path!,
                SystemPrompt = persona,
                ToolBridge = new CircleAI.Tools.LoggedToolBridge(new CircleAI.Assistant.CircleAIToolBridge(() => 64), line => { lock (ran) ran.Add(line); }),
            },
            generatorFactory: _ => Fresh(folder));

        await service.StartAsync();
        var deadline = DateTime.UtcNow.AddMinutes(10);
        while (!Directory.Exists(folder) || Directory.GetFiles(folder, "*.primed").Length < 2)
        {
            Assert.True(DateTime.UtcNow < deadline, "the tools were not read");
            await Task.Delay(100);
        }

        var answer = await service.AgenticChatAsync("Use the calculator: what is 6789 times 3?", Greedy(96));

        output.WriteLine(answer);
        foreach (var line in ran) output.WriteLine(line);
        Assert.Contains(ran, line => line.Contains("calculate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_tool_call_comes_out_in_the_templates_form_and_ends_the_turn()
    {
        var g = Generator();
        if (g is null) return;

        // The system turn exactly as the template writes it when it is given this one tool.
        const string tool = """{"type": "function", "function": {"name": "get_weather", "description": "The weather now in a city", "parameters": {"type": "object", "properties": {"location": {"type": "string", "description": "The city"}}, "required": ["location"]}}}""";
        var system = "# Tools\n\nYou have access to the following functions:\n\n<tools>\n" + tool + "\n</tools>"
            + "\n\nIf you choose to call a function ONLY reply in the following format with NO suffix:\n\n<tool_call>\n<function=example_function_name>\n<parameter=example_parameter_1>\nvalue_1\n</parameter>\n<parameter=example_parameter_2>\nThis is the value for the second parameter\nthat can span\nmultiple lines\n</parameter>\n</function>\n</tool_call>\n\n<IMPORTANT>\nReminder:\n- Function calls MUST follow the specified format: an inner <function=...></function> block must be nested within <tool_call></tool_call> XML tags\n- Required parameters MUST be specified\n- You may provide optional reasoning for your function call in natural language BEFORE the function call, but NOT after\n- If there is no function call available, answer the question like normal with your current knowledge and do not tell the user about function calls\n</IMPORTANT>";

        var response = await g.GenerateResponseAsync(
            [new("system", system), new("user", "What is the weather in Paris right now?")], Greedy(64));
        output.WriteLine(response.Text);

        var at = response.Text.IndexOf(ToolCallGrammar.Opening, StringComparison.Ordinal);
        Assert.True(at >= 0, "the model answered without calling the tool: " + response.Text);
        var call = response.Text[(at + ToolCallGrammar.Opening.Length)..].TrimEnd();
        var grammar = new ToolCallGrammar(["get_weather"]);
        Assert.True(grammar.TryAdvance(grammar.Start, Encoding.UTF8.GetBytes(call), out var state), "not in the template's form: " + call);
        Assert.True(state.MayEnd, "the call did not close: " + call);
        Assert.Equal(FinishReason.Stop, response.FinishReason);
    }
}
