// ConflictResolverTests.cs
//
// Tests for DeclaredWinsResolver and LearnedWinsResolver.

using Circle.AI.Memory;
using Circle.AI.Personality;
using Xunit;

namespace Circle.AI.Personality.Tests;

public sealed class ConflictResolverTests
{
    [Fact]
    public void DeclaredWins_ClampsLearnedAboveCeiling()
    {
        // Declared range: casual..neutral. Learned is "formal" — should be clamped.
        var declared = Persona.Create("U", "en") with
        {
            Formality = new FormalityRange("casual", "neutral"),
        };
        var learned = new PersonaState { UserId = "U", Formality = "formal" };

        var result = new DeclaredWinsResolver().Resolve(declared, learned);

        // Ceiling is still neutral (we don't expand the declared range upward).
        Assert.Equal("neutral", result.Formality.Ceiling);
    }

    [Fact]
    public void DeclaredWins_ClampsLearnedBelowFloor()
    {
        // Declared range: neutral..formal. Learned is "casual" — should be clamped.
        var declared = Persona.Create("U", "en") with
        {
            Formality = new FormalityRange("neutral", "formal"),
        };
        var learned = new PersonaState { UserId = "U", Formality = "casual" };

        var result = new DeclaredWinsResolver().Resolve(declared, learned);

        Assert.Equal("neutral", result.Formality.Floor);
    }

    [Fact]
    public void DeclaredWins_NoChangeWhenLearnedInsideRange()
    {
        var declared = Persona.Create("U", "en") with
        {
            Formality = new FormalityRange("casual", "formal"),
        };
        var learned = new PersonaState { UserId = "U", Formality = "neutral" };

        var result = new DeclaredWinsResolver().Resolve(declared, learned);

        Assert.Equal(declared.Formality.Floor, result.Formality.Floor);
        Assert.Equal(declared.Formality.Ceiling, result.Formality.Ceiling);
    }

    [Fact]
    public void LearnedWins_PassesDeclaredThroughUnchanged()
    {
        var declared = Persona.Create("U", "en") with
        {
            Formality = new FormalityRange("casual", "neutral"),
            Values = new[] { "privacy" },
        };
        var learned = new PersonaState { UserId = "U", Formality = "formal" };

        var result = new LearnedWinsResolver().Resolve(declared, learned);

        Assert.Equal(declared.Id, result.Id);
        Assert.Equal(declared.Formality.Floor, result.Formality.Floor);
        Assert.Equal(declared.Formality.Ceiling, result.Formality.Ceiling);
        Assert.Equal(declared.Values, result.Values);
    }

    [Fact]
    public void DeclaredWins_ThrowsOnNullInputs()
    {
        var r = new DeclaredWinsResolver();
        Assert.Throws<ArgumentNullException>(() => r.Resolve(null!, new PersonaState()));
        Assert.Throws<ArgumentNullException>(() => r.Resolve(Persona.Create("U", "en"), null!));
    }
}
