# memory/compression.py
#
# TurboQuant embedding compression + the compressed store decorators.
#
# Ported EXACTLY from the C# reference so a payload encoded by any language in
# the SDK decodes byte-identically in every other:
#   • CircleAI.Core.Compression.BitPacker
#   • CircleAI.Core.Compression.OrthogonalRotation (+ SeededGaussian)
#   • CircleAI.Core.Compression.BetaLloydMaxCodebook
#   • CircleAI.Core.Compression.TurboQuantCodec (+ TurboQuantPayload)
#   • CircleAI.Memory.Compression.EmbeddingPayloadCodec
#   • CircleAI.Memory.Compression.CompressedEpisodicMemoryStore
#   • CircleAI.Memory.Compression.CompressedMultimodalMemoryStore
# Mirrors the verified TypeScript reference (memory/compression.ts).
#
# TurboQuant is Google Research's data-oblivious vector quantizer
# (arxiv:2504.19874). Per-vector: norm -> unit-normalise -> fixed orthogonal
# rotation -> per-coordinate Lloyd-Max quantise (codebook optimal for the
# Beta((d-1)/2,(d-1)/2) coordinate distribution of a rotated unit vector) ->
# bit-pack. Decode reverses it.
#
# Numeric fidelity notes (why this round-trips bit-for-bit with C#):
#   • The SplitMix64 PRNG state is `ulong`; Python ints are unbounded, so the
#     state math runs masked to 64 bits (& 0xFFFFFFFFFFFFFFFF).
#   • Every place C# stores a `float` (norm, matrix cells, centroids, deltas)
#     we narrow through float32 via struct pack/unpack so the FP32 rounding
#     matches — exactly what the TS port does with Math.fround.
#   • The wire format writes float32 little-endian via struct.pack('<f', …),
#     same as BinaryPrimitives.WriteSingleLittleEndian.

from __future__ import annotations

import base64
import math
import struct
from datetime import datetime
from typing import Optional, Sequence

from .episodic_memory import EpisodicMemoryEntry
from .multimodal import MultimodalMemoryEntry
from .stores import IEpisodicMemoryStore
from .multimodal import IMultimodalMemoryStore


# ─────────────────────────────────────────────────────────────────────────────
# float32 helpers — mirror C# `(float)` casts / TS Math.fround
# ─────────────────────────────────────────────────────────────────────────────

_U64_MASK = 0xFFFFFFFFFFFFFFFF


# PRECOMPILED, because this is the hottest function in the module.
#
# `struct.pack("<f", x)` re-parses the format string on every call. That is
# invisible at normal call rates and is not invisible here: _f32 sits inside the
# O(n^3) inner loop of the Gram-Schmidt that builds the rotation matrix, so
# encoding one 1536-dim vector calls it on the order of a billion times. A
# struct.Struct built once is 2.1x faster for byte-identical results (measured,
# 200k calls: 0.025 s -> 0.012 s).
_F32 = struct.Struct("<f")
_F32_PACK = _F32.pack
_F32_UNPACK = _F32.unpack


def _f32(x: float) -> float:
    """Narrow a Python double to float32 precision (C# `(float)` / TS Math.fround)."""
    return _F32_UNPACK(_F32_PACK(x))[0]


# ─────────────────────────────────────────────────────────────────────────────
# BitPacker — CircleAI.Core.Compression.BitPacker
# ─────────────────────────────────────────────────────────────────────────────


def _validate_width(bits_per_index: int) -> None:
    if not isinstance(bits_per_index, int) or bits_per_index < 1 or bits_per_index > 16:
        raise ValueError("Bits per index must be 1..16.")


class BitPacker:
    """Bit-packing primitives for arbitrary widths (1..16 bits/index)."""

    @staticmethod
    def pack(indices: Sequence[int], bits_per_index: int) -> bytes:
        """Pack *indices* at *bits_per_index* into a new byte array.

        Indices are written least-significant-bit first.
        """
        _validate_width(bits_per_index)
        total_bits = len(indices) * bits_per_index
        packed = bytearray((total_bits + 7) >> 3)

        bit_pos = 0
        for i in range(len(indices)):
            value = indices[i] & 0xFFFFFFFF
            if bits_per_index < 16 and value >= (1 << bits_per_index):
                raise ValueError(
                    f"Index {value} at position {i} exceeds {bits_per_index}-bit range."
                )

            remaining = bits_per_index
            byte_idx = bit_pos >> 3
            bit_offset = bit_pos & 7

            while remaining > 0:
                take = min(remaining, 8 - bit_offset)
                shift = bits_per_index - remaining
                chunk = (value >> shift) & ((1 << take) - 1)
                packed[byte_idx] |= (chunk << bit_offset) & 0xFF

                remaining -= take
                bit_offset = 0
                byte_idx += 1
            bit_pos += bits_per_index
        return bytes(packed)

    @staticmethod
    def unpack(packed: bytes, count: int, bits_per_index: int) -> list[int]:
        """Unpack *count* indices of *bits_per_index* each from *packed*."""
        _validate_width(bits_per_index)
        required_bytes = (count * bits_per_index + 7) >> 3
        if len(packed) < required_bytes:
            raise ValueError(
                f"Packed buffer too small: need {required_bytes} bytes, got {len(packed)}."
            )

        result = [0] * count
        bit_pos = 0
        for i in range(count):
            remaining = bits_per_index
            byte_idx = bit_pos >> 3
            bit_offset = bit_pos & 7
            value = 0

            while remaining > 0:
                take = min(remaining, 8 - bit_offset)
                shift = bits_per_index - remaining
                chunk = (packed[byte_idx] >> bit_offset) & ((1 << take) - 1)
                value |= chunk << shift

                remaining -= take
                bit_offset = 0
                byte_idx += 1
            result[i] = value & 0xFFFF
            bit_pos += bits_per_index
        return result


