// NovaSonicService.cs
//
// (3.3.0) IRealtimeService backed by AWS Nova Sonic. Real production
// use requires SigV4 signing on the WS handshake — surfaced via the
// IRealtimeTransportFactory's headers contract; the host's factory
// implementation performs the signing.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Realtime.Cloud;

/// <summary>(3.3.0) <see cref="IRealtimeService"/> backed by AWS Nova Sonic.</summary>
public sealed class NovaSonicService : IRealtimeService
{
    private readonly NovaSonicOptions _options;
    private readonly IRealtimeTransportFactory _transports;
    private readonly ILogger _logger;

    public NovaSonicService(
        NovaSonicOptions    options,
        IRealtimeTransportFactory? transports = null,
        ILogger<NovaSonicService>? logger = null)
    {
        _options    = options    ?? throw new ArgumentNullException(nameof(options));
        _transports = transports ?? NullRealtimeTransportFactory.Instance;
        _logger     = (ILogger?)logger ?? NullLogger.Instance;
    }

    public string ProviderId    => "aws-nova-sonic";
    public bool   IsConfigured  => !string.IsNullOrWhiteSpace(_options.AccessKeyId)
                              && !string.IsNullOrWhiteSpace(_options.SecretAccessKey);

    public async ValueTask<IRealtimeSession> StartSessionAsync(
        RealtimeSessionConfig config,
        CancellationToken     ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        EnsureConfigured();

        var endpoint = new Uri(
            $"wss://bedrock-runtime.{_options.Region}.amazonaws.com/model/{Uri.EscapeDataString(config.Model)}/invoke-with-bidirectional-stream");

        // Expose the credentials via headers; the host's transport factory
        // is responsible for SigV4-signing the request.
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Amz-Access-Key"] = _options.AccessKeyId!,
            ["X-Amz-Secret-Key"] = _options.SecretAccessKey!,
            ["X-Amz-Region"]     = _options.Region,
        };
        if (!string.IsNullOrWhiteSpace(_options.SessionToken))
        {
            headers["X-Amz-Security-Token"] = _options.SessionToken!;
        }

        var transport = await _transports.ConnectAsync(endpoint, headers, ct).ConfigureAwait(false);
        return new RealtimeWebSocketSession(transport, config, ProviderId, _logger);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "AWS Nova Sonic is not configured. Set NovaSonicOptions.AccessKeyId and SecretAccessKey before calling StartSessionAsync.");
        }
    }
}
