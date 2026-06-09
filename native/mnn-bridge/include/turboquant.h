// turboquant.h
//
// C++ port of CircleAI.Core.Compression.TurboQuantCodec.
//
// TurboQuant is Google Research's data-oblivious vector quantizer
// (arxiv:2504.19874). This port matches the managed implementation
// bit-for-bit on the test fixtures so a vector encoded on the host
// (C#) round-trips identically through the native layer.
//
// CONTRACT (must match TurboQuantCodec.cs):
//   - Rotation seed: 0xC1C1EA10C1C1EA10
//   - PRNG: SplitMix64 -> Box-Muller for Gaussian samples
//   - QR: Modified Gram-Schmidt, columns sign-corrected to q[j,j] >= 0
//   - Codebook: Lloyd-Max for Beta((d-1)/2, (d-1)/2), 200 iter, 1e-12 tol
//   - LogGamma: Lanczos g=7, n=9
//   - Bit-pack: LSB-first within each byte
//
// THREAD-SAFETY:
//   - Get<*>() methods cache results in a thread-safe std::unordered_map
//   - Encode/Decode are pure functions of (vector, bits, dim)

#pragma once

#include <cstddef>
#include <cstdint>
#include <vector>

namespace circleai::turboquant {

// ── Payload ──────────────────────────────────────────────────────────────

struct Payload {
    float                 norm;            // L2 norm of original vector
    std::vector<uint8_t>  packed_indices;  // bit-packed Lloyd-Max bin indices
};

// ── Public API ───────────────────────────────────────────────────────────

/// Encode `vector` (length = dim) at `bits_per_dim` bits/dimension.
/// Equivalent to TurboQuantCodec.Encode in the C# port.
/// Pre-conditions: dim > 1, 1 <= bits_per_dim <= 8.
Payload encode(const float* vector, int dim, int bits_per_dim);

/// Decode `payload` back to a dense float vector of length `dim`.
/// Output vector is allocated by caller (size must be >= dim).
/// Equivalent to TurboQuantCodec.Decode in the C# port.
void decode(const Payload& payload, int dim, int bits_per_dim, float* out);

/// Convenience: encode then immediately decode. Useful for parity tests.
std::vector<float> round_trip(const float* vector, int dim, int bits_per_dim);

/// Byte count of the packed payload (excluding norm header).
constexpr int payload_byte_count(int dim, int bits_per_dim) {
    return (dim * bits_per_dim + 7) / 8;
}

// ── Lower-level pieces (exposed for parity testing) ──────────────────────

namespace bitpacker {
    std::vector<uint8_t> pack(const uint16_t* indices, int count, int bits_per_index);
    std::vector<uint16_t> unpack(const uint8_t* packed, int packed_len, int count, int bits_per_index);
} // namespace bitpacker

namespace rotation {
    /// Rotation seed shared with the C# layer (CircleAI.Core.Compression
    /// .OrthogonalRotation.RotationSeed). DO NOT CHANGE — breaks portability.
    constexpr uint64_t SEED = 0xC1C1EA10C1C1EA10ULL;

    /// Returns the cached dim x dim orthogonal matrix in row-major layout.
    /// Length of returned span is dim*dim. Constructed on first call.
    const float* get_matrix(int dim);

    /// output[i] = sum_j R[i,j] * vector[j]
    void rotate(int dim, const float* vector, float* output);

    /// output[i] = sum_j R[j,i] * vector[j]   (R^T, the inverse)
    void unrotate(int dim, const float* vector, float* output);
} // namespace rotation

namespace codebook {
    struct BetaCodebook {
        std::vector<float> boundaries;   // length 2^bits - 1
        std::vector<float> centroids;    // length 2^bits
    };

    /// Returns the Lloyd-Max codebook for Beta((dim-1)/2, (dim-1)/2) at
    /// `bits` bits per dimension. Cached by (bits, dim).
    /// Pre-conditions: 1 <= bits <= 8, dim > 1.
    const BetaCodebook& get(int bits, int dim);

    /// Linear scan: returns the bin index of `value` against `boundaries`.
    uint16_t bin_for(float value, const float* boundaries, int boundaries_len);
} // namespace codebook

} // namespace circleai::turboquant
