// CatalogueListingTests.cs
//
// The shape ModelScope actually returns, pinned against a real response.
//
// TWO FAULTS ON ONE PATH, BOTH SILENT, NEITHER REACHABLE WITHOUT THE NETWORK.
// Measured against the live API on 2026-09-11:
//
//   * the client asked GET /api/v1/models?Name=MNN, which answers 404 and
//     always has. The working call is PUT /api/v1/dolphin/models with a body.
//   * the parser read Data.Models. The API returns Data.Model.Models - one
//     level deeper - and a missing property here is a `yield break`, so an
//     empty catalogue, no exception, nothing logged.
//
// Either alone meant the live catalogue could never load. Nothing caught them
// because nothing ever called the refresh, and a failed refresh is invisible
// from inside the app BY DESIGN: it keeps the shipped catalogue, which is a
// working app. It would have sat at 404 indefinitely, quietly doing nothing.
//
// The fixture below is a trimmed copy of a real response. Pinning the shape is
// the only way this class of bug is caught without a network call, and a network
// call in a unit test is a flake.

using System.Linq;
using System.Text.Json;
using CircleAI.Core.Models;
using Xunit;

namespace CircleAI.Tests;

public class CatalogueListingTests
{
    /// <summary>A trimmed capture of the real PUT /api/v1/dolphin/models reply.</summary>
    private const string RealShape = """
    {
      "Code": 200,
      "Success": true,
      "Data": {
        "Model": {
          "TotalCount": 519,
          "Models": [
            { "Name": "Qwen3.5-2B-MNN",   "Path": "MNN",        "License": "Apache License 2.0" },
            { "Name": "Qwen3.5-9B-MNN",   "Path": "MNN",        "License": "apache-2.0" },
            { "Name": "Sketchy-7B-MNN",   "Path": "yunlong782", "License": "Apache License 2.0" },
            { "Name": "Unstated-3B-MNN",  "Path": "MNN"                                        },
            { "Name": "Restricted-4B",    "Path": "MNN",        "License": "CC BY-NC 4.0"      }
          ]
        }
      }
    }
    """;

    private static ModelScopeCatalogOptions Options(
        string[]? publishers = null, string[]? licences = null) => new()
        {
            Publishers = publishers ?? ["MNN"],
            Licences   = licences   ?? ["apache", "mit", "bsd"],
        };

    [Fact]
    public void The_models_are_found_one_level_deeper_than_the_parser_used_to_look()
    {
        // THE SILENT ONE. Reading Data.Models yields nothing and reports
        // nothing, so a page of five hundred models catalogues as zero.
        var found = ModelScopeCatalogClient
            .ParseModelListing(RealShape, Options(publishers: [], licences: []))
            .ToList();

        Assert.NotEmpty(found);
        Assert.Contains(found, f => f.Name == "Qwen3.5-2B-MNN");
    }

    [Fact]
    public void A_repo_is_the_publisher_and_the_name_joined()
    {
        var found = ModelScopeCatalogClient
            .ParseModelListing(RealShape, Options(publishers: [], licences: []))
            .ToList();

        Assert.Contains(found, f => f.Repo == "MNN/Qwen3.5-2B-MNN");
    }

    [Fact]
    public void A_flat_shape_would_still_be_read()
    {
        // Accepted in both directions so a future change to either shape keeps
        // working rather than silently cataloguing nothing.
        const string flat = """
        { "Data": { "Models": [ { "Name": "X-MNN", "Path": "MNN", "License": "MIT" } ] } }
        """;

        Assert.Single(ModelScopeCatalogClient.ParseModelListing(flat, Options()));
    }

    [Fact]
    public void A_reply_with_no_models_yields_nothing_rather_than_throwing()
    {
        Assert.Empty(ModelScopeCatalogClient.ParseModelListing("""{"Code":200}""", Options()));
        Assert.Empty(ModelScopeCatalogClient.ParseModelListing("""{"Data":{}}""", Options()));
        Assert.Empty(ModelScopeCatalogClient.ParseModelListing("""{"Data":{"Model":{}}}""", Options()));
    }

    // ── Who may be catalogued ───────────────────────────────────────────

    [Fact]
    public void Only_the_official_publisher_is_catalogued_by_default()
    {
        // A discovered model is code this app will download and RUN. Of the
        // first hundred returned, six were personal accounts.
        var found = ModelScopeCatalogClient
            .ParseModelListing(RealShape, Options(licences: []))
            .ToList();

        Assert.DoesNotContain(found, f => f.Repo.StartsWith("yunlong782/"));
        Assert.Contains(found, f => f.Repo.StartsWith("MNN/"));
    }

    [Fact]
    public void An_empty_publisher_list_means_anyone()
    {
        var found = ModelScopeCatalogClient
            .ParseModelListing(RealShape, Options(publishers: [], licences: []))
            .ToList();

        Assert.Contains(found, f => f.Repo.StartsWith("yunlong782/"));
    }

    // ── Which licences ──────────────────────────────────────────────────

    [Fact]
    public void A_non_commercial_licence_is_not_catalogued()
    {
        // fully-free-opensource-always is a hard rule and says licence FIRST.
        // A catalogue that adds whatever it finds breaks it on device, without
        // anybody choosing to.
        var found = ModelScopeCatalogClient.ParseModelListing(RealShape, Options()).ToList();

        Assert.DoesNotContain(found, f => f.Name == "Restricted-4B");
    }

    [Fact]
    public void A_model_with_no_stated_licence_is_skipped_not_assumed_permissive()
    {
        // THREE OF THE FIRST HUNDRED HAD NONE. "Unstated" is the case where
        // nobody can tell you what you are allowed to do - a reason to skip,
        // not a reason to assume the best.
        var found = ModelScopeCatalogClient.ParseModelListing(RealShape, Options()).ToList();

        Assert.DoesNotContain(found, f => f.Name == "Unstated-3B-MNN");
    }

    [Theory]
    [InlineData("Apache License 2.0")]
    [InlineData("apache-2.0")]
    [InlineData("MIT")]
    [InlineData("BSD-3-Clause")]
    public void The_permissive_licences_this_product_ships_against_are_accepted(string licence)
    {
        var json = $$"""
        { "Data": { "Model": { "Models": [
            { "Name": "X-MNN", "Path": "MNN", "License": {{JsonSerializer.Serialize(licence)}} }
        ] } } }
        """;

        Assert.Single(ModelScopeCatalogClient.ParseModelListing(json, Options(licences:
            ["apache", "mit", "bsd"])));
    }

    [Fact]
    public void LicenseName_is_read_when_License_is_absent()
    {
        const string json = """
        { "Data": { "Model": { "Models": [
            { "Name": "X-MNN", "Path": "MNN", "LicenseName": "Apache License 2.0" }
        ] } } }
        """;

        Assert.Single(ModelScopeCatalogClient.ParseModelListing(json, Options()));
    }

    [Fact]
    public void An_empty_licence_list_means_any_licence()
    {
        var found = ModelScopeCatalogClient
            .ParseModelListing(RealShape, Options(licences: []))
            .ToList();

        Assert.Contains(found, f => f.Name == "Restricted-4B");
        Assert.Contains(found, f => f.Name == "Unstated-3B-MNN");
    }
}
