// CapabilityCatalogTests.cs
//
// The typed discovery reader over capabilities.json. Proves it reads the real embedded
// manifest (so a consumer's "what can you do" is answered from fact), that it surfaces
// per-entry status and the list/measured fields, and that malformed input degrades to
// empty rather than throwing.

using System.Linq;
using CircleAI.Skills;
using Xunit;

namespace CircleAI.Tests;

public sealed class CapabilityCatalogTests
{
    [Fact]
    public void Default_reads_the_real_manifest_and_surfaces_self_healing()
    {
        var all = CapabilityCatalog.Default.All();
        Assert.NotEmpty(all);

        var healing = CapabilityCatalog.Default.Find("self.healing");
        Assert.NotNull(healing);
        Assert.Equal("Self-healing analysis", healing!.Name);
        Assert.Equal("partial", healing.Status);
        Assert.NotEmpty(healing.Limits);   // "absence of limits is itself a claim"
    }

    [Fact]
    public void Find_is_case_insensitive_and_returns_null_for_the_unknown()
    {
        Assert.NotNull(CapabilityCatalog.Default.Find("SELF.HEALING"));
        Assert.Null(CapabilityCatalog.Default.Find("no.such.capability"));
    }

    [Fact]
    public void Parses_all_fields_of_an_entry()
    {
        const string json = """
            { "Capabilities": [ {
                "Id": "demo.thing", "Name": "Demo thing", "Status": "shipping",
                "Summary": "does a demo thing",
                "Package": "CircleAI.Demo", "EntryPoint": "src/x.cs#Y", "Seam": "IDemo",
                "VerifiedBy": [ "DemoTests" ],
                "Requires": [ "a brain" ],
                "Limits": [ "only a demo" ],
                "Measured": { "Device": "P30", "Date": "2026-09-20", "Result": "worked" }
            } ] }
            """;

        var e = new CapabilityCatalog(json).Find("demo.thing");

        Assert.NotNull(e);
        Assert.Equal("shipping", e!.Status);
        Assert.Equal("CircleAI.Demo", e.Package);
        Assert.Equal(new[] { "a brain" }, e.Requires);
        Assert.Equal(new[] { "only a demo" }, e.Limits);
        Assert.NotNull(e.Measured);
        Assert.Equal("P30", e.Measured!.Device);
    }

    [Fact]
    public void A_measured_block_missing_a_field_is_dropped_not_half_read()
    {
        const string json = """
            { "Capabilities": [ {
                "Id": "x", "Name": "X", "Status": "partial", "Summary": "s",
                "Measured": { "Device": "P30", "Result": "no date here" }
            } ] }
            """;

        Assert.Null(new CapabilityCatalog(json).Find("x")!.Measured);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("{\"Capabilities\": \"not an array\"}")]
    public void Malformed_or_empty_manifests_yield_an_empty_catalogue_not_a_throw(string json)
    {
        Assert.Empty(new CapabilityCatalog(json).All());
    }
}
