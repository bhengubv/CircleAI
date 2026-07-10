# directive_publisher.py
#
# Port of CircleAI.Security.DirectivePublisher (C# — the EXACT spec).
#
# Fan-out publisher for PeerDirectives.
#
# Keeps a list of IPeerDirectiveConsumer subscriptions and fans every published
# directive out to all current subscribers. Concurrent subscribe, unsubscribe,
# and publish operations are all thread-safe.
#
# Transport-agnostic: no dependency on any transport package.

from __future__ import annotations

import threading
from typing import List

from .peer_security_types import IDisposable, IPeerDirectiveConsumer, PeerDirective


class DirectivePublisher:
    """Manages :class:`IPeerDirectiveConsumer` subscriptions and fans published
    :class:`PeerDirective` instances out to all subscribers.
    """

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._consumers: List[IPeerDirectiveConsumer] = []

    # ── Public API ───────────────────────────────────────────────────────────

    def subscribe(self, consumer: IPeerDirectiveConsumer) -> IDisposable:
        """Subscribe ``consumer`` to receive directives.

        Dispose the returned handle to unsubscribe. Idempotent disposal.
        """
        if consumer is None:
            raise ValueError("consumer must not be None")
        with self._lock:
            self._consumers.append(consumer)
        return _SubscriptionHandle(self, consumer)

    def publish(self, directive: PeerDirective) -> None:
        """Publish ``directive`` to all current subscribers.

        A snapshot is taken under the lock; callbacks fire OUTSIDE it so a
        consumer that re-enters the publisher (subscribe / unsubscribe) cannot
        self-deadlock.
        """
        with self._lock:
            snapshot = list(self._consumers)

        for c in snapshot:
            c.on_directive(directive)

    @property
    def subscriber_count(self) -> int:
        """Number of currently active subscribers. Useful in tests."""
        with self._lock:
            return len(self._consumers)

    # ── Private ──────────────────────────────────────────────────────────────

    def _unsubscribe(self, consumer: IPeerDirectiveConsumer) -> None:
        with self._lock:
            try:
                self._consumers.remove(consumer)
            except ValueError:
                pass


class _SubscriptionHandle(IDisposable):
    def __init__(
        self, publisher: DirectivePublisher, consumer: IPeerDirectiveConsumer
    ) -> None:
        self._publisher = publisher
        self._consumer = consumer
        self._disposed = False
        self._lock = threading.Lock()

    def dispose(self) -> None:
        # Interlocked-exchange equivalent: flip the flag once, atomically.
        with self._lock:
            if self._disposed:
                return
            self._disposed = True
        self._publisher._unsubscribe(self._consumer)
