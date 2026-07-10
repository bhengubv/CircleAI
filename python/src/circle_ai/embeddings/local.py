# embeddings/local.py
#
# Port of CircleAI.Embeddings.Local:
#   • IEmbeddingEncoder        — text -> dense vector
#   • EmbeddingDocument        — one stored document
#   • EmbeddingSearchHit       — one search hit (doc + score)
#   • ICircleEmbeddingStore    — the store contract
#   • EmbeddingIndexHit        — one index hit (internal id + score)
#   • IEmbeddingIndex          — the vector-index contract
#   • InMemoryEmbeddingStore   — brute-force store over TurboQuant vectors
#   • TurboVecEmbeddingIndex   — deterministic in-memory vector index
#   • HnswEmbeddingStore       — store backed by TurboVecEmbeddingIndex
#
# On-disk formats are byte-compatible with the C# BinaryWriter output:
#   InMemoryEmbeddingStore file : magic 0x4C455143 ("CELQ"), version 1.
#   HnswEmbeddingStore .docs    : magic 0x53434847 ("HGCS"), version 1.
#   TurboVecEmbeddingIndex file : a private (this-port) FP32 dump — the C#
#       equivalent is the opaque turbovec native blob, which has no portable
#       spec; round-trips within this implementation.
# C# BinaryWriter.Write(string) uses a 7-bit-encoded-int length prefix + UTF-8;
# _BinaryWriter/_BinaryReader below reproduce that exactly.

from __future__ import annotations

import io
import math
import os
import struct
import threading
from abc import ABC, abstractmethod
from dataclasses import dataclass
from typing import Dict, List, Mapping, Optional, Sequence, Tuple

from ..memory.compression import TurboQuantCodec, TurboQuantPayload

__all__ = [
    "IEmbeddingEncoder",
    "EmbeddingDocument",
    "EmbeddingSearchHit",
    "ICircleEmbeddingStore",
    "EmbeddingIndexHit",
    "IEmbeddingIndex",
    "InMemoryEmbeddingStore",
    "TurboVecEmbeddingIndex",
    "HnswEmbeddingStore",
]


# ─────────────────────────────────────────────────────────────────────────────
# C#-compatible BinaryWriter / BinaryReader (7-bit-encoded string lengths).
# ─────────────────────────────────────────────────────────────────────────────


class _BinaryWriter:
    def __init__(self) -> None:
        self._buf = bytearray()

    def write_int32(self, v: int) -> None:
        self._buf += struct.pack("<i", v)

    def write_uint16(self, v: int) -> None:
        self._buf += struct.pack("<H", v & 0xFFFF)

    def write_single(self, v: float) -> None:
        self._buf += struct.pack("<f", v)

    def write_bool(self, v: bool) -> None:
        self._buf += b"\x01" if v else b"\x00"

    def write_bytes(self, b: bytes) -> None:
        self._buf += b

    def write_string(self, s: str) -> None:
        data = s.encode("utf-8")
        self._write_7bit_len(len(data))
        self._buf += data

    def _write_7bit_len(self, value: int) -> None:
        # C# BinaryWriter.Write7BitEncodedInt — unsigned LEB128.
        v = value & 0xFFFFFFFF
        while v >= 0x80:
            self._buf.append((v & 0x7F) | 0x80)
            v >>= 7
        self._buf.append(v & 0x7F)

    def getvalue(self) -> bytes:
        return bytes(self._buf)


