// ──────────────────────────────────────────────────────────────────────────
// AetherMeshTelemetryAdapter
//
// Implements CircleAI.Aether.IAetherTelemetry on top of AetherMesh's
// IAetherMeshTelemetry publisher. When a CircleAI consumer subscribes,
// we attach our own IAetherMeshTelemetryObserver to the AetherMesh bus
// and translate each event into the CircleAI shape before fanning out.
//
// Subscriptions are tracked independently — disposing the handle returned
// by Subscribe unhooks just that subscriber from the AetherMesh bus.
// ──────────────────────────────────────────────────────────────────────────

using AetherMesh.Extensibility;
using AetherMesh.Extensibility.Events;
using CircleAI.Aether;

namespace CircleAI.AetherMesh;

/// <summary>
/// Bridges AetherMesh's telemetry bus to CircleAI's <see cref="IAetherTelemetry"/>
/// contract. Each subscriber gets an independent AetherMesh subscription, so
/// disposal cleans up exactly one downstream handle.
/// </summary>
public sealed class AetherMeshTelemetryAdapter : IAetherTelemetry
{
    private readonly IAetherMeshTelemetry _meshTelemetry;

    public AetherMeshTelemetryAdapter(IAetherMeshTelemetry meshTelemetry)
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
    /// Receives AetherMesh events and forwards them to a CircleAI observer
    /// after type translation.
    /// </summary>
    private sealed class ObserverBridge : IAetherMeshTelemetryObserver
    {
        private readonly IAetherTelemetryObserver _target;

        public ObserverBridge(IAetherTelemetryObserver target) => _target = target;

        public void OnNodeEvent(AetherMeshNodeEvent e)
            => _target.OnNodeEvent(EventTranslator.Translate(e));

        public void OnTransportEvent(AetherMeshTransportEvent e)
            => _target.OnTransportEvent(EventTranslator.Translate(e));

        public void OnRouteEvent(AetherMeshRouteEvent e)
            => _target.OnRouteEvent(EventTranslator.Translate(e));

        public void OnSecurityEvent(AetherMeshSecurityEvent e)
            => _target.OnSecurityEvent(EventTranslator.Translate(e));

        public void OnNetworkEvent(AetherMeshNetworkEvent e)
            => _target.OnNetworkEvent(EventTranslator.Translate(e));
    }
}
