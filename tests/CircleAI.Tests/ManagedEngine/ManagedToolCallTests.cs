// ManagedToolCallTests.cs
//
// S8 on the real 2B: a tool call round-trips through ManagedChatGenerator, and a thought ends when
// its budget does. The tools reach the model through its own template's tools block (offered on
// the call, GenerationOptions.Tools) - rendered byte for byte as the pinned fork rendered them -
// and greedy, the 2B then writes the fork's own call, token for token, which the reader takes
// back. Handed its result in the template's own history, it answers from it; and the agentic loop
// does all of that through AIService with a tool bridge. Then thinking per call: off by default,
// on when a call asks, and closed by the generator itself when its budget is spent, so the
// answer still comes.
//
// Needs the 2B on this machine (says SKIPPED otherwise). Run these in Release.

using System.Text;
using System.Text.Json;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class ManagedToolCallTests(ManagedGeneratorFixture fixture, ITestOutputHelper output) : IClassFixture<ManagedGeneratorFixture>
{
    // The tool the fixtures were made with (templates.json, cases "tool" and "tool_roundtrip").
    private static readonly ToolDefinition Weather = new()
    {
        Name = "get_weather",
        Description = "Get the current weather for a place.",
        Parameters = new Dictionary<string, ToolParameter>
        {
            ["location"] = new() { Type = "string", Description = "City name" },
        },
        RequiredParameters = ["location"],
    };

    private static GenerationOptions Greedy(int maxTokens, IReadOnlyList<ToolDefinition>? tools = null) =>
        new() { Temperature = 0, MaxTokens = maxTokens, Budget = PowerBudget.None, Tools = tools };

    private ManagedChatGenerator? Generator()
    {
        if (fixture.Generator is null) output.WriteLine(Bonsai2Model.Missing);
        return fixture.Generator;
    }

    private static JsonElement Case(JsonDocument templates, string name) =>
        templates.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    private static ChatMessage[] Messages(JsonElement conversation) =>
        conversation.GetProperty("messages").EnumerateArray()
            .Select(m => new ChatMessage(m.GetProperty("role").GetString()!, m.GetProperty("content").GetString() ?? ""))
            .ToArray();

    [Fact]
    public void The_offered_tools_reach_the_model_through_its_own_template_as_the_fork_renders_them()
    {
        var g = Generator();
        if (g is null) return;

        using var templates = Bonsai2Model.Fixture("templates.json");
        using var logits = Bonsai2Model.Fixture("logits.json");
        var tool = Case(templates, "tool");

        var prompt = g.RenderPrompt(Messages(tool), [Weather], thinking: false, effort: null);
        Assert.Equal(tool.GetProperty("rendered").GetString(), prompt);

        var forkIds = logits.RootElement.GetProperty("cases").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "tool").GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32());
        Assert.Equal(forkIds, g.Tokenizer.Encode(prompt));

        // AND THE HISTORY THE AGENTIC LOOP WRITES BACK IS THE TEMPLATE'S OWN: the call as the model
        // wrote it, then its result, render exactly as the fork renders a call and its response.
        var roundTrip = Case(templates, "tool_roundtrip");
        ChatMessage[] history =
        [
            new("user", "What is the weather in Durban?"),
            new("assistant", "<tool_call>\n<function=get_weather>\n<parameter=location>\nDurban\n</parameter>\n</function>\n</tool_call>"),
            new("tool", "{\"temp_c\": 24, \"sky\": \"sunny\"}"),
        ];
        Assert.Equal(roundTrip.GetProperty("rendered").GetString(), g.RenderPrompt(history, [Weather], thinking: false, effort: null));
    }

    [Fact]
    public async Task A_tool_call_round_trips_through_the_generator()
    {
        var g = Generator();
        if (g is null) return;

        using var templates = Bonsai2Model.Fixture("templates.json");
        using var logits = Bonsai2Model.Fixture("logits.json");
        var asked = Messages(Case(templates, "tool"));

        // 1. Offered the tool, greedy, the 2B writes the fork's own call, token for token.
        var steps = logits.RootElement.GetProperty("cases").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "tool").GetProperty("steps").EnumerateArray()
            .Select(s => s.GetProperty("token").GetInt32()).ToList();
        g.Tokenizer.TryGetSpecial("<|im_end|>", out var imEnd);
        var reply = steps.TakeWhile(t => t != imEnd).ToList();
        var bytes = new List<byte>();
        foreach (var token in reply) bytes.AddRange(g.Tokenizer.Piece(token, special: false).ToArray());
        var forksCall = Encoding.UTF8.GetString(bytes.ToArray()).TrimStart();

        var first = await g.GenerateResponseAsync(asked, Greedy(64, [Weather]));
        output.WriteLine($"call: {first.Text}");
        Assert.Equal(forksCall, first.Text);
        Assert.Equal(reply.Count, first.TokensOut);
        Assert.Equal(FinishReason.Stop, first.FinishReason);

        // 2. The reader takes it back.
        var call = ToolCallReader.Read(first.Text, [Weather]);
        Assert.NotNull(call);
        Assert.Equal("get_weather", call!.ToolName);
        Assert.Equal("Durban", call.Arguments["location"]);
        Assert.True(ToolCallReader.IsRunnable(call, [Weather], out _));

        // 3. Handed the result in the template's own history, it answers from it.
        ChatMessage[] withResult = [.. asked, new("assistant", first.Text), new("tool", "{\"temp_c\": 24, \"sky\": \"sunny\"}")];
        var answer = await g.GenerateResponseAsync(withResult, Greedy(48, [Weather]));
        output.WriteLine($"answer: {answer.Text}");
        Assert.DoesNotContain("<tool_call>", answer.Text);
        Assert.True(answer.Text.Contains("24", StringComparison.Ordinal) || answer.Text.Contains("sunny", StringComparison.OrdinalIgnoreCase),
            "the answer did not use the tool's result: " + answer.Text);
    }

    private sealed class WeatherBridge : IToolBridge
    {
        public List<ToolInvocation> Calls { get; } = [];
        public IReadOnlyList<ToolDefinition> AvailableTools { get; } = [Weather];

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            Calls.Add(invocation);
            return Task.FromResult(ToolResult.Ok("get_weather", new { temp_c = 24, sky = "sunny" }));
        }
    }

    [Fact]
    public async Task The_agentic_loop_runs_the_tool_the_2B_calls_and_answers_from_its_result()
    {
        if (fixture.Path is not { } path)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        // Its own generator: the service disposes the brain it was given.
        var bridge = new WeatherBridge();
        await using var service = new AIService(
            new AIOptions { ModelPath = path, WarmOnStart = false, ToolBridge = bridge, SystemPrompt = "You are B!, a helpful assistant." },
            generatorFactory: p => ManagedChatGenerator.Load(p, new ManagedGeneratorOptions { ContextLength = 2048, PrimedStartDirectory = "" }));

        var answer = await service.AgenticChatAsync("What is the weather in Durban right now?", Greedy(64));
        output.WriteLine($"answer: {answer}");

        Assert.NotEmpty(bridge.Calls);
        var call = bridge.Calls[0];
        Assert.Equal("get_weather", call.ToolName);
        Assert.Contains("Durban", (string)call.Arguments["location"]!, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<tool_call>", answer);
        Assert.False(string.IsNullOrWhiteSpace(answer));
    }

    [Fact]
    public async Task A_call_thinks_when_it_asks_and_its_budget_closes_the_thought_so_the_answer_still_comes()
    {
        var g = Generator();
        if (g is null) return;
        ChatMessage[] asked = [new("user", "What is 17 times 23?")];

        // The generator was made with thinking off; a call that asks for effort thinks.
        Assert.EndsWith("<|im_start|>assistant\n<think>\n", g.RenderPrompt(asked, null, thinking: true, effort: ReasoningEffort.Medium), StringComparison.Ordinal);
        Assert.EndsWith("<think>\n\n</think>\n\n", g.RenderPrompt(asked), StringComparison.Ordinal);

        var off = await g.GenerateResponseAsync(asked, new GenerationOptions { Temperature = 0, MaxTokens = 24, Budget = PowerBudget.None, Effort = ReasoningEffort.Off });
        Assert.Null(off.ReasoningContent);

        // The card's thinking sampler, seeded; a 12-token budget the thought cannot finish inside.
        const int budget = 12;
        const int answerTokens = 32;
        var thought = await g.GenerateResponseAsync(asked, new GenerationOptions
        {
            Temperature = 1f, Seed = 7, MaxTokens = answerTokens, Budget = PowerBudget.None, IncludeReasoning = true,
            Effort = ReasoningEffort.Medium, ThinkingBudget = budget,
        });
        output.WriteLine($"thought ({thought.TokensOut} tokens out): {thought.ReasoningContent}");
        output.WriteLine($"answer: {thought.Text}");

        Assert.False(string.IsNullOrWhiteSpace(thought.ReasoningContent));
        Assert.True(g.Tokenizer.Encode(thought.ReasoningContent!, parseSpecial: false).Count <= budget + 2,
            "the thought ran past its budget: " + thought.ReasoningContent);
        Assert.False(string.IsNullOrWhiteSpace(thought.Text), "the budget closed the thought and no answer came");
        Assert.DoesNotContain("<think>", thought.Text);
        Assert.DoesNotContain("</think>", thought.Text);
        Assert.True(thought.TokensOut <= answerTokens + budget + 1);
    }
}
