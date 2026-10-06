// ElevenLabsConvService.cs
//
// (3.3.0) IRealtimeService backed by ElevenLabs Conversational AI.
// The endpoint takes ?agent_id={id}; xi-api-key header authenticates.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) <see cref="IRealtimeService"/> backed by ElevenLabs Conversational AI.</summary>
public sealed class ElevenLabsConvService : IRealtimeService
{
    private readonly ElevenLabsConvOptions _options;
    private readonly IRealtimeTransportFactory _transports;
    private readonly ILogger _logger;

    public ElevenLabsConvService(
        ElevenLabsConvOptions    options,
        IRealtimeTransportFactory? transports = null,
        ILogger<ElevenLabsConvService>? logger = null)
    {
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _transports = transports ?? NullRealtimeTransportFactory.Instance;
        _logger     = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ProviderId    => "elevenlabs-conv";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey)
                              && !string.IsNullOrWhiteSpace(_options.AgentId);

    public async ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureConfigured();

        var endpoint = new Uri($"{_options.WebSocketEndpoint}?agent_id={Uri.EscapeDataString(_options.AgentId!)}");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["xi-api-key"] = _options.ApiKey!,
        };

        var transport = await _transports.ConnectAsync(endpoint, headers, ct).ConfigureAwait(false);
        return new RealtimeWebSocketSession(transport, config, ProviderId, _logger);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "ElevenLabs Conversational AI is not configured. Set ElevenLabsConvOptions.ApiKey AND AgentId before calling StartSessionAsync.");
        }
    }
}
