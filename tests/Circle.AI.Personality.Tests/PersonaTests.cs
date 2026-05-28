// PersonaTests.cs
//
// Tests for Persona.Create — verifies that the static factory stamps a
// fresh Id and timestamps and applies the documented defaults.

using Circle.AI.Personality;
using Xunit;

namespace Circle.AI.Personality.Tests;

public sealed class PersonaTests
{
    [Fact]
    public void Create_StampsIdAndTimestamps()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-1);
        var persona = Persona.Create("Thabo", "en-ZA");
        var after = DateTimeOffset.UtcNow.AddSeconds(1);

        Assert.NotEqual(Guid.Empty, persona.Id);
        Assert.Equal("Thabo", persona.DisplayName);
        Assert.Equal("en-ZA", persona.PreferredLocale);
        Assert.InRange(persona.CreatedAt, before, after);
        Assert.Equal(persona.CreatedAt, persona.UpdatedAt);
        Assert.Equal(PrivacyLevel.Balanced, persona.Privacy);
        Assert.Equal("casual", persona.Formality.Floor);
        Assert.Equal("formal", persona.Formality.Ceiling);
        Assert.Empty(persona.IdentityTags);
        Assert.Empty(persona.Values);
        Assert.Empty(persona.Taboos);
        Assert.Null(persona.Pronouns);
        Assert.Null(persona.VoicePreference);
    }

    [Theory]
    [InlineData(null, "en-ZA")]
    [InlineData("", "en-ZA")]
    [InlineData("   ", "en-ZA")]
    [InlineData("Thabo", null)]
    [InlineData("Thabo", "")]
    [InlineData("Thabo", "  ")]
    public void Create_RejectsBlankArguments(string? displayName, string? locale) =>
        Assert.ThrowsAny<ArgumentException>(() => Persona.Create(displayName!, locale!));
}
