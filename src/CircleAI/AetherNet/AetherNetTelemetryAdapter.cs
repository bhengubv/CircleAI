// ──────────────────────────────────────────────────────────────────────────
// AetherNetTelemetryAdapter
//
// Implements CircleAI.Aether.IAetherTelemetry on top of AetherNet's
// IAetherNetTelemetry publisher. When a CircleAI consumer subscribes,
// we attach our own IAetherNetTelemetryObserver to the AetherNet bus
// and translate each event into the CircleAI shape before fanning out.
//
// Subscriptions are tracked independently — disposing the handle returned
// by Subscribe unhooks just that subscriber from the AetherNet bus.
// ──────────────────────────────────────────────────────────────────────────

using AetherNet.Extensibility;
using AetherNet.Extensibility.Events;
using CircleAI.Aether;

namespace CircleAI.AetherNet;

/// <summary>
/// Bridges AetherNet's telemetry bus to CircleAI's <see cref="IAetherTelemetry"/>
/// contract. Each subscriber gets an independent AetherNet subscription, so
/// disposal cleans up exactly one downstream handle.
/// </summary>
public sealed class AetherNetTelemetryAdapter : IAetherTelemetry
{
    private readonly IAetherNetTelemetry _meshTelemetry;

    public AetherNetTelemetryAdapter(IAetherNetTelemetry meshTelemetry)
    {
        ArgumentNullException.ThrowIfNull(meshTelemetry);
        _meshTelemetry = meshTelemetry;
    }

    /// <inheritdoc/>
    public IDisposable Subscribe(IAetherTelemetryObserver observer)
    {
        ArgumentNullException.ThrowIfNull(observer);
        var bridge = new ObserverBridge(observer);
        return _meshTelemetry.Subscribe(bridge);
    }

    /// <summary>
    /// Receives AetherNet events and forwards them to a CircleAI observer
    /// after type translation.
    /// </summary>
    private sealed class ObserverBridge : IAetherNetTelemetryObserver
    {
        private readonly IAetherTelemetryObserver _target;

        public ObserverBridge(IAetherTelemetryObserver target) => _target = target;

        public void OnNodeEvent(AetherNetNodeEvent e)
            => _target.OnNodeEvent(EventTranslator.Translate(e));

        public void OnTransportEvent(AetherNetTransportEvent e)
            => _target.OnTransportEvent(EventTranslator.Translate(e));

        public void OnRouteEvent(AetherNetRouteEvent e)
            => _target.OnRouteEvent(EventTranslator.Translate(e));

        public void OnSecurityEvent(AetherNetSecurityEvent e)
            => _target.OnSecurityEvent(EventTranslator.Translate(e));

        public void OnNetworkEvent(AetherNetNetworkEvent e)
            => _target.OnNetworkEvent(EventTranslator.Translate(e));
    }
}
