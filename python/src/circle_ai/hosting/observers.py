"""PushAIObserver + AetherAIObserver — ports of the CircleAI.Hosting observer
bridges, plus their injected transport contracts.

  * ``IPushNotificationSender`` — platform-agnostic push sender (APN/FCM).
  * ``PushAIObserver`` — delivers butler responses as push notifications.
  * ``ICircleAetherTransport`` — CircleAether mesh pub/sub transport contract.
  * ``AetherAIObserver`` — forwards butler events onto the mesh transport.

Both observers extend :class:`IAIObserver` (all events default no-op) and only
override the chat-completed hook, matching the C#.
"""
from __future__ import annotations

import json as _json
from abc import ABC, abstractmethod

from .ai_observer import AIChatEvent, IAIObserver

__all__ = [
    "IPushNotificationSender",
    "PushAIObserver",
    "ICircleAetherTransport",
    "AetherAIObserver",
]

_MAX_BODY_LENGTH = 100


# ── Push ──────────────────────────────────────────────────────────────────


class IPushNotificationSender(ABC):
    """Platform-agnostic push notification sender. Implement with an APN or FCM
    SDK for real delivery. Mirrors ``IPushNotificationSender``.
    """

    @abstractmethod
    async def send_async(
        self, device_token: str, title: str, body: str, ct: object = None
    ) -> None:
        """Send a push notification to the device identified by ``device_token``."""
        ...


class PushAIObserver(IAIObserver):
    """:class:`IAIObserver` that delivers butler responses as push
    notifications via :class:`IPushNotificationSender`. Mirrors ``PushAIObserver``.
    """

    __slots__ = ("_sender", "_device_token")

    def __init__(self, sender: IPushNotificationSender, device_token: str) -> None:
        if sender is None:
            raise ValueError("sender is required")
        if device_token is None or not device_token.strip():
            raise ValueError("Device token is required.")
        self._sender = sender
        self._device_token = device_token

    async def on_chat_completed_async(
        self, event: AIChatEvent, ct: object = None
    ) -> None:
        await self._send_response(event.response)

    async def on_error(self, ex: BaseException) -> None:
        """Send an error push notification. Mirrors ``OnError`` (async here so
        the send is awaited rather than fire-and-forget).
        """
        if ex is None:
            raise ValueError("ex is required")
        msg = str(ex)
        body = (msg[:_MAX_BODY_LENGTH] + "…") if len(msg) > _MAX_BODY_LENGTH else msg
        await self._sender.send_async(self._device_token, "B! Error", body)

    async def _send_response(self, full_response: str) -> None:
        body = (
            (full_response[:_MAX_BODY_LENGTH] + "…")
            if len(full_response) > _MAX_BODY_LENGTH
            else full_response
        )
        await self._sender.send_async(self._device_token, "B!", body)


# ── Aether ────────────────────────────────────────────────────────────────


class ICircleAetherTransport(ABC):
    """(3.3.0) Publish/subscribe transport contract for the CircleAether mesh.
    Host packages register an implementation (AetherNet, Bluetooth, gRPC, …).
    Mirrors ``ICircleAetherTransport``.
    """

    @abstractmethod
    async def publish_async(self, topic: str, payload: bytes, ct: object = None) -> None:
        """Publish a payload to the given topic."""
        ...


class AetherAIObserver(IAIObserver):
    """:class:`IAIObserver` that forwards butler events to a CircleAether mesh
    transport. Mirrors ``AetherAIObserver``.
    """

    __slots__ = ("_transport",)

    def __init__(self, transport: ICircleAetherTransport) -> None:
        if transport is None:
            raise ValueError("transport is required")
        self._transport = transport

    async def on_chat_completed_async(
        self, event: AIChatEvent, ct: object = None
    ) -> None:
        payload = _json.dumps({"response": event.response}).encode("utf-8")
        await self._transport.publish_async("butler/response", payload)

    async def on_error(self, ex: BaseException) -> None:
        """Publish an error payload to the ``butler/error`` topic. Mirrors
        ``OnError``.
        """
        if ex is None:
            raise ValueError("ex is required")
        payload = _json.dumps(
            {"error": type(ex).__name__, "message": str(ex)}
        ).encode("utf-8")
        await self._transport.publish_async("butler/error", payload)
