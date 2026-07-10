# mesh_directive_store.py
#
# Port of CircleAI.Security.AetherNet.MeshDirectiveStore (C# — the EXACT spec).
#
# In-memory record of every active SecurityDirective the mesh has issued against
# a node. Implements CircleAI.Aether.ISecurityDirectiveConsumer so it can be
# plugged in as the sink for directive notifications.
#
# Two query surfaces are exposed:
#   * is_blocked(node_id)          — fast hot-path check (returns (blocked, reason))
#   * get_active_directives(node_id) — full audit detail
#
# Expiry is handled lazily on read — no background timer to leak. Block state
# observes Avoid + Quarantine; Release lifts both.

from __future__ import annotations

import threading
from datetime import datetime, timezone
from typing import Callable, Dict, List, Optional, Tuple

from ..aether.security_layer import (
    ISecurityDirectiveConsumer,
    SecurityDirective,
    SecurityDirectiveKind,
)


def _utc_now() -> datetime:
    return datetime.now(timezone.utc)


class MeshDirectiveStore(ISecurityDirectiveConsumer):
    """Thread-safe in-memory registry of security directives received from the
    mesh. Acts as both the directive sink and the query surface that other
    CircleAI components consult before serving a request.

    :param clock: Optional clock override for testing. Defaults to UTC now.
    """

    def __init__(self, clock: Optional[Callable[[], datetime]] = None) -> None:
        if clock is None:
            clock = _utc_now
        self._clock = clock
        self._by_node: Dict[str, List[SecurityDirective]] = {}
        # Guards the dict structure. Per-list mutation also happens under it —
        # single lock is sufficient and avoids the C# per-list lock nuance.
        self._lock = threading.Lock()

    def on_directive(self, directive: SecurityDirective) -> None:
        if directive is None:
            raise ValueError("directive must not be None")
        if not directive.has_target:
            return
        node_id = directive.target_node_id
        assert node_id is not None  # has_target guarantees a non-blank id

        if directive.kind == SecurityDirectiveKind.RELEASE_NODE:
            # Release lifts every Avoid/Quarantine for the node.
            with self._lock:
                self._by_node.pop(node_id, None)
            return

        with self._lock:
            self._by_node.setdefault(node_id, []).append(directive)

    def is_blocked(self, node_id: str) -> Tuple[bool, str]:
        """Returns ``(blocked, reason)``. ``blocked`` is True when an unexpired
        Avoid or Quarantine directive is active for the node; ``reason`` carries
        the most recent block's reason text (empty when not blocked).
        """
        if not node_id or not node_id.strip():
            return (False, "")

        now = self._clock()
        latest_block: Optional[SecurityDirective] = None

        with self._lock:
            lst = self._by_node.get(node_id)
            if lst is None:
                return (False, "")
            # Drop expired entries while we walk the list.
            for i in range(len(lst) - 1, -1, -1):
                d = lst[i]
                if _is_expired(d, now):
                    del lst[i]
                    continue
                if _is_block_kind(d.kind) and (
                    latest_block is None or d.issued_at > latest_block.issued_at
                ):
                    latest_block = d
            if len(lst) == 0:
                self._by_node.pop(node_id, None)

        if latest_block is None:
            return (False, "")
        return (True, latest_block.reason)

    def get_active_directives(self, node_id: str) -> List[SecurityDirective]:
        """Lists every unexpired directive for the node — useful for
        audit/diagnostics.
        """
        if not node_id or not node_id.strip():
            return []
        now = self._clock()
        with self._lock:
            lst = self._by_node.get(node_id)
            if lst is None:
                return []
            return [d for d in lst if not _is_expired(d, now)]

    @property
    def tracked_node_count(self) -> int:
        """Number of nodes with at least one tracked directive (post-expiry
        sweep on read of individual nodes; this is the raw structural count).
        """
        with self._lock:
            return len(self._by_node)


def _is_block_kind(k: SecurityDirectiveKind) -> bool:
    return k in (SecurityDirectiveKind.AVOID_NODE, SecurityDirectiveKind.QUARANTINE_NODE)


def _is_expired(d: SecurityDirective, now: datetime) -> bool:
    return d.duration is not None and (d.issued_at + d.duration) <= now
