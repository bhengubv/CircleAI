# network_policy.py
#
# INetworkPolicy contract + the permissive DefaultNetworkPolicy + the fluent
# NetworkPolicyBuilder.
#
# Ported faithfully from CircleAI.Networking (C# — the spec):
#   INetworkPolicy.cs       -> INetworkPolicy
#   DefaultNetworkPolicy.cs -> DefaultNetworkPolicy (singleton)
#   NetworkPolicyBuilder.cs -> NetworkPolicyBuilder (+ private _Policy)

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Optional, Set

from .network_types import NetworkPayload, TransportKind

# Cloud transports blocked when NoCloud is set — matches the C# `t is Http or
# WebSocket or Grpc or Mqtt` guard in NetworkPolicyBuilder.Policy.Permits.
_CLOUD_TRANSPORTS = frozenset(
    {
        TransportKind.HTTP,
        TransportKind.WEB_SOCKET,
        TransportKind.GRPC,
        TransportKind.MQTT,
    }
)


class INetworkPolicy(ABC):
    """Policy rules applied before choosing a transport.

    Examples: "WiFi-only", "mesh-first", "no cloud when roaming".
    Faithful port of the C# ``INetworkPolicy`` interface.
    """

    @abstractmethod
    def permits(self, transport: TransportKind, payload: NetworkPayload) -> bool:
        """True if ``transport`` may carry ``payload`` under this policy."""
        ...

    @property
    @abstractmethod
    def force_transport(self) -> Optional[TransportKind]:
        """If set, the selector must use exactly this transport (or fail)."""
        ...

    @property
    @abstractmethod
    def mesh_first(self) -> bool:
        """Prefer mesh/local transports ahead of cloud ones."""
        ...

    @property
    @abstractmethod
    def offline_queue_enabled(self) -> bool:
        """Whether an offline LocalStore queue is available as a last resort."""
        ...

    @property
    @abstractmethod
    def allow_cloud_transports(self) -> bool:
        """Whether cloud transports (HTTP/WebSocket/gRPC/MQTT) are permitted."""
        ...


class DefaultNetworkPolicy(INetworkPolicy):
    """Permissive default: all transports allowed, offline queue on.

    Faithful port of the C# ``DefaultNetworkPolicy`` sealed singleton. Use
    :data:`DefaultNetworkPolicy.INSTANCE` (mirrors the C# ``Instance`` field).
    """

    INSTANCE: "DefaultNetworkPolicy"

    def permits(self, transport: TransportKind, payload: NetworkPayload) -> bool:
        return True

    @property
    def force_transport(self) -> Optional[TransportKind]:
        return None

    @property
    def mesh_first(self) -> bool:
        return False

    @property
    def offline_queue_enabled(self) -> bool:
        return True

    @property
    def allow_cloud_transports(self) -> bool:
        return True


DefaultNetworkPolicy.INSTANCE = DefaultNetworkPolicy()


class _Policy(INetworkPolicy):
    """Concrete policy produced by :class:`NetworkPolicyBuilder`.

    Faithful port of the private ``NetworkPolicyBuilder.Policy`` C# class.
    ``allowed`` is ``None`` when no allow-list was configured (everything
    permitted, subject to the no-cloud guard).
    """

    def __init__(
        self,
        allowed: Optional[Set[TransportKind]],
        mesh_first: bool,
        no_cloud: bool,
        queue_enabled: bool,
        force: Optional[TransportKind],
    ) -> None:
        self._allowed = allowed
        self._mesh_first = mesh_first
        self._no_cloud = no_cloud
        self._queue_enabled = queue_enabled
        self._force = force

    def permits(self, transport: TransportKind, payload: NetworkPayload) -> bool:
        if self._no_cloud and transport in _CLOUD_TRANSPORTS:
            return False
        return self._allowed is None or transport in self._allowed

    @property
    def force_transport(self) -> Optional[TransportKind]:
        return self._force

    @property
    def mesh_first(self) -> bool:
        return self._mesh_first

    @property
    def offline_queue_enabled(self) -> bool:
        return self._queue_enabled

    @property
    def allow_cloud_transports(self) -> bool:
        return not self._no_cloud


class NetworkPolicyBuilder:
    """Fluent builder for :class:`INetworkPolicy`.

    Faithful port of the C# ``NetworkPolicyBuilder``. Every mutator returns
    ``self`` for chaining; :meth:`build` freezes the configuration into an
    immutable :class:`_Policy`.
    """

    def __init__(self) -> None:
        self._allowed: Set[TransportKind] = set()
        self._mesh_first: bool = False
        self._no_cloud: bool = False
        self._queue_enabled: bool = True
        self._force: Optional[TransportKind] = None

    def mesh_first(self) -> "NetworkPolicyBuilder":
        self._mesh_first = True
        return self

    def no_cloud(self) -> "NetworkPolicyBuilder":
        self._no_cloud = True
        return self

    def disable_queue(self) -> "NetworkPolicyBuilder":
        self._queue_enabled = False
        return self

    def force(self, t: TransportKind) -> "NetworkPolicyBuilder":
        self._force = t
        return self

    def allow(self, *kinds: TransportKind) -> "NetworkPolicyBuilder":
        for k in kinds:
            self._allowed.add(k)
        return self

    def build(self) -> INetworkPolicy:
        # Match C#: pass a *copy* of the allow-set when non-empty, else None.
        allowed = set(self._allowed) if len(self._allowed) > 0 else None
        return _Policy(
            allowed,
            self._mesh_first,
            self._no_cloud,
            self._queue_enabled,
            self._force,
        )
