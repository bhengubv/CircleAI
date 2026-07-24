// MeshOffloadClient.cs
//
// The single owner of the shared INetworkTransport's receive stream. It plays
// three roles off one pump:
//   1. CONSUMER  - RequestAsync sends a turn to a peer and awaits the reply,
//                  matched by correlation id.
//   2. PROVIDER  - inbound requests are served with the local brain
//                  (ILocalInferenceFallback) and answered, honouring a
//                  concurrency cap so a thin device is not swamped.
//   3. INGEST    - inbound advertisements are folded into the shared
//                  IMeshCapabilityRegistry so the router can find peers.
//
// It does NOT discover peers or open sockets - it rides whatever reachable
// transport the host wired (hotspot / LAN / Aether mesh). Zero-infrastructure
// BLE / Wi-Fi Direct discovery is AetherNet's responsibility (aether-protocol
// repo), not this package's.

using System.Collections.Concurrent;
using System.Diagnostics;
using CircleAI.AetherNet;
using CircleAI.Core;
using CircleAI.Networking;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace CircleAI.Mesh;

/// <summary>
/// Transport engine for mesh offload: request/reply correlation, inbound
/// serving, and advertisement ingest, all off one receive pump. Register it as
/// a singleton and a hosted service so the pump runs for the app's lifetime.
/// </summary>
public sealed class MeshOffloadClient : IMeshOffloadClient, IHostedService, IAsyncDisposable
{
    private readonly INetworkTransport _transport;
    private readonly IMeshCapabilityRegistry _registry;
    private readonly ILocalInferenceFallback _localFallback;
    private readonly MeshOffloadOptions _options;
    private readonly ILogger<MeshOffloadClient> _logger;

    private readonly ConcurrentDictionary<string, TaskCompletionSource<OffloadReplyEnvelope>> _pending
        = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _serveGate;

    private CancellationTokenSource? _pumpCts;
    private Task? _pumpTask;

