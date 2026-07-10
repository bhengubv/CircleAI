# transport_selector.py
#
# DefaultTransportSelector — a working, deterministic ITransportSelector that
# implements the cascade documented on the C# ``ITransportSelector`` interface:
#
#   gRPC -> WebSocket -> HTTP -> MQTT -> TCP ->
#   WiFi -> Bluetooth -> NearLink -> Aether -> DTN -> LocalStore
#
# The C# source ships only the interface + the documented cascade order (no
# concrete selector), so this is the faithful realisation of that contract. It
# folds in the INetworkPolicy gates (force / mesh-first / allow-cloud / permits)
# and the NetworkContext availability set.

from __future__ import annotations

from typing import List

from .interfaces import ITransportSelector
from .network_policy import DefaultNetworkPolicy, INetworkPolicy
from .network_types import (
    ConnectivityState,
    NetworkContext,
    NetworkPayload,
    TransportKind,
)

# The canonical cascade, best-first, exactly as documented on ITransportSelector.
_CASCADE: tuple[TransportKind, ...] = (
    TransportKind.GRPC,
    TransportKind.WEB_SOCKET,
    TransportKind.HTTP,
    TransportKind.MQTT,
    TransportKind.TCP,
    TransportKind.WIFI,
    TransportKind.BLUETOOTH,
    TransportKind.NEAR_LINK,
    TransportKind.AETHER,
    TransportKind.DTN,
    TransportKind.LOCAL_STORE,
)

# Cloud transports — gated by policy.allow_cloud_transports. Matches the set the
# NetworkPolicyBuilder no-cloud guard blocks.
_CLOUD: frozenset[TransportKind] = frozenset(
    {
        TransportKind.HTTP,
        TransportKind.WEB_SOCKET,
        TransportKind.GRPC,
        TransportKind.MQTT,
    }
)

# Mesh/local transports — floated to the front when policy.mesh_first is set.
# Order within the group is preserved from the canonical cascade.
_MESH: tuple[TransportKind, ...] = (
    TransportKind.WIFI,
    TransportKind.BLUETOOTH,
    TransportKind.NEAR_LINK,
    TransportKind.AETHER,
    TransportKind.DTN,
    TransportKind.LOCAL_STORE,
)


class DefaultTransportSelector(ITransportSelector):
    """Deterministic realisation of the documented transport cascade.

    Selection order:
      1. If the policy forces a transport and it survives the gates, that one
         transport is the whole cascade.
      2. Otherwise the canonical cascade is taken (mesh-first reorders it),
         filtered by ``policy.permits`` / ``allow_cloud_transports`` and by the
         context's available-transport set. ``LocalStore`` is always retained
         as the terminal offline fallback when the policy enables the queue.
    """

    def __init__(self, policy: INetworkPolicy | None = None) -> None:
        self._policy: INetworkPolicy = policy or DefaultNetworkPolicy.INSTANCE

    # ── public API ──────────────────────────────────────────────────────────

    def get_cascade(
        self, payload: NetworkPayload, context: NetworkContext
    ) -> List[TransportKind]:
        policy = self._policy

        forced = policy.force_transport
        if forced is not None:
            # Forced transport still has to satisfy Permits; if it does it is the
            # entire cascade, else the cascade is empty (nothing else is allowed
            # to override an explicit force).
            return [forced] if policy.permits(forced, payload) else []

        order = self._ordered(policy.mesh_first)
        available = set(context.available_transports)
        queue_on = policy.offline_queue_enabled

        result: List[TransportKind] = []
        for kind in order:
            if not policy.permits(kind, payload):
                continue
            if kind in _CLOUD and not policy.allow_cloud_transports:
                continue
            if kind == TransportKind.LOCAL_STORE:
                # The offline queue is a device-local capability: it is
                # available whenever the policy enables it, regardless of the
                # context's live-transport set.
                if queue_on:
                    result.append(kind)
                continue
            if kind in available:
                result.append(kind)

        return result

    def select_best(
        self, payload: NetworkPayload, context: NetworkContext
    ) -> TransportKind:
        cascade = self.get_cascade(payload, context)
        if cascade:
            return cascade[0]
        # Nothing live and nothing forced survived. Fall back to the offline
        # queue if the policy allows it (deterministic last resort), otherwise
        # surface the impossibility rather than guessing.
        if self._policy.offline_queue_enabled and (
            self._policy.force_transport is None
        ):
            return TransportKind.LOCAL_STORE
        raise RuntimeError(
            "no permitted transport for payload "
            f"{payload.id!r} in state {context.state!r}"
        )

    # ── internals ───────────────────────────────────────────────────────────

    @staticmethod
    def _ordered(mesh_first: bool) -> tuple[TransportKind, ...]:
        if not mesh_first:
            return _CASCADE
        mesh_set = set(_MESH)
        rest = tuple(k for k in _CASCADE if k not in mesh_set)
        return _MESH + rest
