# core/shard_kv_codec.py
#
# (3.3.0) Shard-style KV cache compression — byte-exact port of
# CircleAI.Core.Compression.ShardKvCodec.
#
# Compresses K via per-layer online PCA + Hadamard (Walsh-Hadamard)
# rotation quantised to int8, and V via product / nearest-codeword vector
# quantisation. A frame is self-describing: it carries the flattened PCA
# axes so a decoder can stand alone.
#
# Wire format (matches C# BinaryPrimitives, all little-endian):
#   CompressedK : bytes[kRank + 4]
#       [0..3]      = scale               (float32 LE)
#       [4 + r]     = int8 (as unsigned)  quantised projected component r
#   CompressedV : bytes[idxBytes]  where idxBytes = 1 if vCodewords<=256,
#                                         2 if <=65536, else 4
#       little-endian unsigned index of the nearest codeword
#
# Cross-language determinism:
#   • Every C# `float` site is narrowed through float32 (struct '<f').
#   • The default V codebook is seeded with .NET System.Random(seed).NextDouble()
#     reproduced exactly (Knuth subtractive generator) so two codecs built
#     with the same seed pick identical codeword indices and decode
#     byte-identically across languages.

from __future__ import annotations

import math
import struct
from dataclasses import dataclass, field
from typing import List, Sequence, Tuple


# ─────────────────────────────────────────────────────────────────────────────
# float32 helper — mirror C# `(float)` casts.
# ─────────────────────────────────────────────────────────────────────────────


def _f32(x: float) -> float:
    """Narrow a Python double to float32 precision (C# ``(float)``)."""
    return struct.unpack("<f", struct.pack("<f", x))[0]


# ─────────────────────────────────────────────────────────────────────────────
# .NET System.Random(seed) — Knuth subtractive generator.
#
# `ShardKvCodec.SeedCodebook` fills the default V codebook with
# `new Random(seed).NextDouble()`. To byte-match, reproduce the exact
# legacy algorithm .NET uses for a seeded Random (unchanged from .NET
# Framework through modern .NET for the *seeded* constructor):
#
#   MBIG   = int.MaxValue (2147483647)
#   MSEED  = 161803398
#   seedArray[55] built from a subtractive fill, then three warm-up passes.
#   InternalSample() returns an int in [0, MBIG); Sample() = that / MBIG.
# ─────────────────────────────────────────────────────────────────────────────


class _DotNetRandom:
    """Deterministic reproduction of .NET ``System.Random(int seed)``.

    Only the members ShardKvCodec needs are implemented: construction with a
    32-bit seed and :meth:`next_double` (``NextDouble()``). The integer math
    matches .NET's ``int`` semantics exactly (32-bit signed wraparound).
    """

    _MBIG = 2147483647   # int.MaxValue
    _MSEED = 161803398
    _INT32_MIN = -2147483648
    _INT32_MAX = 2147483647

    __slots__ = ("_seed_array", "_inext", "_inextp")

    def __init__(self, seed: int) -> None:
        seed_array = [0] * 56
        # C#: Subtract = (Seed == int.MinValue) ? int.MaxValue : Math.Abs(Seed)
        if seed == self._INT32_MIN:
            subtract = self._INT32_MAX
        else:
            subtract = abs(self._to_int32(seed))

        mj = self._MSEED - subtract
        seed_array[55] = mj
        mk = 1
        for i in range(1, 55):
            ii = (21 * i) % 55
            seed_array[ii] = mk
            mk = self._sub_i32(mj, mk)
            if mk < 0:
                mk = self._add_i32(mk, self._MBIG)
            mj = seed_array[ii]
        for k in range(1, 5):
            for i in range(1, 56):
                seed_array[i] = self._sub_i32(seed_array[i], seed_array[1 + (i + 30) % 55])
                if seed_array[i] < 0:
                    seed_array[i] = self._add_i32(seed_array[i], self._MBIG)
        self._seed_array = seed_array
        self._inext = 0
        self._inextp = 21

    # -- 32-bit signed int helpers (C# `int` arithmetic wraps mod 2^32) -----

    @staticmethod
    def _to_int32(v: int) -> int:
        v &= 0xFFFFFFFF
        return v - 0x100000000 if v >= 0x80000000 else v

    def _add_i32(self, a: int, b: int) -> int:
        return self._to_int32(a + b)

    def _sub_i32(self, a: int, b: int) -> int:
        return self._to_int32(a - b)

    def _internal_sample(self) -> int:
        inext = self._inext + 1
        if inext >= 56:
            inext = 1
        inextp = self._inextp + 1
        if inextp >= 56:
            inextp = 1

        ret_val = self._sub_i32(self._seed_array[inext], self._seed_array[inextp])
        if ret_val == self._MBIG:
            ret_val -= 1
        if ret_val < 0:
            ret_val = self._add_i32(ret_val, self._MBIG)

        self._seed_array[inext] = ret_val
        self._inext = inext
        self._inextp = inextp
        return ret_val

    def _sample(self) -> float:
        # C#: InternalSample() * (1.0 / MBIG)
        return self._internal_sample() * (1.0 / self._MBIG)

    def next_double(self) -> float:
        """``System.Random.NextDouble()`` — a double in [0.0, 1.0)."""
        return self._sample()


