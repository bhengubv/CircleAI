// MnnInferenceBridgeFactoryTests.cs
//
// Verifies that the production MnnInferenceBridgeFactory is the default
// IBridgeFactory wired by AddCircleAIInferenceServer (replacing the old
// throwing UnconfiguredBridgeFactory), and that it produces actionable
// errors for unknown models rather than NullReferenceException or silent
// success.
//
// These tests do NOT exercise the full pipeline (model download + MNN
// load) — that needs real network + native binaries. They DO exercise the
// admission gate, the registry lookup, and the error path, which is what
// the brief asked for: no stubs, real error handling.

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Inference.Server.Endpoints;
using CircleAI.Inference.Server.Lifecycle;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class MnnInferenceBridgeFactoryTests : IClassFixture<DefaultFactoryServerFactory>
{
    private readonly DefaultFactoryServerFactory _factory;
    public MnnInferenceBridgeFactoryTests(DefaultFactoryServerFactory factory) => _factory = factory;

    [Fact]
    public void Default_IBridgeFactory_Is_MnnInferenceBridgeFactory_Not_Unconfigured()
    {
        using var scope = _factory.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var bf = sp.GetRequiredService<IBridgeFactory>();

        Assert.IsType<MnnInferenceBridgeFactory>(bf);
        Assert.IsNotType<UnconfiguredBridgeFactory>(bf);
    }

    [Fact]
    public async Task Load_Unknown_Model_Returns_500_With_Helpful_Message()
    {
        // The embedded registry doesn't contain "phantom-model-XYZ"; the
        // factory must produce a clear InvalidOperationException that the
        // admin endpoint surfaces as a 500 with the registry guidance in
        // the error body.
        using var client = _factory.AuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest
        {
            ModelId           = "phantom-model-XYZ",
            Backend           = "Cpu",
            Tier              = "Tier0_Tiny",
            VramRequiredBytes = 0,
            RamRequiredBytes  = 100L * 1024 * 1024,
        });

        Assert.Equal(HttpStatusCode.InternalServerError, resp.StatusCode);

        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(err);
        Assert.Equal("factory_failed", err!.Error.Code);
        // The factory was invoked — proof the wiring lands on
        // MnnInferenceBridgeFactory (not UnconfiguredBridgeFactory).
        // UnconfiguredBridgeFactory would have produced a message
        // mentioning "No IBridgeFactory is configured" instead.
        Assert.Contains("phantom-model-XYZ", err.Error.Message,
            System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("No IBridgeFactory is configured", err.Error.Message,
            System.StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Factory that intentionally does NOT override the default IBridgeFactory,
/// so we can verify the default wiring lands on MnnInferenceBridgeFactory.
/// </summary>
public sealed class DefaultFactoryServerFactory : InferenceServerFactory
{
    // No override of ConfigureWebHost — inherits InferenceServerFactory's
    // app-config + model registration but NOT AdminTestFactory's
    // AlwaysSucceedBridgeFactory swap. The result: the default
    // MnnInferenceBridgeFactory wired by AddCircleAIInferenceServer.
}
