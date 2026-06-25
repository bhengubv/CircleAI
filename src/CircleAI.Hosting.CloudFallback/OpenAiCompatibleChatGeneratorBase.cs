// OpenAiCompatibleChatGeneratorBase.cs
//
// (3.3.0) Shared streaming SSE chat-completions implementation for any
// vendor that speaks the OpenAI Chat Completions wire format. Groq,
// Cerebras, Together AI, and DeepSeek all do — each subclass supplies
// its own provider id, model name, and HTTP base address.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Inference;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Hosting.CloudFallback;

/// <summary>(3.3.0) Shared OpenAI-compatible streaming chat generator.</summary>
public abstract class OpenAiCompatibleChatGeneratorBase : IChatGenerator, IConfigurableChatGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger _logger;

    protected OpenAiCompatibleChatGeneratorBase(HttpClient http, Uri baseAddress, ILogger logger)
    {
        _http   = http   ?? throw new ArgumentNullException(nameof(http));
        _logger = logger ?? NullLogger.Instance;
        if (_http.BaseAddress is null) _http.BaseAddress = baseAddress;
    }

    /// <summary>Vendor identifier (e.g. <c>groq</c>, <c>cerebras</c>).</summary>
    public abstract string Id { get; }

    /// <summary>Human label like <c>Groq · llama-3.3-70b-versatile</c>.</summary>
    public abstract string EngineLabel { get; }

    /// <summary>API key — null/empty means not configured.</summary>
    protected abstract string? ApiKey { get; }

    /// <summary>Default model name.</summary>
    protected abstract string Model { get; }

    /// <summary>Default sampling temperature.</summary>
    protected abstract float DefaultTemperature { get; }

    /// <summary>Default max output tokens.</summary>
    protected abstract int DefaultMaxTokens { get; }

    /// <summary>Path to the chat-completions endpoint. Most vendors use <c>/v1/chat/completions</c>.</summary>
    protected virtual string ChatCompletionsPath => "/v1/chat/completions";

    public bool   IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);
    public string StatusMessage => IsConfigured ? $"Ready · {Model}" : $"{Id} API key not configured.";

    public async Task<string> GenerateAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        CancellationToken          ct      = default)
    {
        var sb = new StringBuilder();
        await foreach (var chunk in StreamAsync(messages, options, ct).ConfigureAwait(false))
        {
            sb.Append(chunk);
        }
        return sb.ToString();
    }

    public async IAsyncEnumerable<string> StreamAsync(
        IReadOnlyList<ChatMessage> messages,
        GenerationOptions?         options = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!IsConfigured)
        {
            yield return $"[{StatusMessage}]";
            yield break;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, ChatCompletionsPath);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);

        var body = new
        {
            model       = Model,
            stream      = true,
            temperature = options?.Temperature ?? DefaultTemperature,
            max_tokens  = options?.MaxTokens   ?? DefaultMaxTokens,
            messages    = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("{Provider} returned {Status}: {Body}", Id, response.StatusCode, error);
            yield return $"[{Id} error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, ct))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("choices", out var choices)
                    && choices.GetArrayLength() > 0
                    && choices[0].TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("content", out var contentEl)
                    && contentEl.ValueKind == JsonValueKind.String)
                {
                    delta = contentEl.GetString();
                }
            }
            catch (JsonException)
            {
                continue;
            }

            if (!string.IsNullOrEmpty(delta))
            {
                yield return delta;
            }
        }
    }

    public void Dispose() => GC.SuppressFinalize(this);

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
