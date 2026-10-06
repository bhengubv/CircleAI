// AnthropicChatGenerator.cs
//
// (3.2.0) IChatGenerator backed by Anthropic's Messages API. Lifted
// from Concierge's AnthropicChatRuntime. Anthropic differs from OpenAI
// in two ways: (1) system prompt is a top-level field, not a
// role: "system" entry; (2) streamed deltas come back as
// content_block_delta events with payload { delta: { type, text } }.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
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

/// <summary>
/// (3.2.0) <see cref="IChatGenerator"/> backed by Anthropic's
/// <c>/v1/messages</c> streaming endpoint.
/// </summary>
public sealed class AnthropicChatGenerator : IChatGenerator, IConfigurableChatGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly AnthropicChatOptions _options;
    private readonly ILogger _logger;

    public AnthropicChatGenerator(HttpClient http, AnthropicChatOptions options, ILogger<AnthropicChatGenerator>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id           => "anthropic";
    public string EngineLabel  => $"Anthropic · {_options.Model}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsConfigured
        ? $"Ready · {_options.Model}"
        : "Anthropic API key not configured.";

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

        // Anthropic wants system prompt out-of-band; split user/assistant from system.
        var system = string.Join(
            "\n\n",
            messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));
        var chat = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new { role = m.Role.ToLowerInvariant(), content = m.Content })
            .ToArray();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages");
        request.Headers.Add("x-api-key", _options.ApiKey);
        request.Headers.Add("anthropic-version", _options.AnthropicVersion);

        object body = string.IsNullOrEmpty(system)
            ? new
            {
                model       = _options.Model,
                max_tokens  = options?.MaxTokens   ?? _options.MaxTokens,
                temperature = options?.Temperature ?? _options.Temperature,
                stream      = true,
                messages    = chat,
            }
            : new
            {
                model       = _options.Model,
                max_tokens  = options?.MaxTokens   ?? _options.MaxTokens,
                temperature = options?.Temperature ?? _options.Temperature,
                stream      = true,
                system,
                messages    = chat,
            };
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Anthropic returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[Anthropic error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, ct))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("type", out var typeEl)
                    && typeEl.GetString() == "content_block_delta"
                    && doc.RootElement.TryGetProperty("delta", out var deltaEl)
                    && deltaEl.TryGetProperty("text", out var textEl)
                    && textEl.ValueKind == JsonValueKind.String)
                {
                    delta = textEl.GetString();
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

    public void Dispose()
    {
        GC.SuppressFinalize(this);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
