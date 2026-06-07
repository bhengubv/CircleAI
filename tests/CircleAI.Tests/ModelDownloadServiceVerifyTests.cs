// ModelDownloadServiceVerifyTests.cs
//
// Regression tests for the SHA-256 prefix-strip bug that prevented ANY
// model from loading on a clean host. The runtime's VerifySha256Async
// compared the raw "sha256:<hex>" registry pin against the lowercase
// hex of the file's hash without stripping the prefix, so every model
// hit a "SHA-256 mismatch" 500 even though the pins were correct.

using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class ModelDownloadServiceVerifyTests
{
    [Theory]
    [InlineData("sha256:abc123",   "abc123")]
    [InlineData("SHA-256:abc123",  "abc123")]
    [InlineData("Sha256:abcdef",   "abcdef")]
    [InlineData("abc123",          "abc123")] // bare hex passes through
    [InlineData("",                "")]
    public void StripShaAlgorithmPrefix_KnownInputs(string raw, string expected)
    {
        Assert.Equal(expected, ModelDownloadService.StripShaAlgorithmPrefix(raw));
    }

    [Fact]
    public void StripShaAlgorithmPrefix_DoesNotStripUnknownPrefixes()
    {
        // A long token-before-colon is treated as data, not an algorithm name.
        var longPrefix = new string('a', 32) + ":hex";
        Assert.Equal(longPrefix, ModelDownloadService.StripShaAlgorithmPrefix(longPrefix));
    }

    [Fact]
    public void StripShaAlgorithmPrefix_PreservesEmbeddedColons()
    {
        // Once we've stripped the leading "sha256:" the remainder is
        // returned verbatim — a downstream comparator can still spot a bad
        // value.
        Assert.Equal("ab:cd", ModelDownloadService.StripShaAlgorithmPrefix("sha256:ab:cd"));
    }

    [Fact]
    public void StripShaAlgorithmPrefix_TrimsWhitespace()
    {
        Assert.Equal("abc", ModelDownloadService.StripShaAlgorithmPrefix("  sha256:abc  "));
        Assert.Equal("abc", ModelDownloadService.StripShaAlgorithmPrefix("abc  "));
    }

    [Fact]
    public void StripShaAlgorithmPrefix_RejectsNonAlphanumericPrefix()
    {
        // A "prefix" with punctuation isn't a real algorithm name — leave it
        // alone so a malformed input doesn't get silently corrupted.
        var weird = "sha 256:abc";
        Assert.Equal(weird, ModelDownloadService.StripShaAlgorithmPrefix(weird));
    }
}
