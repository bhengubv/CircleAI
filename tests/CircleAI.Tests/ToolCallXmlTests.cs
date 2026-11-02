// ToolCallXmlTests.cs
//
// The XML tool-call form Qwen3.5 writes (Bonsai 2 and the MNN brains alike), end to end on the
// parts that need no model: the reader takes the form and types its values by what each tool
// declares; the stream detector stops a call in that form being spoken; and the grammar, given
// the tools' signatures, lets out only a call the tool can run - its own parameter names, each
// once, every required one, a fixed set's values only.

using System.Collections.Generic;
using System.Linq;
using System.Text;
using CircleAI.Assistant;
using CircleAI.Inference.Managed.Sampling;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public sealed class ToolCallXmlTests
{
    private static readonly IReadOnlyList<ToolDefinition> Tools =
    [
        new ToolDefinition
        {
            Name = "get_weather",
            Description = "Current weather for a place.",
            Parameters = new Dictionary<string, ToolParameter>
            {
                ["location"] = new() { Type = "string", Description = "City name" },
                ["unit"] = new() { Type = "string", Description = "Scale", Enum = ["celsius", "fahrenheit"] },
            },
            RequiredParameters = ["location"],
        },
        new ToolDefinition
        {
            Name = "book",
            Description = "Book seats.",
            Parameters = new Dictionary<string, ToolParameter>
            {
                ["seats"] = new() { Type = "integer", Description = "How many." },
                ["price"] = new() { Type = "number", Description = "Each." },
                ["tiny"] = new() { Type = "number", Description = "A small number." },
                ["window"] = new() { Type = "boolean", Description = "A window seat." },
                ["tags"] = new() { Type = "array", Description = "Labels." },
                ["who"] = new() { Type = "object", Description = "The traveller." },
                ["pin"] = new() { Type = "string", Description = "Digits that stay text." },
                ["note"] = new() { Type = "integer", Description = "Nothing, here." },
            },
            RequiredParameters = ["seats"],
        },
        new ToolDefinition
        {
            Name = "get_battery_level",
            Description = "The battery.",
            Parameters = new Dictionary<string, ToolParameter>(),
            RequiredParameters = [],
        },
    ];

    // ── reading the form ────────────────────────────────────────────────

    [Fact]
    public void The_form_qwen35_writes_is_read()
    {
        var call = ToolCallReader.Read(
            "<tool_call>\n<function=get_weather>\n<parameter=location>\nDurban\n</parameter>\n</function>\n</tool_call>");

        Assert.NotNull(call);
        Assert.Equal("get_weather", call!.ToolName);
        Assert.Equal("Durban", call.Arguments["location"]);
    }

    [Fact]
    public void A_value_keeps_its_own_lines_and_loses_only_the_forms_line_breaks()
    {
        var call = ToolCallReader.Read(
            "<tool_call>\n<function=note>\n<parameter=text>\nline one\n\nline two\n</parameter>\n</function>\n</tool_call>");

        Assert.Equal("line one\n\nline two", call!.Arguments["text"]);
    }

    [Fact]
    public void Prose_before_the_call_and_two_calls_are_both_read()
    {
        // The template lets the model reason in words BEFORE a call, and two calls follow each other.
        var calls = ToolCallReader.ReadAll(
            "Checking both.\n\n<tool_call>\n<function=get_weather>\n<parameter=location>\nDurban\n</parameter>\n</function>\n</tool_call>"
            + "\n<tool_call>\n<function=get_weather>\n<parameter=location>\nCape Town\n</parameter>\n</function>\n</tool_call>");

        Assert.Equal(["Durban", "Cape Town"], calls.Select(c => (string)c.Arguments["location"]!).ToArray());
    }

    [Fact]
    public void A_call_the_token_ceiling_cut_off_still_yields_what_it_had_decided()
    {
        var call = ToolCallReader.Read("<tool_call>\n<function=get_weather>\n<parameter=location>\nDurb");

        Assert.Equal("get_weather", call!.ToolName);
        Assert.Equal("Durb", call.Arguments["location"]);
        Assert.Empty(ToolCallReader.ReadAll("<tool_call>\n<function=get_wea")); // a name cut in half is nothing
    }

    [Fact]
    public void The_bare_block_is_read_when_the_tag_was_dropped_and_never_twice_when_it_was_not()
    {
        Assert.Equal("get_battery_level",
            ToolCallReader.Read("<function=get_battery_level>\n</function>")!.ToolName);
        Assert.Single(ToolCallReader.ReadAll(
            "<tool_call>\n<function=get_battery_level>\n</function>\n</tool_call>"));
    }

    [Fact]
    public void Values_are_typed_by_the_type_their_tool_declares()
    {
        // The form writes every value with Python's str(): True, None, 2.5, a JSON list.
        var call = ToolCallReader.Read(
            "<tool_call>\n<function=book>\n"
            + "<parameter=seats>\n2\n</parameter>\n<parameter=price>\n99.5\n</parameter>\n"
            + "<parameter=tiny>\n1e-07\n</parameter>\n<parameter=window>\nTrue\n</parameter>\n"
            + "<parameter=tags>\n[\"a\", 3]\n</parameter>\n<parameter=who>\n{\"name\": \"Thandi\"}\n</parameter>\n"
            + "<parameter=pin>\n0123\n</parameter>\n<parameter=note>\nNone\n</parameter>\n"
            + "</function>\n</tool_call>", Tools)!;

        Assert.Equal(2L, call.Arguments["seats"]);
        Assert.Equal(99.5d, call.Arguments["price"]);
        Assert.Equal(1e-07d, call.Arguments["tiny"]);
        Assert.Equal(true, call.Arguments["window"]);
        Assert.Equal("[\"a\", 3]", call.Arguments["tags"]);              // the JSON text, as the JSON form hands it over
        Assert.Equal("{\"name\": \"Thandi\"}", call.Arguments["who"]);
        Assert.Equal("0123", call.Arguments["pin"]);                    // a string stays text, digits and all
        Assert.Null(call.Arguments["note"]);                            // None is null for a non-string
        Assert.True(ToolCallReader.IsRunnable(call, Tools, out _));
    }

    [Fact]
    public void Without_the_tools_every_value_stays_text()
    {
        var call = ToolCallReader.Read("<tool_call>\n<function=book>\n<parameter=seats>\n2\n</parameter>\n</function>\n</tool_call>");
        Assert.Equal("2", call!.Arguments["seats"]);
    }

    [Fact]
    public void The_json_form_still_reads_beside_it()
    {
        var call = ToolCallReader.Read("<tool_call>{\"name\":\"get_weather\",\"arguments\":{\"location\":\"Durban\"}}</tool_call>", Tools);
        Assert.Equal("Durban", call!.Arguments["location"]);
    }

    [Fact]
    public void What_the_2Bs_own_template_writes_reads_back_value_for_value()
    {
        // The template's rendering of a call with every kind of value (fixtures/bonsai2, made by
        // jinja2 from the 2B's own template) - the form a call takes in the conversation's history.
        using var templates = Bonsai2Model.Fixture("templates.json");
        var rendered = templates.RootElement.GetProperty("cases").EnumerateArray()
            .First(c => c.GetProperty("name").GetString() == "argument_types").GetProperty("rendered").GetString()!;

        // ONLY THE ASSISTANT'S TURN, as a reply reaches the reader: the system turn above it
        // teaches the form with an example call of its own (<function=example_function_name>).
        var reply = rendered[rendered.LastIndexOf("<|im_start|>assistant\n", System.StringComparison.Ordinal)..];
        Assert.Equal("example_function_name", ToolCallReader.Read(rendered)!.ToolName);

        var call = ToolCallReader.Read(reply, Tools)!;

        Assert.Equal("book", call.ToolName);
        Assert.Equal(2L, call.Arguments["seats"]);
        Assert.Equal(99.5d, call.Arguments["price"]);
        Assert.Equal(true, call.Arguments["window"]);
        Assert.Equal("say \"hi\"\n\ttab", call.Arguments["quote"]);
        Assert.Equal("Ngiyabonga \U0001F600", call.Arguments["unicode"]);
    }

    // ── noticing it mid-stream ──────────────────────────────────────────

    [Theory]
    [InlineData("<tool_call>\n<function=get_weather>")]
    [InlineData("Let me check.\n<function=get_weather>\n<parameter=location>")]
    public void The_stream_detector_sees_the_xml_form(string streamed) => Assert.True(ToolCall.Looks(streamed));

    [Fact]
    public void Prose_about_a_function_element_is_not_a_call()
    {
        Assert.False(ToolCall.Looks("In XML a <function> element names the call."));
    }

    /// <summary>A head that streams the given pieces and answers the tool pass with a fixed text.</summary>
    private sealed class StreamingBrain(string[] pieces, string tooled) : IBrain
    {
        public int ToolPasses { get; private set; }
        public int MaxImageEdge => 0;
        public System.Threading.Tasks.Task<BrainState> StateAsync(System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult(new BrainState(true, ""));
        public System.Threading.Tasks.Task<string> AskAsync(string prompt, System.Action<string>? token = null, System.Threading.CancellationToken ct = default)
        {
            foreach (var piece in pieces) token?.Invoke(piece);
            return System.Threading.Tasks.Task.FromResult(string.Concat(pieces));
        }
        public System.Threading.Tasks.Task<string> SeeAsync(string question, byte[] image, System.Action<string>? token = null, System.Threading.CancellationToken ct = default) =>
            System.Threading.Tasks.Task.FromResult("");
        public System.Threading.Tasks.Task<string> AskWithToolsAsync(string prompt, System.Threading.CancellationToken ct = default)
        {
            ToolPasses++;
            return System.Threading.Tasks.Task.FromResult(tooled);
        }
    }

    [Fact]
    public async System.Threading.Tasks.Task The_shared_turn_says_none_of_an_xml_call_and_answers_from_the_tool_pass()
    {
        // The pieces the C# engine streams for a Qwen3.5 call: its <tool_call> token whole, then
        // the call. Nothing of it is shown or spoken; the tool pass answers instead.
        var brain = new StreamingBrain(
            ["Let me check. ", "<tool_call>", "\n<function=get_weather>", "\n<parameter=location>\nDurban\n</parameter>\n</function>\n</tool_call>"],
            tooled: "It is 24 C and sunny in Durban.");
        var shown = new List<string>();
        var started = 0;

        var reply = await brain.AskMaybeToolsAsync("What is the weather in Durban?", shown.Add, onToolStarted: () => started++);

        Assert.True(reply.UsedTools);
        Assert.Equal("It is 24 C and sunny in Durban.", reply.Text);
        Assert.Equal(["Let me check. "], shown);
        Assert.Equal(1, started);
        Assert.Equal(1, brain.ToolPasses);
    }

    // ── holding a call to what the tool can run ─────────────────────────

    private static ToolCallGrammar Signatures() => new(
    [
        new ToolCallGrammar.Function("get_weather",
        [
            new ToolCallGrammar.Parameter("location", Required: true),
            new ToolCallGrammar.Parameter("loc_hint"),
            new ToolCallGrammar.Parameter("unit", Values: ["celsius", "fahrenheit"]),
        ]),
        new ToolCallGrammar.Function("get_battery_level", []),
    ]);

    private static int FirstMisfit(ToolCallGrammar grammar, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        var state = grammar.Start;
        for (var i = 0; i < bytes.Length; i++)
        {
            if (!grammar.TryAdvance(state, bytes.AsSpan(i, 1), out state)) return i;
        }
        return -1;
    }

    private static ToolCallGrammar.State? Run(ToolCallGrammar grammar, string text) =>
        grammar.TryAdvance(grammar.Start, Encoding.UTF8.GetBytes(text), out var state) ? state : null;

    [Fact]
    public void A_call_the_tool_can_run_is_accepted_whole()
    {
        var state = Run(Signatures(),
            "\n<function=get_weather>\n<parameter=unit>\ncelsius\n</parameter>\n<parameter=location>\nDurban\n</parameter>\n</function>\n</tool_call>");
        Assert.True(state!.Value.MayEnd);
    }

    [Fact]
    public void A_parameter_name_the_tool_does_not_take_is_refused_where_it_goes_wrong()
    {
        var text = "\n<function=get_weather>\n<parameter=city";
        Assert.Equal(text.IndexOf("city", System.StringComparison.Ordinal), FirstMisfit(Signatures(), text));
    }

    [Fact]
    public void The_call_cannot_close_before_every_required_parameter_is_in_it()
    {
        var text = "\n<function=get_weather>\n<parameter=unit>\ncelsius\n</parameter>\n</";
        Assert.Equal(text.Length - 1, FirstMisfit(Signatures(), text));
    }

    [Fact]
    public void A_parameter_is_given_once()
    {
        // Refused at the first byte only the given name could follow: "loc" may still become
        // loc_hint, "loca" can only become location again.
        var text = "\n<function=get_weather>\n<parameter=location>\nDurban\n</parameter>\n<parameter=location>";
        Assert.Equal(text.LastIndexOf("location", System.StringComparison.Ordinal) + 3, FirstMisfit(Signatures(), text));

        // A name that only a given one could finish is never begun: "loc" leads on to loc_hint.
        Assert.Equal(-1, FirstMisfit(Signatures(), "\n<function=get_weather>\n<parameter=location>\nx\n</parameter>\n<parameter=loc_"));
    }

    [Fact]
    public void A_fixed_set_takes_one_of_its_values_spelled_as_declared()
    {
        var open = "\n<function=get_weather>\n<parameter=location>\nx\n</parameter>\n<parameter=unit>\n";
        Assert.Equal(open.Length, FirstMisfit(Signatures(), open + "kelvin"));
        Assert.Equal(open.Length, FirstMisfit(Signatures(), open + "Celsius"));
        var cut = open + "cels\n";
        Assert.Equal(cut.Length - 1, FirstMisfit(Signatures(), cut)); // a prefix of a value is not one
    }

    [Fact]
    public void A_tool_that_takes_nothing_closes_at_once_and_takes_no_parameter()
    {
        Assert.True(Run(Signatures(), "\n<function=get_battery_level>\n</function>\n</tool_call>")!.Value.MayEnd);
        var text = "\n<function=get_battery_level>\n<p";
        Assert.Equal(text.Length - 1, FirstMisfit(Signatures(), text));
    }

    [Fact]
    public void Only_the_offered_tools_can_be_named()
    {
        Assert.Equal(11, FirstMisfit(Signatures(), "\n<function=set_alarm>"));
        Assert.False(ToolCallGrammar.CanCarry("has space"));
        Assert.True(ToolCallGrammar.CanCarry("get_weather"));
    }
}
