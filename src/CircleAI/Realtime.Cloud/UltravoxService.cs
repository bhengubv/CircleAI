// UltravoxService.cs
//
// (3.3.0) IRealtimeService backed by Ultravox. Two-step: POST /api/calls
// to create a call → returns joinUrl → open WS to joinUrl.

using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) <see cref="IRealtimeService"/> backed by Ultravox.</summary>
public sealed class UltravoxService : IRealtimeService
{
    private readonly HttpClient _http;
    private readonly UltravoxOptions _options;
    private readonly IRealtimeTransportFactory _transports;
    private readonly ILogger _logger;

    public UltravoxService(
        HttpClient                  http,
        UltravoxOptions             options,
        IRealtimeTransportFactory?  transports = null,
        ILogger<UltravoxService>?   logger     = null)
    {
        _http       = http       ?? throw new ArgumentNullException(nameof(http));
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _transports = transports ?? NullRealtimeTransportFactory.Instance;
        _logger     = (ILogger?)logger ?? NullLogger.Instance;

        if (_http.BaseAddress is null) _http.BaseAddress = options.ApiEndpoint;
    }

    public string ProviderId    => "ultravox";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureConfigured();

        var modelToUse = string.IsNullOrWhiteSpace(config.Model) ? _options.DefaultModel : config.Model;
        var voiceToUse = string.IsNullOrWhiteSpace(config.VoiceId) ? _options.DefaultVoice : config.VoiceId;

        using var req = new HttpRequestMessage(HttpMethod.Post, "/api/calls")
        {
            Content = JsonContent.Create(new
            {
                model              = modelToUse,
                voice              = voiceToUse,
                systemPrompt       = config.SystemPrompt,
                medium             = new { serverWebSocket = new { inputSampleRate = 16000, outputSampleRate = 24000 } },
            }),
        };
        req.Headers.Add("X-API-Key", _options.ApiKey);

        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();
        using var doc = await JsonDocument.ParseAsync(
            await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false),
            cancellationToken: ct).ConfigureAwait(false);

        var joinUrl = doc.RootElement.TryGetProperty("joinUrl", out var ju) ? ju.GetString() : null;
        if (string.IsNullOrWhiteSpace(joinUrl))
        {
            throw new InvalidOperationException("Ultravox API did not return a joinUrl.");
        }

        var transport = await _transports.ConnectAsync(new Uri(joinUrl), headers: null, ct).ConfigureAwait(false);
        return new RealtimeWebSocketSession(transport, config, ProviderId, _logger);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Ultravox is not configured. Set UltravoxOptions.ApiKey before calling StartSessionAsync.");
        }
    }
}