# ─────────────────────────────────────────────────────────────────────────────
# SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)
# ─────────────────────────────────────────────────────────────────────────────

_SPLITMIX_GAMMA = 0x9E3779B97F4A7C15
_SPLITMIX_M1 = 0xBF58476D1CE4E5B9
_SPLITMIX_M2 = 0x94D049BB133111EB
_TWO_POW_53 = float(1 << 53)


class _SeededGaussian:
    """Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.

    Hand-rolled (not `random`) so output is reproducible across platforms and
    byte-identical with the C# `SeededGaussian`.
    """

    def __init__(self, seed: int) -> None:
        self._state = 0xDEADBEEFCAFEBABE if seed == 0 else (seed & _U64_MASK)
        self._has_spare = False
        self._spare = 0.0

    def sample(self) -> float:
        if self._has_spare:
            self._has_spare = False
            return self._spare

        # Two uniforms in (0, 1].
        u = self._next_uniform()
        while u <= 1e-300:
            u = self._next_uniform()
        v = self._next_uniform()
        magnitude = math.sqrt(-2.0 * math.log(u))
        angle = 2.0 * math.pi * v
        self._spare = magnitude * math.sin(angle)
        self._has_spare = True
        return magnitude * math.cos(angle)

    def _next_uniform(self) -> float:
        # SplitMix64 step (all arithmetic masked to 64 unsigned bits).
        self._state = (self._state + _SPLITMIX_GAMMA) & _U64_MASK
        z = self._state
        z = ((z ^ (z >> 30)) * _SPLITMIX_M1) & _U64_MASK
        z = ((z ^ (z >> 27)) * _SPLITMIX_M2) & _U64_MASK
        z = (z ^ (z >> 31)) & _U64_MASK
        # Convert top 53 bits to a double in [0, 1).
        return (z >> 11) * (1.0 / _TWO_POW_53)


# ─────────────────────────────────────────────────────────────────────────────
# OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation
# ─────────────────────────────────────────────────────────────────────────────

_ROTATION_SEED = 0xC1C1EA10C1C1EA10


class OrthogonalRotation:
    """Deterministic random orthogonal rotation matrix for a given dimension.

    Constructed via QR (modified Gram-Schmidt) of a seeded Gaussian matrix, then
    sign-corrected. Cached per dimension (construction is O(d^3)). The matrix is
    stored row-major as a flat list of float32 values (length dim*dim).
    """

    ROTATION_SEED = _ROTATION_SEED
    _cache: dict[int, list[float]] = {}

    @classmethod
    def get_matrix(cls, dim: int) -> list[float]:
        """Return the dim×dim orthogonal matrix in row-major layout (length dim*dim).

        Cached after the first call for a given dimension.
        """
        if dim <= 0:
            raise ValueError("dim must be positive")
        m = cls._cache.get(dim)
        if m is None:
            m = _build_matrix(dim)
            cls._cache[dim] = m
        return m

    @classmethod
    def rotate(cls, dim: int, vector: Sequence[float], output: list[float]) -> None:
        """output[i] = Σ R[i,j] * vector[j]."""
        if len(vector) != dim:
            raise ValueError("vector length must equal dim.")
        if len(output) != dim:
            raise ValueError("output length must equal dim.")
        matrix = cls.get_matrix(dim)
        for i in range(dim):
            s = 0.0
            row_start = i * dim
            for j in range(dim):
                s = _f32(s + matrix[row_start + j] * vector[j])
            output[i] = s

    @classmethod
    def unrotate(cls, dim: int, vector: Sequence[float], output: list[float]) -> None:
        """Inverse rotation — multiplies the TRANSPOSE of the rotation matrix by *vector*.

        The transpose of an orthogonal matrix is its inverse.
        """
        if len(vector) != dim:
            raise ValueError("vector length must equal dim.")
        if len(output) != dim:
            raise ValueError("output length must equal dim.")
        matrix = cls.get_matrix(dim)
        for i in range(dim):
            s = 0.0
            for j in range(dim):
                s = _f32(s + matrix[j * dim + i] * vector[j])
            output[i] = s


