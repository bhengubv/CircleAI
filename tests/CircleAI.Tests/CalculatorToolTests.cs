// CalculatorToolTests.cs
//
// The calculator, from the model's side of the seam: the tool the bridge
// exposes, what it returns, and that an arithmetic question actually reaches it.
//
// The evaluator itself is covered by ArithmeticTests; this is the wiring - a
// tool a 0.6B can call, because it cannot add ("12 times 8" -> "6" on a P30).

using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CircleAI.Assistant;
using CircleAI.Tools;
using Xunit;

namespace CircleAI.Tests;

public class CalculatorToolTests
{
    private static async Task<ToolResult> Call(string expression)
    {
        var bridge = new CircleAIToolBridge();
        return await bridge.InvokeAsync(new ToolInvocation
        {
            ToolName  = "calculate",
            Arguments = new Dictionary<string, object?> { ["expression"] = expression },
        });
    }

    [Fact]
    public void The_calculator_is_one_of_the_tools_offered()
    {
        var bridge = new CircleAIToolBridge();
        Assert.Contains(bridge.AvailableTools, t => t.Name == "calculate");

        var calc = bridge.AvailableTools.First(t => t.Name == "calculate");
        Assert.Contains("expression", calc.RequiredParameters);

        // The Maths tool-cue serves "arithmetic"/"multiply"; the description has
        // to carry one or the cue will never offer this tool.
        Assert.Contains("arithmetic", calc.Description, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_case_the_phone_got_wrong_comes_back_right()
    {
        var result = await Call("12 * 8");
        Assert.True(result.Success);
        Assert.Equal("96", result.Result?.ToString());
    }

    [Fact]
    public async Task A_string_that_is_not_a_sum_is_refused_with_a_sentence()
    {
        var result = await Call("SELECT * FROM users");
        Assert.False(result.Success);
        Assert.Contains("not a sum", result.Error ?? "", System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Dividing_by_zero_fails_rather_than_returning_infinity()
    {
        var result = await Call("5 / 0");
        Assert.False(result.Success);
    }

    [Fact]
    public async Task A_missing_expression_is_a_clean_failure()
    {
        var bridge = new CircleAIToolBridge();
        var result = await bridge.InvokeAsync(new ToolInvocation
        {
            ToolName  = "calculate",
            Arguments = new Dictionary<string, object?>(),
        });
        Assert.False(result.Success);
    }

    [Theory]
    [InlineData("times")]
    [InlineData("plus")]
    [InlineData("minus")]
    [InlineData("divided")]
    [InlineData("multiplied")]
    public void An_arithmetic_operator_is_a_tool_cue(string op)
    {
        // "12 times 8" carries no "calculate"/"how many" - only an operator - so
        // unless the operator is itself a cue, the tool block is never offered and
        // the model answers from its own (wrong) head. Reached by reflection: the
        // cue table is private, and the thing worth pinning is that the operator
        // is IN it.
        var type   = typeof(CircleAI.Hosting.AIService);
        var cues   = type.GetField("ToolCues", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null);
        var phrases = ((System.Collections.IEnumerable)cues!)
            .Cast<object>()
            .Select(c => (string)c.GetType().GetProperty("Phrase")!.GetValue(c)!)
            .ToList();

        Assert.Contains(op, phrases);
    }
}
