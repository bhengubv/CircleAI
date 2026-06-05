// ChatCompletionsEndpointTests.cs
//
// End-to-end verification of POST /v1/chat/completions in both non-stream
// and SSE-stream modes. Boots the real Program.cs pipeline via
// WebApplicationFactory so middleware, auth, and DI exactly match production.

using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using CircleAI.Inference.Server.Models.OpenAI;
using CircleAI.Inference.Server.Tests.TestFixtures;
using Xunit;

namespace CircleAI.Inference.Server.Tests;

public sealed class ChatCompletionsEndpointTests : IClassFixture<InferenceServerFactory>
{
    private readonly InferenceServerFactory _factory;
    public ChatCompletionsEndpointTests(InferenceServerFactory factory) => _factory = factory;

    [Fact]
    public async Task NonStreaming_Returns_OpenAI_Shaped_Response()
    {
        using var client = _factory.AuthenticatedClient();

        var resp = await client.PostAsJsonAsync("/v1/chat/completions", new ChatCompletionRequest
        {
            Model    = "qwen-test",
            Messages = new List<ChatCompletionMessage>
            {
                new() { Role = "user", Content = "ping" }
            },
            Stream = false,
        });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var body = await resp.Content.ReadFromJsonAsync<ChatCompletionResponse>();
        Assert.NotNull(body);
        Assert.StartsWith("chatcmpl-", body!.Id);
        Assert.Equal("qwen-test", body.Model);
        Assert.Single(body.Choices);
        Assert.Contains("echo:", body.Choices[0].Message.Content);
        Assert.Equal("stop", body.Choices[0].FinishReason);
        Assert.True(body.Usage.TotalTokens > 0);
    }

    [Fact]
    public async Task Missing_Model_Returns_400()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/chat/completions", new ChatCompletionRequest
        {
            Model = "",
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "hi" } }
        });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Unknown_Model_Returns_404()
    {
        using var client = _factory.AuthenticatedClient();
        var resp = await client.PostAsJsonAsync("/v1/chat/completions", new ChatCompletionRequest
        {
            Model = "does-not-exist",
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "hi" } }
        });
        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        var err = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        Assert.NotNull(err);
        Assert.Equal("model_not_found", err!.Error.Code);
    }

    [Fact]
    public async Task Missing_ApiKey_Returns_401_When_Auth_Enabled()
    {
        using var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/v1/chat/completions", new ChatCompletionRequest
        {
            Model    = "qwen-test",
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "hi" } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Wrong_ApiKey_Returns_401()
    {
        using var client = _factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-CircleAI-Api-Key", "wrong-key");
        var resp = await client.PostAsJsonAsync("/v1/chat/completions", new ChatCompletionRequest
        {
            Model    = "qwen-test",
            Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "hi" } }
        });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
    }

    [Fact]
    public async Task Streaming_Emits_SSE_Frames_And_Done_Terminator()
    {
        using var client = _factory.AuthenticatedClient();
        var req = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions")
        {
            Content = JsonContent.Create(new ChatCompletionRequest
            {
                Model    = "qwen-test",
                Messages = new List<ChatCompletionMessage> { new() { Role = "user", Content = "ping" } },
                Stream   = true,
            })
        };

        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.StartsWith("text/event-stream", resp.Content.Headers.ContentType?.ToString() ?? "");

        var body = await resp.Content.ReadAsStringAsync();
        var frames = body.Split("\n\n", StringSplitOptions.RemoveEmptyEntries);

        // Expect ≥ 2 data frames + the [DONE] terminator.
        Assert.True(frames.Length >= 3, $"Too few SSE frames: {frames.Length}");
        Assert.Contains("[DONE]", body);

        // At least one delta frame must carry real content from the stub.
        Assert.Contains("hello", body, StringComparison.Ordinal);
    }
}