def _build_matrix(dim: int) -> list[float]:
    # 1. Generate a seeded Gaussian matrix G (dim × dim) in float64.
    gauss = [0.0] * (dim * dim)
    rng = _SeededGaussian(_ROTATION_SEED)
    for i in range(len(gauss)):
        gauss[i] = rng.sample()

    # 2. QR decomposition via modified Gram-Schmidt.
    q = _modified_gram_schmidt(gauss, dim)

    # 3. Sign-correct columns so Q is deterministic.
    _sign_correct_columns(q, dim)

    # 4. Convert to row-major float32.
    return [_f32(v) for v in q]


def _modified_gram_schmidt(g: list[float], dim: int) -> list[float]:
    """Modified Gram-Schmidt QR.

    Returns Q (orthonormal columns) in row-major flat layout (float64). The
    input *g* is not reused after this call.
    """
    # WORKS IN COLUMNS, THEN TRANSPOSES ONCE.
    #
    # This is O(dim^3) and it dominates everything: profiled at dim=1536 it is
    # 386 s of a 393 s encode — 98%, in ONE call, all of it inline arithmetic.
    # The C# reference does the same work about fifty times faster, which is why
    # the cost is invisible there and unmissable here.
    #
    # The algorithm is unchanged. What changed is how the data is reached. The
    # obvious transcription indexes `q[i * dim + j]`, walking a COLUMN down a
    # ROW-MAJOR array: an index multiply and add per element, every access a
    # cache miss, and the whole inner loop interpreted. Holding each column as
    # its own list instead makes the two hot loops zip/sum over contiguous
    # sequences, which CPython runs at C speed.
    #
    # NOT BIT-IDENTICAL IN float64, AND THAT WAS CHECKED RATHER THAN ASSUMED.
    # Measured against the previous implementation at dim 8: 41 of 64 elements
    # differ, worst case 5.0e-16 — last-ULP rounding from `sum()` accumulating a
    # generator instead of an explicit `+=`, not a change of algorithm. Every
    # element is IDENTICAL once narrowed to float32, which is the precision this
    # matrix is consumed at (`rotate` narrows every accumulation through _f32),
    # and the fp32-exact parity tests against the C# reference still pass.
    #
    # `math.fsum` would NOT be a safe substitute here — it is more accurate, and
    # therefore different in a way float32 would not absorb.
    cols: list[list[float]] = []

    for j in range(dim):
        # Column j of g.
        col_j = g[j::dim]

        # Subtract projections onto already-processed columns.
        for col_k in cols:
            dot = sum(a * b for a, b in zip(col_j, col_k))
            col_j = [a - dot * b for a, b in zip(col_j, col_k)]

        # Normalise column j.
        norm = math.sqrt(sum(a * a for a in col_j))
        if norm < 1e-15:
            raise RuntimeError(
                f"Gram-Schmidt produced a near-zero column at j={j} (dim={dim}). "
                "This is statistically impossible for a Gaussian matrix; check the RNG seed."
            )
        inv = 1.0 / norm
        cols.append([a * inv for a in col_j])

    # Back to row-major, the layout every caller expects.
    q = [0.0] * (dim * dim)
    for j, col in enumerate(cols):
        q[j::dim] = col
    return q


def _sign_correct_columns(q: list[float], dim: int) -> None:
    for j in range(dim):
        # Diagonal-based sign convention: ensure q[j,j] >= 0.
        diag = q[j * dim + j]
        if diag < 0.0:
            for i in range(dim):
                q[i * dim + j] = -q[i * dim + j]


# ─────────────────────────────────────────────────────────────────────────────
# BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook
# ─────────────────────────────────────────────────────────────────────────────


class BetaCodebook:
    """A Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1].

    ``boundaries`` has length 2^bits-1; ``centroids`` has length 2^bits. Both
    are float32-narrowed lists.
    """

    __slots__ = ("boundaries", "centroids")

    def __init__(self, boundaries: list[float], centroids: list[float]) -> None:
        self.boundaries = boundaries
        self.centroids = centroids


_codebook_cache: dict[tuple[int, int], BetaCodebook] = {}


class BetaLloydMaxCodebook:
    """Computes / caches Lloyd-Max codebooks for Beta((d-1)/2,(d-1)/2)."""

    @staticmethod
    def get(bits: int, dim: int) -> BetaCodebook:
        """Return the codebook for the given bit width and dimension.

        Computed on first request; cached by (bits, dim).
        """
        if bits < 1 or bits > 8:
            raise ValueError("bits must be in 1..8.")
        if dim <= 1:
            raise ValueError("dim must be > 1.")
        key = (bits, dim)
        cb = _codebook_cache.get(key)
        if cb is None:
            cb = _compute_codebook(bits, dim)
            _codebook_cache[key] = cb
        return cb

    @staticmethod
    def bin_for(value: float, boundaries: Sequence[float]) -> int:
        """Return the bin index for *value* against *boundaries* (linear scan)."""
        for i in range(len(boundaries)):
            if value < boundaries[i]:
                return i
        return len(boundaries)


