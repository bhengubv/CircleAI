// OpenAiChatGenerator.cs
//
// (3.2.0) IChatGenerator backed by OpenAI's Chat Completions API.
// Works against the official OpenAI endpoint or any compatible
// self-hosted gateway (LM Studio, llama.cpp's HTTP server, vLLM) by
// repointing OpenAiChatOptions.BaseAddress. Direct lift from Concierge's
// OpenAiChatRuntime — same SSE streaming, same fail-soft when key is
// missing, adapted to CircleAI.Inference.IChatGenerator.

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

/// <summary>
/// (3.2.0) <see cref="IChatGenerator"/> backed by OpenAI's
/// <c>/v1/chat/completions</c> streaming endpoint. Fail-soft: if the API
/// key is missing the stream yields one frame with the status reason
/// and stops, so a fallback chain can move on.
/// </summary>
public sealed class OpenAiChatGenerator : IChatGenerator, IConfigurableChatGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OpenAiChatOptions _options;
    private readonly ILogger _logger;

    public OpenAiChatGenerator(HttpClient http, OpenAiChatOptions options, ILogger<OpenAiChatGenerator>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id           => "openai";
    public string EngineLabel  => $"OpenAI · {_options.Model}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsConfigured
        ? $"Ready · {_options.Model}"
        : "OpenAI API key not configured.";

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

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/chat/completions");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        var body = new
        {
            model       = _options.Model,
            stream      = true,
            temperature = options?.Temperature ?? _options.Temperature,
            max_tokens  = options?.MaxTokens   ?? _options.MaxTokens,
            messages    = messages.Select(m => new { role = m.Role, content = m.Content }).ToArray(),
        };
        request.Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("OpenAI returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[OpenAI error {(int)response.StatusCode}: {Truncate(error, 240)}]";
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
                // Heartbeat / partial frame — skip without breaking the stream.
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
        // HttpClient is owned by IHttpClientFactory; we don't dispose it here.
        GC.SuppressFinalize(this);
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "…";
}
