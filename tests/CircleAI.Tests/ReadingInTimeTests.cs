// ReadingInTimeTests.cs
//
// A brain is handed only what it can read in its turn's time. On the P30 on 2026-11-05 the C# 2B
// read at about 450 ms a token, the queue sent the session's history with every question, and "Say
// hello in one word." ended at the two-minute deadline with nothing said. What a turn may leave out
// is the tools it did not ask for and its oldest history; the question always goes in.
//
// AND WHAT NO TURN CAN READ IN TIME IS READ BEFORE ONE ASKS. "Use the calculator: what is 6789 times
// 3?" stopped at the same deadline: the phone's three tools are about 540 tokens, and a skill note
// picked by the question's words added 154 more. The service has a brain read every tool once, after
// it warms; a tool turn carries no skill note.
//
// AND A TOOL TURN STAYS ITS OWN. One left the block on every question after it, and a token went from
// about 560 ms to about 800 ms; the persona and the tools are each a start on disk, so each turn is
// offered what it asks for.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Assistant.Requests;
using CircleAI.Hosting;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Skills;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

[Collection(nameof(PowerBudgetConditionsCollection))]
public sealed class ReadingInTimeTests : IDisposable
{
    private const string Question = "Name one fruit that is yellow.";

    private static readonly string[] EveryTool = ["get_battery_level", "calculate", "web_search"];

    private readonly string _placeholder = Path.Combine(Path.GetTempPath(), $"circleai-reading-{Guid.NewGuid():N}.model");

    public ReadingInTimeTests() => File.WriteAllText(_placeholder, "placeholder");

    public void Dispose()
    {
        PowerBudgetPolicy.Conditions = null;
        try { File.Delete(_placeholder); } catch { /* best effort */ }
    }

    /// <summary>Records what it was handed. A word is a token, and a second reads one.</summary>
    private class Brain : IChatGenerator
    {
        public List<(List<ChatMessage> Messages, GenerationOptions? Options)> Calls { get; } = [];

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null, CancellationToken ct = default)
        {
            lock (Calls) Calls.Add((messages.ToList(), options));
            return Task.FromResult("A banana.");
        }

        public async IAsyncEnumerable<string> StreamAsync(IReadOnlyList<ChatMessage> messages, GenerationOptions? options = null,
                                                         [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return await GenerateAsync(messages, options, ct);
        }

        public void Dispose() { }
    }

    /// <summary>The same, and it measures its reading, as the C# engine does.</summary>
    private sealed class ReadingBrain : Brain, IToolCallingGenerator, IReadingGenerator
    {
        public TimeSpan? ReadCost { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>What it holds already: the messages from the start it would not read again.</summary>
        public IReadOnlyList<ChatMessage> Held { get; init; } = [];

        public int ToolBlock { get; init; } = 500;

        /// <summary>How many tools the block it has already read holds: that block costs it nothing.</summary>
        public int ToolsRead { get; init; }

        /// <summary>What it was given to read ahead of a turn.</summary>
        public TaskCompletionSource<(IReadOnlyList<ChatMessage> Messages, IReadOnlyList<ToolDefinition>? Tools)> Primed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Unread(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools)
        {
            var shared = 0;
            while (shared < Held.Count && shared < messages.Count && Held[shared].Content == messages[shared].Content) shared++;
            return messages.Skip(shared).Sum(Words) + (tools is { Count: > 0 } offered && offered.Count != ToolsRead ? ToolBlock : 0);
        }

        public Task<bool> PrimeAsync(IReadOnlyList<ChatMessage> messages, IReadOnlyList<ToolDefinition>? tools, CancellationToken ct = default)
        {
            Primed.TrySetResult((messages, tools));
            return Task.FromResult(true);
        }
    }

    private static int Words(ChatMessage message) => (message.Content ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;

    private AIService Service(Brain brain, IToolBridge? bridge = null, bool warm = false, ISkillStore? skills = null, IMemoryPressureSource? pressure = null) => new(
        new AIOptions { ModelPath = _placeholder, WarmOnStart = warm, ToolBridge = bridge, SystemPrompt = "You are B!.", SkillStore = skills },
        modelLoader: null, generatorFactory: _ => brain, modelSelector: null, modelRegistry: null, memoryPressureSource: pressure);

    // Three exchanges of 10 words a side, then the question: 66 words with the system turn left out.
    private static List<ChatMessage> Conversation()
    {
        var turns = new List<ChatMessage>();
        foreach (var n in new[] { "first", "second", "third" })
        {
            turns.Add(new ChatMessage("user", $"{n} a b c d e f g h i"));
            turns.Add(new ChatMessage("assistant", $"{n} reply b c d e f g h i"));
        }
        turns.Add(new ChatMessage("user", Question));
        return turns;
    }

    private static async Task<List<ChatMessage>> Asked(AIService service, Brain brain, IReadOnlyList<ChatMessage> messages, TimeSpan? within)
    {
        await foreach (var _ in service.StreamAsync(messages, new GenerationOptions { ReadWithin = within })) { }
        return brain.Calls[^1].Messages;
    }

    private static string[] Said(IEnumerable<ChatMessage> messages) =>
        messages.Where(m => m.Role != "system").Select(m => m.Content.Split(' ')[0]).ToArray();

    [Fact]
    public async Task A_turn_keeps_the_newest_history_it_can_read_in_time_and_the_question()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain);

