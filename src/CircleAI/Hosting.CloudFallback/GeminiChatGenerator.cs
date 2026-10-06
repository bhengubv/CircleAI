// GeminiChatGenerator.cs
//
// (3.2.0) IChatGenerator backed by Google's Gemini
// streamGenerateContent endpoint. Lifted from Concierge's
// GeminiChatRuntime. Gemini differs from OpenAI/Anthropic: roles use
// "model" rather than "assistant", and system prompt rides on a
// separate systemInstruction field.

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
/// (3.2.0) <see cref="IChatGenerator"/> backed by Google Gemini's
/// <c>/v1beta/models/{model}:streamGenerateContent</c> endpoint.
/// </summary>
public sealed class GeminiChatGenerator : IChatGenerator, IConfigurableChatGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly GeminiChatOptions _options;
    private readonly ILogger _logger;

    public GeminiChatGenerator(HttpClient http, GeminiChatOptions options, ILogger<GeminiChatGenerator>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string Id           => "gemini";
    public string EngineLabel  => $"Gemini · {_options.Model}";
    public bool   IsConfigured => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsConfigured
        ? $"Ready · {_options.Model}"
        : "Gemini API key not configured.";

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

        var system = string.Join(
            "\n\n",
            messages
                .Where(m => string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
                .Select(m => m.Content));
        var contents = messages
            .Where(m => !string.Equals(m.Role, "system", StringComparison.OrdinalIgnoreCase))
            .Select(m => new
            {
                role  = string.Equals(m.Role, "assistant", StringComparison.OrdinalIgnoreCase) ? "model" : m.Role.ToLowerInvariant(),
                parts = new[] { new { text = m.Content } },
            })
            .ToArray();

        object body = string.IsNullOrEmpty(system)
            ? new
            {
                contents,
                generationConfig = new
                {
                    temperature     = options?.Temperature ?? _options.Temperature,
                    maxOutputTokens = options?.MaxTokens   ?? _options.MaxOutputTokens,
                },
            }
            : new
            {
                contents,
                systemInstruction = new { parts = new[] { new { text = system } } },
                generationConfig = new
                {
                    temperature     = options?.Temperature ?? _options.Temperature,
                    maxOutputTokens = options?.MaxTokens   ?? _options.MaxOutputTokens,
                },
            };

        var path = $"/v1beta/models/{Uri.EscapeDataString(_options.Model)}:streamGenerateContent?alt=sse&key={Uri.EscapeDataString(_options.ApiKey!)}";
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json"),
        };

        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("Gemini returned {Status}: {Body}", response.StatusCode, error);
            yield return $"[Gemini error {(int)response.StatusCode}: {Truncate(error, 240)}]";
            yield break;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await foreach (var frame in ServerSentEventsReader.ReadFramesAsync(stream, ct))
        {
            string? delta = null;
            try
            {
                using var doc = JsonDocument.Parse(frame);
                if (doc.RootElement.TryGetProperty("candidates", out var candidates)
                    && candidates.GetArrayLength() > 0
                    && candidates[0].TryGetProperty("content", out var contentEl)
                    && contentEl.TryGetProperty("parts", out var partsEl)
                    && partsEl.GetArrayLength() > 0
                    && partsEl[0].TryGetProperty("text", out var textEl)
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