class _BinaryReader:
    def __init__(self, data: bytes) -> None:
        self._data = data
        self._pos = 0

    def read_int32(self) -> int:
        v = struct.unpack_from("<i", self._data, self._pos)[0]
        self._pos += 4
        return v

    def read_uint16(self) -> int:
        v = struct.unpack_from("<H", self._data, self._pos)[0]
        self._pos += 2
        return v

    def read_single(self) -> float:
        v = struct.unpack_from("<f", self._data, self._pos)[0]
        self._pos += 4
        return v

    def read_bool(self) -> bool:
        v = self._data[self._pos]
        self._pos += 1
        return v != 0

    def read_bytes(self, n: int) -> bytes:
        b = self._data[self._pos : self._pos + n]
        self._pos += n
        return bytes(b)

    def read_string(self) -> str:
        length = self._read_7bit_len()
        b = self._data[self._pos : self._pos + length]
        self._pos += length
        return b.decode("utf-8")

    def _read_7bit_len(self) -> int:
        result = 0
        shift = 0
        while True:
            byte = self._data[self._pos]
            self._pos += 1
            result |= (byte & 0x7F) << shift
            if (byte & 0x80) == 0:
                break
            shift += 7
        return result


# ─────────────────────────────────────────────────────────────────────────────
# Records + contracts.
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class EmbeddingDocument:
    """One document in the store. ``id`` is caller-chosen and uniquely
    identifies the document for delete / update."""

    id: str
    text: str
    metadata: Optional[Mapping[str, str]] = None


@dataclass(frozen=True, slots=True)
class EmbeddingSearchHit:
    """One hit from a store search. Higher ``score`` = closer (cosine)."""

    document: EmbeddingDocument
    score: float


@dataclass(frozen=True, slots=True)
class EmbeddingIndexHit:
    """One hit returned by :meth:`IEmbeddingIndex.search_async`. ``internal_id``
    is the insertion-order id assigned by ``add_async``. Higher ``score`` =
    closer."""

    internal_id: int
    score: float


class IEmbeddingEncoder(ABC):
    """Translates text into a dense vector. Bring your own."""

    @property
    @abstractmethod
    def dimension(self) -> int:
        """Vector dimension this encoder produces."""
        raise NotImplementedError

    @abstractmethod
    async def encode_async(self, text: str, ct: object = None) -> List[float]:
        """Encode one text into a dense vector."""
        raise NotImplementedError


class ICircleEmbeddingStore(ABC):
    """On-device embedding store with a built-in RAG primitive. Async-disposable."""

    @property
    @abstractmethod
    def dimension(self) -> int:
        raise NotImplementedError

    @property
    @abstractmethod
    def count(self) -> int:
        raise NotImplementedError

    @abstractmethod
    async def add_async(
        self,
        document: EmbeddingDocument,
        vector: Optional[Sequence[float]] = None,
        ct: object = None,
    ) -> None:
        """Add (or, for the in-memory store, replace) one document. When
        *vector* is ``None`` the encoder produces it; otherwise the supplied
        vector is used (length must equal :attr:`dimension`)."""
        raise NotImplementedError

    @abstractmethod
    async def remove_async(self, id: str, ct: object = None) -> bool:
        raise NotImplementedError

    @abstractmethod
    async def search_async(
        self,
        query,
        top_k: int = 5,
        ct: object = None,
    ) -> List[EmbeddingSearchHit]:
        """Search by text (``str``) or a pre-computed query vector
        (sequence of floats)."""
        raise NotImplementedError

    @abstractmethod
    async def save_async(self, path: str, ct: object = None) -> None:
        raise NotImplementedError

    @abstractmethod
    async def load_async(self, path: str, ct: object = None) -> None:
        raise NotImplementedError

    async def dispose_async(self) -> None:
        return None


class IEmbeddingIndex(ABC):
    """Vector index contract — the search primitive the store layers on top of.
    Disposable."""

    @property
    @abstractmethod
    def dimension(self) -> int:
        raise NotImplementedError

    @property
    @abstractmethod
    def count(self) -> int:
        raise NotImplementedError

    @abstractmethod
    async def add_async(self, vector: Sequence[float], ct: object = None) -> int:
        """Append one vector; return the internal id assigned."""
        raise NotImplementedError

    @abstractmethod
    async def search_async(
        self, query_vector: Sequence[float], top_k: int, ct: object = None
    ) -> List[EmbeddingIndexHit]:
        raise NotImplementedError

    @abstractmethod
    async def save_async(self, path: str, ct: object = None) -> None:
        raise NotImplementedError

    @abstractmethod
    async def load_async(self, path: str, ct: object = None) -> None:
        raise NotImplementedError

    def dispose(self) -> None:
        return None


