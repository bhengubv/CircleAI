// StabilityImageGenerator.cs
//
// (3.2.0) IImageGenerator backed by Stability AI's
// /v2beta/stable-image/generate/sd3 endpoint. Direct lift of Concierge's
// StabilityImageRuntime — Stability returns one image per call, so we
// loop on the caller's behalf to honour Count.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Vision.Cloud;

/// <summary>
/// (3.2.0) <see cref="IImageGenerator"/> backed by Stability AI. Returns
/// images inline as bytes (no remote URL).
/// </summary>
public sealed class StabilityImageGenerator : IImageGenerator
{
    private readonly HttpClient _http;
    private readonly StabilityImageOptions _options;
    private readonly ILogger _logger;

    public StabilityImageGenerator(HttpClient http, StabilityImageOptions options, ILogger<StabilityImageGenerator>? logger = null)
    {
        _http    = http    ?? throw new ArgumentNullException(nameof(http));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger  = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null)
        {
            _http.BaseAddress = options.BaseAddress;
        }
    }

    public string GeneratorId   => "stability";
    public string DisplayLabel  => $"Stability AI · {_options.Model}";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey);
    public string StatusMessage => IsConfigured
        ? $"Ready · {_options.Model}"
        : "Stability AI API key not configured — set Stability:ApiKey to enable.";

    public async Task<IReadOnlyList<ImageArtifact>> GenerateAsync(
        ImageGenerationRequest request,
        CancellationToken      ct = default)
    {
        if (!IsConfigured)
        {
            return Array.Empty<ImageArtifact>();
        }

        var artifacts = new List<ImageArtifact>();
        var count = Math.Clamp(request.Count, 1, 4);
        for (var i = 0; i < count; i++)
        {
            ct.ThrowIfCancellationRequested();

            using var msg = new HttpRequestMessage(HttpMethod.Post, "/v2beta/stable-image/generate/sd3");
            msg.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
            msg.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue($"image/{_options.OutputFormat}"));

            var form = new MultipartFormDataContent
            {
                { new StringContent(request.Prompt),         "prompt"        },
                { new StringContent(_options.OutputFormat),  "output_format" },
                { new StringContent(_options.Model),         "model"         },
            };
            if (!string.IsNullOrEmpty(request.NegativePrompt))
            {
                form.Add(new StringContent(request.NegativePrompt), "negative_prompt");
            }
            msg.Content = form;

            using var response = await _http.SendAsync(msg, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                var error = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                _logger.LogWarning("Stability returned {Status}: {Body}", response.StatusCode, error);
                continue;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
            artifacts.Add(new ImageArtifact(
                GeneratorId:    GeneratorId,
                Prompt:         request.Prompt,
                MimeType:       $"image/{_options.OutputFormat}",
                Url:            null,
                Bytes:          bytes,
                GeneratedAtUtc: DateTimeOffset.UtcNow));
        }

        return artifacts;
    }
}
