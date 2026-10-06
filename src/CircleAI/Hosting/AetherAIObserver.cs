// AetherAIObserver.cs
//
// (3.3.0) IAIObserver bridge that publishes butler events onto the
// CircleAether mesh transport. The transport contract lives in this
// assembly — host packages (CircleAI.AetherNet, CircleAI.Networking.*)
// implement it directly. There is no separate Aether SDK to bind to.

using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting;

/// <summary>
/// (3.3.0) Publish/subscribe transport contract for the CircleAether
/// mesh. Host packages register an implementation (e.g. AetherNet,
/// Bluetooth, NearLink, gRPC) and this is the contract the observer
/// publishes butler events through.
/// </summary>
public interface ICircleAetherTransport
{
    /// <summary>Publish a payload to the given topic.</summary>
    Task PublishAsync(string topic, ReadOnlyMemory<byte> payload, CancellationToken ct = default);
}

/// <summary>
/// <see cref="IAIObserver"/> implementation that forwards butler events
/// to a CircleAether mesh transport.
/// </summary>
public sealed class AetherAIObserver : IAIObserver
{
    private readonly ICircleAetherTransport _transport;

    /// <param name="transport">The Aether transport to publish to.</param>
    public AetherAIObserver(ICircleAetherTransport transport)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
    }

    /// <inheritdoc />
    public ValueTask OnStartedAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnStoppedAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnChatCompletedAsync(AIChatEvent @event, CancellationToken ct = default)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { response = @event.Response });
        // Fire-and-forget — keep the ValueTask non-blocking.
        _ = _transport.PublishAsync("butler/response",
            new ReadOnlyMemory<byte>(payload),
            CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask OnStreamStartedAsync(AIStreamEvent @event, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnStreamCompletedAsync(AIStreamEvent @event, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <inheritdoc />
    public ValueTask OnToolInvokedAsync(AIToolEvent @event, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Publishes an error payload to the <c>butler/error</c> topic.
    /// Call this from error-handling code that cannot surface through
    /// the standard <see cref="IAIObserver"/> lifecycle.
    /// </summary>
    public void OnError(Exception ex)
    {
        ArgumentNullException.ThrowIfNull(ex);
        var payload = JsonSerializer.SerializeToUtf8Bytes(new
        {
            error = ex.GetType().Name,
            message = ex.Message
        });
        _ = _transport.PublishAsync("butler/error",
            new ReadOnlyMemory<byte>(payload),
            CancellationToken.None);
    }
}
