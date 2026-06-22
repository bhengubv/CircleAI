// OpenAiImageGenerator.cs
//
// (3.2.0) IImageGenerator backed by OpenAI's /v1/images/generations
// endpoint. Direct lift of Concierge.Media.Cloud.OpenAiImageRuntime —
// same response_format=url path, same Math.Clamp(Count, 1, 4) safety,
// adapted to CircleAI's IImageGenerator surface.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Vision.Cloud;

/// <summary>
/// (3.2.0) <see cref="IImageGenerator"/> backed by OpenAI DALL-E. Fail-soft
/// when the API key is missing — returns an empty artifact list so a
/// fallback chain can move on.
/// </summary>
public sealed class OpenAiImageGenerator : IImageGenerator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly OpenAiImageOptions _options;
    private readonly ILogger _logger;

    public OpenAiImageGenerator(HttpClient http, OpenAiImageOptions options, ILogger<OpenAiImageGenerator>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string GeneratorId   => "openai-images";
    public string DisplayLabel  => $"OpenAI · {_options.Model}";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsConfigured
        ? $"Ready · {_options.Model}"
        : "OpenAI API key not configured — set OpenAI:ApiKey to enable.";

    public async Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken      ct = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<ImageArtifact>();
        }

        using var msg = new HttpRequestMessage(HttpMethod.Post, "/v1/images/generations");
        msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        msg.Content = JsonContent.Create(new
        {
            model           = _options.Model,
            prompt          = request.Prompt,
            n               = Math.Clamp(request.Count, 1, 4),
            size            = $"{request.Size}x{request.Size}",
            response_format = "url",
        }, options: JsonOptions);

        using var response = await _http.SendAsync(msg, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            _logger.LogWarning("OpenAI images returned {Status}: {Body}", response.StatusCode, error);
            return Array.Empty<ImageArtifact>();
        }

        using var doc = await JsonDocument.ParseAsync(
            await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var artifacts = new List<ImageArtifact>();
        if (doc.RootElement.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in data.EnumerateArray())
            {
                if (item.TryGetProperty("url", out var url) && url.ValueKind == JsonValueKind.String)
                {
                    artifacts.Add(new ImageArtifact(
                        GeneratorId:    GeneratorId,
                        Prompt:         request.Prompt,
                        MimeType:       "image/png",
                        Url:            url.GetString(),
                        Bytes:          null,
                        GeneratedAtUtc: DateTimeOffset.UtcNow));
                }
            }
        }

        return artifacts;
    }
}
