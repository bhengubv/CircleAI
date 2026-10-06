// OpenAiRealtimeService.cs
//
// (3.3.0) IRealtimeService backed by OpenAI's gpt-4o-realtime WSS API.
// Authenticates with Bearer + OpenAI-Beta: realtime=v1 header. The
// session translates RealtimeAudioFrame ↔ OpenAI's input_audio_buffer
// / response.audio.delta JSON envelopes.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) <see cref="IRealtimeService"/> backed by OpenAI Realtime.</summary>
public sealed class OpenAiRealtimeService : IRealtimeService
{
    private readonly OpenAiRealtimeOptions _options;
    private readonly IRealtimeTransportFactory _transports;
    private readonly ILogger _logger;

    public OpenAiRealtimeService(
        OpenAiRealtimeOptions    options,
        IRealtimeTransportFactory? transports = null,
        ILogger<OpenAiRealtimeService>? logger = null)
    {
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _transports = transports ?? NullRealtimeTransportFactory.Instance;
        _logger     = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ProviderId    => "openai-realtime";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureConfigured();

        var modelToUse = string.IsNullOrWhiteSpace(config.Model) ? _options.DefaultModel : config.Model;
        var endpoint   = new Uri($"{_options.WebSocketEndpoint}?model={Uri.EscapeDataString(modelToUse)}");

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Authorization"]  = $"Bearer {_options.ApiKey}",
            ["OpenAI-Beta"]    = _options.BetaHeader,
        };

        var transport = await _transports.ConnectAsync(endpoint, headers, ct).ConfigureAwait(false);
        return new RealtimeWebSocketSession(transport, config, ProviderId, _logger);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "OpenAI Realtime is not configured. Set OpenAiRealtimeOptions.ApiKey before calling StartSessionAsync.");
        }
    }
}
