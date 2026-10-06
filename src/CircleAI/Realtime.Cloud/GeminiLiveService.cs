// GeminiLiveService.cs
//
// (3.3.0) IRealtimeService backed by Google Gemini Live (BidiGenerateContent).
// Authenticates with the API key on the query string; uses Google's
// setup / clientContent / serverContent JSON envelope.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) <see cref="IRealtimeService"/> backed by Gemini Live.</summary>
public sealed class GeminiLiveService : IRealtimeService
{
    private readonly GeminiLiveOptions _options;
    private readonly IRealtimeTransportFactory _transports;
    private readonly ILogger _logger;

    public GeminiLiveService(
        GeminiLiveOptions    options,
        IRealtimeTransportFactory? transports = null,
        ILogger<GeminiLiveService>? logger = null)
    {
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _transports = transports ?? NullRealtimeTransportFactory.Instance;
        _logger     = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ProviderId    => "gemini-live";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.ApiKey);

    public async ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureConfigured();

        var endpoint = new Uri($"{_options.WebSocketEndpoint}?key={Uri.EscapeDataString(_options.ApiKey!)}");
        var transport = await _transports.ConnectAsync(endpoint, headers: null, ct).ConfigureAwait(false);
        return new RealtimeWebSocketSession(transport, config, ProviderId, _logger);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Gemini Live is not configured. Set GeminiLiveOptions.ApiKey before calling StartSessionAsync.");
        }
    }
}
