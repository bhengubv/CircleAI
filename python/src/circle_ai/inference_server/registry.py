"""In-process model registry.

Ports ``CircleAI.Inference.Server.Models.IInferenceServerModelRegistry`` and
``InferenceServerModelRegistry`` — maps logical model IDs to the
:class:`IInferenceBridge` (chat) / ``ITextEmbedder`` (embeddings) that serves
them. The host populates this at startup and the endpoints look up by
``request.model``. Thread-safe via a lock (the C# uses ConcurrentDictionary).
"""
from __future__ import annotations

import threading
from abc import ABC, abstractmethod
from typing import Dict, List, Optional

from ..hosting.inference_bridge import IInferenceBridge

__all__ = ["IInferenceServerModelRegistry", "InferenceServerModelRegistry"]


class IInferenceServerModelRegistry(ABC):
    """In-process registry of bridge/embedder instances keyed by model ID.
    Mirrors ``IInferenceServerModelRegistry``.
    """

    @abstractmethod
    def register(self, model_id: str, bridge: IInferenceBridge) -> None: ...

    @abstractmethod
    def register_embedder(self, model_id: str, embedder: object) -> None: ...

    @abstractmethod
    def deregister(self, model_id: str) -> bool: ...

    @abstractmethod
    def resolve(self, model_id: str) -> Optional[IInferenceBridge]: ...

    @abstractmethod
    def resolve_embedder(self, model_id: str) -> Optional[object]: ...

    @abstractmethod
    def all_model_ids(self) -> List[str]: ...

    @abstractmethod
    def chat_model_ids(self) -> List[str]: ...


class InferenceServerModelRegistry(IInferenceServerModelRegistry):
    """Default thread-safe implementation. Mirrors ``InferenceServerModelRegistry``."""

    __slots__ = ("_lock", "_chat", "_embed")

    def __init__(self) -> None:
        self._lock = threading.Lock()
        self._chat: Dict[str, IInferenceBridge] = {}
        self._embed: Dict[str, object] = {}

    def register(self, model_id: str, bridge: IInferenceBridge) -> None:
        if not model_id or not model_id.strip():
            raise ValueError("model_id is required")
        if bridge is None:
            raise ValueError("bridge is required")
        with self._lock:
            self._chat[model_id] = bridge

    def register_embedder(self, model_id: str, embedder: object) -> None:
        if not model_id or not model_id.strip():
            raise ValueError("model_id is required")
        if embedder is None:
            raise ValueError("embedder is required")
        with self._lock:
            self._embed[model_id] = embedder

    def deregister(self, model_id: str) -> bool:
        with self._lock:
            return self._chat.pop(model_id, None) is not None

    def resolve(self, model_id: str) -> Optional[IInferenceBridge]:
        with self._lock:
            return self._chat.get(model_id)

    def resolve_embedder(self, model_id: str) -> Optional[object]:
        with self._lock:
            return self._embed.get(model_id)

    def all_model_ids(self) -> List[str]:
        with self._lock:
            # Distinct, preserving chat-then-embed insertion order.
            seen = list(self._chat.keys())
            for k in self._embed.keys():
                if k not in self._chat:
                    seen.append(k)
            return seen

    def chat_model_ids(self) -> List[str]:
        with self._lock:
            return list(self._chat.keys())