        // The system turn's three words and the question's six leave 31 seconds for history: one exchange.
        var handed = await Asked(service, brain, Conversation(), TimeSpan.FromSeconds(30));

        Assert.Equal(["third", "third", "Name"], Said(handed));
        Assert.Equal("user", handed[^3].Role);        // a question goes with its answer
        Assert.Equal("assistant", handed[^2].Role);
        Assert.Equal(Question, handed[^1].Content);
    }

    [Fact]
    public async Task A_turn_that_fits_reaches_the_brain_as_it_came()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain);

        Assert.Equal(7, Said(await Asked(service, brain, Conversation(), TimeSpan.FromMinutes(5))).Length);
    }

    [Fact]
    public async Task What_the_brain_already_holds_is_not_counted_against_the_time()
    {
        // A live conversation: the brain holds all of it but the last exchange and the question.
        var conversation = Conversation();
        var system = new ChatMessage("system", "You are B!.");
        var brain = new ReadingBrain { Held = [system, .. conversation.Take(4)] };
        await using var service = Service(brain);

        // 26 words to read of the 69 there are, and 30 seconds is enough for them.
        Assert.Equal(7, Said(await Asked(service, brain, conversation, TimeSpan.FromSeconds(30))).Length);
    }

    [Fact]
    public async Task A_question_is_read_however_long_it_takes_when_nothing_is_left_to_cut()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain);

        var handed = await Asked(service, brain, Conversation(), TimeSpan.FromSeconds(1));

        Assert.Equal(Question, Assert.Single(handed, m => m.Role != "system").Content);
    }

    [Fact]
    public async Task Nothing_is_cut_with_no_time_given_or_before_a_brain_has_measured_its_reading()
    {
        var measured = new ReadingBrain();
        await using var noTime = Service(measured);
        Assert.Equal(7, Said(await Asked(noTime, measured, Conversation(), within: null)).Length);

        var unmeasured = new ReadingBrain { ReadCost = null };
        await using var noMeasure = Service(unmeasured);
        Assert.Equal(7, Said(await Asked(noMeasure, unmeasured, Conversation(), TimeSpan.FromSeconds(1))).Length);
    }

    [Fact]
    public async Task A_brain_that_does_not_measure_its_reading_is_handed_the_turn_as_before()
    {
        var brain = new Brain();
        await using var service = Service(brain);

        Assert.Equal(7, Said(await Asked(service, brain, Conversation(), TimeSpan.FromSeconds(1))).Length);
    }

    [Fact]
    public async Task Tools_a_turn_did_not_ask_for_go_before_the_history()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        // A tool cue offers the calculator. Its block is not primed here, so it cannot be read in time;
        // the history can, and the answer comes without the tools.
        var conversation = Conversation();
        conversation[^1] = new ChatMessage("user", "What is 17 times 4?");
        await foreach (var _ in service.StreamAsync(conversation, new GenerationOptions { ReadWithin = TimeSpan.FromMinutes(2) })) { }

        var (messages, options) = brain.Calls[^1];
        Assert.Null(options!.Tools);
        Assert.Equal(7, Said(messages).Length);
    }

    [Fact]
    public async Task A_tool_block_the_brain_can_read_in_time_stays()
    {
        var brain = new ReadingBrain { ToolBlock = 20 };
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        var conversation = Conversation();
        conversation[^1] = new ChatMessage("user", "What is 17 times 4?");
        await foreach (var _ in service.StreamAsync(conversation, new GenerationOptions { ReadWithin = TimeSpan.FromMinutes(2) })) { }

        Assert.Contains("calculate", brain.Calls[^1].Options!.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task The_tool_loop_keeps_its_tools_whatever_they_cost()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        await service.AgenticChatAsync(Question, new GenerationOptions { ReadWithin = TimeSpan.FromSeconds(1) });

        Assert.NotEmpty(brain.Calls[^1].Options!.Tools!);
    }

    private async Task<(TimeSpan Within, List<ChatMessage> Handed)> Queued(TimeSpan left, string? conversation = null)
    {
        var brain = new ReadingBrain();
        var service = Service(brain);
        await using var worker = new InProcessBrainWorker(async ct =>
        {
            await service.StartAsync(ct);
            return new InProcessBrain(service, "Ternary-Bonsai-2-2B");
        });

        var job = new BrainJob("ask-1", Conversation(), Question, 64, false, DateTimeOffset.UtcNow.Add(left), Conversation: conversation);
        await foreach (var _ in worker.GenerateAsync(job, CancellationToken.None)) { }
        return (brain.Calls[^1].Options!.ReadWithin!.Value, brain.Calls[^1].Messages);
    }

    [Fact]
    public async Task A_queued_answer_spends_seconds_reading_however_long_it_has()
    {
        var (within, handed) = await Queued(TimeSpan.FromMinutes(2));

        // Three seconds at a second a token: the question goes in whatever it costs, and history goes.
        Assert.Equal(TimeSpan.FromSeconds(3), within);
        Assert.Equal(["Name"], Said(handed));
    }

    [Fact]
    public async Task A_conversation_the_brain_has_lost_is_read_again_as_far_as_a_moment_goes()
    {
        var (within, handed) = await Queued(TimeSpan.FromMinutes(2), "internal:assistant|talk");

        // Fifteen seconds at a second a token, and nine of them are the question and the system turn.
        Assert.Equal(TimeSpan.FromSeconds(15), within);
        Assert.Equal(["Name"], Said(handed));

        // On a phone at 128 ms a token, the 103 that came back after a kill on 2026-11-07 fit.
        Assert.True(TimeSpan.FromMilliseconds(128 * 103) < within);
    }

    [Fact]
    public async Task An_answer_short_of_time_keeps_two_thirds_of_it_to_write_in()
    {
        var (within, _) = await Queued(TimeSpan.FromSeconds(6));

        Assert.InRange(within, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task A_sum_is_offered_every_tool_when_that_is_the_block_the_brain_has_read()
    {
        var brain = new ReadingBrain { ToolsRead = EveryTool.Length };
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        // The cue asks for the calculator alone, which is a block of its own and 500 tokens from nothing.
        var conversation = Conversation();
        conversation[^1] = new ChatMessage("user", "What is 17 times 4?");
        await foreach (var _ in service.StreamAsync(conversation, new GenerationOptions { ReadWithin = TimeSpan.FromMinutes(2) })) { }

        var (messages, options) = brain.Calls[^1];
        Assert.Equal(EveryTool, options!.Tools!.Select(t => t.Name));
        Assert.Equal(7, Said(messages).Length);
    }

    [Fact]
    public async Task Tools_that_cost_nothing_stay_when_the_history_has_to_go()
    {
        var brain = new ReadingBrain { ToolsRead = EveryTool.Length };
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        var conversation = Conversation();
        conversation[^1] = new ChatMessage("user", "What is 17 times 4?");
        await foreach (var _ in service.StreamAsync(conversation, new GenerationOptions { ReadWithin = TimeSpan.FromSeconds(30) })) { }

        var (messages, options) = brain.Calls[^1];
        Assert.Equal(EveryTool, options!.Tools!.Select(t => t.Name));
        Assert.Equal(["third", "third", "What"], Said(messages));
    }

    [Fact]
    public async Task A_brain_is_given_every_tool_to_read_once_it_is_warm_as_a_tool_turn_sends_them()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), warm: true);

        await service.StartAsync();
        var (seed, tools) = await brain.Primed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await service.AgenticChatAsync(Question);
        var (messages, options) = brain.Calls[^1];
        Assert.Equal(options!.Tools!.Select(t => t.Name), tools!.Select(t => t.Name));
        Assert.Equal(EveryTool, tools!.Select(t => t.Name));
        Assert.Equal(messages[0].Content, seed[0].Content);
        Assert.Equal("system", seed[0].Role);
    }

    [Fact]
    public async Task Reading_ahead_leaves_a_plain_question_without_tools()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), warm: true);

        await service.StartAsync();
        await brain.Primed.Task.WaitAsync(TimeSpan.FromSeconds(30));

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", Question)])) { }

        Assert.Null(brain.Calls[^1].Options!.Tools);
    }

    [Fact]
    public async Task A_tool_turn_leaves_the_question_after_it_without_tools_and_the_next_tool_turn_with_all_of_them()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        await service.AgenticChatAsync("Use the calculator: what is 6789 times 3?");
        Assert.Equal(EveryTool, brain.Calls[^1].Options!.Tools!.Select(t => t.Name));

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", Question)])) { }
        Assert.Null(brain.Calls[^1].Options!.Tools);

        await service.AgenticChatAsync("Use the calculator: what is 6789 times 3?");
        Assert.Equal(EveryTool, brain.Calls[^1].Options!.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task A_cue_leaves_the_question_after_it_without_tools()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", "What is 17 times 4?")])) { }
        Assert.NotNull(brain.Calls[^1].Options!.Tools);

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", Question)])) { }

        Assert.Null(brain.Calls[^1].Options!.Tools);
    }

    [Fact]
    public async Task A_cue_is_offered_what_it_asks_for_and_not_what_the_cue_before_it_asked_for()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", "What is 17 times 4?")])) { }
        await foreach (var _ in service.StreamAsync([new ChatMessage("user", "How is my battery?")])) { }

        Assert.Equal(["get_battery_level"], brain.Calls[^1].Options!.Tools!.Select(t => t.Name));
    }

    [Fact]
    public async Task A_brain_that_keeps_no_system_turns_keeps_its_tools_on_as_before()
    {
        var brain = new Brain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64));

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", "What is 17 times 4?")])) { }
        await foreach (var _ in service.StreamAsync([new ChatMessage("user", Question)])) { }

        Assert.Contains("calculate", brain.Calls[^1].Messages[^1].Content, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nothing_is_read_ahead_without_a_warm_start_or_without_tools()
    {
        var cold = new ReadingBrain();
        await using var noWarmth = Service(cold, new CircleAIToolBridge(() => 64));
        await noWarmth.StartAsync();

        var bare = new ReadingBrain();
        await using var noTools = Service(bare, warm: true);
        await noTools.StartAsync();

        await Task.Delay(200);
        Assert.False(cold.Primed.Task.IsCompleted);
        Assert.False(bare.Primed.Task.IsCompleted);
    }

    [Fact]
    public async Task A_device_short_of_memory_reads_nothing_ahead()
    {
        var pressure = new ManualMemoryPressureSource();
        await pressure.Raise(MemoryPressureLevel.Trim);
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), warm: true, pressure: pressure);

        await service.StartAsync();

        await Task.Delay(200);
        Assert.False(brain.Primed.Task.IsCompleted);
    }

    [Theory]
    [InlineData(8, false)]
    [InlineData(80, true)]
    public async Task A_flat_or_hot_device_reads_nothing_ahead(int battery, bool hot)
    {
        PowerBudgetPolicy.Conditions = () => (battery, hot);
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), warm: true);

        await service.StartAsync();

        await Task.Delay(200);
        Assert.False(brain.Primed.Task.IsCompleted);
    }

    // One skill, found by its one word, so a turn on that word carries its note.
    private static async Task<InMemorySkillStore> CalendarSkill()
    {
        var skills = new InMemorySkillStore();
        await skills.UpsertAsync("calendar", new SkillDraft("Calendar", "Your week", "Look in the agenda view.", ["calendar"]));
        return skills;
    }

    private static bool CarriesTheSkill(IEnumerable<ChatMessage> messages) =>
        messages.Any(m => m.Content.Contains("agenda view", StringComparison.Ordinal));

    [Fact]
    public async Task A_tool_turn_leaves_the_skill_note_off_and_a_plain_turn_keeps_it()
    {
        var brain = new ReadingBrain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), skills: await CalendarSkill());

        await foreach (var _ in service.StreamAsync([new ChatMessage("user", "calendar")])) { }
        Assert.True(CarriesTheSkill(brain.Calls[^1].Messages));

        await service.AgenticChatAsync("calendar");
        Assert.False(CarriesTheSkill(brain.Calls[^1].Messages));
    }

    [Fact]
    public async Task A_brain_told_of_its_tools_in_a_note_keeps_its_skill_note_on_a_tool_turn()
    {
        var brain = new Brain();
        await using var service = Service(brain, new CircleAIToolBridge(() => 64), skills: await CalendarSkill());

        await service.AgenticChatAsync("calendar");

        Assert.True(CarriesTheSkill(brain.Calls[^1].Messages));
    }

    [Fact]
    public void A_read_limit_survives_a_copy()
    {
        var options = new GenerationOptions { ReadWithin = TimeSpan.FromSeconds(40) }.WithPrefixCache().WithTools(null);

        Assert.Equal(TimeSpan.FromSeconds(40), options.ReadWithin);
    }
}
