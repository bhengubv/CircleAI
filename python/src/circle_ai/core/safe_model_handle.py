# core/safe_model_handle.py
#
# Port of CircleAI.Core.SafeModelHandle + PlatformInterop.
#
# The C# SafeModelHandle wraps an opaque native ``llama_model*`` pointer with
# a release callback so CircleAI.Core stays free of native imports. Python has
# no P/Invoke to llama.cpp, so:
#   • SafeModelHandle is a deterministic handle wrapper around an integer
#     "native pointer" with a caller-supplied release callback, matching the
#     C# SafeHandle lifecycle (invalid until set, released exactly once).
#   • PlatformInterop.load_model validates the path (as C# does) then delegates
#     the actual "native load" to an injectable loader function. The default
#     loader is a deterministic in-memory native shim — it never touches a real
#     .so/.dll but honours the full contract (returns a live handle for a real
#     file, frees exactly once). Hosts inject a real llama.cpp loader when one
#     is available.

from __future__ import annotations

import os
import threading
from typing import Callable, Optional


class SafeModelHandle:
    """Handle wrapper around an opaque native model pointer.

    Mirrors the C# ``SafeModelHandle`` (a ``SafeHandle`` over a ``llama_model*``).
    The release callback is supplied by the loader so this module stays free of
    native imports. The handle is released exactly once — either explicitly via
    :meth:`dispose`, on context-manager exit, or by the finalizer.
    """

    __slots__ = ("_handle", "_release_callback", "_released", "_lock", "__weakref__")

    def __init__(
        self,
        native_handle: int = 0,
        release_callback: Optional[Callable[[int], None]] = None,
    ) -> None:
        """Construct a wrapper.

        With no arguments this mirrors the C# parameterless constructor: the
        handle is invalid until :meth:`set_handle` + :meth:`with_release_callback`.
        With a non-zero ``native_handle`` and a ``release_callback`` it mirrors
        the explicit C# constructor.
        """
        self._lock = threading.Lock()
        self._released = False
        self._handle = native_handle
        if native_handle != 0:
            if release_callback is None:
                raise ValueError("release_callback")
            self._release_callback = release_callback
        else:
            self._release_callback = release_callback

    @property
    def handle(self) -> int:
        """The raw native pointer value (0 == invalid)."""
        return self._handle

    @property
    def is_invalid(self) -> bool:
        """True when the handle is the invalid (zero) pointer."""
        return self._handle == 0

    def set_handle(self, native_handle: int) -> None:
        """Set the raw native pointer. Used when the runtime constructs this
        handle via marshalling and fills it in afterwards."""
        self._handle = native_handle

    def with_release_callback(
        self, release_callback: Callable[[int], None]
    ) -> "SafeModelHandle":
        """Wire up the release callback after construction. Returns self."""
        if release_callback is None:
            raise ValueError("release_callback")
        self._release_callback = release_callback
        return self

    def _release_handle(self) -> bool:
        if self._handle != 0:
            if self._release_callback is not None:
                self._release_callback(self._handle)
            self._handle = 0
        return True

    def dispose(self) -> None:
        """Release the native handle exactly once (idempotent)."""
        with self._lock:
            if self._released:
                return
            self._released = True
            self._release_handle()

    def __enter__(self) -> "SafeModelHandle":
        return self

    def __exit__(self, exc_type, exc, tb) -> None:
        self.dispose()

    def __del__(self) -> None:  # pragma: no cover - finalizer timing
        try:
            self.dispose()
        except Exception:
            pass


# ─────────────────────────────────────────────────────────────────────────────
# PlatformInterop — loads native models, returns a SafeModelHandle.
# ─────────────────────────────────────────────────────────────────────────────

# Injection point for the "native" loader. A loader takes a model path and
# returns a (native_handle, free_callback) pair. native_handle must be
# non-zero on success. The default is a deterministic in-memory shim.
NativeLoader = Callable[[str], "tuple[int, Callable[[int], None]]"]


class _DeterministicNativeShim:
    """Deterministic stand-in for the llama.cpp native layer.

    Hands out monotonically-increasing non-zero "pointers" and tracks which are
    live, so a full load/free lifecycle is observable and correct without any
    real native library. This is the injected default — production hosts swap in
    a real llama.cpp binding through :func:`set_native_loader`.
    """

    def __init__(self) -> None:
        self._next = 0x1000
        self._live: set[int] = set()
        self._lock = threading.Lock()

    def load(self, path: str) -> "tuple[int, Callable[[int], None]]":
        with self._lock:
            self._next += 0x10
            ptr = self._next
            self._live.add(ptr)
        return ptr, self._free

    def _free(self, ptr: int) -> None:
        with self._lock:
            self._live.discard(ptr)

    def is_live(self, ptr: int) -> bool:
        with self._lock:
            return ptr in self._live


_default_shim = _DeterministicNativeShim()
_native_loader: NativeLoader = _default_shim.load


def set_native_loader(loader: Optional[NativeLoader]) -> None:
    """Inject the native model loader used by :func:`load_model`.

    Pass ``None`` to restore the deterministic in-memory shim (the default).
    Hosts with a real llama.cpp binding wire it here.
    """
    global _native_loader
    _native_loader = loader if loader is not None else _default_shim.load


def default_shim() -> _DeterministicNativeShim:
    """Expose the default deterministic shim (for tests that assert free)."""
    return _default_shim


def load_model(path: str) -> SafeModelHandle:
    """Load a GGUF model from *path* and wrap it in a :class:`SafeModelHandle`.

    Validation matches the C# ``PlatformInterop.LoadModel``:
      * raises ``ValueError`` when the path is null/blank,
      * raises ``FileNotFoundError`` when the file does not exist,
      * raises ``RuntimeError`` when the native load returns a null pointer.
    """
    if path is None or path.strip() == "":
        raise ValueError("Model path is required.")
    if not os.path.isfile(path):
        raise FileNotFoundError(f"GGUF model file not found: {path}")

    native_handle, free_callback = _native_loader(path)
    if native_handle == 0:
        raise RuntimeError(
            f"llama.cpp failed to load model at '{path}'. "
            "Verify the file is a valid GGUF and that the native llama "
            "library is on the search path."
        )
    return SafeModelHandle(native_handle, free_callback)
