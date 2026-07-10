"""test_directive_publisher.py — fan-out publisher for PeerDirectives.

Covers multi-subscriber fan-out, snapshot-under-lock delivery (a consumer that
subscribes from within on_directive does not deadlock), unsubscribe via the
disposable handle, idempotent disposal, and subscriber counting.
"""
from __future__ import annotations

from datetime import datetime, timezone

import pytest

from circle_ai.security import (
    DirectivePublisher,
    IPeerDirectiveConsumer,
    PeerDirective,
    PeerDirectiveKind,
    PeerThreatLevel,
)


def _directive(kind=PeerDirectiveKind.AVOID_NODE, node="n1"):
    return PeerDirective(
        kind=kind,
        target_node_id=node,
        trust_score=0.4,
        threat_level=PeerThreatLevel.HIGH,
        reason="test",
        duration=None,
        issued_at=datetime.now(timezone.utc),
    )


class _Collector(IPeerDirectiveConsumer):
    def __init__(self):
        self.received = []

    def on_directive(self, directive):
        self.received.append(directive)


def test_publish_reaches_all_subscribers():
    pub = DirectivePublisher()
    a, b = _Collector(), _Collector()
    pub.subscribe(a)
    pub.subscribe(b)
    d = _directive()
    pub.publish(d)
    assert a.received == [d]
    assert b.received == [d]


def test_subscriber_count():
    pub = DirectivePublisher()
    assert pub.subscriber_count == 0
    h1 = pub.subscribe(_Collector())
    h2 = pub.subscribe(_Collector())
    assert pub.subscriber_count == 2
    h1.dispose()
    assert pub.subscriber_count == 1
    h2.dispose()
    assert pub.subscriber_count == 0


def test_dispose_unsubscribes():
    pub = DirectivePublisher()
    c = _Collector()
    handle = pub.subscribe(c)
    handle.dispose()
    pub.publish(_directive())
    assert c.received == []


def test_dispose_is_idempotent():
    pub = DirectivePublisher()
    c = _Collector()
    handle = pub.subscribe(c)
    handle.dispose()
    handle.dispose()  # must not raise or double-remove
    assert pub.subscriber_count == 0


def test_handle_is_context_manager():
    pub = DirectivePublisher()
    c = _Collector()
    with pub.subscribe(c):
        assert pub.subscriber_count == 1
    assert pub.subscriber_count == 0


def test_none_consumer_rejected():
    pub = DirectivePublisher()
    with pytest.raises(ValueError):
        pub.subscribe(None)  # type: ignore[arg-type]


def test_reentrant_subscribe_during_publish_does_not_deadlock():
    # Callbacks fire OUTSIDE the lock, so a consumer may subscribe a new
    # consumer while handling a directive without self-deadlocking.
    pub = DirectivePublisher()
    late = _Collector()

    class _Reentrant(IPeerDirectiveConsumer):
        def __init__(self):
            self.count = 0

        def on_directive(self, directive):
            self.count += 1
            if self.count == 1:
                pub.subscribe(late)

    r = _Reentrant()
    pub.subscribe(r)
    pub.publish(_directive())
    # The late subscriber joined during the first publish; the second reaches it.
    assert late.received == []
    pub.publish(_directive())
    assert len(late.received) == 1
