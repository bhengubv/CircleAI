// BenchContracts.cs
//
// (Phase E7) Bench-task and scoring contracts for CircleAI.SelfBench.
// Bench tasks are bundled as JSON files alongside the assembly or
// constructed in-code; each task gets a prompt, an expected answer, and
// a scoring strategy (exact match, substring match, numeric tolerance,
// regex match, or a custom scorer).

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace CircleAI.SelfBench;

public enum BenchScoring
{
    ExactMatch,
    Substring,
    Regex,
    NumericTolerance,
    /// <summary>Custom scorer name registered with the runner.</summary>
    CustomScorer
}

/// <summary>(Phase E7) One bench task — a prompt, an expected answer, and how to score it.</summary>
public sealed record BenchTask(
    string                 Id,
    string                 Suite,
    string                 Prompt,
    string                 Expected,
    BenchScoring           Scoring                 = BenchScoring.ExactMatch,
    double                 NumericTolerance        = 0.0,
    string?                CustomScorerName        = null,
    double                 MaxLatencyMs            = 30_000,
    // If true, regression on this task FAILS the gate even with overall improvement.
    bool                   IsCritical              = false);

/// <summary>(Phase E7) Result of running one bench task.</summary>
public sealed record BenchResult(
    string         TaskId,
    string         Suite,
    string         ActualAnswer,
    double         Score,             // 0..1
    double         LatencyMs,
    bool           Passed,
    string?        FailureReason      = null);

/// <summary>(Phase E7) Aggregate metrics across a full bench run.</summary>
public sealed record BenchSummary(
    string                                  RunId,
    string                                  SuiteId,
    int                                     TaskCount,
    int                                     PassCount,
    double                                  MeanScore,
    double                                  P50LatencyMs,
    double                                  P95LatencyMs,
    IReadOnlyDictionary<string, double>     PerTaskScore,
    DateTimeOffset                          CompletedAtUtc);

public interface IBenchScorer
{
    string Name { get; }
    double Score(string expected, string actual, BenchTask task);
}

/// <summary>(Phase E7) Built-in scorers covering exact / substring / regex / numeric matching.</summary>
public static class BuiltInScorers
{
    public sealed class ExactMatchScorer : IBenchScorer
    {
        public string Name => "exact";
        public double Score(string expected, string actual, BenchTask task) =>
            string.Equals(expected?.Trim(), actual?.Trim(), StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    public sealed class SubstringScorer : IBenchScorer
    {
        public string Name => "substring";
        public double Score(string expected, string actual, BenchTask task) =>
            !string.IsNullOrEmpty(actual)
            && actual.Contains(expected ?? string.Empty, StringComparison.OrdinalIgnoreCase) ? 1.0 : 0.0;
    }

    public sealed class RegexScorer : IBenchScorer
    {
        public string Name => "regex";
        public double Score(string expected, string actual, BenchTask task)
        {
            if (string.IsNullOrEmpty(expected) || string.IsNullOrEmpty(actual)) return 0.0;
            try { return Regex.IsMatch(actual, expected, RegexOptions.IgnoreCase) ? 1.0 : 0.0; }
            catch (ArgumentException) { return 0.0; }
        }
    }

    public sealed class NumericToleranceScorer : IBenchScorer
    {
        public string Name => "numeric-tolerance";
        public double Score(string expected, string actual, BenchTask task)
        {
            if (!TryParseNumber(expected, out var eVal)) return 0.0;
            if (!TryParseNumber(actual, out var aVal))   return 0.0;
            var tol = Math.Max(0, task.NumericTolerance);
            return Math.Abs(eVal - aVal) <= tol ? 1.0 : 0.0;
        }
        private static bool TryParseNumber(string? s, out double value)
        {
            value = 0;
            if (string.IsNullOrWhiteSpace(s)) return false;
            // Extract the first number-like substring (handles "the answer is 42").
            var m = Regex.Match(s, @"-?\d+(\.\d+)?([eE][+-]?\d+)?");
            if (!m.Success) return false;
            return double.TryParse(m.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
        }
    }
}