def _compute_codebook(
    bits: int, dim: int, max_iter: int = 200, tol: float = 1e-12
) -> BetaCodebook:
    a = (dim - 1.0) / 2.0
    n_levels = 1 << bits

    # Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
    std = math.sqrt((2.0 * a) / ((2.0 * a + 1.0) * 4.0 * a))
    spread = 3.0 * std
    centroids = [0.0] * n_levels
    for i in range(n_levels):
        centroids[i] = -spread + (2.0 * spread * i) / (n_levels - 1)

    for _ in range(max_iter):
        # Boundaries = midpoints between adjacent centroids.
        boundaries = [0.0] * (n_levels - 1)
        for i in range(n_levels - 1):
            boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0

        edges = [0.0] * (n_levels + 1)
        edges[0] = -1.0
        for i in range(len(boundaries)):
            edges[i + 1] = boundaries[i]
        edges[n_levels] = 1.0

        new_centroids = [0.0] * n_levels
        for i in range(n_levels):
            lo = edges[i]
            hi = edges[i + 1]
            cdf_lo = _beta_cdf_symmetric(a, (lo + 1.0) / 2.0)
            cdf_hi = _beta_cdf_symmetric(a, (hi + 1.0) / 2.0)
            prob = cdf_hi - cdf_lo

            if prob < 1e-15:
                new_centroids[i] = centroids[i]
            else:
                mean = _adaptive_simpson(
                    lambda x: (x * _beta_pdf_symmetric(a, (x + 1.0) / 2.0)) / 2.0,
                    lo,
                    hi,
                    1e-14,
                    50,
                )
                new_centroids[i] = mean / prob

        # CONVERGE AT THE PRECISION WE ACTUALLY STORE.
        #
        # The centroids go through _f32() a dozen lines below, and float32
        # resolves about 1e-8 near these magnitudes. The double-precision `tol`
        # of 1e-12 is therefore chasing four orders of magnitude that the very
        # next statement discards — and it is not merely wasteful, it is
        # UNREACHABLE: measured across bits x dim, EVERY 4-bit codebook ran all
        # 200 iterations without ever meeting it, and (2 bits, dim 64) did too.
        # Nothing reported that; the loop just silently spent 200 iterations.
        #
        # Stopping when the float32 projection stops moving is the same answer
        # for less work: verified BIT-IDENTICAL float32 output against the old
        # 1e-12 loop for bits 2 and 4 across dim 4, 8, 16, 64, 128 and 256.
        # Lloyd-Max is a descent iteration, so once the stored representation is
        # stable it stays stable.
        #
        # This is also why the port was slow where C# was not: identical work,
        # but a Python interpreter doing it — 36 s for (2 bits, dim 4) against
        # 965 ms in C#. The old absolute test is kept; whichever fires first.
        stable_in_storage = all(
            _f32(centroids[i]) == _f32(new_centroids[i]) for i in range(n_levels)
        )

        max_change = 0.0
        for i in range(n_levels):
            max_change = max(max_change, abs(centroids[i] - new_centroids[i]))
        centroids = new_centroids

        if stable_in_storage or max_change < tol:
            break

    final_boundaries = [
        _f32((centroids[i] + centroids[i + 1]) / 2.0) for i in range(n_levels - 1)
    ]
    final_centroids = [_f32(centroids[i]) for i in range(n_levels)]
    return BetaCodebook(final_boundaries, final_centroids)


# ── Beta(a, a) PDF / CDF on [0, 1] ─────────────────────────────────────────
# The "symmetric" suffix is a reminder that we always use shape Beta(a, a).


def _beta_pdf_symmetric(a: float, x: float) -> float:
    if x <= 0.0 or x >= 1.0:
        return 0.0
    # f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
    log_pdf = (
        (a - 1.0) * math.log(x)
        + (a - 1.0) * math.log(1.0 - x)
        - _log_beta(a, a)
    )
    return math.exp(log_pdf)


def _beta_cdf_symmetric(a: float, x: float) -> float:
    if x <= 0.0:
        return 0.0
    if x >= 1.0:
        return 1.0
    return _regularized_incomplete_beta(a, a, x)


def _log_beta(a: float, b: float) -> float:
    return _log_gamma(a) + _log_gamma(b) - _log_gamma(a + b)


# Lanczos coefficients for g = 7.
_LANCZOS_G7 = (
    0.99999999999980993,
    676.5203681218851,
    -1259.1392167224028,
    771.32342877765313,
    -176.61502916214059,
    12.507343278686905,
    -0.13857109526572012,
    9.9843695780195716e-6,
    1.5056327351493116e-7,
)


def _log_gamma(x: float) -> float:
    """log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9)."""
    if x < 0.5:
        # Reflection: Γ(x)Γ(1-x) = π/sin(πx)
        return math.log(math.pi / math.sin(math.pi * x)) - _log_gamma(1.0 - x)
    x -= 1.0
    t = x + 7.5
    s = _LANCZOS_G7[0]
    for i in range(1, len(_LANCZOS_G7)):
        s += _LANCZOS_G7[i] / (x + i)
    return 0.5 * math.log(2.0 * math.pi) + (x + 0.5) * math.log(t) - t + math.log(s)


