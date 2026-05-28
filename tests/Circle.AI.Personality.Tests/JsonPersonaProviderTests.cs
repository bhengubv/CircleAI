// JsonPersonaProviderTests.cs
//
// Tests for JsonPersonaProvider: round-trip, existence, export.

using Circle.AI.Personality;
using Xunit;

namespace Circle.AI.Personality.Tests;

public sealed class JsonPersonaProviderTests : IDisposable
{
    private readonly string _root;

    public JsonPersonaProviderTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "circle-ai-personality-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task SaveAndGet_RoundTripsPersona()
    {
        var provider = new JsonPersonaProvider(_root);
        var original = Persona.Create("Nomvula", "zu-ZA") with
        {
            Pronouns = "she/her",
            IdentityTags = new[] { "isiZulu native", "parent" },
            Values = new[] { "family", "privacy" },
            Taboos = new[] { "politics" },
            Privacy = PrivacyLevel.Strict,
            VoicePreference = "warm-female",
            Formality = new FormalityRange("casual", "neutral"),
        };

        var saved = await provider.SaveAsync("user-1", original);
        var loaded = await provider.GetAsync("user-1");

        Assert.NotNull(loaded);
        Assert.Equal(original.Id, loaded!.Id);
        Assert.Equal(original.DisplayName, loaded.DisplayName);
        Assert.Equal(original.Pronouns, loaded.Pronouns);
        Assert.Equal(original.IdentityTags, loaded.IdentityTags);
        Assert.Equal(original.Values, loaded.Values);
        Assert.Equal(original.Taboos, loaded.Taboos);
        Assert.Equal(original.PreferredLocale, loaded.PreferredLocale);
        Assert.Equal(original.VoicePreference, loaded.VoicePreference);
        Assert.Equal(original.Formality.Floor, loaded.Formality.Floor);
        Assert.Equal(original.Formality.Ceiling, loaded.Formality.Ceiling);
        Assert.Equal(original.Privacy, loaded.Privacy);
        Assert.Equal(saved.UpdatedAt, loaded.UpdatedAt);
        // UpdatedAt should be refreshed on save
        Assert.True(saved.UpdatedAt >= original.UpdatedAt);
    }

    [Fact]
    public async Task ExistsAsync_ReturnsFalseForMissingUser()
    {
        var provider = new JsonPersonaProvider(_root);
        Assert.False(await provider.ExistsAsync("never-seen"));
    }

    [Fact]
    public async Task ExistsAsync_ReturnsTrueAfterSave()
    {
        var provider = new JsonPersonaProvider(_root);
        await provider.SaveAsync("u-2", Persona.Create("Bongi", "en-ZA"));
        Assert.True(await provider.ExistsAsync("u-2"));
    }

    [Fact]
    public async Task GetAsync_ReturnsNullForMissingUser()
    {
        var provider = new JsonPersonaProvider(_root);
        Assert.Null(await provider.GetAsync("never-seen"));
    }

    [Fact]
    public async Task ExportAllAsync_YieldsAllSavedPersonas()
    {
        var provider = new JsonPersonaProvider(_root);
        await provider.SaveAsync("u-a", Persona.Create("A", "en"));
        await provider.SaveAsync("u-b", Persona.Create("B", "en"));
        await provider.SaveAsync("u-c", Persona.Create("C", "en"));

        var names = new List<string>();
        await foreach (var p in provider.ExportAllAsync()) names.Add(p.DisplayName);

        Assert.Equal(3, names.Count);
        Assert.Contains("A", names);
        Assert.Contains("B", names);
        Assert.Contains("C", names);
    }

    [Fact]
    public async Task SaveAsync_IsIdempotentForSameUser()
    {
        var provider = new JsonPersonaProvider(_root);
        await provider.SaveAsync("u-x", Persona.Create("First", "en"));
        await provider.SaveAsync("u-x", Persona.Create("Second", "en"));

        var loaded = await provider.GetAsync("u-x");
        Assert.NotNull(loaded);
        Assert.Equal("Second", loaded!.DisplayName);

        int count = 0;
        await foreach (var _ in provider.ExportAllAsync()) count++;
        Assert.Equal(1, count);
    }
}
