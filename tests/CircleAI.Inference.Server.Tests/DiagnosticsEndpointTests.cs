// DiagnosticsEndpointTests.cs

using System.Net;
using System.Net.Http.Json;
using CircleAI.Inference.Server.Models.Diagnostics;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class DiagnosticsEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public DiagnosticsEndpointTests(InferenceServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Healthz_Returns_200_Without_Auth()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/healthz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<HealthResponse>();
        Assert.Equal("alive", body!.Status);
    }

    [Fact]
    public async Task Readyz_Returns_200_When_Models_Are_Registered()
    {
        using var client = _factory.CreateClient();
        var resp = await client.GetAsync("/v1/readyz");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Diagnostics_Requires_Auth_And_Reports_Loaded_Models()
    {
        using var unauth = _factory.CreateClient();
        var bad = await unauth.GetAsync("/v1/diagnostics");
        Assert.Equal(HttpStatusCode.Unauthorized, bad.StatusCode);

        using var ok = _factory.AuthenticatedClient();
        var good = await ok.GetAsync("/v1/diagnostics");
        Assert.Equal(HttpStatusCode.OK, good.StatusCode);
        var body = await good.Content.ReadFromJsonAsync<DiagnosticsResponse>();
        Assert.NotNull(body);
        Assert.Contains(body!.LoadedModels, m => m.Id == "qwen-test");
        Assert.Contains(body.LoadedModels,  m => m.Id == "qwen-embed-test");
        Assert.NotNull(body.HostProfile);
        Assert.NotNull(body.BackendSelection);
        Assert.False(string.IsNullOrEmpty(body.BackendSelection!.Rationale));
    }

    [Fact]
    public async Task Models_List_Returns_Both_Chat_And_Embedding_Ids()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var json = await resp.Content.ReadAsStringAsync();
        Assert.Contains("qwen-test", json);
        Assert.Contains("qwen-embed-test", json);
    }
}