# ─────────────────────────────────────────────────────────────────────────────
# ShardCompressedFrame — CircleAI.Core.Compression.ShardCompressedFrame
# ─────────────────────────────────────────────────────────────────────────────


@dataclass(frozen=True, slots=True)
class ShardCompressedFrame:
    """(3.3.0) Encoded shard KV pair (compressed K + compressed V)."""

    compressed_k: bytes
    compressed_v: bytes
    k_principal_axes: Tuple[float, ...]
    k_original_dim: int
    v_original_dim: int


# ─────────────────────────────────────────────────────────────────────────────
# ShardKvCodec — CircleAI.Core.Compression.ShardKvCodec
# ─────────────────────────────────────────────────────────────────────────────


class ShardKvCodec:
    """(3.3.0) Online-PCA-on-K + VQ-on-V KV compressor.

    Stateless across frames — the host re-trains the PCA basis with
    :meth:`observe_k` when desired, and uses the current basis to encode
    subsequent frames.
    """

    __slots__ = (
        "_k_dim",
        "_k_rank",
        "_v_dim",
        "_v_codewords",
        "_v_codebook",
        "_hadamard_scratch",
        "_k_center",
        "_k_axes",
        "_samples_observed",
    )

    def __init__(
        self,
        k_dim: int,
        k_rank: int,
        v_dim: int,
        v_codewords: int,
        v_codebook_seed: int = 0,
    ) -> None:
        """(3.3.0)

        :param k_dim: K-vector dimensionality (e.g. 128 for a typical head).
        :param k_rank: number of principal components to keep on K (e.g. 32).
        :param v_dim: V-vector dimensionality.
        :param v_codewords: number of VQ codewords for V (must be a power of 2).
        :param v_codebook_seed: seed for the deterministic initial codebook.
        """
        if k_dim <= 0:
            raise ValueError("k_dim")
        if k_rank <= 0 or k_rank > k_dim:
            raise ValueError("k_rank")
        if v_dim <= 0:
            raise ValueError("v_dim")
        if v_codewords <= 1 or (v_codewords & (v_codewords - 1)) != 0:
            raise ValueError("Codeword count must be a power of two greater than 1.")

        self._k_dim = k_dim
        self._k_rank = k_rank
        self._v_dim = v_dim
        self._v_codewords = v_codewords
        self._k_center: List[float] = [0.0] * k_dim
        # _k_axes[r][i] — row-major (kRank, kDim).
        self._k_axes: List[List[float]] = [[0.0] * k_dim for _ in range(k_rank)]
        self._v_codebook = self._seed_codebook(v_dim, v_codewords, v_codebook_seed)
        self._hadamard_scratch: List[float] = [0.0] * self._pow2_ceil(k_dim)
        self._samples_observed = 0

        # Initialise PCA axes to identity-top-rank for sane defaults.
        for r in range(k_rank):
            self._k_axes[r][r] = 1.0

    # ── properties ──────────────────────────────────────────────────────────

    @property
    def samples_observed(self) -> int:
        """(3.3.0) Number of K samples used to update the PCA centre."""
        return self._samples_observed

    # ── training ────────────────────────────────────────────────────────────

    def observe_k(self, k: Sequence[float]) -> None:
        """(3.3.0) Update the online K mean estimate with this sample."""
        if len(k) != self._k_dim:
            raise ValueError("Input dim mismatch")
        self._samples_observed += 1
        n = self._samples_observed
        center = self._k_center
        for i in range(self._k_dim):
            # Running mean — narrow each step through float32 to match C# float.
            center[i] = _f32(center[i] + _f32((_f32(k[i]) - center[i]) / n))

    def set_principal_axes(self, axes: Sequence[Sequence[float]]) -> None:
        """(3.3.0) Replace the current PCA axes with *axes* (shape kRank×kDim)."""
        if len(axes) != self._k_rank or any(len(row) != self._k_dim for row in axes):
            raise ValueError("Axes shape must be (kRank, kDim).")
        for r in range(self._k_rank):
            row = axes[r]
            dst = self._k_axes[r]
            for i in range(self._k_dim):
                dst[i] = _f32(row[i])

    def set_v_codebook(self, codebook: Sequence[Sequence[float]]) -> None:
        """(3.3.0) Replace the V codebook with *codebook*."""
        if len(codebook) != self._v_codewords:
            raise ValueError("Codebook size mismatch.")
        for i in range(len(codebook)):
            if len(codebook[i]) != self._v_dim:
                raise ValueError("Codeword dim mismatch.")
            dst = self._v_codebook[i]
            src = codebook[i]
            for j in range(self._v_dim):
                dst[j] = _f32(src[j])

    # ── encode ──────────────────────────────────────────────────────────────

    def encode(self, k: Sequence[float], v: Sequence[float]) -> ShardCompressedFrame:
        """(3.3.0) Encode one (K, V) pair."""
        if len(k) != self._k_dim:
            raise ValueError("K dim mismatch")
        if len(v) != self._v_dim:
            raise ValueError("V dim mismatch")

        # K: centre -> Hadamard -> project to top-rank axes -> quantise to int8.
        centred = [_f32(_f32(k[i]) - self._k_center[i]) for i in range(self._k_dim)]
        self._apply_hadamard_in_place(centred)

        projected = [0.0] * self._k_rank
        for r in range(self._k_rank):
            dot = 0.0
            axr = self._k_axes[r]
            for i in range(self._k_dim):
                dot = _f32(dot + _f32(centred[i] * axr[i]))
            projected[r] = dot

        # Scale that fits all components into int8 dynamic range.
        max_abs = _f32(1e-9)
        for r in range(self._k_rank):
            a = abs(projected[r])
            if a > max_abs:
                max_abs = a
        scale = _f32(max_abs / _f32(127.0))

        encoded_k = bytearray(self._k_rank + 4)  # +4 for float32 LE scale
        struct.pack_into("<f", encoded_k, 0, scale)
        for r in range(self._k_rank):
            q = self._round_half_away(projected[r] / scale)
            if q < -127:
                q = -127
            elif q > 127:
                q = 127
            encoded_k[4 + r] = q & 0xFF  # (byte)((sbyte)q)

        # V: nearest-codeword VQ.
        best_idx = 0
        best_dist = math.inf
        for c in range(self._v_codewords):
            word = self._v_codebook[c]
            d = 0.0
            for i in range(self._v_dim):
                diff = _f32(v[i] - word[i])
                d = _f32(d + _f32(diff * diff))
            if d < best_dist:
                best_dist = d
                best_idx = c

        idx_bytes = 1 if self._v_codewords <= 256 else (2 if self._v_codewords <= 65536 else 4)
        encoded_v = bytearray(idx_bytes)
        if idx_bytes == 1:
            encoded_v[0] = best_idx & 0xFF
        elif idx_bytes == 2:
            struct.pack_into("<H", encoded_v, 0, best_idx & 0xFFFF)
        else:
            struct.pack_into("<I", encoded_v, 0, best_idx & 0xFFFFFFFF)

        # Materialise PCA axes so the decoder can stand alone.
        axes_flat = [0.0] * (self._k_rank * self._k_dim)
        for r in range(self._k_rank):
            base = r * self._k_dim
            axr = self._k_axes[r]
            for i in range(self._k_dim):
                axes_flat[base + i] = axr[i]

        return ShardCompressedFrame(
            bytes(encoded_k),
            bytes(encoded_v),
            tuple(axes_flat),
            self._k_dim,
            self._v_dim,
        )

    # ── decode ──────────────────────────────────────────────────────────────

    def decode(self, frame: ShardCompressedFrame) -> Tuple[List[float], List[float]]:
        """(3.3.0) Decode a frame back to approximate K and V."""
        if frame is None:
            raise ValueError("frame")
        if frame.k_original_dim != self._k_dim:
            raise ValueError("Codec K-dim does not match frame.")
        if frame.v_original_dim != self._v_dim:
            raise ValueError("Codec V-dim does not match frame.")

        # K decode: int8 + scale -> projected -> un-rotate -> un-Hadamard -> recenter.
        scale = struct.unpack_from("<f", frame.compressed_k, 0)[0]
        projected = [0.0] * self._k_rank
        for r in range(self._k_rank):
            projected[r] = _f32(self._as_sbyte(frame.compressed_k[4 + r]) * scale)

        k = [0.0] * self._k_dim
        axes = frame.k_principal_axes
        for i in range(self._k_dim):
            acc = 0.0
            for r in range(self._k_rank):
                acc = _f32(acc + _f32(projected[r] * axes[r * self._k_dim + i]))
            k[i] = acc
        self._apply_hadamard_in_place(k)  # Hadamard is self-inverse up to 1/n.
        for i in range(self._k_dim):
            k[i] = _f32(_f32(k[i] / self._k_dim) + self._k_center[i])

        # V decode: read index, copy codeword.
        idx_bytes = 1 if self._v_codewords <= 256 else (2 if self._v_codewords <= 65536 else 4)
        if idx_bytes == 1:
            idx = frame.compressed_v[0]
        elif idx_bytes == 2:
            idx = struct.unpack_from("<H", frame.compressed_v, 0)[0]
        else:
            idx = struct.unpack_from("<I", frame.compressed_v, 0)[0]

        v = list(self._v_codebook[idx])
        return (k, v)

    # ── internals ─────────────────────────────────────────────────────────────

    def _apply_hadamard_in_place(self, buffer: List[float]) -> None:
        # Fast Walsh-Hadamard transform on the next-power-of-two scratch.
        n = len(self._hadamard_scratch)
        scratch = self._hadamard_scratch
        for i in range(n):
            scratch[i] = 0.0
        m = min(len(buffer), n)
        for i in range(m):
            scratch[i] = buffer[i]

        h = 1
        while h < n:
            step = h * 2
            for i in range(0, n, step):
                for j in range(i, i + h):
                    x = scratch[j]
                    y = scratch[j + h]
                    scratch[j] = _f32(x + y)
                    scratch[j + h] = _f32(x - y)
            h <<= 1

        for i in range(m):
            buffer[i] = scratch[i]

    @staticmethod
    def _pow2_ceil(v: int) -> int:
        p = 1
        while p < v:
            p <<= 1
        return p

    @staticmethod
    def _as_sbyte(b: int) -> int:
        """Interpret an unsigned byte as a C# signed sbyte."""
        return b - 256 if b >= 128 else b

    @staticmethod
    def _round_half_away(x: float) -> int:
        """C# Math.Round default — banker's? No: Math.Round(double) with no
        MidpointRounding uses ToEven. But (int)Math.Round(...) here casts the
        double result; C#'s Math.Round(double) is banker's rounding (ToEven).
        """
        return _round_to_even(x)

    @staticmethod
    def _seed_codebook(dim: int, count: int, seed: int) -> List[List[float]]:
        rng = _DotNetRandom(seed)
        cb: List[List[float]] = []
        for _ in range(count):
            word = [0.0] * dim
            for i in range(dim):
                # (float)(rng.NextDouble() * 2.0 - 1.0) — uniform [-1, 1].
                word[i] = _f32(rng.next_double() * 2.0 - 1.0)
            cb.append(word)
        return cb


def _round_to_even(x: float) -> int:
    """Reproduce C# ``Math.Round(double)`` (MidpointRounding.ToEven) then the
    ``(int)`` cast. Python's built-in ``round`` is already banker's rounding
    and returns an int, matching the .NET semantics for the values in range.
    """
    return int(round(x))