def _regularized_incomplete_beta(a: float, b: float, x: float) -> float:
    """Regularised incomplete beta function I_x(a, b) (Numerical Recipes 6.4)."""
    if x < 0.0 or x > 1.0:
        raise ValueError("x must be in [0, 1].")
    if x == 0.0 or x == 1.0:
        return x

    bt = math.exp(
        _log_gamma(a + b)
        - _log_gamma(a)
        - _log_gamma(b)
        + a * math.log(x)
        + b * math.log(1.0 - x)
    )
    if x < (a + 1.0) / (a + b + 2.0):
        return (bt * _beta_continued_fraction(a, b, x)) / a
    return 1.0 - (bt * _beta_continued_fraction(b, a, 1.0 - x)) / b


def _beta_continued_fraction(a: float, b: float, x: float) -> float:
    max_iter = 200
    eps = 3e-15
    fpmin = 1e-300

    qab = a + b
    qap = a + 1.0
    qam = a - 1.0
    c = 1.0
    d = 1.0 - (qab * x) / qap
    if abs(d) < fpmin:
        d = fpmin
    d = 1.0 / d
    h = d

    for m in range(1, max_iter + 1):
        m2 = 2 * m
        aa = (m * (b - m) * x) / ((qam + m2) * (a + m2))
        d = 1.0 + aa * d
        if abs(d) < fpmin:
            d = fpmin
        c = 1.0 + aa / c
        if abs(c) < fpmin:
            c = fpmin
        d = 1.0 / d
        h *= d * c

        aa = (-(a + m) * (qab + m) * x) / ((a + m2) * (qap + m2))
        d = 1.0 + aa * d
        if abs(d) < fpmin:
            d = fpmin
        c = 1.0 + aa / c
        if abs(c) < fpmin:
            c = fpmin
        d = 1.0 / d
        delta = d * c
        h *= delta
        if abs(delta - 1.0) < eps:
            return h
    return h  # best effort if no convergence


# ── Adaptive Simpson integration ───────────────────────────────────────────


def _adaptive_simpson(f, a: float, b: float, tol: float, max_depth: int) -> float:
    mid = (a + b) / 2.0
    fa = f(a)
    fb = f(b)
    fm = f(mid)
    whole = ((b - a) / 6.0) * (fa + 4.0 * fm + fb)
    return _adaptive_simpson_rec(f, a, b, fa, fb, fm, whole, tol, max_depth)


def _adaptive_simpson_rec(
    f,
    a: float,
    b: float,
    fa: float,
    fb: float,
    fm: float,
    whole: float,
    tol: float,
    depth: int,
) -> float:
    mid = (a + b) / 2.0
    m1 = (a + mid) / 2.0
    m2 = (mid + b) / 2.0
    fm1 = f(m1)
    fm2 = f(m2)
    left = ((mid - a) / 6.0) * (fa + 4.0 * fm1 + fm)
    right = ((b - mid) / 6.0) * (fm + 4.0 * fm2 + fb)
    refined = left + right

    if depth == 0 or abs(refined - whole) < 15.0 * tol:
        return refined + (refined - whole) / 15.0
    return _adaptive_simpson_rec(
        f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1
    ) + _adaptive_simpson_rec(
        f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1
    )


# ─────────────────────────────────────────────────────────────────────────────
# TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec
# ─────────────────────────────────────────────────────────────────────────────


class TurboQuantPayload:
    """Output of :meth:`TurboQuantCodec.encode`.

    - ``norm``: L2 norm of the original vector (float32) — needed to reconstruct
      magnitude.
    - ``packed_indices``: bit-packed Lloyd-Max bin indices, one per dimension.
    """

    __slots__ = ("norm", "packed_indices")

    def __init__(self, norm: float, packed_indices: bytes) -> None:
        self.norm = norm
        self.packed_indices = packed_indices


