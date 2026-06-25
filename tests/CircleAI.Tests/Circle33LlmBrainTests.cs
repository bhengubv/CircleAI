// Circle33LlmBrainTests.cs
//
// (3.3.0) Tests for the 4 new OpenAI-compatible LLM brain connectors
// (Groq, Cerebras, Together AI, DeepSeek). OpenAI / Anthropic / Gemini
// were shipped in 3.2.0.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Hosting.CloudFallback;
using CircleAI.Inference;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class Circle33LlmBrainTests
{
    private static readonly IReadOnlyList<ChatMessage> Messages = new[]
    {
        new ChatMessage("user", "hi"),
    };

    // ===== Groq =====

    [Fact]
    public void Groq_NotConfigured_WhenKeyMissing()
    {
        var gen = new GroqChatGenerator(new HttpClient(new BrainHandler()), new GroqChatOptions());
        Assert.False(gen.IsConfigured);
        Assert.Equal("groq", gen.Id);
    }

    [Fact]
    public async Task Groq_NotConfigured_YieldsStatusReason()
    {
        var gen = new GroqChatGenerator(new HttpClient(new BrainHandler()), new GroqChatOptions());
        var output = await gen.GenerateAsync(Messages);
        Assert.Contains("not configured", output);
    }

    [Fact]
    public async Task Groq_Configured_StreamsContent_AndUsesOpenAiPath()
    {
        var handler = new BrainHandler((_ => true, Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hi\"}}]}\n\n",
            "data: {\"choices\":[{\"delta\":{\"content\":\" there\"}}]}\n\n",
            "data: [DONE]\n\n")));
        var gen = new GroqChatGenerator(new HttpClient(handler),
            new GroqChatOptions { ApiKey = "k", Model = "llama-3.3-70b-versatile" });

        var output = await gen.GenerateAsync(Messages);

        Assert.Equal("hi there", output);
        Assert.Equal("/openai/v1/chat/completions", handler.Requests[0].RequestUri!.AbsolutePath);
        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization?.Scheme);
    }

    // ===== Cerebras =====

    [Fact]
    public void Cerebras_NotConfigured_WhenKeyMissing()
    {
        var gen = new CerebrasChatGenerator(new HttpClient(new BrainHandler()), new CerebrasChatOptions());
        Assert.False(gen.IsConfigured);
        Assert.Equal("cerebras", gen.Id);
    }

    [Fact]
    public async Task Cerebras_Configured_StreamsContent()
    {
        var handler = new BrainHandler((_ => true, Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\n",
            "data: [DONE]\n\n")));
        var gen = new CerebrasChatGenerator(new HttpClient(handler),
            new CerebrasChatOptions { ApiKey = "k" });

        var output = await gen.GenerateAsync(Messages);

        Assert.Equal("hello", output);
        Assert.Equal("/v1/chat/completions", handler.Requests[0].RequestUri!.AbsolutePath);
    }

    // ===== Together AI =====

    [Fact]
    public void Together_NotConfigured_WhenKeyMissing()
    {
        var gen = new TogetherChatGenerator(new HttpClient(new BrainHandler()), new TogetherChatOptions());
        Assert.False(gen.IsConfigured);
        Assert.Equal("together", gen.Id);
    }

    [Fact]
    public async Task Together_Configured_StreamsContent()
    {
        var handler = new BrainHandler((_ => true, Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"sup\"}}]}\n\n",
            "data: [DONE]\n\n")));
        var gen = new TogetherChatGenerator(new HttpClient(handler),
            new TogetherChatOptions { ApiKey = "k" });

        var output = await gen.GenerateAsync(Messages);
        Assert.Equal("sup", output);
    }

    // ===== DeepSeek =====

    [Fact]
    public void DeepSeek_NotConfigured_WhenKeyMissing()
    {
        var gen = new DeepSeekChatGenerator(new HttpClient(new BrainHandler()), new DeepSeekChatOptions());
        Assert.False(gen.IsConfigured);
        Assert.Equal("deepseek", gen.Id);
    }

    [Fact]
    public async Task DeepSeek_Configured_StreamsContent()
    {
        var handler = new BrainHandler((_ => true, Sse(
            "data: {\"choices\":[{\"delta\":{\"content\":\"yo\"}}]}\n\n",
            "data: [DONE]\n\n")));
        var gen = new DeepSeekChatGenerator(new HttpClient(handler),
            new DeepSeekChatOptions { ApiKey = "k" });

        var output = await gen.GenerateAsync(Messages);
        Assert.Equal("yo", output);
    }

    // ===== Error handling =====

    [Fact]
    public async Task Groq_OnError_YieldsErrorReason()
    {
        var handler = new BrainHandler((_ => true,
            new HttpResponseMessage(HttpStatusCode.TooManyRequests)
            {
                Content = new StringContent("rate limited"),
            }));
        var gen = new GroqChatGenerator(new HttpClient(handler),
            new GroqChatOptions { ApiKey = "k" });

        var output = await gen.GenerateAsync(Messages);
        Assert.Contains("groq error", output);
        Assert.Contains("429", output);
    }

    // ===== DI =====

    [Fact]
    public void DI_AllFourBrains_RegisterUnderKeyedIChatGenerator()
    {
        var services = new ServiceCollection();
        services.AddGroqChatGenerator    (_ => new GroqChatOptions     { ApiKey = "x" });
        services.AddCerebrasChatGenerator(_ => new CerebrasChatOptions { ApiKey = "x" });
        services.AddTogetherChatGenerator(_ => new TogetherChatOptions { ApiKey = "x" });
        services.AddDeepSeekChatGenerator(_ => new DeepSeekChatOptions { ApiKey = "x" });
        using var sp = services.BuildServiceProvider();

        Assert.IsType<GroqChatGenerator>    (sp.GetRequiredKeyedService<IChatGenerator>("groq"));
        Assert.IsType<CerebrasChatGenerator>(sp.GetRequiredKeyedService<IChatGenerator>("cerebras"));
        Assert.IsType<TogetherChatGenerator>(sp.GetRequiredKeyedService<IChatGenerator>("together"));
        Assert.IsType<DeepSeekChatGenerator>(sp.GetRequiredKeyedService<IChatGenerator>("deepseek"));
    }

    [Fact]
    public void EngineLabel_IncludesProviderAndModel()
    {
        var gen = new GroqChatGenerator(new HttpClient(new BrainHandler()),
            new GroqChatOptions { ApiKey = "k", Model = "mixtral-8x7b" });
        Assert.Equal("Groq · mixtral-8x7b", gen.EngineLabel);
    }

    // ===== Helpers =====

    private static HttpResponseMessage Sse(params string[] frames)
    {
        var ms = new MemoryStream();
        foreach (var f in frames)
        {
            var bytes = Encoding.UTF8.GetBytes(f);
            ms.Write(bytes, 0, bytes.Length);
        }
        ms.Position = 0;
        var msg = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StreamContent(ms),
        };
        msg.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/event-stream");
        return msg;
    }

    private sealed class BrainHandler : HttpMessageHandler
    {
        private readonly List<(Func<HttpRequestMessage, bool> Match, HttpResponseMessage Response)> _responses;
        public List<HttpRequestMessage> Requests { get; } = new();

        public BrainHandler(params (Func<HttpRequestMessage, bool>, HttpResponseMessage)[] responses)
        {
            _responses = responses.ToList();
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            for (int i = 0; i < _responses.Count; i++)
            {
                if (_responses[i].Match(request))
                {
                    var resp = _responses[i].Response;
                    _responses.RemoveAt(i);
                    return Task.FromResult(resp);
                }
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
