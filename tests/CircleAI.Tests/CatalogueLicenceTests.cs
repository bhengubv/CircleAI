// CatalogueLicenceTests.cs
//
// What this product is allowed to download and run.
//
// THE GATE ENFORCED A HARD STANDING RULE AND HAD NO TESTS. fully-free-opensource-always
// is not a preference here, and the licence allowlist is the only thing between
// it and a phone that has quietly fetched a model nobody may ship. It was eight
// strings matched with String.Contains, and three things were wrong with that:
//
//   openrail was ON the allowlist. OpenRAIL carries behavioural use
//   restrictions and is not OSI-approved - it is the licence family the rule
//   exists to exclude, and it is what stable-diffusion-v1-5 actually ships
//   under, so the one model in the live listing that must never be catalogued
//   was admitted by name.
//
//   cc-by-nc-4.0 passed, because it CONTAINS "cc-by". A NonCommercial licence
//   through a gate for free software. cc-by-nd-4.0 passed the same way. No
//   longer allowlist fixes that, because NC and ND are suffixes on a stem that
//   is genuinely allowed - it needs a denylist, checked first.
//
//   "Limited Commercial Licence" passed, because li-MIT-ed contains "mit".
//
// Every refusal here is a licence a real model actually carries.

using System.Linq;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public sealed class CatalogueLicenceTests
{
    private static readonly ModelScopeCatalogOptions Default = new();

    static bool Allowed(string? licence) =>
        ModelScopeCatalogClient.LicenceAllowed(licence, Default);

    // ── what may be shipped ──────────────────────────────────────────────────

    [Theory]
    [InlineData("Apache License 2.0")]    // 96 of the first 100 live models
    [InlineData("apache-2.0")]
    [InlineData("MIT")]
    [InlineData("mit")]
    [InlineData("BSD-3-Clause")]
    [InlineData("cc0-1.0")]
    [InlineData("cc-by-4.0")]
    [InlineData("Public Domain")]
    [InlineData("Unlicense")]
    public void PermissiveLicences_AreAllowed(string licence)
    {
        Assert.True(Allowed(licence), $"{licence} should be shippable");
    }

    // ── use-restricted licences are refused, however they are spelled ────────

    [Theory]
    [InlineData("cc-by-nc-4.0")]          // NonCommercial: passed before, by containing "cc-by"
    [InlineData("CC BY-NC-SA 4.0")]       // the spaced spelling of the same thing
    [InlineData("cc-by-nc-nd-4.0")]
    [InlineData("cc-by-nd-4.0")]          // NoDerivatives: passed before, the same way
    public void NonCommercialAndNoDerivatives_AreRefused(string licence)
    {
        Assert.False(Allowed(licence), $"{licence} restricts use and must not be catalogued");
    }

    [Theory]
    [InlineData("creativeml-openrail-m")]   // what stable-diffusion-v1-5 really carries
    [InlineData("bigscience-openrail-m")]
    [InlineData("bigscience-bloom-rail-1.0")]
    public void OpenRail_IsRefused_ThoughItUsedToBeOnTheAllowlist(string licence)
    {
        Assert.False(Allowed(licence), $"{licence} is use-restricted, not free");
    }

    [Fact]
    public void TheSubstringTrap_IsClosed()
    {
        // li-MIT-ed. This is the whole argument for matching on token
        // boundaries rather than String.Contains, and it is why a licence gate
        // cannot be a list of substrings.
        Assert.False(Allowed("Limited Commercial Licence"));
        Assert.False(Allowed("Limited Evaluation License"));

        // ...without breaking the licence it was trying to match.
        Assert.True(Allowed("MIT License"));
    }

    [Theory]
    [InlineData("Llama 3 Community License")]
    [InlineData("gemma-terms-of-use")]
    [InlineData("gpl-3.0")]
    [InlineData("agpl-3.0")]
    [InlineData("other")]
    [InlineData("proprietary")]
    public void AnythingNotOnTheAllowlist_IsRefused(string licence)
    {
        Assert.False(Allowed(licence));
    }

    // ── unstated is not permissive ───────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NoStatedLicence_IsRefused(string? licence)
    {
        // Three of the first hundred live models carry none. "Unstated" is the
        // case where nobody can tell you what you are allowed to do.
        Assert.False(Allowed(licence));
    }

    // ── the denylist outranks the allowlist, including when it is empty ──────

    [Fact]
    public void RefusalWins_EvenWhenTheAllowlistIsEmpty()
    {
        // An empty allowlist means "any", which is a legitimate configuration
        // for a private catalogue. It must still not mean "any, including the
        // ones that forbid this".
        var anything = new ModelScopeCatalogOptions { Licences = [] };

        Assert.True(ModelScopeCatalogClient.LicenceAllowed("some-house-licence", anything));
        Assert.False(ModelScopeCatalogClient.LicenceAllowed("creativeml-openrail-m", anything));
        Assert.False(ModelScopeCatalogClient.LicenceAllowed("cc-by-nc-4.0", anything));
    }

    [Fact]
    public void OpenRail_IsNoLongerNamedOnTheAllowlist()
    {
        // A guard against somebody putting it back: the string itself is the
        // bug, and a denylist entry plus an allowlist entry would fight.
        Assert.DoesNotContain(Default.Licences,
            x => x.Contains("rail", System.StringComparison.OrdinalIgnoreCase));
    }
}
