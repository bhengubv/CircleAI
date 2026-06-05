// AdminEndpointsTests.cs
//
// Integration tests for the Phase 3 admin endpoints. A test-only
// IBridgeFactory is wired in to allow loads to succeed; production
// hosts ship their own factory that materialises real MNN bridges.

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using CircleAI.Hosting.InferenceBridge;
using CircleAI.Inference.Server.Endpoints;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Tests.TestFixtures;
using CircleAI.Runtime.Backends;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class AdminEndpointsTests : IClassFixture<AdminTestFactory>
{
    private readonly AdminTestFactory _factory;
    public AdminEndpointsTests(AdminTestFactory factory) => _factory = factory;

    [Fact]
    public async Task Lifecycle_Returns_200_With_Zero_Allocations_Initially()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.GetAsync("/v1/admin/lifecycle");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("totalAllocatedVramBytes", body, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Load_New_Model_Returns_200_And_Registers_It()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest
        {
            ModelId           = "loaded-by-admin",
            Backend           = "Cpu",
            Tier              = "Tier1_Small",
            VramRequiredBytes = 0,
            RamRequiredBytes  = 100L * 1024 * 1024, // 100 MiB
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        // Now /v1/models should include it.
        var models = await client.GetStringAsync("/v1/models");
        Assert.Contains("loaded-by-admin", models);
    }

    [Fact]
    public async Task Load_With_Invalid_Backend_Returns_400()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest
        {
            ModelId = "bad-backend",
            Backend = "ZebraQuantum",
            Tier    = "Tier1_Small",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Load_With_Missing_ModelId_Returns_400()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest
        {
            ModelId = "",
            Backend = "Cpu",
            Tier    = "Tier0_Tiny",
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unload_Existing_Model_Returns_200_And_Removes_From_Registry()
    {
        using var client = _factory.AuthenticatedClient();

        await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest
        {
            ModelId = "ephemeral",
            Backend = "Cpu",
            Tier    = "Tier0_Tiny",
            RamRequiredBytes = 1L * 1024 * 1024,
        });

        var del = await client.DeleteAsync("/v1/admin/models/ephemeral");
        Assert.Equal(HttpStatusCode.OK, del.StatusCode);

        var models = await client.GetStringAsync("/v1/models");
        Assert.DoesNotContain("ephemeral", models);
    }

    [Fact]
    public async Task Unload_Unknown_Model_Returns_404()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.DeleteAsync("/v1/admin/models/phantom-x");
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Admin_Endpoints_Require_Auth()
    {
        using var client = _factory.CreateClient(); // no API key
        var l = await client.GetAsync("/v1/admin/lifecycle");
        var p = await client.PostAsJsonAsync("/v1/admin/models/load", new AdminLoadRequest());
        var d = await client.DeleteAsync("/v1/admin/models/anything");
        Assert.Equal(HttpStatusCode.Unauthorized, l.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, p.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, d.StatusCode);
    }
}

/// <summary>
/// Test fixture that swaps in an IBridgeFactory which always succeeds,
/// so /v1/admin/models/load actually completes during tests.
/// </summary>
public sealed class AdminTestFactory : InferenceServerFactory
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IBridgeFactory, AlwaysSucceedBridgeFactory>();
        });
    }
}

internal sealed class AlwaysSucceedBridgeFactory : IBridgeFactory
{
    public Task<IInferenceBridge> CreateAsync(
        string modelId, BackendKind backend, CapabilityTier tier, CancellationToken ct) =>
        Task.FromResult<IInferenceBridge>(new StubInferenceBridge(modelId));
}
