# core/circle_engine.py
#
# Port of:
#   • CircleAI.Core.CircleEngine        — top-level facade + type-keyed module bag
#   • CircleAI.Core.ICircleModule       — attachable module contract
#   • CircleAI.Core.IEmbeddingService   — embedding module contract
#
# The C# module bag is keyed on the CLR generic type argument (typeof(T)).
# Python has no reified generics, so register_module / get_module / has_module
# take the key type explicitly. When omitted on register, the concrete type of
# the instance is used — the common case.

from __future__ import annotations

from abc import ABC, abstractmethod
from typing import Dict, Optional, Type, TypeVar

from .model_loader import IModelLoader

T = TypeVar("T")


# ─────────────────────────────────────────────────────────────────────────────
# ICircleModule — attachable module contract.
# ─────────────────────────────────────────────────────────────────────────────


class ICircleModule(ABC):
    """A module that attaches to a :class:`CircleEngine`.

    Disposable — hosts call :meth:`dispose` on teardown.
    """

    @property
    @abstractmethod
    def module_name(self) -> str:
        """Human-readable module name."""
        raise NotImplementedError

    @abstractmethod
    async def init_async(self, engine: "CircleEngine") -> None:
        """Initialise the module against the owning engine."""
        raise NotImplementedError

    @property
    @abstractmethod
    def is_model_loaded(self) -> bool:
        """True once the module's backing model is loaded."""
        raise NotImplementedError

    def dispose(self) -> None:
        """Release resources. Default is a no-op; override as needed."""
        return None


# ─────────────────────────────────────────────────────────────────────────────
# IEmbeddingService — embedding module contract.
# ─────────────────────────────────────────────────────────────────────────────


class IEmbeddingService(ICircleModule, ABC):
    """An :class:`ICircleModule` that produces dense embeddings."""

    @abstractmethod
    def generate_embedding(self, text: str) -> list[float]:
        """Embed *text* into a dense float vector."""
        raise NotImplementedError

    @property
    @abstractmethod
    def embedding_size(self) -> int:
        """Number of floats each embedding carries."""
        raise NotImplementedError


# ─────────────────────────────────────────────────────────────────────────────
# CircleEngine — top-level facade.
# ─────────────────────────────────────────────────────────────────────────────


class CircleEngine:
    """Top-level facade for the CircleAI on-device stack.

    Holds the :class:`IModelLoader` and a small type-keyed registry of attached
    modules (embeddings, search, chat generators, tool bridges) wired in from
    downstream packages.
    """

    __slots__ = ("_model_loader", "_modules", "embedding_service")

    def __init__(self, model_loader: IModelLoader) -> None:
        if model_loader is None:
            raise ValueError("model_loader")
        self._model_loader = model_loader
        self._modules: Dict[type, object] = {}
        # Optional embedding service — wired in by the embeddings package.
        # Kept as a plain attribute so Core need not reference downstream impls.
        self.embedding_service: Optional[object] = None

    @property
    def model_loader(self) -> IModelLoader:
        """The model loader used to acquire and cache model files."""
        return self._model_loader

    def register_module(self, module: T, key_type: Optional[Type] = None) -> "CircleEngine":
        """Register a module instance keyed by ``key_type`` (defaults to the
        instance's concrete type). Returns self for chaining."""
        if module is None:
            raise ValueError("module")
        key = key_type if key_type is not None else type(module)
        self._modules[key] = module
        return self

    def get_module(self, key_type: Type[T]) -> Optional[T]:
        """Retrieve a previously registered module, or ``None`` if none was
        registered for that type."""
        return self._modules.get(key_type)  # type: ignore[return-value]

    def has_module(self, key_type: Type) -> bool:
        """True if a module of the given type has been registered."""
        return key_type in self._modules
