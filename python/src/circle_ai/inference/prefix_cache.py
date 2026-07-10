"""Cross-session prefix cache (RT-06).

Port of ``CircleAI.Inference.PrefixCacheService`` — an on-disk cache of "warm"
model sessions keyed by the hash of (modelId, systemPrompt). Generators that
opt in via ``GenerationOptions.use_prefix_cache`` consult this before resetting
the model handle for a new conversation.

The on-disk session snapshot format is owned by the native inference engine;
this service owns only the indexing + LRU eviction. Key derivation, path
layout, and the 500 MB LRU-by-mtime eviction policy match the C# byte-for-byte.
"""
from __future__ import annotations

import hashlib
import os
import time

__all__ = ["PrefixCacheService"]

_CAP_BYTES = 500 * 1024 * 1024  # 500 MB


def _sha256_hex(value: str) -> str:
    return hashlib.sha256(value.encode("utf-8")).hexdigest()


def _default_root() -> str:
    # Windows: %LOCALAPPDATA%/CircleAI/prefix-cache
    # Unix-like: ~/.circleai/prefix-cache
    local = os.environ.get("LOCALAPPDATA")
    if local and local.strip():
        return os.path.join(local, "CircleAI", "prefix-cache")
    home = os.path.expanduser("~")
    return os.path.join(home, ".circleai", "prefix-cache")


class PrefixCacheService:
    """Manages an on-disk cache of warm model sessions keyed by
    hash(modelId, systemPrompt). Mirrors ``CircleAI.Inference.PrefixCacheService``.

    The service is safe to share across generators. Construct with an explicit
    ``root`` for tests; the process-wide default rooted at the platform cache
    directory is exposed via :meth:`default`.
    """

    __slots__ = ("_root",)

    _default_instance: "PrefixCacheService | None" = None

    def __init__(self, root: str) -> None:
        if not root or not root.strip():
            raise ValueError("root is required.")
        self._root = root
        os.makedirs(self._root, exist_ok=True)

    @classmethod
    def default(cls) -> "PrefixCacheService":
        """The default per-app instance rooted at the platform cache directory."""
        if cls._default_instance is None:
            cls._default_instance = cls(_default_root())
        return cls._default_instance

    @property
    def root(self) -> str:
        return self._root

    @staticmethod
    def key_for(model_id: str, system_prompt: str | None) -> str | None:
        """Compute the cache key for a (modelId, systemPrompt) pair.

        Returns ``None`` when ``system_prompt`` is null/empty — there is nothing
        to cache without a system prompt to key against. First 16 hex chars per
        component, joined with ``_`` (matches the C# ``[..16]`` slice).
        """
        if not model_id or not model_id.strip():
            return None
        if not system_prompt:
            return None
        model_hash = _sha256_hex(model_id)
        system_hash = _sha256_hex(system_prompt)
        return f"{model_hash[:16]}_{system_hash[:16]}"

    def path_for(self, key: str) -> str:
        """The cache path for ``key`` (may or may not exist)."""
        return os.path.join(self._root, f"{key}.session")

    async def has_entry_async(self, key: str, ct: object = None) -> bool:
        """``True`` when a cached entry exists for ``key``."""
        return os.path.isfile(self.path_for(key))

    def touch(self, key: str) -> None:
        """Bump the entry's mtime so LRU eviction treats it as recently used."""
        path = self.path_for(key)
        if os.path.isfile(path):
            now = time.time()
            os.utime(path, (now, now))

    async def evict_if_needed_async(self, ct: object = None) -> None:
        """Evict oldest entries until the directory is under the 500 MB cap.

        Called after every successful save to keep the cache bounded.
        Best-effort — per-file delete failures are swallowed.
        """
        if not os.path.isdir(self._root):
            return

        files = []
        for name in os.listdir(self._root):
            if not name.endswith(".session"):
                continue
            full = os.path.join(self._root, name)
            try:
                st = os.stat(full)
            except OSError:
                continue
            files.append((full, st.st_mtime, st.st_size))

        # Oldest first (ascending mtime).
        files.sort(key=lambda f: f[1])
        total = sum(f[2] for f in files)
        i = 0
        while total > _CAP_BYTES and i < len(files):
            full, _mtime, size = files[i]
            i += 1
            try:
                total -= size
                os.remove(full)
            except OSError:
                pass  # best effort