class TurboQuantCodec:
    """TurboQuant encoder / decoder."""

    @staticmethod
    def encode(vector: Sequence[float], bits_per_dim: int) -> TurboQuantPayload:
        """Encode a float vector at *bits_per_dim* bits per dimension.

        Higher bits = better fidelity, larger payload. Typical: 2 bits (16×),
        3 bits (~10×).
        """
        if len(vector) <= 1:
            raise ValueError("Vector must have length > 1.")
        if bits_per_dim < 1 or bits_per_dim > 8:
            raise ValueError("bits_per_dim must be 1..8.")

        dim = len(vector)

        # 1. Norm — accumulate in double, then narrow to float32.
        sum_sq = 0.0
        for i in range(dim):
            sum_sq += float(vector[i]) * float(vector[i])
        norm = _f32(math.sqrt(sum_sq))

        # Edge case — zero vector. Round-trip preserves the all-zero shape.
        if norm < 1e-20:
            all_zeros = bytes((dim * bits_per_dim + 7) >> 3)
            return TurboQuantPayload(0.0, all_zeros)

        # 2. Unit-normalise (float32 arithmetic).
        inv_norm = _f32(1.0 / norm)
        unit = [_f32(vector[i] * inv_norm) for i in range(dim)]

        # 3. Rotate.
        rotated = [0.0] * dim
        OrthogonalRotation.rotate(dim, unit, rotated)

        # 4. Quantize per-coordinate.
        codebook = BetaLloydMaxCodebook.get(bits_per_dim, dim)
        indices = [
            BetaLloydMaxCodebook.bin_for(rotated[i], codebook.boundaries)
            for i in range(dim)
        ]

        # 5. Pack.
        packed = BitPacker.pack(indices, bits_per_dim)
        return TurboQuantPayload(norm, packed)

    @staticmethod
    def decode(
        payload: TurboQuantPayload, dim: int, bits_per_dim: int
    ) -> list[float]:
        """Decode a TurboQuant payload back into the original-magnitude vector
        (modulo quantization error).
        """
        if payload is None:
            raise ValueError("payload required")
        if dim <= 1:
            raise ValueError("dim must be > 1")
        if bits_per_dim < 1 or bits_per_dim > 8:
            raise ValueError("bits_per_dim must be 1..8")

        result = [0.0] * dim
        if payload.norm == 0.0:
            return result  # all zeros

        # 1. Unpack indices.
        indices = BitPacker.unpack(payload.packed_indices, dim, bits_per_dim)

        # 2. Map indices -> centroids (rotated-space reconstruction).
        centroids = BetaLloydMaxCodebook.get(bits_per_dim, dim).centroids
        rotated = [centroids[indices[i]] for i in range(dim)]

        # 3. Inverse rotation.
        unit = [0.0] * dim
        OrthogonalRotation.unrotate(dim, rotated, unit)

        # 4. Scale by stored norm (float32 arithmetic).
        scale = payload.norm
        for i in range(dim):
            result[i] = _f32(unit[i] * scale)
        return result

    @staticmethod
    def round_trip(vector: Sequence[float], bits_per_dim: int) -> list[float]:
        """Convenience: encode then decode, returning the reconstruction."""
        encoded = TurboQuantCodec.encode(vector, bits_per_dim)
        return TurboQuantCodec.decode(encoded, len(vector), bits_per_dim)

    @staticmethod
    def payload_byte_count(dim: int, bits_per_dim: int) -> int:
        """Bytes-per-vector required at the given dim and bits_per_dim
        (excluding the 4-byte norm header).
        """
        return (dim * bits_per_dim + 7) >> 3

    @staticmethod
    def compression_ratio(dim: int, bits_per_dim: int) -> float:
        """Compression ratio vs raw FP32 (vector bytes / encoded bytes incl. norm)."""
        raw = dim * 4
        encoded = TurboQuantCodec.payload_byte_count(dim, bits_per_dim) + 4  # norm
        return raw / encoded


# ─────────────────────────────────────────────────────────────────────────────
# EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
# ─────────────────────────────────────────────────────────────────────────────
#
# Wire format (binary):
#   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
#   bytes [4..7]   = bit-width as uint32 little-endian
#   bytes [8..11]  = dimension as uint32 little-endian
#   bytes [12..15] = norm as float32 little-endian
#   bytes [16..]   = packed indices
# Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
# decode without out-of-band metadata.

_MAGIC = bytes((0x54, 0x51, 0x33, 0x01))  # "TQ3\1"


class EmbeddingPayloadCodec:
    """Encodes / decodes TurboQuant-compressed embeddings as binary blobs
    suitable for persistence (e.g. in a tag value).
    """

    MAGIC = _MAGIC

    @staticmethod
    def encode(vector: Sequence[float], bits_per_dim: int) -> bytes:
        """Encode *vector* at *bits_per_dim* bits per coordinate into a
        self-describing byte payload.
        """
        if len(vector) <= 1:
            raise ValueError("Vector must have length > 1.")

        payload = TurboQuantCodec.encode(vector, bits_per_dim)
        buf = bytearray()
        buf += _MAGIC
        buf += struct.pack("<I", bits_per_dim & 0xFFFFFFFF)
        buf += struct.pack("<I", len(vector) & 0xFFFFFFFF)
        buf += struct.pack("<f", payload.norm)
        buf += payload.packed_indices
        return bytes(buf)

    @staticmethod
    def decode(data: bytes) -> list[float]:
        """Decode a byte payload produced by :meth:`encode` back into a float list."""
        if len(data) < len(_MAGIC) + 12:
            raise ValueError("Payload too short.")
        if not _has_magic(data):
            raise ValueError("Magic header missing — not a TurboQuant payload.")

        o = len(_MAGIC)
        bits_per_dim = struct.unpack_from("<I", data, o)[0]
        o += 4
        dim = struct.unpack_from("<I", data, o)[0]
        o += 4
        norm = struct.unpack_from("<f", data, o)[0]
        o += 4
        packed = bytes(data[o:])
        payload = TurboQuantPayload(norm, packed)
        return TurboQuantCodec.decode(payload, dim, bits_per_dim)

    @staticmethod
    def is_encoded(data: bytes) -> bool:
        """True when the byte span begins with the TurboQuant magic header."""
        return len(data) >= len(_MAGIC) and _has_magic(data)

    @staticmethod
    def encode_base64(vector: Sequence[float], bits_per_dim: int) -> str:
        """Convenience: encode + base64-stringify for tag-style storage."""
        return base64.b64encode(
            EmbeddingPayloadCodec.encode(vector, bits_per_dim)
        ).decode("ascii")

    @staticmethod
    def decode_base64(b64: str) -> list[float]:
        """Convenience: base64-decode + decode."""
        if b64 is None:
            raise ValueError("base64 required")
        return EmbeddingPayloadCodec.decode(base64.b64decode(b64))


