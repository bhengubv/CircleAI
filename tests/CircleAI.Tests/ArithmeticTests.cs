// ArithmeticTests.cs
//
// The calculator the phone's model needs. Measured on a P30 2026-09-14: the
// 0.6B answered "what is 12 times 8" with "6". A tool does the sum instead, and
// these pin that it does it right and that a malicious or malformed string is
// rejected rather than run.

using System;
using CircleAI.Assistant;
using Xunit;

namespace CircleAI.Tests;

public class ArithmeticTests
{
    [Theory]
    [InlineData("12 * 8", 96)]
    [InlineData("12*8", 96)]
    [InlineData("100 / 4", 25)]
    [InlineData("7 + 5", 12)]
    [InlineData("20 - 6", 14)]
    [InlineData("(2 + 3) * 4", 20)]
    [InlineData("2 + 3 * 4", 14)]          // precedence: * before +
    [InlineData("2 * 3 + 4", 10)]
    [InlineData("-5 + 3", -2)]
    [InlineData("2 * -3", -6)]
    [InlineData("3.5 + 1.5", 5)]
    [InlineData("10 / 4", 2.5)]
    [InlineData("((1 + 2) * (3 + 4))", 21)]
    public void It_works_the_sum_out(string expression, double expected)
        => Assert.Equal(expected, Arithmetic.Evaluate(expression), precision: 9);

    [Fact]
    public void The_case_the_phone_got_wrong()
    {
        // The whole reason this exists. The model said "6"; the tool says 96.
        Assert.Equal("96", Arithmetic.Format(Arithmetic.Evaluate("12 * 8")));
    }

    [Theory]
    [InlineData(96.0, "96")]              // a whole number loses its point
    [InlineData(2.5, "2.5")]
    [InlineData(25.0, "25")]
    [InlineData(-2.0, "-2")]
    public void The_answer_reads_like_a_person_wrote_it(double value, string shown)
        => Assert.Equal(shown, Arithmetic.Format(value));

    [Fact]
    public void Floating_point_tails_are_trimmed()
    {
        // 0.1 + 0.2 is 0.30000000000000004 in a double; a person expects 0.3.
        Assert.Equal("0.3", Arithmetic.Format(Arithmetic.Evaluate("0.1 + 0.2")));
    }

    [Fact]
    public void Division_by_zero_is_refused_not_infinity()
    {
        Assert.False(Arithmetic.TryEvaluate("5 / 0", out _));
        Assert.Throws<DivideByZeroException>(() => Arithmetic.Evaluate("5 / 0"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("12 *")]                  // trailing operator
    [InlineData("* 3")]                   // leading operator
    [InlineData("(1 + 2")]                // unclosed bracket
    [InlineData("2 ** 3")]                // not an operator we have
    [InlineData("hello")]                 // words
    [InlineData("2 + abc")]               // words after a number
    [InlineData("SELECT * FROM x")]       // anything that is not a sum
    [InlineData("2 3")]                   // two numbers, no operator
    public void Anything_that_is_not_a_sum_is_rejected_not_run(string expression)
    {
        Assert.False(Arithmetic.TryEvaluate(expression, out _));
    }

    [Fact]
    public void A_lone_number_is_a_valid_expression()
    {
        // "what is 42" -> the model may just pass "42"; that is fine.
        Assert.True(Arithmetic.TryEvaluate("42", out var v));
        Assert.Equal(42, v);
    }
}