# ─────────────────────────────────────────────────────────────────────────────
# InMemoryEmbeddingStore — brute-force over TurboQuant-compressed vectors.
# ─────────────────────────────────────────────────────────────────────────────


def _norm_safe(v: Sequence[float]) -> float:
    s = 0.0
    for x in v:
        s += x * x
    return math.sqrt(s)


class InMemoryEmbeddingStore(ICircleEmbeddingStore):
    """Default :class:`ICircleEmbeddingStore`: brute-force search over
    TurboQuant-compressed vectors held in memory."""

    _FILE_MAGIC = 0x4C455143  # "CELQ" little-endian
    _FILE_VERSION = 1
    _DEFAULT_BITS_PER_DIM = 4

    def __init__(self, encoder: IEmbeddingEncoder, bits_per_dim: int = _DEFAULT_BITS_PER_DIM) -> None:
        if encoder is None:
            raise ValueError("encoder")
        if bits_per_dim < 1 or bits_per_dim > 8:
            raise ValueError("Valid range: 1-8.")
        self._encoder = encoder
        self._bits_per_dim = bits_per_dim
        self._entries: Dict[str, Tuple[EmbeddingDocument, TurboQuantPayload]] = {}
        self._lock = threading.Lock()
        self._disposed = False

    @property
    def dimension(self) -> int:
        return self._encoder.dimension

    @property
    def count(self) -> int:
        return len(self._entries)

    async def add_async(
        self,
        document: EmbeddingDocument,
        vector: Optional[Sequence[float]] = None,
        ct: object = None,
    ) -> None:
        if document is None:
            raise ValueError("document")
        self._throw_if_disposed()
        if vector is None:
            vector = await self._encoder.encode_async(document.text, ct)
        if len(vector) != self.dimension:
            raise ValueError(
                f"Vector length {len(vector)} != store dimension {self.dimension}."
            )
        payload = TurboQuantCodec.encode(vector, self._bits_per_dim)
        with self._lock:
            self._entries[document.id] = (document, payload)

    async def remove_async(self, id: str, ct: object = None) -> bool:
        if id is None or id.strip() == "":
            raise ValueError("id")
        self._throw_if_disposed()
        with self._lock:
            return self._entries.pop(id, None) is not None

    async def search_async(
        self, query, top_k: int = 5, ct: object = None
    ) -> List[EmbeddingSearchHit]:
        self._throw_if_disposed()
        if isinstance(query, str):
            if query == "":
                raise ValueError("query_text")
            query_vector = await self._encoder.encode_async(query, ct)
        else:
            query_vector = list(query)

        if len(query_vector) != self.dimension:
            raise ValueError(
                f"Vector length {len(query_vector)} != store dimension {self.dimension}."
            )
        if top_k <= 0:
            raise ValueError("top_k")

        q_norm = _norm_safe(query_vector)
        q = list(query_vector)
        if q_norm > 0:
            for i in range(len(q)):
                q[i] /= q_norm

        # Brute-force cosine. Each entry decoded on demand. Running top-K.
        # heap holds (score, id); we keep the smallest at the front.
        heap: List[Tuple[float, str]] = []
        with self._lock:
            items = list(self._entries.items())
        for entry_id, (doc, payload) in items:
            decoded = TurboQuantCodec.decode(payload, self.dimension, self._bits_per_dim)
            entry_norm = _norm_safe(decoded)
            if entry_norm <= 0:
                continue
            dot = 0.0
            for i in range(self.dimension):
                dot += q[i] * (decoded[i] / entry_norm)

            if len(heap) < top_k:
                _heap_add(heap, (dot, entry_id))
            elif dot > heap[0][0]:
                # Match C# exactly: replace the current minimum only when the
                # candidate's score is *strictly* greater than heap.Min.Score.
                _heap_pop(heap)
                _heap_add(heap, (dot, entry_id))

        ordered = sorted(heap, key=lambda t: t[0], reverse=True)
        result: List[EmbeddingSearchHit] = []
        with self._lock:
            for score, entry_id in ordered:
                doc = self._entries[entry_id][0]
                result.append(EmbeddingSearchHit(doc, score))
        return result

    async def save_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        with self._lock:
            snapshot = list(self._entries.items())
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)

        bw = _BinaryWriter()
        bw.write_int32(self._FILE_MAGIC)
        bw.write_uint16(self._FILE_VERSION)
        bw.write_uint16(self._bits_per_dim)
        bw.write_int32(self.dimension)
        bw.write_int32(len(snapshot))
        for entry_id, (doc, payload) in snapshot:
            bw.write_string(entry_id)
            bw.write_string(doc.text)
            meta = doc.metadata
            bw.write_int32(len(meta) if meta is not None else 0)
            if meta is not None:
                for k, v in meta.items():
                    bw.write_string(k)
                    bw.write_string(v)
            bw.write_single(payload.norm)
            bw.write_int32(len(payload.packed_indices))
            bw.write_bytes(payload.packed_indices)

        tmp = path + ".tmp"
        with open(tmp, "wb") as fh:
            fh.write(bw.getvalue())
        if os.path.exists(path):
            os.remove(path)
        os.replace(tmp, path)

    async def load_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        if not os.path.isfile(path):
            raise FileNotFoundError(f"Embedding store file not found: {path}")

        with open(path, "rb") as fh:
            br = _BinaryReader(fh.read())
        magic = br.read_int32()
        if magic != self._FILE_MAGIC:
            raise ValueError("Not a CircleAI embedding store file.")
        version = br.read_uint16()
        if version != self._FILE_VERSION:
            raise ValueError(f"Unsupported file version {version}.")
        file_bits = br.read_uint16()
        if file_bits != self._bits_per_dim:
            raise ValueError(
                f"Bits-per-dim mismatch: store={self._bits_per_dim}, file={file_bits}."
            )
        file_dim = br.read_int32()
        if file_dim != self.dimension:
            raise ValueError(
                f"Dimension mismatch: store={self.dimension}, file={file_dim}."
            )
        count = br.read_int32()
        new_entries: Dict[str, Tuple[EmbeddingDocument, TurboQuantPayload]] = {}
        for _ in range(count):
            entry_id = br.read_string()
            text = br.read_string()
            meta_count = br.read_int32()
            metadata: Optional[Dict[str, str]] = None
            if meta_count > 0:
                metadata = {}
                for _m in range(meta_count):
                    key = br.read_string()
                    metadata[key] = br.read_string()
            norm = br.read_single()
            packed_len = br.read_int32()
            packed = br.read_bytes(packed_len)
            new_entries[entry_id] = (
                EmbeddingDocument(entry_id, text, metadata),
                TurboQuantPayload(norm, packed),
            )
        with self._lock:
            self._entries = new_entries

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        with self._lock:
            self._entries.clear()

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("InMemoryEmbeddingStore is disposed")


