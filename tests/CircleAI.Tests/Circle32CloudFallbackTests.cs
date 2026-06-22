// Circle32CloudFallbackTests.cs
//
// (3.2.0) Tests for CircleAI.Hosting.CloudFallback. Fail-soft path
// (no API key configured) is covered without making HTTP calls so the
// suite is hermetic.

using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.CloudFallback;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle32CloudFallbackTests
{
    // ── Provider metadata ─────────────────────────────────────────────

    [Fact]
    public void OpenAi_HasExpectedIdAndLabel()
    {
        var g = new OpenAiChatGenerator(new HttpClient(),
            new OpenAiChatOptions { Model = "gpt-4o-mini" });

        Assert.Equal("openai", g.Id);
        Assert.Contains("gpt-4o-mini", g.EngineLabel);
        Assert.False(g.IsConfigured);
        Assert.Contains("not configured", g.StatusMessage);
    }

    [Fact]
    public void Anthropic_HasExpectedIdAndLabel()
    {
        var g = new AnthropicChatGenerator(new HttpClient(),
            new AnthropicChatOptions { Model = "claude-3-5-sonnet-latest" });

        Assert.Equal("anthropic", g.Id);
        Assert.Contains("claude-3-5-sonnet-latest", g.EngineLabel);
        Assert.False(g.IsConfigured);
    }

    [Fact]
    public void Gemini_HasExpectedIdAndLabel()
    {
        var g = new GeminiChatGenerator(new HttpClient(),
            new GeminiChatOptions { Model = "gemini-2.0-flash" });

        Assert.Equal("gemini", g.Id);
        Assert.Contains("gemini-2.0-flash", g.EngineLabel);
        Assert.False(g.IsConfigured);
    }

    [Fact]
    public void Configured_WhenApiKeyPresent()
    {
        var openai = new OpenAiChatGenerator(new HttpClient(),
            new OpenAiChatOptions { ApiKey = "sk-test" });
        Assert.True(openai.IsConfigured);
        Assert.Contains("Ready", openai.StatusMessage);
    }

    // ── Fail-soft when not configured ─────────────────────────────────

    [Fact]
    public async Task OpenAi_NoKey_StreamYieldsStatusFrameAndStops()
    {
        var g = new OpenAiChatGenerator(new HttpClient(), new OpenAiChatOptions());
        var msgs = new List<ChatMessage> { new("user", "hi") };

        var chunks = new List<string>();
        await foreach (var chunk in g.StreamAsync(msgs))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Contains("not configured", chunks[0]);
    }

    [Fact]
    public async Task Anthropic_NoKey_GenerateReturnsStatusMessage()
    {
        var g = new AnthropicChatGenerator(new HttpClient(), new AnthropicChatOptions());
        var msgs = new List<ChatMessage> { new("user", "hi") };

        var result = await g.GenerateAsync(msgs);
        Assert.Contains("not configured", result);
    }

    [Fact]
    public async Task Gemini_NoKey_StreamYieldsStatusFrameAndStops()
    {
        var g = new GeminiChatGenerator(new HttpClient(), new GeminiChatOptions());
        var msgs = new List<ChatMessage> { new("user", "hi") };

        var chunks = new List<string>();
        await foreach (var chunk in g.StreamAsync(msgs))
        {
            chunks.Add(chunk);
        }

        Assert.Single(chunks);
        Assert.Contains("not configured", chunks[0]);
    }

    // ── CloudFallbackChain ────────────────────────────────────────────

    [Fact]
    public async Task Chain_AllUnconfigured_ReturnsSentinel()
    {
        var chain = new CloudFallbackChain(new IChatGenerator[]
        {
            new OpenAiChatGenerator(new HttpClient(),    new OpenAiChatOptions()),
            new AnthropicChatGenerator(new HttpClient(), new AnthropicChatOptions()),
            new GeminiChatGenerator(new HttpClient(),    new GeminiChatOptions()),
        });

        var msgs = new List<ChatMessage> { new("user", "hi") };
        var result = await chain.GenerateAsync(msgs);
        Assert.Contains("no configured generator", result);
    }

    [Fact]
    public async Task Chain_FirstConfigured_Wins()
    {
        // Fake configured-and-yielding generator first; real (unconfigured)
        // cloud generators after. Chain should yield the fake's output.
        var fake = new ConfiguredFakeGenerator("hello world");
        var chain = new CloudFallbackChain(new IChatGenerator[]
        {
            fake,
            new OpenAiChatGenerator(new HttpClient(), new OpenAiChatOptions()),
        });

        var result = await chain.GenerateAsync(new List<ChatMessage> { new("user", "hi") });
        Assert.Equal("hello world", result);
    }

    [Fact]
    public async Task Chain_SkipsUnconfigured_AndUsesNextReady()
    {
        // Unconfigured OpenAI first, ready fake second. Chain should
        // skip the OpenAI fail-soft frame and use the fake.
        var fake = new ConfiguredFakeGenerator("from-fake");
        var chain = new CloudFallbackChain(new IChatGenerator[]
        {
            new OpenAiChatGenerator(new HttpClient(), new OpenAiChatOptions()),
            fake,
        });

        var chunks = new List<string>();
        await foreach (var chunk in chain.StreamAsync(new List<ChatMessage> { new("user", "hi") }))
        {
            chunks.Add(chunk);
        }
        Assert.Equal("from-fake", string.Concat(chunks));
    }

    [Fact]
    public void Chain_ExposesGeneratorsInOrder()
    {
        var a = new OpenAiChatGenerator(new HttpClient(),    new OpenAiChatOptions());
        var b = new AnthropicChatGenerator(new HttpClient(), new AnthropicChatOptions());
        var c = new GeminiChatGenerator(new HttpClient(),    new GeminiChatOptions());

        var chain = new CloudFallbackChain(new IChatGenerator[] { a, b, c });
        var ordered = chain.Generators.ToList();
        Assert.Equal(3, ordered.Count);
        Assert.Same(a, ordered[0]);
        Assert.Same(b, ordered[1]);
        Assert.Same(c, ordered[2]);
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private sealed class ConfiguredFakeGenerator : IChatGenerator, IConfigurableChatGenerator
    {
        private readonly string _payload;
        public ConfiguredFakeGenerator(string payload) { _payload = payload; }

        public string Id => "fake";
        public string EngineLabel => "Fake";
        public bool   IsConfigured => true;
        public string StatusMessage => "Ready";

        public Task<string> GenerateAsync(IReadOnlyList<ChatMessage> _, GenerationOptions? __ = null, CancellationToken ___ = default)
            => Task.FromResult(_payload);

        public async IAsyncEnumerable<string> StreamAsync(
            IReadOnlyList<ChatMessage> _, GenerationOptions? __ = null,
            [EnumeratorCancellation] CancellationToken ___ = default)
        {
            yield return _payload;
            await Task.CompletedTask;
        }

        public void Dispose() { }
    }
}
