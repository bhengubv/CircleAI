// BiometricMatcherFixtureTests.cs
//
// The test that was missing. BiometricMatcher.cs has carried the line
// "Validated against fixtures/facex_biometric_vectors.json with 1e-5 tolerance"
// for a long time while NO C# test ever loaded that file. Go, Python,
// TypeScript and HarmonyOS all assert against it directly, so C# was the one
// port free to drift — and it did, on two of the six rows.
//
// The same JSON drives the equivalent tests in every other port. A changed
// fixture is a contract change.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using CircleAI.Identity;
using Xunit;

namespace CircleAI.Tests;

public sealed class BiometricMatcherFixtureTests
{
    private static readonly string FixturePath = Path.GetFullPath(Path.Combine(
        AppContext.BaseDirectory,
        "..", "..", "..", "..", "..", "fixtures", "facex_biometric_vectors.json"));

    private const string MatchKey = "expected_is_match_at_threshold_0_85";

    private static JsonElement Vectors()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(FixturePath));
        return doc.RootElement.GetProperty("cosine_similarity_vectors").Clone();
    }

    private static JsonElement Entry(string id)
    {
        foreach (var e in Vectors().EnumerateArray())
            if (e.GetProperty("id").GetString() == id)
                return e.Clone();
        throw new InvalidOperationException($"fixture entry not found: {id}");
    }

    private static float[] Floats(JsonElement entry, string name)
    {
        var values = new List<float>();
        foreach (var v in entry.GetProperty(name).EnumerateArray())
            values.Add((float)v.GetDouble());
        return values.ToArray();
    }

    public static IEnumerable<object[]> CosineIds()
    {
        foreach (var e in Vectors().EnumerateArray())
            yield return new object[] { e.GetProperty("id").GetString()! };
    }

    public static IEnumerable<object[]> MatchIds()
    {
        foreach (var e in Vectors().EnumerateArray())
            if (e.TryGetProperty(MatchKey, out _))
                yield return new object[] { e.GetProperty("id").GetString()! };
    }

    [Fact]
    public void FixtureFile_Exists()
    {
        Assert.True(File.Exists(FixturePath), $"Fixture not found at: {FixturePath}");
    }

    [Theory]
    [MemberData(nameof(CosineIds))]
    public void CosineSimilarity_MatchesFixture(string id)
    {
        var entry = Entry(id);
        var expected = entry.GetProperty("expected_similarity").GetDouble();
        var tolerance = entry.TryGetProperty("tolerance", out var t) ? t.GetDouble() : 1e-5;

        var actual = BiometricMatcher.CosineSimilarity(Floats(entry, "a"), Floats(entry, "b"));

        Assert.True(Math.Abs(actual - expected) <= tolerance,
            $"[{id}] expected {expected}, got {actual} (tolerance {tolerance})");
    }

    [Theory]
    [MemberData(nameof(MatchIds))]
    public void IsMatch_MatchesFixture(string id)
    {
        var entry = Entry(id);
        var expected = entry.GetProperty(MatchKey).GetBoolean();

        var profile = new BiometricProfile
        {
            IdentityId = "test",
            EmbeddingVector = Floats(entry, "b"),
            MatchThreshold = 0.85f,
        };

        Assert.Equal(expected, BiometricMatcher.IsMatch(Floats(entry, "a"), profile));
    }

    [Fact]
    public void CosineSimilarity_RefusesMismatchedDimensions()
    {
        var ex = Assert.Throws<ArgumentException>(() =>
            BiometricMatcher.CosineSimilarity(new[] { 1.0f }, new[] { 1.0f, 0.5f }));
        Assert.Contains("Embedding dimension mismatch", ex.Message);
    }

    [Fact]
    public void CosineSimilarity_UnnormalisedVectors_StayWithinRange()
    {
        // [3,4] and [30,40] point the same way, so the answer is 1.0. The bare
        // dot product this replaced returned 250 — a "similarity" ten times
        // past the top of its own documented range.
        var similarity = BiometricMatcher.CosineSimilarity(
            new[] { 3.0f, 4.0f }, new[] { 30.0f, 40.0f });

        Assert.InRange(similarity, 1.0f - 1e-5f, 1.0f);
    }

    [Fact]
    public void IsMatch_UnnormalisedNonMatch_IsRejected()
    {
        // The false-match path itself. These directions sit 45 degrees apart —
        // cosine 0.707, comfortably under the 0.85 default, so: not a match.
        // The bare dot product scored 10.0 and called it one, and magnitude is
        // not something the enrolled profile validates: EmbeddingVector is a
        // plain float[] and nothing enforces the L2-normalisation the docs
        // assume.
        var profile = new BiometricProfile
        {
            IdentityId = "test",
            EmbeddingVector = new[] { 10.0f, 10.0f },
        };

        Assert.False(BiometricMatcher.IsMatch(new[] { 1.0f, 0.0f }, profile));
    }

    [Fact]
    public void CosineSimilarity_ZeroMagnitude_ReturnsZero()
    {
        Assert.Equal(0.0f, BiometricMatcher.CosineSimilarity(
            new[] { 0.0f, 0.0f }, new[] { 1.0f, 0.0f }));
    }
}
