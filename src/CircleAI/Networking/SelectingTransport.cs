// SelectingTransport.cs
//
// Several transports as one, choosing per payload (OPEN-GAPS A10).
//
// THE MESH RODE ONE TRANSPORT. Everything above INetworkTransport takes exactly one - the
// AetherNet carrier's own registration removes any other - so with eleven transports written,
// a payload could only ever go the one way that was registered. This is one INetworkTransport
// over many: each send asks the selector for the cascade and goes the first way that is up and
// takes it, falling through when one fails; everything any of them receives comes out of one
// stream.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace CircleAI.Networking;

/// <summary>An <see cref="INetworkTransport"/> over several, routed by an <see cref="ITransportSelector"/>.</summary>
public sealed class SelectingTransport : INetworkTransport, IAsyncDisposable
{
    private readonly IReadOnlyList<INetworkTransport> _transports;
    private readonly ITransportSelector _selector;
    private readonly Func<NetworkContext>? _context;

    /// <param name="transports">The ways a payload can go; one of each kind is used.</param>
    /// <param name="selector">What orders them for a payload.</param>
    /// <param name="context">
    /// The moment's connectivity, when a monitor can say; null builds it from which of the
    /// transports report themselves available.
    /// </param>
    public SelectingTransport(
        IEnumerable<INetworkTransport> transports, ITransportSelector selector, Func<NetworkContext>? context = null)
    {
        ArgumentNullException.ThrowIfNull(transports);
        _selector = selector ?? throw new ArgumentNullException(nameof(selector));
        _transports = [.. transports.GroupBy(t => t.Kind).Select(g => g.First())];
        _context = context;
    }

    /// <summary>The kind a plain payload would go by now.</summary>
    public TransportKind Kind => _selector.SelectBest(NetworkPayload.Create(ReadOnlyMemory<byte>.Empty), Context());

    /// <inheritdoc />
    public bool IsAvailable => _transports.Any(t => t.IsAvailable);

    /// <summary>The way each payload last went, for the log and for tests.</summary>
    public TransportKind? LastUsed { get; private set; }

    /// <inheritdoc />
    /// <remarks>Each is started on its own: one that will not start must not keep the rest down.</remarks>
    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var t in _transports)
        {
            try { await t.StartAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var t in _transports)
        {
            try { await t.StopAsync(ct).ConfigureAwait(false); }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }
    }

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">No transport in the cascade could take it; each one's reason is in the message.</exception>
    public async Task SendAsync(NetworkPayload payload, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(payload);
        var failures = new List<string>();

        foreach (var kind in _selector.GetCascade(payload, Context()))
        {
            var transport = _transports.FirstOrDefault(t => t.Kind == kind);
            if (transport is null || !transport.IsAvailable) continue;
            try
            {
                await transport.SendAsync(payload, ct).ConfigureAwait(false);
                LastUsed = kind;
                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { failures.Add($"{kind}: {ex.Message}"); }
        }

        throw new InvalidOperationException(failures.Count == 0
            ? "No transport that the moment allows is up."
            : "Every transport that was up failed: " + string.Join("; ", failures));
    }

    /// <inheritdoc />
    /// <remarks>Every transport's payloads, in the order they arrive, from one stream.</remarks>
    public async IAsyncEnumerable<NetworkPayload> ReceiveAsync([EnumeratorCancellation] CancellationToken ct = default)
    {
        var merged = Channel.CreateUnbounded<NetworkPayload>();
        var pumps = _transports.Select(t => Task.Run(async () =>
        {
            try
            {
                await foreach (var p in t.ReceiveAsync(ct).ConfigureAwait(false))
                    await merged.Writer.WriteAsync(p, ct).ConfigureAwait(false);
            }
            catch (Exception) when (!ct.IsCancellationRequested) { }
        }, ct)).ToArray();

        _ = Task.WhenAll(pumps).ContinueWith(_ => merged.Writer.TryComplete(), TaskScheduler.Default);

        await foreach (var payload in merged.Reader.ReadAllAsync(ct).ConfigureAwait(false))
            yield return payload;
    }

    private NetworkContext Context() => _context?.Invoke() ?? new NetworkContext(
        _transports.Any(t => t.IsAvailable) ? ConnectivityState.Online : ConnectivityState.Offline,
        _transports.FirstOrDefault(t => t.IsAvailable)?.Kind ?? TransportKind.LocalStore,
        [.. _transports.Where(t => t.IsAvailable).Select(t => t.Kind)],
        null, null, null, 0, DateTimeOffset.UtcNow);

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        foreach (var t in _transports)
            if (t is IAsyncDisposable d) await d.DisposeAsync().ConfigureAwait(false);
    }
}
