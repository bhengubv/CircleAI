// EmbeddingsEndpointTests.cs

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class EmbeddingsEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public EmbeddingsEndpointTests(InferenceServerFactory factory) => _factory = factory;

    [Fact]
    public async Task Single_String_Input_Returns_One_Embedding()
    {
        using var client = _factory.AuthenticatedClient();
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            model = "qwen-embed-test",
            input = "hello world"
        }));
        using var resp = await client.PostAsync("/v1/embeddings",
            JsonContent.Create(payload.RootElement));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<EmbeddingsResponse>();
        Assert.NotNull(body);
        Assert.Single(body!.Data);
        Assert.True(body.Data[0].Embedding.Count > 0);
        Assert.Equal("qwen-embed-test", body.Model);
        Assert.True(body.Usage.PromptTokens > 0);
    }

    [Fact]
    public async Task Array_Input_Returns_One_Embedding_Per_Element()
    {
        using var client = _factory.AuthenticatedClient();
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            model = "qwen-embed-test",
            input = new[] { "alpha", "beta", "gamma" }
        }));
        using var resp = await client.PostAsync("/v1/embeddings",
            JsonContent.Create(payload.RootElement));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);

        var body = await resp.Content.ReadFromJsonAsync<EmbeddingsResponse>();
        Assert.NotNull(body);
        Assert.Equal(3, body!.Data.Count);
        for (var i = 0; i < 3; i++) Assert.Equal(i, body.Data[i].Index);
    }

    [Fact]
    public async Task Unknown_Model_Returns_404()
    {
        using var client = _factory.AuthenticatedClient();
        var payload = JsonDocument.Parse(JsonSerializer.Serialize(new
        {
            model = "no-such-model",
            input = "x"
        }));
        using var resp = await client.PostAsync("/v1/embeddings",
            JsonContent.Create(payload.RootElement));
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