    public MeshOffloadClient(
        INetworkTransport transport,
        IMeshCapabilityRegistry registry,
        ILocalInferenceFallback localFallback,
        IOptions<MeshOffloadOptions> options,
        ILogger<MeshOffloadClient>? logger = null)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _localFallback = localFallback ?? throw new ArgumentNullException(nameof(localFallback));
        _options = (options ?? throw new ArgumentNullException(nameof(options))).Value;
        _logger = logger ?? NullLogger<MeshOffloadClient>.Instance;
        _serveGate = new SemaphoreSlim(Math.Max(1, _options.MaxConcurrentServed));
    }

    /// <inheritdoc/>
    public bool IsReady => _transport.IsAvailable && _pumpTask is { IsCompleted: false };

    // ── Lifecycle (IHostedService) ─────────────────────────────────────────

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_pumpTask is not null) return; // already started

        if (_options.StartTransport && !_transport.IsAvailable)
        {
            try
            {
                await _transport.StartAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh offload: transport {Kind} failed to start; running degraded.", _transport.Kind);
            }
        }

        // The pump lives for the service lifetime, not just the start window, so
        // it gets its own CTS cancelled in StopAsync.
        _pumpCts = new CancellationTokenSource();
        CancellationToken pumpToken = _pumpCts.Token;
        _pumpTask = Task.Run(() => PumpAsync(pumpToken), CancellationToken.None);
        _logger.LogInformation("Mesh offload client started on transport {Kind} as node {Node}.", _transport.Kind, _options.LocalNodeId);
    }

    /// <inheritdoc/>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_pumpCts is not null)
        {
            await _pumpCts.CancelAsync().ConfigureAwait(false);
        }

        if (_pumpTask is not null)
        {
            try
            {
                await _pumpTask.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { /* expected on shutdown */ }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Mesh offload: receive pump ended with an exception.");
            }
            _pumpTask = null;
        }

        // Release anyone still awaiting a reply.
        foreach (var kv in _pending)
        {
            kv.Value.TrySetException(new OperationCanceledException("Mesh offload client stopped."));
        }
        _pending.Clear();
    }

    // ── Consumer side ─────────────────────────────────────────────────────

    /// <inheritdoc/>
    public async Task<OffloadResult> RequestAsync(
        string peerId, OffloadTurn turn, TimeSpan timeout, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(peerId);
        ArgumentNullException.ThrowIfNull(turn);

        if (!_transport.IsAvailable)
        {
            return OffloadResult.Fail($"Transport {_transport.Kind} is not available.", OffloadServedBy.None);
        }

        var tcs = new TaskCompletionSource<OffloadReplyEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(turn.CorrelationId, tcs))
        {
            return OffloadResult.Fail("Duplicate correlation id already in flight.", OffloadServedBy.None);
        }

        var sw = Stopwatch.StartNew();
        try
        {
            var env = new OffloadRequestEnvelope(
                turn.CorrelationId,
                _options.LocalNodeId,
                turn.ModelId,
                turn.Prompt,
                turn.MaxOutputTokens,
                turn.Temperature,
                turn.TopP,
                turn.StopSequences.ToArray(),
                turn.CreatedAtUtc);

            var payload = MeshOffloadWire.EncodeRequest(_options.LocalNodeId, peerId, env, timeout);
            await _transport.SendAsync(payload, ct).ConfigureAwait(false);

            OffloadReplyEnvelope reply = await tcs.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
            sw.Stop();

            string output = reply.OutputText ?? string.Empty;
            if (!reply.Success)
            {
                return new OffloadResult(
                    false, output, OffloadServedBy.RemotePeer, peerId, reply.OutputTokenCount,
                    sw.Elapsed.TotalMilliseconds, reply.FailureReason ?? "Remote peer reported failure.", reply.ReasoningText);
            }

            return new OffloadResult(
                true, output, OffloadServedBy.RemotePeer, peerId, reply.OutputTokenCount,
                sw.Elapsed.TotalMilliseconds, null, reply.ReasoningText);
        }
        catch (TimeoutException)
        {
            sw.Stop();
            return OffloadResult.Fail(
                $"Peer {peerId} did not reply within {timeout.TotalSeconds:0.#}s.", OffloadServedBy.None, sw.Elapsed.TotalMilliseconds);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            return OffloadResult.Fail(
                $"Offload to peer {peerId} failed: {ex.Message}", OffloadServedBy.None, sw.Elapsed.TotalMilliseconds);
        }
        finally
        {
            _pending.TryRemove(turn.CorrelationId, out _);
        }
    }

    // ── Receive pump + dispatch ───────────────────────────────────────────

    private async Task PumpAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var payload in _transport.ReceiveAsync(ct).ConfigureAwait(false))
                {
                    Dispatch(payload, ct);
                }

                // Stream ended without cancellation - transport closed it. Pause
                // briefly then re-subscribe (a transport reconnect is the
                // transport's / AetherNet's concern, not ours).
                if (!ct.IsCancellationRequested)
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh offload: receive pump error; retrying shortly.");
                try { await Task.Delay(TimeSpan.FromSeconds(1), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
            }
        }
    }

    private void Dispatch(NetworkPayload payload, CancellationToken ct)
    {
        switch (payload.ContentType)
        {
            case MeshOffloadWire.ReplyContentType:
                HandleReply(payload);
                break;

            case MeshOffloadWire.RequestContentType:
                // Serve on a background task: a slow inference must not stall the
                // pump, or we could not receive replies to our own outbound turns.
                _ = Task.Run(() => ServeAsync(payload, ct), CancellationToken.None);
                break;

            case MeshOffloadWire.AdvertContentType:
                _ = IngestAdvertAsync(payload);
                break;

            default:
                // Shared transport carrying other CircleAI traffic - not ours.
                break;
        }
    }

    private void HandleReply(NetworkPayload payload)
    {
        OffloadReplyEnvelope? reply;
        try { reply = MeshOffloadWire.DecodeReply(payload); }
        catch (Exception ex) { _logger.LogDebug(ex, "Mesh offload: dropped an undecodable reply."); return; }

        if (reply is null) return;
        if (_pending.TryRemove(reply.CorrelationId, out var tcs))
        {
            tcs.TrySetResult(reply);
        }
        // else: late or unknown correlation id - the requester already gave up.
    }

    // ── Provider side ─────────────────────────────────────────────────────

    private async Task ServeAsync(NetworkPayload payload, CancellationToken ct)
    {
        if (!_options.ServeInboundRequests) return;

        OffloadRequestEnvelope? req;
        try { req = MeshOffloadWire.DecodeRequest(payload); }
        catch (Exception ex) { _logger.LogDebug(ex, "Mesh offload: dropped an undecodable request."); return; }

        if (req is null || string.IsNullOrWhiteSpace(req.ReplyToNodeId)) return;

        OffloadReplyEnvelope reply;
        if (!await _serveGate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            reply = new OffloadReplyEnvelope(
                req.CorrelationId, false, string.Empty, 0,
                "Serving peer is at capacity.", null, DateTimeOffset.UtcNow);
        }
        else
        {
            try
            {
                var turn = new OffloadTurn(
                    req.ModelId, req.Prompt, req.MaxOutputTokens, req.Temperature, req.TopP,
                    req.StopSequences, req.CorrelationId, req.CreatedAtUtc);

                OffloadResult result = await _localFallback.CompleteAsync(turn, ct).ConfigureAwait(false);

                reply = new OffloadReplyEnvelope(
                    req.CorrelationId, result.Success, result.OutputText ?? string.Empty,
                    result.OutputTokenCount, result.FailureReason, result.ReasoningText, DateTimeOffset.UtcNow);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return; // shutting down - do not bother replying
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Mesh offload: serving request {Corr} threw.", req.CorrelationId);
                reply = new OffloadReplyEnvelope(
                    req.CorrelationId, false, string.Empty, 0,
                    "Serving peer raised an exception: " + ex.Message, null, DateTimeOffset.UtcNow);
            }
            finally
            {
                _serveGate.Release();
            }
        }

        try
        {
            var replyPayload = MeshOffloadWire.EncodeReply(_options.LocalNodeId, req.ReplyToNodeId, reply, _options.RequestTimeout);
            await _transport.SendAsync(replyPayload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Mesh offload: failed to send reply {Corr}.", req.CorrelationId);
        }
    }

    // ── Advertisement ingest ──────────────────────────────────────────────

    private async Task IngestAdvertAsync(NetworkPayload payload)
    {
        try
        {
            var env = MeshOffloadWire.DecodeAdvert(payload);
            if (env is null || string.IsNullOrWhiteSpace(env.PeerId)) return;

            // Ignore our own advertisement echoed back over a broadcast transport.
            if (string.Equals(env.PeerId, _options.LocalNodeId, StringComparison.Ordinal)) return;

            var ad = new MeshCapabilityAdvertisement(
                PeerId: env.PeerId,
                ModelId: env.ModelId,
                FreeKvTokens: env.FreeKvTokens,
                Tier: (DeviceTier)env.Tier,
                ContextWindowTokens: env.ContextWindowTokens,
                AdvertisedAtUtc: env.AdvertisedAtUtc,
                LatencyHintMs: env.LatencyHintMs);

            await _registry.UpsertAsync(ad).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Mesh offload: advert ingest failed.");
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        try { await StopAsync(CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { _logger.LogDebug(ex, "Mesh offload: dispose stop failed."); }
        _pumpCts?.Dispose();
        _serveGate.Dispose();
    }
}