def _has_magic(data: bytes) -> bool:
    return (
        len(data) >= 4
        and data[0] == _MAGIC[0]
        and data[1] == _MAGIC[1]
        and data[2] == _MAGIC[2]
        and data[3] == _MAGIC[3]
    )


# ─────────────────────────────────────────────────────────────────────────────
# Shared cosine — matches the C# stores' internal CosineSimilarity.Score
# ─────────────────────────────────────────────────────────────────────────────


def _cosine_score(a: list[float], b: list[float]) -> float:
    if len(a) != len(b):
        return 0.0
    dot = 0.0
    mag_a = 0.0
    mag_b = 0.0
    for i in range(len(a)):
        dot += a[i] * b[i]
        mag_a += a[i] * a[i]
        mag_b += b[i] * b[i]
    denom = math.sqrt(mag_a) * math.sqrt(mag_b)
    return 0.0 if denom == 0.0 else dot / denom


#: Tag key under which the compressed embedding is stored.
COMPRESSED_TAG_KEY = "x-tq-embedding"


# ─────────────────────────────────────────────────────────────────────────────
# CompressedEpisodicMemoryStore — CircleAI.Memory.Compression
# ─────────────────────────────────────────────────────────────────────────────


class CompressedEpisodicMemoryStore:
    """Wraps any :class:`IEpisodicMemoryStore` and stores its embeddings in
    TurboQuant-compressed form. Default 2 bits per dim (~16× shrink).

    The inner store sees ``embedding = None``; the compressed base64 payload
    lives in the entry's tags under :data:`COMPRESSED_TAG_KEY`. Reads rehydrate
    the embedding by decoding the tag, and search rebuilds embeddings on the
    read path so cosine ranking works against the reconstructed vectors.
    """

    #: Tag key under which the compressed embedding is stored.
    CompressedTagKey = COMPRESSED_TAG_KEY

    def __init__(self, inner: IEpisodicMemoryStore, bits_per_dim: int = 2) -> None:
        if inner is None:
            raise ValueError("inner required")
        if bits_per_dim < 1 or bits_per_dim > 8:
            raise ValueError("bits_per_dim must be 1..8")
        self._inner = inner
        self._bits_per_dim = bits_per_dim

    async def add_async(
        self, entry: EpisodicMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        if entry is None:
            raise ValueError("entry required")
        if entry.embedding is not None and len(entry.embedding) > 1:
            rewritten = EpisodicMemoryEntry(
                id=entry.id,
                recorded_at_utc=entry.recorded_at_utc,
                user_text=entry.user_text,
                assistant_text=entry.assistant_text,
                app_context=entry.app_context,
                embedding=None,  # dropped — lives in tags
                tags=self._copy_tags_with_compressed(entry.tags, entry.embedding),
            )
        else:
            rewritten = entry
        await self._inner.add_async(rewritten, ct=ct)

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[EpisodicMemoryEntry]:
        # The inner store sees embedding = None on every entry, so we cannot
        # defer to its cosine ranking. Load recent, rehydrate, then rank here.
        all_entries = await self._inner.get_recent_async(_INT_MAX, ct=ct)
        rehydrated = [_rehydrate_episodic(e) for e in all_entries]

        if query_embedding is None:
            return rehydrated[:top_k]

        scored = [
            (e, _cosine_score(query_embedding, e.embedding))
            for e in rehydrated
            if e.embedding is not None and len(e.embedding) > 0
        ]
        scored.sort(key=lambda t: t[1], reverse=True)
        return [e for e, _ in scored[:top_k]]

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[EpisodicMemoryEntry]:
        recent = await self._inner.get_recent_async(count, ct=ct)
        return [_rehydrate_episodic(e) for e in recent]

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return await self._inner.count_async(ct=ct)

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        return await self._inner.prune_older_than_async(cutoff, ct=ct)

    def _copy_tags_with_compressed(
        self, src: Optional[dict[str, str]], embedding: list[float]
    ) -> dict[str, str]:
        dict_out: dict[str, str] = dict(src) if src else {}
        dict_out[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encode_base64(
            embedding, self._bits_per_dim
        )
        return dict_out


def _rehydrate_episodic(e: EpisodicMemoryEntry) -> EpisodicMemoryEntry:
    if e.embedding is not None and len(e.embedding) > 0:
        return e  # never compressed
    if e.tags is None:
        return e
    b64 = e.tags.get(COMPRESSED_TAG_KEY)
    if b64 is None:
        return e
    try:
        floats = EmbeddingPayloadCodec.decode_base64(b64)
        return EpisodicMemoryEntry(
            id=e.id,
            recorded_at_utc=e.recorded_at_utc,
            user_text=e.user_text,
            assistant_text=e.assistant_text,
            app_context=e.app_context,
            embedding=floats,
            tags=e.tags,
        )
    except Exception:
        # Malformed tag — return entry as-is so the caller can still see it.
        return e


# ─────────────────────────────────────────────────────────────────────────────
# CompressedMultimodalMemoryStore — CircleAI.Memory.Compression
# ─────────────────────────────────────────────────────────────────────────────


class CompressedMultimodalMemoryStore:
    """Wraps any :class:`IMultimodalMemoryStore` and stores its embeddings in
    TurboQuant-compressed form. Same wire format + tag key as the episodic
    decorator.
    """

    #: Tag key under which the compressed embedding is stored.
    CompressedTagKey = COMPRESSED_TAG_KEY

    def __init__(self, inner: IMultimodalMemoryStore, bits_per_dim: int = 2) -> None:
        if inner is None:
            raise ValueError("inner required")
        if bits_per_dim < 1 or bits_per_dim > 8:
            raise ValueError("bits_per_dim must be 1..8")
        self._inner = inner
        self._bits_per_dim = bits_per_dim

    async def add_async(
        self, entry: MultimodalMemoryEntry, *, ct: Optional[object] = None
    ) -> None:
        if entry is None:
            raise ValueError("entry required")
        if entry.embedding is not None and len(entry.embedding) > 1:
            rewritten = self._compress(entry)
        else:
            rewritten = entry
        await self._inner.add_async(rewritten, ct=ct)

    async def get_by_hash_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> Optional[MultimodalMemoryEntry]:
        got = await self._inner.get_by_hash_async(source_sha256, ct=ct)
        return None if got is None else _rehydrate_multimodal(got)

    async def reinforce_async(
        self, source_sha256: str, *, ct: Optional[object] = None
    ) -> None:
        await self._inner.reinforce_async(source_sha256, ct=ct)

    async def search_async(
        self,
        query_embedding: Optional[list[float]],
        top_k: int = 5,
        *,
        ct: Optional[object] = None,
    ) -> list[MultimodalMemoryEntry]:
        all_entries = await self._inner.get_recent_async(_INT_MAX, ct=ct)
        rehydrated = [_rehydrate_multimodal(e) for e in all_entries]
        if query_embedding is None:
            return rehydrated[:top_k]

        scored = [
            (e, _cosine_score(query_embedding, e.embedding))
            for e in rehydrated
            if e.embedding is not None and len(e.embedding) > 0
        ]
        scored.sort(key=lambda t: t[1], reverse=True)
        return [e for e, _ in scored[:top_k]]

    async def get_recent_async(
        self, count: int = 10, *, ct: Optional[object] = None
    ) -> list[MultimodalMemoryEntry]:
        recent = await self._inner.get_recent_async(count, ct=ct)
        return [_rehydrate_multimodal(e) for e in recent]

    async def prune_older_than_async(
        self, cutoff: datetime, *, ct: Optional[object] = None
    ) -> int:
        return await self._inner.prune_older_than_async(cutoff, ct=ct)

    async def count_async(self, *, ct: Optional[object] = None) -> int:
        return await self._inner.count_async(ct=ct)

    def _compress(self, entry: MultimodalMemoryEntry) -> MultimodalMemoryEntry:
        tags: dict[str, str] = dict(entry.tags) if entry.tags else {}
        tags[COMPRESSED_TAG_KEY] = EmbeddingPayloadCodec.encode_base64(
            entry.embedding, self._bits_per_dim
        )

        return MultimodalMemoryEntry(
            id=entry.id,
            recorded_at_utc=entry.recorded_at_utc,
            modality=entry.modality,
            caption=entry.caption,
            embedding=None,
            source_sha256=entry.source_sha256,
            source_mime_type=entry.source_mime_type,
            source_byte_count=entry.source_byte_count,
            source_uri=entry.source_uri,
            width_px=entry.width_px,
            height_px=entry.height_px,
            duration_ms=entry.duration_ms,
            reference_count=entry.reference_count,
            tags=tags,
        )


def _rehydrate_multimodal(e: MultimodalMemoryEntry) -> MultimodalMemoryEntry:
    if e.embedding is not None and len(e.embedding) > 0:
        return e
    if e.tags is None:
        return e
    b64 = e.tags.get(COMPRESSED_TAG_KEY)
    if b64 is None:
        return e
    try:
        floats = EmbeddingPayloadCodec.decode_base64(b64)
        return MultimodalMemoryEntry(
            id=e.id,
            recorded_at_utc=e.recorded_at_utc,
            modality=e.modality,
            caption=e.caption,
            embedding=floats,
            source_sha256=e.source_sha256,
            source_mime_type=e.source_mime_type,
            source_byte_count=e.source_byte_count,
            source_uri=e.source_uri,
            width_px=e.width_px,
            height_px=e.height_px,
            duration_ms=e.duration_ms,
            reference_count=e.reference_count,
            tags=e.tags,
        )
    except Exception:
        return e


# C# uses int.MaxValue for "load everything"; mirror that sentinel.
_INT_MAX = 2**31 - 1
