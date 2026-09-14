using System;
using System.Globalization;

namespace CircleAI.Tools
{
    // Arithmetic.cs
    //
    // A calculator, because the model on the phone cannot do sums.
    //
    // WHY A TOOL AT ALL, when this file's neighbour (CircleAIToolBridge) says an
    // add_numbers tool is "useless as evidence" - a capable model would guess the
    // answer and you could not tell whether the tool ran. That reasoning is about
    // a GOOD model. Measured on a P30 2026-09-14, the 0.6B answered "what is 12
    // times 8" with "6". On a model that cannot add, a CORRECT answer is proof the
    // tool ran, and - more to the point - it is the only way the person gets a
    // right answer at all.
    //
    // SAFE BY CONSTRUCTION. No Eval, no reflection, no code path a crafted string
    // can escape into. A hand-written recursive-descent parser over exactly five
    // operators and parentheses, on doubles. The worst a malicious expression can
    // do is be rejected.
    //
    // The MODEL builds the expression: it turns "12 times 8" into "12 * 8" and
    // "half of 40" into "40 / 2" before calling. This only has to evaluate the
    // ordinary notation that comes back, which is why the grammar is small.

    /// <summary>Evaluates a plain arithmetic expression. No code, only sums.</summary>
    public static class Arithmetic
    {
        /// <summary>
        /// Evaluates <paramref name="expression"/> ( + - * / and parentheses over
        /// decimal numbers ), or throws <see cref="FormatException"/> when it is not
        /// a well-formed sum.
        /// </summary>
        public static double Evaluate(string expression)
        {
            if (string.IsNullOrWhiteSpace(expression))
                throw new FormatException("There is nothing to calculate.");

            var parser = new Parser(expression);
            var value = parser.ParseExpression();
            parser.ExpectEnd();

            if (double.IsNaN(value) || double.IsInfinity(value))
                throw new FormatException("That does not have a finite answer.");

            return value;
        }

        /// <summary>Evaluate, returning false rather than throwing on bad input.</summary>
        public static bool TryEvaluate(string expression, out double value)
        {
            try { value = Evaluate(expression); return true; }
            catch (FormatException) { value = 0; return false; }
            catch (DivideByZeroException) { value = 0; return false; }
        }

        /// <summary>
        /// The answer as a person would write it: a whole number with no decimal
        /// point, otherwise trimmed of trailing zeros.
        /// </summary>
        /// <remarks>
        /// "12 * 8" reads better as "96" than "96.0", and "10 / 4" as "2.5" not
        /// "2.50000000". Rounded to twelve significant figures first, so 0.1 + 0.2
        /// is "0.3" and not the floating-point tail.
        /// </remarks>
        public static string Format(double value)
        {
            var rounded = Math.Round(value, 12, MidpointRounding.AwayFromZero);
            if (rounded == Math.Truncate(rounded) && Math.Abs(rounded) < 1e15)
                return ((long)rounded).ToString(CultureInfo.InvariantCulture);

            return rounded.ToString("0.############", CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------
        // The grammar, smallest form that covers what a phone user asks:
        //   expression := term   (('+' | '-') term)*
        //   term       := factor (('*' | '/') factor)*
        //   factor     := number | '(' expression ')' | ('+' | '-') factor
        //   number     := digit+ ('.' digit+)?     (leading '.' allowed too)
        // ------------------------------------------------------------------
        private ref struct Parser
        {
            private readonly ReadOnlySpan<char> _s;
            private int _i;

            public Parser(string s)
            {
                _s = s.AsSpan();
                _i = 0;
            }

            public double ParseExpression()
            {
                var value = ParseTerm();
                while (true)
                {
                    SkipSpace();
                    if (Match('+')) value += ParseTerm();
                    else if (Match('-')) value -= ParseTerm();
                    else return value;
                }
            }

            private double ParseTerm()
            {
                var value = ParseFactor();
                while (true)
                {
                    SkipSpace();
                    if (Match('*')) value *= ParseFactor();
                    else if (Match('/'))
                    {
                        var divisor = ParseFactor();
                        if (divisor == 0) throw new DivideByZeroException("Cannot divide by zero.");
                        value /= divisor;
                    }
                    else return value;
                }
            }

            private double ParseFactor()
            {
                SkipSpace();
                if (Match('+')) return ParseFactor();
                if (Match('-')) return -ParseFactor();

                if (Match('('))
                {
                    var inner = ParseExpression();
                    SkipSpace();
                    if (!Match(')')) throw new FormatException("A bracket was left open.");
                    return inner;
                }

                return ParseNumber();
            }

            private double ParseNumber()
            {
                SkipSpace();
                var start = _i;
                while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                if (_i < _s.Length && _s[_i] == '.')
                {
                    _i++;
                    while (_i < _s.Length && char.IsDigit(_s[_i])) _i++;
                }

                if (_i == start || (_i == start + 1 && _s[start] == '.'))
                    throw new FormatException("Expected a number.");

                return double.Parse(_s[start.._i], NumberStyles.Float, CultureInfo.InvariantCulture);
            }

            public void ExpectEnd()
            {
                SkipSpace();
                if (_i != _s.Length)
                    throw new FormatException("That is not a sum I can work out.");
            }

            private void SkipSpace()
            {
                while (_i < _s.Length && char.IsWhiteSpace(_s[_i])) _i++;
            }

            private bool Match(char c)
            {
                if (_i < _s.Length && _s[_i] == c) { _i++; return true; }
                return false;
            }
        }
    }
}
