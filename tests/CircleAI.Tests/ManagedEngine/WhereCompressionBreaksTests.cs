// WhereCompressionBreaksTests.cs
//
// The plan's "where compression breaks", on the real 2B, in each working memory mode - off, and
// TurboQuant at 4, 3 and 2 bits, the keys in the MSE form as BrainFactory runs them:
//   - a coding prompt and an isiZulu prompt, teacher-forced on the pinned fork's tokens: off and 4
//     bits (the default) give every fork token the fork's own margin settles; 3 and 2 are measured;
//   - a tool call, greedy, held to the template's form by ToolCallGrammar as ManagedChatGenerator
//     holds it, and free: off writes a call the reader can run either way, and so does 4 bits held;
//     what every mode writes, held and free, is measured;
//   - one fact recalled from the middle of 8K and 32K tokens: off recalls it, and 4 bits wherever
//     off does; 3 and 2 are measured. That is hours of prefill on this engine, so it runs only when
//     BONSAI2_RECALL names the lengths (BONSAI2_RECALL=8192,32768).
//
// Needs the 2B on this machine (says SKIPPED otherwise). BONSAI2_KV_STEPS steps of each prompt (8
// unless set; 32 is all the fork recorded). Run these in Release.

using System.Diagnostics;
using System.Text;
using CircleAI.Core;
using CircleAI.Core.Compression;
using CircleAI.Inference;
using CircleAI.Inference.Managed;
using CircleAI.Inference.Managed.Gguf;
using CircleAI.Inference.Managed.Model;
using CircleAI.Inference.Managed.Runtime;
using CircleAI.Inference.Managed.Sampling;
using CircleAI.Inference.Managed.Text;
using CircleAI.Tools;
using Xunit;
using Xunit.Abstractions;

namespace CircleAI.Tests;

public sealed class WhereCompressionBreaksTests(ITestOutputHelper output)
{
    private static readonly KvCompressionMode[] Modes =
    [
        KvCompressionMode.Off, KvCompressionMode.TurboQuant4Bit, KvCompressionMode.TurboQuant3Bit, KvCompressionMode.TurboQuant2Bit,
    ];

    // The tool the fixtures were made with (templates.json, case "tool").
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

    private static int Setting(string name, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(name), out var value) && value > 0 ? value : fallback;

    private static int[] PromptOf(System.Text.Json.JsonDocument logits, string name) =>
        [.. Case(logits, name).GetProperty("prompt_ids").EnumerateArray().Select(e => e.GetInt32())];

    private static System.Text.Json.JsonElement Case(System.Text.Json.JsonDocument logits, string name) =>
        logits.RootElement.GetProperty("cases").EnumerateArray().First(c => c.GetProperty("name").GetString() == name);

    private static int ArgMax(ReadOnlySpan<float> values)
    {
        var best = 0;
        for (var i = 1; i < values.Length; i++)
        {
            if (values[i] > values[best]) best = i;
        }
        return best;
    }

    [Fact]
    public void A_coding_prompt_and_an_isiZulu_prompt_measured_in_each_working_memory_mode()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        using var fixture = Bonsai2Model.Fixture("logits.json");
        var stepCount = Setting("BONSAI2_KV_STEPS", 8);
        var logits = new float[model.Config.Vocabulary];

