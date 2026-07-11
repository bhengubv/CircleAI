# federated_averaging.py
#
# Port of CircleAI.Federation FederatedAveraging.cs (C# — the EXACT spec).
#
# Sample-size-weighted averaging over ModelDelta.DeltaPayload arrays interpreted
# as little-endian IEEE 754 float[]. The reference encoding uses
# BinaryPrimitives.ReadSingleLittleEndian / WriteSingleLittleEndian, which map
# to struct.pack/unpack with the "<f" format (single-precision little-endian).
#
# The double-precision accumulator matches the C# `double[] accumulator`; the
# final round-trip back to float32 (struct.pack "<f") reproduces the C# cast
# (float)accumulator[i] byte-for-byte.

from __future__ import annotations

import struct
from typing import List

from .model_delta import ModelDelta

_FLOAT_SIZE = 4  # sizeof(float)


def _read_single_le(payload: bytes, offset: int) -> float:
    return struct.unpack_from("<f", payload, offset)[0]


def _write_single_le(buf: bytearray, offset: int, value: float) -> None:
    struct.pack_into("<f", buf, offset, value)


class FederatedAveraging:
    """Static utility mirroring the C# ``static class FederatedAveraging``."""

    @staticmethod
    def average(deltas: List[ModelDelta]) -> bytes:
        """Sample-size-weighted average of the deltas, encoded little-endian
        IEEE 754."""
        if deltas is None:
            raise ValueError("deltas must not be None")
        if len(deltas) == 0:
            raise ValueError("Cannot average an empty delta list.")

        expected_bytes = len(deltas[0].delta_payload)
        if expected_bytes == 0:
            raise ValueError("Delta payloads must be non-empty.")
        if expected_bytes % _FLOAT_SIZE != 0:
            raise ValueError(
                f"Delta payload length ({expected_bytes}) must be a multiple of {_FLOAT_SIZE} bytes."
            )

        for i in range(1, len(deltas)):
            if len(deltas[i].delta_payload) != expected_bytes:
                raise ValueError(
                    f"Delta payload length mismatch: index 0 = {expected_bytes} bytes, "
                    f"index {i} = {len(deltas[i].delta_payload)} bytes."
                )

        float_count = expected_bytes // _FLOAT_SIZE
        total_samples = 0
        for d in deltas:
            if d.sample_count < 0:
                raise ValueError(
                    f"SampleCount must be non-negative; delta {d.id} reported {d.sample_count}."
                )
            total_samples += d.sample_count
        if total_samples == 0:
            raise ValueError(
                "Total sample weight across deltas is zero — cannot perform weighted average."
            )

        accumulator = [0.0] * float_count
        for d in deltas:
            weight = d.sample_count / total_samples
            payload = d.delta_payload
            for i in range(float_count):
                value = _read_single_le(payload, i * _FLOAT_SIZE)
                accumulator[i] += value * weight

        output = bytearray(expected_bytes)
        for i in range(float_count):
            _write_single_le(output, i * _FLOAT_SIZE, accumulator[i])
        return bytes(output)

    @staticmethod
    def encode_floats(values: List[float]) -> bytes:
        """Encode a float list as little-endian IEEE 754 bytes."""
        if values is None:
            raise ValueError("values must not be None")
        output = bytearray(len(values) * _FLOAT_SIZE)
        for i, v in enumerate(values):
            _write_single_le(output, i * _FLOAT_SIZE, v)
        return bytes(output)

    @staticmethod
    def decode_floats(payload: bytes) -> List[float]:
        """Decode little-endian IEEE 754 bytes into a float list."""
        if payload is None:
            raise ValueError("payload must not be None")
        if len(payload) % _FLOAT_SIZE != 0:
            raise ValueError(
                f"Payload length ({len(payload)}) must be a multiple of {_FLOAT_SIZE} bytes."
            )
        count = len(payload) // _FLOAT_SIZE
        return [_read_single_le(payload, i * _FLOAT_SIZE) for i in range(count)]
