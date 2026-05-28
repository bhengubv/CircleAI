// AffectVadTests.cs
//
// Fixture-driven tests for AffectVad.From(AffectState).
// The same JSON file (fixtures/affect_vad_derivation.json) drives the
// equivalent ports in Rust, Go, Python, TypeScript, Kotlin, Swift, C, and ArkTS.

using System;
using System.IO;
using System.Text.Json;
using Circle.AI.Memory;
using Xunit;

namespace Circle.AI.Memory.Tests;

public sealed class AffectVadTests
{
    private const float Epsilon = 1e-5f;

    [Fact]
    public void From_DefaultState_MatchesFixture()
    {
        var state = new AffectState
        {
            Curiosity = 0.5f, Engagement = 0.5f, Uncertainty = 0.2f,
            Rapport = 0.0f,   Energy = 0.5f,
        };
        var vad = AffectVad.From(state);
        Assert.InRange(vad.Valence,   0.43333333f - Epsilon, 0.43333333f + Epsilon);
        Assert.InRange(vad.Arousal,   0.425f      - Epsilon, 0.425f      + Epsilon);
        Assert.InRange(vad.Dominance, 0.65f       - Epsilon, 0.65f       + Epsilon);
    }

    [Fact]
    public void From_AllMax_ProducesUnitValenceAndDominance()
    {
        var state = new AffectState
        {
            Curiosity = 1f, Engagement = 1f, Uncertainty = 0f,
            Rapport = 1f, Energy = 1f,
        };
        var vad = AffectVad.From(state);
        Assert.InRange(vad.Valence,   1f    - Epsilon, 1f    + Epsilon);
        Assert.InRange(vad.Arousal,   0.75f - Epsilon, 0.75f + Epsilon);
        Assert.InRange(vad.Dominance, 1f    - Epsilon, 1f    + Epsilon);
    }

    [Fact]
    public void From_AllMinHighUncertainty_ProducesZeroValenceAndDominance()
    {
        var state = new AffectState
        {
            Curiosity = 0f, Engagement = 0f, Uncertainty = 1f,
            Rapport = 0f, Energy = 0f,
        };
        var vad = AffectVad.From(state);
        Assert.InRange(vad.Valence,   0f    - Epsilon, 0f    + Epsilon);
        Assert.InRange(vad.Arousal,   0.25f - Epsilon, 0.25f + Epsilon);
        Assert.InRange(vad.Dominance, 0f    - Epsilon, 0f    + Epsilon);
    }

    [Fact]
    public void ToVad_ExtensionMethod_EquivalentToStaticFactory()
    {
        var state = new AffectState { Engagement = 0.7f, Uncertainty = 0.3f };
        Assert.Equal(AffectVad.From(state), state.ToVad());
    }

    [Fact]
    public void From_NullState_Throws() =>
        Assert.Throws<ArgumentNullException>(() => AffectVad.From(null!));

    // ── Fixture-driven test ──────────────────────────────────────────────────
    // Walks every vector in fixtures/affect_vad_derivation.json and validates
    // the C# implementation matches the canonical math.

    [Fact]
    public void AllFixtureVectors_MatchDerivation()
    {
        var path = Path.Combine(
            AppContext.BaseDirectory, "fixtures", "affect_vad_derivation.json");
        Assert.True(File.Exists(path), $"Fixture not copied: {path}");

        using var doc = JsonDocument.Parse(File.ReadAllText(path));
        var epsilon = (float)doc.RootElement.GetProperty("epsilon").GetDouble();

        foreach (var vector in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            var input  = vector.GetProperty("input");
            var expect = vector.GetProperty("expected");
            var id     = vector.GetProperty("id").GetString();

            var state = new AffectState
            {
                Curiosity   = input.GetProperty("curiosity").GetSingle(),
                Engagement  = input.GetProperty("engagement").GetSingle(),
                Uncertainty = input.GetProperty("uncertainty").GetSingle(),
                Rapport     = input.GetProperty("rapport").GetSingle(),
                Energy      = input.GetProperty("energy").GetSingle(),
            };

            var vad = AffectVad.From(state);

            var expV = expect.GetProperty("valence").GetSingle();
            var expA = expect.GetProperty("arousal").GetSingle();
            var expD = expect.GetProperty("dominance").GetSingle();

            Assert.True(Math.Abs(vad.Valence   - expV) <= epsilon,
                $"{id}: Valence expected {expV}, got {vad.Valence}");
            Assert.True(Math.Abs(vad.Arousal   - expA) <= epsilon,
                $"{id}: Arousal expected {expA}, got {vad.Arousal}");
            Assert.True(Math.Abs(vad.Dominance - expD) <= epsilon,
                $"{id}: Dominance expected {expD}, got {vad.Dominance}");
        }
    }
}
