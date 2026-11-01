// ToolCallGrammarTests.cs
//
// The XML tool-call form of the Qwen3.5 template, as the sampler holds a call to it: the template's
// own rendering of a call is accepted whole; what the model card warns of - a missing close, a
// value swallowing its own "</parameter>", a call that never ends - is refused at the byte where it
// goes wrong; and masking leaves only the tokens that keep a call well formed.

using System.Text;
using CircleAI.Inference.Managed.Sampling;
using Xunit;

namespace CircleAI.Tests;

public sealed class ToolCallGrammarTests
{
    // How the 2B's template writes a call after "<tool_call>" (its lines for message.tool_calls).
    private const string Weather = "\n<function=get_weather>\n<parameter=location>\nParis\n</parameter>\n</function>\n</tool_call>";

    private static ToolCallGrammar.State? Run(ToolCallGrammar grammar, string text) =>
        grammar.TryAdvance(grammar.Start, Encoding.UTF8.GetBytes(text), out var state) ? state : null;

    // The byte at which the text stops fitting, or -1 when all of it fits.
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

    [Fact]
    public void The_templates_own_call_is_accepted_and_the_turn_may_end_after_it()
    {
        var grammar = new ToolCallGrammar();
        var state = Run(grammar, Weather);
        Assert.NotNull(state);
        Assert.True(state!.Value.MayEnd);

        // A newline after the close may still end the turn; mid-call it may not.
        Assert.True(Run(grammar, Weather + "\n")!.Value.MayEnd);
        Assert.False(Run(grammar, "\n<function=get_weather>\n")!.Value.MayEnd);
    }

    [Fact]
    public void A_value_holds_any_text_over_many_lines_but_never_its_own_closing_tag()
    {
        var grammar = new ToolCallGrammar();
        const string open = "\n<function=note>\n<parameter=text>\n";
        Assert.NotNull(Run(grammar, open + "line one\n<b>bold</b> and </param but not more\n\nlast\n</parameter>\n</function>\n</tool_call>"));
        Assert.NotNull(Run(grammar, open + "\n</parameter>\n</function>\n</tool_call>")); // an empty value

        var swallowed = open + "x\n</parameter>y";
        Assert.Equal(swallowed.Length - 1, FirstMisfit(grammar, swallowed));
    }

    [Fact]
    public void JSON_and_a_missing_close_are_refused_where_they_begin()
    {
        var grammar = new ToolCallGrammar();
        Assert.Equal(1, FirstMisfit(grammar, "\n{\"name\": \"get_weather\"}"));

        // The template closes </function> before </tool_call>; skipping it fails at the 't'.
        var skipped = "\n<function=get_weather>\n<parameter=location>\nParis\n</parameter>\n</tool_call>";
        Assert.Equal(skipped.IndexOf("</tool_call>", StringComparison.Ordinal) + 2, FirstMisfit(grammar, skipped));

        Assert.Equal(11, FirstMisfit(grammar, "\n<function=>")); // a name cannot be empty
        Assert.Equal(14, FirstMisfit(grammar, "\n<function=get weather>")); // nor hold a space
    }

    [Fact]
    public void Parameters_and_values_have_a_ceiling_that_forces_the_call_shut()
    {
        var grammar = new ToolCallGrammar(maxParameters: 1, maxValueBytes: 4);
        Assert.NotNull(Run(grammar, "\n<function=f>\n<parameter=a>\nabcd\n</parameter>\n</function>\n</tool_call>"));

        var second = "\n<function=f>\n<parameter=a>\n1\n</parameter>\n<parameter=b>";
        Assert.Equal(second.LastIndexOf('p'), FirstMisfit(grammar, second));

        var tooLong = "\n<function=f>\n<parameter=a>\nabcde";
        Assert.Equal(tooLong.Length - 1, FirstMisfit(grammar, tooLong));
    }

    [Fact]
    public void Known_tools_narrow_the_function_name_to_one_of_theirs()
    {
        var grammar = new ToolCallGrammar(["get_weather", "get_time"]);
        Assert.NotNull(Run(grammar, "\n<function=get_time>\n</function>\n</tool_call>"));
        Assert.Equal(11, FirstMisfit(grammar, "\n<function=set_time>"));
        Assert.Equal(15, FirstMisfit(grammar, "\n<function=get_>")); // a prefix of a name is not a name
        Assert.Throws<ArgumentException>(() => new ToolCallGrammar(["has space"]));
    }

    [Fact]
    public void A_second_call_may_follow_the_first()
    {
        var grammar = new ToolCallGrammar();
        var state = Run(grammar, Weather + "\n<tool_call>\n<function=get_time>\n</function>\n</tool_call>");
        Assert.True(state!.Value.MayEnd);
        Assert.Equal(Weather.Length + 1, FirstMisfit(grammar, Weather + "\nAnd"));
    }

    [Fact]
    public void Masking_leaves_only_the_tokens_that_keep_the_call_well_formed()
    {
        // A tiny vocabulary: 0 "{", 1 "\n<function=", 2 "\n", 3 the end of the turn, 4 "<", 5 "".
        string[] texts = ["{", "\n<function=", "\n", "<|im_end|>", "<", ""];
        var pieces = texts.Select(Encoding.UTF8.GetBytes).ToArray();
        ReadOnlySpan<byte> Piece(int token) => token == 3 ? default : pieces[token];
        var endOfTurn = new HashSet<int> { 3 };
        var grammar = new ToolCallGrammar();

        // Right after "<tool_call>" the model would rather write JSON: overruled, and only the
        // tokens that can begin "\n<function=" are left.
        float[] logits = [5f, 1f, 2f, 4f, 3f, 0f];
        Assert.Equal(ToolCallGrammar.Masked.Overruled, grammar.Mask(logits, grammar.Start, Piece, endOfTurn));
        Assert.Equal([float.NegativeInfinity, 1f, 2f, float.NegativeInfinity, float.NegativeInfinity, float.NegativeInfinity], logits);

        // After a complete call the turn may end, and the model's own choice to end it is kept.
        var done = Run(grammar, Weather)!.Value;
        logits = [0f, 1f, 2f, 9f, 3f, 0f];
        Assert.Equal(ToolCallGrammar.Masked.Kept, grammar.Mask(logits, done, Piece, endOfTurn));
        Assert.Equal(9f, logits[3]);
        Assert.Equal(float.NegativeInfinity, logits[0]);

        // Nothing fits: the logits are left alone and the caller is told.
        var middle = Run(grammar, "\n<function=f")!.Value;
        logits = [1f, 1f, 1f, 1f, 1f, 1f];
        Assert.Equal(ToolCallGrammar.Masked.Nothing, grammar.Mask(logits, middle with { Phase = ToolCallGrammar.Phase.CloseCall, At = 0 }, i => i == 3 ? default : "x"u8, endOfTurn));
        Assert.All(logits, l => Assert.Equal(1f, l));
    }
}
