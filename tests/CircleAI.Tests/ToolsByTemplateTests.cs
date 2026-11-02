// ToolsByTemplateTests.cs
//
// How AIService hands tools to a brain, by what the brain can take. A generator that takes them
// through its own chat template (IToolCallingGenerator - the C# engine) is given the tools on the
// call and no note describing them in JSON; any other generator gets the note exactly as before.
// The agentic loop reads the XML form such a brain writes and types its values by the registry.
// And a plain call - a translation - reaches the model exactly as written.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class ToolsByTemplateTests : System.IDisposable
{
    private readonly string _placeholder = Path.Combine(Path.GetTempPath(), $"circleai-tools-{System.Guid.NewGuid():N}.model");

    public ToolsByTemplateTests() => File.WriteAllText(_placeholder, "placeholder");

    public void Dispose()
    {
        try { File.Delete(_placeholder); } catch { /* best effort */ }
    }

    /// <summary>Records every call: the messages, and the options they came with.</summary>
    private class Recorder : IChatGenerator
    {
        public List<(List<ChatMessage> Messages, GenerationOptions? Options)> Calls { get; } = [];
        public Queue<string> Replies { get; } = new();

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            Calls.Add((messages.ToList(), options));
            return Task.FromResult(Replies.Count > 0 ? Replies.Dequeue() : "Done.");
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await GenerateAsync(messages, options, ct);
        }

        public void Dispose() { }
    }

    /// <summary>The same, but it takes tools in its own template.</summary>
    private sealed class TemplateRecorder : Recorder, IToolCallingGenerator { }

    private sealed class WeatherBridge : IToolBridge
    {
        public List<ToolInvocation> Calls { get; } = [];

        public IReadOnlyList<ToolDefinition> AvailableTools { get; } =
        [
            new ToolDefinition
            {
                Name = "get_weather",
                Description = "The weather now in a city.",
                Parameters = new Dictionary<string, ToolParameter>
                {
                    ["location"] = new() { Type = "string", Description = "The city" },
                    ["days"] = new() { Type = "integer", Description = "How many days ahead" },
                },
                RequiredParameters = ["location"],
            },
        ];

        public Task<ToolResult> InvokeAsync(ToolInvocation invocation, CancellationToken ct = default)
        {
            Calls.Add(invocation);
            return Task.FromResult(ToolResult.Ok("get_weather", "24 C and sunny"));
        }
    }

    private AIService Service(Recorder generator, WeatherBridge? bridge = null) => new(
        new AIOptions
        {
            ModelPath = _placeholder,
            WarmOnStart = false,
            ToolBridge = bridge,
            SystemPrompt = "You are B!.",
        },
        generatorFactory: _ => generator);

    private const string XmlCall =
        "<tool_call>\n<function=get_weather>\n<parameter=location>\nDurban\n</parameter>\n<parameter=days>\n2\n</parameter>\n</function>\n</tool_call>";

    [Fact]
    public async Task A_brain_that_takes_tools_in_its_template_gets_the_tools_and_no_note()
    {
        var brain = new TemplateRecorder();
        brain.Replies.Enqueue(XmlCall);
        brain.Replies.Enqueue("It is 24 C and sunny in Durban.");
        var bridge = new WeatherBridge();
        await using var service = Service(brain, bridge);

        var answer = await service.AgenticChatAsync("What is the weather in Durban?");

        Assert.Equal("It is 24 C and sunny in Durban.", answer);
        var (messages, options) = brain.Calls[0];
        Assert.Equal("get_weather", Assert.Single(options!.Tools!).Name);
        Assert.DoesNotContain(messages, m => m.Content.Contains("# Tools", System.StringComparison.Ordinal));

        // The XML call ran, its values typed by what the tool declares.
        var call = Assert.Single(bridge.Calls);
        Assert.Equal("Durban", call.Arguments["location"]);
        Assert.Equal(2L, call.Arguments["days"]);

        // The second pass carries the call and its result, and the tools again.
        var (second, secondOptions) = brain.Calls[1];
        Assert.Contains(second, m => m.Role == "assistant" && m.Content.Contains("<function=get_weather>", System.StringComparison.Ordinal));
        Assert.Contains(second, m => m.Role == "tool" && m.Content.Contains("sunny", System.StringComparison.Ordinal));
        Assert.NotNull(secondOptions!.Tools);
    }

    [Fact]
    public async Task Any_other_brain_is_told_about_the_tools_in_the_note_as_before()
    {
        var brain = new Recorder();
        brain.Replies.Enqueue("<tool_call>{\"name\": \"get_weather\", \"arguments\": {\"location\": \"Durban\"}}</tool_call>");
        brain.Replies.Enqueue("Sunny.");
        var bridge = new WeatherBridge();
        await using var service = Service(brain, bridge);

        await service.AgenticChatAsync("What is the weather in Durban?");

        var (messages, options) = brain.Calls[0];
        Assert.Null(options!.Tools);
        Assert.Contains(messages, m => m.Content.Contains("# Tools", System.StringComparison.Ordinal));
        Assert.Single(bridge.Calls);
    }

    [Fact]
    public async Task A_question_with_no_tool_in_it_gives_a_template_brain_no_tools()
    {
        var brain = new TemplateRecorder();
        await using var service = Service(brain, new WeatherBridge());

        await service.ChatAsync([new ChatMessage("user", "I am feeling lonely today.")]);

        Assert.Null(brain.Calls[0].Options!.Tools);   // the cue test decides, as it does for the note
    }

    [Fact]
    public async Task A_plain_call_reaches_the_model_exactly_as_written()
    {
        var brain = new TemplateRecorder();
        brain.Replies.Enqueue("Ngiyabonga");
        await using var service = Service(brain, new WeatherBridge());

        // "What can you do" has a fixed answer as a turn, and is only words to translate here.
        var prompt = "Translate the following text into isiZulu.\n\nWhat can you do? Check the weather.";
        var answer = await service.ChatAsync([new ChatMessage("user", prompt)], new GenerationOptions { Plain = true });

        Assert.Equal("Ngiyabonga", answer);
        var (messages, options) = Assert.Single(brain.Calls);
        var only = Assert.Single(messages);                        // no persona, no notes
        Assert.Equal(prompt, only.Content);
        Assert.Null(options!.Tools);                               // and no tools, for all its "check"
        Assert.True(options.Plain);
    }

    [Fact]
    public void Every_option_survives_a_copy()
    {
        var tools = new WeatherBridge().AvailableTools;
        var options = new GenerationOptions
        {
            MaxTokens = 7, Effort = ReasoningEffort.High, ThinkingBudget = 99, Plain = true, RetrievalQuery = "q",
        }.WithTools(tools).WithPrefixCache();

        Assert.Equal((7, ReasoningEffort.High, 99, true, "q", true), (options.MaxTokens, options.Effort, options.ThinkingBudget, options.Plain, options.RetrievalQuery, options.UsePrefixCache));
        Assert.Same(tools, options.Tools);
    }
}
