"""test_http_transport.py

Verifies the HTTP transport module: HttpStatusFamily predicates, the
endpoint/request-summary/cache-key records, InMemoryHttpRequestMetrics, the
IHttpMessageSender seam + InMemoryHttpMessageSender, and HttpNetworkTransport
(POST URL shaping, headers, the 3-attempt exponential-backoff retry loop, and
the empty receive stream).

Mirrors CircleAI.Networking.Http HttpTransportCommons.cs /
HttpNetworkTransport.cs (C# — the spec).
"""
from __future__ import annotations

import dataclasses
from datetime import datetime, timedelta, timezone

import pytest

from circle_ai.networking import (
    HttpCacheKey,
    HttpEndpointDescriptor,
    HttpNetworkTransport,
    HttpRequestException,
    HttpRequestFailedError,
    HttpRequestSummary,
    HttpStatusFamily,
    HttpTransientError,
    InMemoryHttpMessageSender,
    InMemoryHttpRequestMetrics,
    MessagePriority,
    NetworkPayload,
    TransportKind,
)


def _now() -> datetime:
    return datetime.now(timezone.utc)


def _transport(sender: InMemoryHttpMessageSender, base="https://api.test/v1"):
    # backoff_base tiny so the retry path never waits real seconds.
    return HttpNetworkTransport(sender, base, backoff_base_seconds=0.0)


# ── HttpStatusFamily ─────────────────────────────────────────────────────────


def test_status_family_predicates() -> None:
    assert HttpStatusFamily.is_2xx(200) and HttpStatusFamily.is_2xx(299)
    assert not HttpStatusFamily.is_2xx(300)
    assert HttpStatusFamily.is_3xx(301)
    assert HttpStatusFamily.is_4xx(404)
    assert HttpStatusFamily.is_5xx(503)


def test_status_family_should_retry() -> None:
    for retryable in (408, 425, 429, 500, 502, 503, 599):
        assert HttpStatusFamily.should_retry(retryable) is True
    for terminal in (200, 301, 400, 401, 404):
        assert HttpStatusFamily.should_retry(terminal) is False


# ── records ──────────────────────────────────────────────────────────────────


def test_endpoint_descriptor_allows_none_headers_and_is_frozen() -> None:
    e = HttpEndpointDescriptor("POST", "https://api.test", "/messages", None)
    assert e.method == "POST"
    assert e.default_headers is None
    with pytest.raises(dataclasses.FrozenInstanceError):
        e.path = "/x"  # type: ignore[misc]


def test_cache_key_record() -> None:
    k = HttpCacheKey("GET", "https://api.test/x", "application/json")
    assert k.method == "GET"
    assert k.full_uri == "https://api.test/x"
    assert k.accept_header == "application/json"


# ── InMemoryHttpRequestMetrics ───────────────────────────────────────────────


def test_metrics_register_and_get() -> None:
    m = InMemoryHttpRequestMetrics()
    d = HttpEndpointDescriptor("GET", "https://api.test", "/x", None)
    m.register("e1", d)
    assert m.get_endpoint("e1") is d
    assert m.get_endpoint("missing") is None


def test_metrics_recent_requests_newest_first_and_limited() -> None:
    m = InMemoryHttpRequestMetrics()
    base = _now()
    for i in range(4):
        m.log(
            HttpRequestSummary("e1", 200, 0.01, 100, base + timedelta(seconds=i))
        )
    recent = m.recent_requests(limit=2)
    assert len(recent) == 2
    assert recent[0].at_utc > recent[1].at_utc


def test_metrics_avg_2xx_latency_ms_empty_is_zero() -> None:
    m = InMemoryHttpRequestMetrics()
    assert m.avg_2xx_latency_ms("e1") == 0.0


def test_metrics_avg_2xx_latency_ms_only_counts_2xx_and_reports_ms() -> None:
    m = InMemoryHttpRequestMetrics()
    # latencies stored in seconds; only 2xx counted; report in ms
    m.log(HttpRequestSummary("e1", 200, 0.10, 10, _now()))  # 100 ms
    m.log(HttpRequestSummary("e1", 200, 0.20, 10, _now()))  # 200 ms
    m.log(HttpRequestSummary("e1", 500, 9.99, 10, _now()))  # excluded (5xx)
    assert m.avg_2xx_latency_ms("e1") == pytest.approx(150.0)  # mean(100,200)


