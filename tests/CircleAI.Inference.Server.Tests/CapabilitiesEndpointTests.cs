// CapabilitiesEndpointTests.cs
//
// GET /v1/capabilities — Circle AI's honest self-catalogue over HTTP.
// Auth-gated like the other read endpoints; the body is the embedded manifest,
// so the assertions are deterministic (no model, no pack, no flake).

using System.Net;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class CapabilitiesEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public CapabilitiesEndpointTests(InferenceServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Capabilities_Requires_Auth()
    {
        using var unauth = _factory.CreateClient();
        var resp = await unauth.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Capabilities_Authed_Returns_The_Honest_Catalogue()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.GetAsync("/v1/capabilities");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var json = await resp.Content.ReadAsStringAsync();
        // The default catalogue is the embedded manifest; the self-healing sense
        // built in Slice 0 is an entry in it, carrying its status and limits.
        Assert.Contains("\"capabilities\"", json);
        Assert.Contains("self.healing", json);
        Assert.Contains("\"status\"", json);
        Assert.Contains("\"limits\"", json);
    }
}