        foreach (var name in new[] { "code", "isizulu" })
        {
            var prompt = PromptOf(fixture, name);
            var steps = Case(fixture, name).GetProperty("steps").EnumerateArray().Take(stepCount).ToList();
            var agreement = new Dictionary<KvCompressionMode, (int Agree, int Judged)>();
            foreach (var mode in Modes)
            {
                var state = model.NewState(prompt.Length + steps.Count + 1, mode, TurboQuantForm.Mse);
                for (var i = 0; i < prompt.Length; i++) model.Forward(prompt[i], state, i == prompt.Length - 1 ? logits : default);
                int agree = 0, judged = 0;
                var worst = 0f;
                for (var s = 0; s < steps.Count; s++)
                {
                    var step = steps[s];
                    var expected = step.GetProperty("token").GetInt32();
                    foreach (var pair in step.GetProperty("top").EnumerateArray())
                        worst = MathF.Max(worst, MathF.Abs(logits[pair[0].GetInt32()] - pair[1].GetSingle()));
                    // A step is judged only where the fork's own top two are 0.3 apart (Qwen35ModelTests).
                    if (step.GetProperty("gap").GetSingle() >= 0.3f)
                    {
                        judged++;
                        if (ArgMax(logits) == expected) agree++;
                    }
                    if (s + 1 < steps.Count) model.Forward(expected, state, logits);
                }
                Assert.All(logits, l => Assert.True(float.IsFinite(l)));
                agreement[mode] = (agree, judged);
                output.WriteLine($"{name}, {mode,-15}: the fork's token {agree} of {judged}, largest top-20 drift {worst:F3}");
            }

            Assert.True(agreement[KvCompressionMode.Off].Judged > 0, $"no step of {name} was judged");
            Assert.Equal(agreement[KvCompressionMode.Off].Judged, agreement[KvCompressionMode.Off].Agree);
            Assert.Equal(agreement[KvCompressionMode.TurboQuant4Bit].Judged, agreement[KvCompressionMode.TurboQuant4Bit].Agree); // the default
        }
    }

    [Fact]
    public void A_tool_call_in_each_working_memory_mode_held_to_its_form_and_free()
    {
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        using var file = GgufFile.Open(path);
        using var pool = new WorkerPool(Math.Max(1, Environment.ProcessorCount));
        var model = Qwen35Model.Load(file, pool);
        var tokenizer = ByteLevelBpe.FromGguf(file);
        using var fixture = Bonsai2Model.Fixture("logits.json");
        // The fork's own rendering of the question with get_weather offered.
        var prompt = PromptOf(fixture, "tool");
        var endOfTurn = new HashSet<int>();
        foreach (var text in new[] { "<|im_end|>", "<|endoftext|>" })
        {
            if (tokenizer.TryGetSpecial(text, out var id)) endOfTurn.Add(id);
        }
        // The grammar ManagedChatGenerator holds a call to when get_weather is offered (GrammarFor).
        var grammar = new ToolCallGrammar(new[] { new ToolCallGrammar.Function("get_weather", [new ToolCallGrammar.Parameter("location", Required: true)]) });
        ReadOnlySpan<byte> Piece(int token) => tokenizer.Piece(token, special: false);

        var runnable = new Dictionary<(KvCompressionMode Mode, bool Held), string?>();
        foreach (var mode in Modes)
        {
            foreach (var held in new[] { true, false })
            {
                var text = Write(model, prompt, mode, held ? grammar : null, Piece, endOfTurn);
                var call = ToolCallReader.Read(text, [Weather]);
                var location = call is not null && call.ToolName == "get_weather" && ToolCallReader.IsRunnable(call, [Weather], out _)
                    ? call.Arguments["location"]?.ToString()
                    : null;
                runnable[(mode, held)] = location;
                var verdict = location is not null ? $"get_weather({location})" : text.Contains(ToolCallGrammar.Opening, StringComparison.Ordinal) ? "a call that cannot run" : "no call";
                output.WriteLine($"{mode,-15} {(held ? "held" : "free")}: {verdict} - {text.Replace("\n", "\\n")}");
            }
        }

        // The fork wrote get_weather(Durban); fp16 writes it with the grammar or without, and the
        // default writes a call that runs once the grammar holds it.
        Assert.Equal("Durban", runnable[(KvCompressionMode.Off, false)]);
        Assert.Equal("Durban", runnable[(KvCompressionMode.Off, true)]);
        Assert.NotNull(runnable[(KvCompressionMode.TurboQuant4Bit, true)]);
    }

    // Greedy from the prompt in one mode, until the turn ends or 48 tokens; from the token after
    // "<tool_call>" the call is held to its form when a grammar is given - the steps
    // ManagedChatGenerator takes, without its sampler.
    private static string Write(Qwen35Model model, int[] prompt, KvCompressionMode mode, ToolCallGrammar? grammar,
        ToolCallGrammar.PieceOf piece, HashSet<int> endOfTurn)
    {
        const int most = 48;
        var state = model.NewState(prompt.Length + most + 1, mode, TurboQuantForm.Mse);
        var logits = new float[model.Config.Vocabulary];
        for (var i = 0; i < prompt.Length; i++) model.Forward(prompt[i], state, i == prompt.Length - 1 ? logits : default);

        var bytes = new List<byte>();
        ToolCallGrammar.State? call = null;
        for (var n = 0; n < most; n++)
        {
            if (grammar is not null && call is { } held && grammar.Mask(logits, held, piece, endOfTurn) == ToolCallGrammar.Masked.Nothing)
                call = null;
            var token = ArgMax(logits);
            if (endOfTurn.Contains(token)) break;
            if (call is { } open) call = grammar!.TryAdvance(open, piece(token), out var next) ? next : null;
            bytes.AddRange(piece(token).ToArray());
            if (grammar is not null && call is null && Encoding.UTF8.GetString(bytes.ToArray()).EndsWith(ToolCallGrammar.Opening, StringComparison.Ordinal))
                call = grammar.Start;
            if (n + 1 < most) model.Forward(token, state, logits);
        }
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimStart();
    }

    private const string Code = "7429";
    private const string Needle = "Somewhere in all of this, the secret code is " + Code + ".";
    private const string Ask = "\n\nWhat is the secret code? Answer with the number only.";

    [Fact]
    public async Task One_fact_recalled_from_the_middle_of_a_long_prompt_in_each_working_memory_mode()
    {
        var lengths = (Environment.GetEnvironmentVariable("BONSAI2_RECALL") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToArray();
        if (lengths.Length == 0)
        {
            output.WriteLine("SKIPPED: recall at length is hours of prefill on this engine; set BONSAI2_RECALL=8192,32768 to run it.");
            return;
        }
        var path = Bonsai2Model.TwoB();
        if (path is null)
        {
            output.WriteLine(Bonsai2Model.Missing);
            return;
        }

        var greedy = new GenerationOptions { Temperature = 0, MaxTokens = 16, Budget = PowerBudget.None, Effort = ReasoningEffort.Off };
        foreach (var length in lengths)
        {
            ChatMessage[]? question = null;
            var recalled = new Dictionary<KvCompressionMode, bool>();
            foreach (var mode in Modes)
            {
                using var g = ManagedChatGenerator.Load(path, new ManagedGeneratorOptions
                {
                    ContextLength = length + 64, KvMode = mode, KeyForm = TurboQuantForm.Mse, PrimedStartDirectory = "",
                });
                question ??= new ChatMessage[] { new("user", Haystack(g, length)) };
                var clock = Stopwatch.StartNew();
                var answer = await g.GenerateAsync(question, greedy);
                recalled[mode] = answer.Contains(Code, StringComparison.Ordinal);
                output.WriteLine($"{length} tokens, {mode,-15}: {(recalled[mode] ? "recalled" : "lost")} in {clock.Elapsed.TotalMinutes:F1} min - {answer.Trim()}");
            }

            Assert.True(recalled[KvCompressionMode.Off], $"the 2B in fp16 does not recall the fact at {length} tokens, so no mode can be judged there");
            Assert.True(recalled[KvCompressionMode.TurboQuant4Bit], $"4 bits - the default - lost at {length} tokens what fp16 keeps");
        }
    }

    // Plain sentences that say nothing in particular, as many as fit in `tokens` with the needle
    // halfway through them and the question after - the fixtures' "long" case drawn out.
    private static string Haystack(ManagedChatGenerator g, int tokens)
    {
        string[] towns = ["Durban", "Polokwane", "Kimberley", "Mthatha", "George", "Upington", "Mbombela", "Bloemfontein"];
        string[] goods = ["maize", "oranges", "blankets", "pumpkins", "beans", "candles", "rope", "salt"];
        var random = new Random(11);
        var sentences = new List<string>();
        string Text(int count)
        {
            var some = sentences.Take(count).ToList();
            some.Insert(count / 2, Needle);
            return string.Join(" ", some) + Ask;
        }
        int Count(int count) => g.Tokenizer.Encode(g.RenderPrompt([new ChatMessage("user", Text(count))])).Count;

        // Grown 64 sentences at a time past the length, then the most that fit, found by halving.
        var low = 0;
        while (true)
        {
            for (var k = 0; k < 64; k++)
                sentences.Add($"On day {sentences.Count + 1} the market in {towns[random.Next(towns.Length)]} sold {random.Next(2, 90)} bags of {goods[random.Next(goods.Length)]} before noon.");
            if (Count(sentences.Count) > tokens) break;
            low = sentences.Count;
        }
        var high = sentences.Count;
        while (high - low > 1)
        {
            var middle = (low + high) / 2;
            if (Count(middle) <= tokens) low = middle;
            else high = middle;
        }
        return Text(Math.Max(low, 1));
    }
}