def _heap_add(heap: List[Tuple[float, str]], item: Tuple[float, str]) -> None:
    # Keep the buffer ascending by (score, id) so heap[0] is the minimum —
    # the same element C#'s SortedSet<(float, string)> exposes as .Min.
    heap.append(item)
    heap.sort(key=lambda t: (t[0], t[1]))


def _heap_pop(heap: List[Tuple[float, str]]) -> None:
    # Remove the minimum (front after the sort maintained by _heap_add).
    if heap:
        heap.pop(0)


# ─────────────────────────────────────────────────────────────────────────────
# TurboVecEmbeddingIndex — deterministic in-memory vector index.
#
# The C# type wraps a native Rust crate (turbovecbridge). Per the port rules the
# native dependency is replaced with a deterministic in-memory implementation:
# vectors are held as FP32, search is brute-force cosine. It honours the whole
# IEmbeddingIndex contract (dimension multiple of 8, bit-width 2..4, insertion
# ids, -1 padding collapsed to valid hits, save/load round-trip).
# ─────────────────────────────────────────────────────────────────────────────


class TurboVecEmbeddingIndex(IEmbeddingIndex):
    """Deterministic in-memory :class:`IEmbeddingIndex` (network-/native-free
    stand-in for the turbovec-backed C# index)."""

    _FILE_MAGIC = 0x54564543  # "CEVT" — Circle Embeddings Vec Turbovec
    _FILE_VERSION = 1

    def __init__(self, dimension: int, bit_width: int = 4) -> None:
        if dimension <= 0:
            raise ValueError("Dimension must be positive.")
        if dimension % 8 != 0:
            raise ValueError("Dimension must be a multiple of 8.")
        if bit_width < 2 or bit_width > 4:
            raise ValueError("BitWidth must be 2, 3, or 4.")
        self._dimension = dimension
        self._bit_width = bit_width
        self._vectors: List[List[float]] = []
        self._lock = threading.Lock()
        self._disposed = False

    @property
    def dimension(self) -> int:
        self._throw_if_disposed()
        return self._dimension

    @property
    def count(self) -> int:
        self._throw_if_disposed()
        with self._lock:
            return len(self._vectors)

    @property
    def bit_width(self) -> int:
        return self._bit_width

    async def add_async(self, vector: Sequence[float], ct: object = None) -> int:
        self._throw_if_disposed()
        if len(vector) != self._dimension:
            raise ValueError(
                f"Vector length {len(vector)} != index dimension {self._dimension}."
            )
        with self._lock:
            internal_id = len(self._vectors)
            self._vectors.append([float(x) for x in vector])
            return internal_id

    async def search_async(
        self, query_vector: Sequence[float], top_k: int, ct: object = None
    ) -> List[EmbeddingIndexHit]:
        self._throw_if_disposed()
        if len(query_vector) != self._dimension:
            raise ValueError(
                f"Query length {len(query_vector)} != index dimension {self._dimension}."
            )
        if top_k <= 0:
            raise ValueError("top_k")
        with self._lock:
            vectors = [(i, v) for i, v in enumerate(self._vectors)]
        if not vectors:
            return []

        q = list(query_vector)
        q_norm = _norm_safe(q)
        if q_norm > 0:
            q = [x / q_norm for x in q]

        scored: List[Tuple[float, int]] = []
        for idx, v in vectors:
            v_norm = _norm_safe(v)
            if v_norm <= 0:
                score = 0.0
            else:
                dot = 0.0
                for i in range(self._dimension):
                    dot += q[i] * (v[i] / v_norm)
                score = dot
            scored.append((score, idx))

        # Highest score first; tie-break by ascending internal id (stable, and
        # matches "first inserted wins" the way the native crate reports).
        scored.sort(key=lambda t: (-t[0], t[1]))
        top = scored[:top_k]
        return [EmbeddingIndexHit(idx, score) for score, idx in top]

    async def save_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        with self._lock:
            snapshot = [list(v) for v in self._vectors]
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)
        bw = _BinaryWriter()
        bw.write_int32(self._FILE_MAGIC)
        bw.write_uint16(self._FILE_VERSION)
        bw.write_int32(self._dimension)
        bw.write_int32(self._bit_width)
        bw.write_int32(len(snapshot))
        for v in snapshot:
            for x in v:
                bw.write_single(x)
        with open(path, "wb") as fh:
            fh.write(bw.getvalue())

    async def load_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        if not os.path.isfile(path):
            raise FileNotFoundError(f"Index file not found: {path}")
        with open(path, "rb") as fh:
            br = _BinaryReader(fh.read())
        magic = br.read_int32()
        if magic != self._FILE_MAGIC:
            raise ValueError("Not a TurboVecEmbeddingIndex file.")
        version = br.read_uint16()
        if version != self._FILE_VERSION:
            raise ValueError(f"Unsupported index version {version}.")
        loaded_dim = br.read_int32()
        if loaded_dim != self._dimension:
            raise ValueError(
                f"Loaded index dim {loaded_dim} != configured dim {self._dimension}."
            )
        _bit_width = br.read_int32()
        count = br.read_int32()
        new_vectors: List[List[float]] = []
        for _ in range(count):
            new_vectors.append([br.read_single() for _i in range(self._dimension)])
        with self._lock:
            self._vectors = new_vectors

    def dispose(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        with self._lock:
            self._vectors.clear()

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("TurboVecEmbeddingIndex is disposed")

    @staticmethod
    def native_abi_version() -> int:
        """ABI version reported by the (here in-memory) index backend."""
        return 1


# ─────────────────────────────────────────────────────────────────────────────
# HnswEmbeddingStore — store backed by a TurboVecEmbeddingIndex.
# ─────────────────────────────────────────────────────────────────────────────


class HnswEmbeddingStore(ICircleEmbeddingStore):
    """:class:`ICircleEmbeddingStore` backed by a :class:`TurboVecEmbeddingIndex`.
    Add-only (call :meth:`remove_async` before re-adding an id)."""

    _DOCS_MAGIC = 0x53434847  # "HGCS" — Hnsw Generic Circle Store
    _DOCS_VERSION = 1
    _DEFAULT_BIT_WIDTH = 4

    def __init__(self, encoder: IEmbeddingEncoder, bit_width: int = _DEFAULT_BIT_WIDTH) -> None:
        if encoder is None:
            raise ValueError("encoder")
        if encoder.dimension <= 0 or encoder.dimension % 8 != 0:
            raise ValueError(
                f"Encoder dimension {encoder.dimension} must be > 0 and a multiple of 8 for turbovec."
            )
        self._encoder = encoder
        self._index = TurboVecEmbeddingIndex(encoder.dimension, bit_width)
        self._by_id: List[EmbeddingDocument] = []
        self._id_lookup: Dict[str, int] = {}
        self._lock = threading.Lock()
        self._disposed = False

    @property
    def dimension(self) -> int:
        return self._encoder.dimension

    @property
    def count(self) -> int:
        return len(self._by_id)

    async def add_async(
        self,
        document: EmbeddingDocument,
        vector: Optional[Sequence[float]] = None,
        ct: object = None,
    ) -> None:
        if document is None:
            raise ValueError("document")
        self._throw_if_disposed()
        if vector is None:
            vector = await self._encoder.encode_async(document.text, ct)
        if len(vector) != self.dimension:
            raise ValueError(
                f"Vector length {len(vector)} != store dimension {self.dimension}."
            )
        with self._lock:
            if document.id in self._id_lookup:
                raise RuntimeError(
                    f"Document id '{document.id}' already exists. Call RemoveAsync first."
                )
            internal_id = await self._index.add_async(vector, ct)
            self._by_id.append(document)
            self._id_lookup[document.id] = internal_id

    async def remove_async(self, id: str, ct: object = None) -> bool:
        if id is None or id.strip() == "":
            raise ValueError("id")
        self._throw_if_disposed()
        with self._lock:
            if id in self._id_lookup:
                del self._id_lookup[id]
                return True
            return False

    async def search_async(
        self, query, top_k: int = 5, ct: object = None
    ) -> List[EmbeddingSearchHit]:
        self._throw_if_disposed()
        if isinstance(query, str):
            if query == "":
                raise ValueError("query_text")
            query_vector = await self._encoder.encode_async(query, ct)
        else:
            query_vector = list(query)
        if len(query_vector) != self.dimension:
            raise ValueError(
                f"Query length {len(query_vector)} != store dimension {self.dimension}."
            )
        if top_k <= 0:
            raise ValueError("top_k")

        over_fetch = min(self._index.count, max(top_k * 2, top_k + 10))
        if over_fetch == 0:
            return []
        raw_hits = await self._index.search_async(query_vector, over_fetch, ct)
        if not raw_hits:
            return []

        results: List[EmbeddingSearchHit] = []
        for hit in raw_hits:
            if hit.internal_id < 0 or hit.internal_id >= len(self._by_id):
                continue
            doc = self._by_id[hit.internal_id]
            if doc.id not in self._id_lookup:
                continue  # removed
            results.append(EmbeddingSearchHit(doc, hit.score))
            if len(results) == top_k:
                break
        return results

    async def save_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        d = os.path.dirname(path)
        if d:
            os.makedirs(d, exist_ok=True)

        await self._index.save_async(path, ct)

        bw = _BinaryWriter()
        bw.write_int32(self._DOCS_MAGIC)
        bw.write_uint16(self._DOCS_VERSION)
        bw.write_int32(self.dimension)
        with self._lock:
            docs = list(self._by_id)
            lookup = dict(self._id_lookup)
        bw.write_int32(len(docs))
        for doc in docs:
            bw.write_string(doc.id)
            bw.write_string(doc.text)
            bw.write_bool(doc.id in lookup)  # live flag
            meta = doc.metadata
            bw.write_int32(len(meta) if meta is not None else 0)
            if meta is not None:
                for k, v in meta.items():
                    bw.write_string(k)
                    bw.write_string(v)

        docs_path = path + ".docs"
        tmp = docs_path + ".tmp"
        with open(tmp, "wb") as fh:
            fh.write(bw.getvalue())
        if os.path.exists(docs_path):
            os.remove(docs_path)
        os.replace(tmp, docs_path)

    async def load_async(self, path: str, ct: object = None) -> None:
        if path is None or path.strip() == "":
            raise ValueError("path")
        self._throw_if_disposed()
        docs_path = path + ".docs"
        if not os.path.isfile(path):
            raise FileNotFoundError(f"Index file not found: {path}")
        if not os.path.isfile(docs_path):
            raise FileNotFoundError(f"Docs sidecar not found: {docs_path}")

        await self._index.load_async(path, ct)

        with open(docs_path, "rb") as fh:
            br = _BinaryReader(fh.read())
        magic = br.read_int32()
        if magic != self._DOCS_MAGIC:
            raise ValueError("Not an HnswEmbeddingStore docs sidecar.")
        version = br.read_uint16()
        if version != self._DOCS_VERSION:
            raise ValueError(f"Unsupported docs version {version}.")
        file_dim = br.read_int32()
        if file_dim != self.dimension:
            raise ValueError(
                f"Dimension mismatch: store={self.dimension}, file={file_dim}."
            )
        count = br.read_int32()
        new_by_id: List[EmbeddingDocument] = []
        new_lookup: Dict[str, int] = {}
        for i in range(count):
            doc_id = br.read_string()
            text = br.read_string()
            live = br.read_bool()
            meta_count = br.read_int32()
            metadata: Optional[Dict[str, str]] = None
            if meta_count > 0:
                metadata = {}
                for _m in range(meta_count):
                    key = br.read_string()
                    metadata[key] = br.read_string()
            doc = EmbeddingDocument(doc_id, text, metadata)
            new_by_id.append(doc)
            if live:
                new_lookup[doc_id] = i
        with self._lock:
            self._by_id = new_by_id
            self._id_lookup = new_lookup

    async def dispose_async(self) -> None:
        if self._disposed:
            return
        self._disposed = True
        self._index.dispose()
        with self._lock:
            self._by_id.clear()
            self._id_lookup.clear()

    def _throw_if_disposed(self) -> None:
        if self._disposed:
            raise RuntimeError("HnswEmbeddingStore is disposed")