# ── HttpNetworkTransport ─────────────────────────────────────────────────────


def test_transport_kind_and_always_available() -> None:
    t = _transport(InMemoryHttpMessageSender())
    assert t.kind is TransportKind.HTTP
    assert t.is_available is True


def test_transport_rejects_none_sender_and_blank_base() -> None:
    with pytest.raises(ValueError):
        HttpNetworkTransport(None, "https://x")  # type: ignore[arg-type]
    with pytest.raises(ValueError):
        HttpNetworkTransport(InMemoryHttpMessageSender(), "   ")


async def test_send_posts_to_directed_url_with_headers() -> None:
    sender = InMemoryHttpMessageSender(default_status=200)
    t = _transport(sender, base="https://api.test/v1/")  # trailing slash trimmed
    p = NetworkPayload.create(
        b"body", destination_id="node 2", priority=MessagePriority.HIGH,
        content_type="application/json",
    )
    await t.send_async(p)
    assert sender.post_count == 1
    url, body, headers = sender.posts[0]
    # destination URL-escaped; base trailing slash removed
    assert url == "https://api.test/v1/messages/node%202"
    assert body == b"body"
    assert headers["Content-Type"] == "application/json"
    assert headers["X-Payload-Id"] == p.id
    assert headers["X-Payload-Priority"] == "High"


async def test_send_broadcast_url_when_no_destination() -> None:
    sender = InMemoryHttpMessageSender()
    t = _transport(sender)
    await t.send_async(NetworkPayload.create(b"x"))
    url, _, _ = sender.posts[0]
    assert url.endswith("/messages")
    assert "/messages/" not in url


async def test_send_retries_transient_then_succeeds() -> None:
    sender = InMemoryHttpMessageSender()
    t = _transport(sender)
    url = "https://api.test/v1/messages/dest"
    # First two attempts transient, third succeeds.
    sender.script(url, sender.TRANSIENT, sender.TRANSIENT, 200)
    await t.send_async(NetworkPayload.create(b"x", destination_id="dest"))
    assert sender.post_count == 3  # exactly 3 attempts


async def test_send_gives_up_after_three_transient_attempts() -> None:
    sender = InMemoryHttpMessageSender()
    t = _transport(sender)
    url = "https://api.test/v1/messages/dest"
    sender.script(url, sender.TRANSIENT, sender.TRANSIENT, sender.TRANSIENT)
    with pytest.raises(HttpTransientError):
        await t.send_async(NetworkPayload.create(b"x", destination_id="dest"))
    # C#: loop runs attempts 0,1,2 -> 3 POSTs; the 3rd re-raises (no 4th).
    assert sender.post_count == 3


async def test_send_non_2xx_is_retried_then_raises() -> None:
    # In C# EnsureSuccessStatusCode() throws HttpRequestException, which the same
    # `catch (HttpRequestException) when (attempt < 2)` retries — so a persistent
    # non-2xx status is retried across all 3 attempts, then re-raised.
    sender = InMemoryHttpMessageSender(default_status=500)
    t = _transport(sender)
    with pytest.raises(HttpRequestException) as ei:
        await t.send_async(NetworkPayload.create(b"x", destination_id="dest"))
    assert ei.value.status_code == 500
    assert sender.post_count == 3


async def test_send_non_2xx_recovers_on_retry() -> None:
    # A transient 503 that becomes 200 on the 2nd attempt succeeds (both are
    # HttpRequestException-driven, exactly as C#).
    sender = InMemoryHttpMessageSender()
    t = _transport(sender)
    url = "https://api.test/v1/messages/dest"
    sender.script(url, 503, 200)
    await t.send_async(NetworkPayload.create(b"x", destination_id="dest"))
    assert sender.post_count == 2


async def test_receive_yields_nothing() -> None:
    t = _transport(InMemoryHttpMessageSender())
    collected = [item async for item in t.receive_async()]
    assert collected == []
