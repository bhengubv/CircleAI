// CompanionEndpointTests.cs

using System.Net;
using System.Net.Http.Json;
using CircleAI.Inference.Server.Models.Companion;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class CompanionEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public CompanionEndpointTests(InferenceServerFactory factory)
    {
        _factory = factory;
        _factory.CompanionResolver.Register("sess-1", "uhid-1");
    }

    [Fact]
    public async Task Turn_Returns_Stub_Reply()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/companion/turn", new CompanionTurnRequest
        {
            SessionId  = "sess-1",
            IdentityId = "uhid-1",
            Message    = "hello",
            Agentic    = false,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CompanionTurnResponse>();
        Assert.NotNull(body);
        Assert.Equal("sess-1", body!.SessionId);
        Assert.Equal("stub-reply(hello)", body.Reply);
        Assert.False(body.Agentic);
    }

    [Fact]
    public async Task Turn_With_Agentic_True_Calls_AgentAsync()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/companion/turn", new CompanionTurnRequest
        {
            SessionId  = "sess-1",
            IdentityId = "uhid-1",
            Message    = "do thing",
            Agentic    = true,
        });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<CompanionTurnResponse>();
        Assert.Equal("stub-agent(do thing)", body!.Reply);
        Assert.True(body.Agentic);
    }

    [Fact]
    public async Task Unknown_Session_Returns_404()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/companion/turn", new CompanionTurnRequest
        {
            SessionId  = "no-such-session",
            IdentityId = "no-such-uhid",
            Message    = "hi"
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }

    [Fact]
    public async Task Missing_Field_Returns_400()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/companion/turn", new CompanionTurnRequest());
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }
}
