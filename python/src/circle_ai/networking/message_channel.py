# message_channel.py
#
# TransportMessageChannel — a working IMessageChannel over any INetworkTransport,
# plus the pluggable IMessageSerializer seam and a JSON default.
#
# The C# ``IMessageChannel`` is generic (``SendAsync<T>`` / ``ReceiveAsync<T>``).
# Python carries no runtime generic type, so the (de)serialisation strategy is
# injected as IMessageSerializer; JsonMessageSerializer is a deterministic
# stdlib-only default that round-trips str / bytes / mappings / dataclasses.

from __future__ import annotations

import dataclasses
import json
from abc import ABC, abstractmethod
from typing import AsyncIterator, Optional, Type, TypeVar

from .interfaces import IMessageChannel, INetworkTransport
from .network_types import MessagePriority, NetworkPayload

T = TypeVar("T")

_JSON_CONTENT_TYPE = "application/json"


class IMessageSerializer(ABC):
    """Turns typed messages into payload bytes and back.

    Injected into :class:`TransportMessageChannel` so the channel stays
    transport- and type-agnostic (the seam that replaces C# generics).
    """

    @abstractmethod
    def serialize(self, message: object) -> bytes:
        """Encode ``message`` to bytes for a :class:`NetworkPayload`."""
        ...

    @abstractmethod
    def deserialize(self, data: bytes, message_type: Optional[Type[T]]) -> T:
        """Decode payload bytes back to ``message_type`` (or a plain object when
        ``message_type`` is ``None``).
        """
        ...

    @property
    def content_type(self) -> str:
        """Content-type stamped onto outbound payloads."""
        return "application/octet-stream"


class JsonMessageSerializer(IMessageSerializer):
    """Deterministic JSON serializer (stdlib only).

    Encoding rules:
      • ``bytes``           -> passed through verbatim.
      • ``str``             -> UTF-8 bytes of the raw string (not JSON-quoted).
      • dataclass instance  -> ``json.dumps(asdict(...))`` with sorted keys.
      • mapping / sequence  -> ``json.dumps(...)`` with sorted keys.

    Decoding rules (``deserialize``):
      • target ``bytes``    -> raw bytes.
      • target ``str``      -> UTF-8 decode.
      • target dataclass    -> ``json.loads`` then field-mapped construction.
      • otherwise           -> ``json.loads`` (dict / list / scalar).
    ``sort_keys=True`` keeps the wire bytes stable across runs.
    """

    def serialize(self, message: object) -> bytes:
        if isinstance(message, (bytes, bytearray)):
            return bytes(message)
        if isinstance(message, str):
            return message.encode("utf-8")
        if dataclasses.is_dataclass(message) and not isinstance(message, type):
            payload = dataclasses.asdict(message)
            return json.dumps(payload, sort_keys=True).encode("utf-8")
        return json.dumps(message, sort_keys=True).encode("utf-8")

    def deserialize(self, data: bytes, message_type: Optional[Type[T]]) -> T:
        if message_type is bytes or message_type is bytearray:
            return bytes(data)  # type: ignore[return-value]
        if message_type is str:
            return data.decode("utf-8")  # type: ignore[return-value]
        obj = json.loads(data.decode("utf-8"))
        if (
            message_type is not None
            and dataclasses.is_dataclass(message_type)
            and isinstance(obj, dict)
        ):
            field_names = {f.name for f in dataclasses.fields(message_type)}
            kwargs = {k: v for k, v in obj.items() if k in field_names}
            return message_type(**kwargs)  # type: ignore[return-value]
        return obj  # type: ignore[return-value]

    @property
    def content_type(self) -> str:
        return _JSON_CONTENT_TYPE


class TransportMessageChannel(IMessageChannel):
    """`IMessageChannel` over a single :class:`INetworkTransport`.

    Serialises via the injected :class:`IMessageSerializer` (JSON by default),
    wraps the bytes in a :class:`NetworkPayload`, and sends. ``receive_async``
    streams inbound payloads, filters to those whose content-type the serializer
    produced, and deserialises each to the requested type.
    """

    def __init__(
        self,
        transport: INetworkTransport,
        serializer: Optional[IMessageSerializer] = None,
        *,
        priority: MessagePriority = MessagePriority.NORMAL,
    ) -> None:
        if transport is None:
            raise ValueError("transport required")
        self._transport = transport
        self._serializer: IMessageSerializer = serializer or JsonMessageSerializer()
        self._priority = priority

    async def send_async(
        self, destination_id: str, message: T, *, ct: Optional[object] = None
    ) -> None:
        if message is None:
            raise ValueError("message required")
        data = self._serializer.serialize(message)
        payload = NetworkPayload.create(
            data=data,
            destination_id=destination_id,
            priority=self._priority,
            content_type=self._serializer.content_type,
        )
        await self._transport.send_async(payload, ct=ct)

    def receive_async(
        self,
        message_type: Optional[Type[T]] = None,
        *,
        ct: Optional[object] = None,
    ) -> AsyncIterator[T]:
        serializer = self._serializer
        # Open the underlying transport iterator SYNCHRONOUSLY (before returning
        # and before any await) so its receive queue is registered now — a
        # message sent right after this call cannot race the subscription and be
        # lost. Deferring this into the generator body would re-introduce that
        # Wave-1 race, since the body runs only on the first __anext__.
        inner = self._transport.receive_async(ct=ct)

        async def _iter() -> AsyncIterator[T]:
            async for payload in inner:
                yield serializer.deserialize(payload.data, message_type)

        return _iter()
