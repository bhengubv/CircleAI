// turboquant.cpp
//
// C++ port of CircleAI.Core.Compression.{TurboQuantCodec, BitPacker,
// OrthogonalRotation, BetaLloydMaxCodebook}. Bit-identical to the managed
// implementation on the parity fixtures.

#include "turboquant.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <mutex>
#include <stdexcept>
#include <unordered_map>

namespace circleai::turboquant {

// ═══════════════════════════════════════════════════════════════════════
// BitPacker — LSB-first within each byte
// ═══════════════════════════════════════════════════════════════════════

namespace bitpacker {

static void validate_width(int bits) {
    if (bits < 1 || bits > 16)
        throw std::out_of_range("bits_per_index must be in 1..16");
}

std::vector<uint8_t> pack(const uint16_t* indices, int count, int bits_per_index) {
    validate_width(bits_per_index);
    const int total_bits = count * bits_per_index;
    std::vector<uint8_t> out((total_bits + 7) / 8, 0);

    int bit_pos = 0;
    for (int i = 0; i < count; ++i) {
        uint32_t value = indices[i];
        if (bits_per_index < 16 && value >= (1u << bits_per_index))
            throw std::out_of_range("index exceeds bit-width range");

        int remaining = bits_per_index;
        int byte_idx  = bit_pos >> 3;
        int bit_off   = bit_pos & 7;

        while (remaining > 0) {
            const int take  = std::min(remaining, 8 - bit_off);
            const int shift = bits_per_index - remaining;
            const uint8_t chunk = static_cast<uint8_t>((value >> shift) & ((1u << take) - 1));
            out[byte_idx] = static_cast<uint8_t>(out[byte_idx] | (chunk << bit_off));
            remaining -= take;
            bit_off    = 0;
            ++byte_idx;
        }
        bit_pos += bits_per_index;
    }
    return out;
}

std::vector<uint16_t> unpack(const uint8_t* packed, int packed_len, int count, int bits_per_index) {
    validate_width(bits_per_index);
    const int required = (count * bits_per_index + 7) / 8;
    if (packed_len < required)
        throw std::invalid_argument("packed buffer too small");

    std::vector<uint16_t> out(count);
    int bit_pos = 0;
    for (int i = 0; i < count; ++i) {
        int remaining = bits_per_index;
        int byte_idx  = bit_pos >> 3;
        int bit_off   = bit_pos & 7;
        uint32_t value = 0;

        while (remaining > 0) {
            const int take  = std::min(remaining, 8 - bit_off);
            const int shift = bits_per_index - remaining;
            const uint32_t chunk =
                (static_cast<uint32_t>(packed[byte_idx]) >> bit_off) & ((1u << take) - 1);
            value |= chunk << shift;
            remaining -= take;
            bit_off    = 0;
            ++byte_idx;
        }
        out[i] = static_cast<uint16_t>(value);
        bit_pos += bits_per_index;
    }
    return out;
}

} // namespace bitpacker

// ═══════════════════════════════════════════════════════════════════════
// OrthogonalRotation — Box-Muller over SplitMix64 + Modified Gram-Schmidt
// ═══════════════════════════════════════════════════════════════════════

namespace rotation {

// Seeded Gaussian sampler. Matches CircleAI.Core.Compression.SeededGaussian.
class SeededGaussian {
public:
    explicit SeededGaussian(uint64_t seed)
        : state_(seed == 0 ? 0xDEADBEEFCAFEBABEULL : seed),
          has_spare_(false), spare_(0.0) {}

    double sample() {
        if (has_spare_) { has_spare_ = false; return spare_; }
        double u, v;
        do { u = next_uniform(); } while (u <= 1e-300);
        v = next_uniform();
        const double magnitude = std::sqrt(-2.0 * std::log(u));
        const double angle     = 2.0 * 3.14159265358979323846 * v;
        spare_     = magnitude * std::sin(angle);
        has_spare_ = true;
        return magnitude * std::cos(angle);
    }

private:
    double next_uniform() {
        // SplitMix64.
        state_ += 0x9E3779B97F4A7C15ULL;
        uint64_t z = state_;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
        z = z ^ (z >> 31);
        // Top 53 bits -> [0, 1).
        return (z >> 11) * (1.0 / (1ULL << 53));
    }

    uint64_t state_;
    bool     has_spare_;
    double   spare_;
};

// Modified Gram-Schmidt QR. Returns Q (row-major, dim*dim). `g` is destroyed.
static std::vector<double> modified_gram_schmidt(std::vector<double>& g, int dim) {
    std::vector<double> q(static_cast<size_t>(dim) * dim, 0.0);
    for (int j = 0; j < dim; ++j) {
        // Copy column j of g into q.
        for (int i = 0; i < dim; ++i) q[i * dim + j] = g[i * dim + j];

        for (int k = 0; k < j; ++k) {
            double dot = 0.0;
            for (int i = 0; i < dim; ++i) dot += q[i * dim + j] * q[i * dim + k];
            for (int i = 0; i < dim; ++i) q[i * dim + j] -= dot * q[i * dim + k];
        }

        double norm = 0.0;
        for (int i = 0; i < dim; ++i) norm += q[i * dim + j] * q[i * dim + j];
        norm = std::sqrt(norm);
        if (norm < 1e-15)
            throw std::runtime_error("Gram-Schmidt produced near-zero column");
        const double inv = 1.0 / norm;
        for (int i = 0; i < dim; ++i) q[i * dim + j] *= inv;
    }
    return q;
}

// Sign-correct columns so q[j,j] >= 0.
static void sign_correct_columns(std::vector<double>& q, int dim) {
    for (int j = 0; j < dim; ++j) {
        if (q[j * dim + j] < 0.0) {
            for (int i = 0; i < dim; ++i) q[i * dim + j] = -q[i * dim + j];
        }
    }
}

static std::vector<float> build_matrix(int dim) {
    std::vector<double> g(static_cast<size_t>(dim) * dim);
    SeededGaussian rng(SEED);
    for (auto& x : g) x = rng.sample();

    auto q = modified_gram_schmidt(g, dim);
    sign_correct_columns(q, dim);

    std::vector<float> result(q.size());
    for (size_t i = 0; i < q.size(); ++i) result[i] = static_cast<float>(q[i]);
    return result;
}

// Thread-safe cache (dim -> matrix).
static std::mutex g_matrix_mutex;
static std::unordered_map<int, std::vector<float>> g_matrix_cache;

const float* get_matrix(int dim) {
    if (dim <= 0) throw std::out_of_range("dim must be > 0");
    std::lock_guard<std::mutex> lock(g_matrix_mutex);
    auto it = g_matrix_cache.find(dim);
    if (it != g_matrix_cache.end()) return it->second.data();
    auto [inserted, _] = g_matrix_cache.emplace(dim, build_matrix(dim));
    return inserted->second.data();
}

void rotate(int dim, const float* vector, float* output) {
    const float* m = get_matrix(dim);
    for (int i = 0; i < dim; ++i) {
        float sum = 0.0f;
        const int row = i * dim;
        for (int j = 0; j < dim; ++j) sum += m[row + j] * vector[j];
        output[i] = sum;
    }
}

void unrotate(int dim, const float* vector, float* output) {
    const float* m = get_matrix(dim);
    for (int i = 0; i < dim; ++i) {
        float sum = 0.0f;
        for (int j = 0; j < dim; ++j) sum += m[j * dim + i] * vector[j];
        output[i] = sum;
    }
}

} // namespace rotation

// ═══════════════════════════════════════════════════════════════════════
// BetaLloydMaxCodebook — Lloyd-Max for Beta((d-1)/2, (d-1)/2)
// ═══════════════════════════════════════════════════════════════════════

namespace codebook {

// LogGamma via Lanczos g=7, n=9. Matches CircleAI.Core.Compression
// .BetaLloydMaxCodebook.LogGamma byte-for-byte (same coefficients).
static double log_gamma(double x) {
    static const double c[] = {
         0.99999999999980993,
         676.5203681218851,
        -1259.1392167224028,
         771.32342877765313,
        -176.61502916214059,
         12.507343278686905,
        -0.13857109526572012,
         9.9843695780195716e-6,
         1.5056327351493116e-7,
    };
    constexpr double PI = 3.14159265358979323846;
    if (x < 0.5) {
        // Reflection: Gamma(x)*Gamma(1-x) = pi / sin(pi*x)
        return std::log(PI / std::sin(PI * x)) - log_gamma(1.0 - x);
    }
    x -= 1.0;
    const double t = x + 7.5;
    double sum = c[0];
    for (int i = 1; i < 9; ++i) sum += c[i] / (x + i);
    return 0.5 * std::log(2.0 * PI) + (x + 0.5) * std::log(t) - t + std::log(sum);
}

static double log_beta(double a, double b) {
    return log_gamma(a) + log_gamma(b) - log_gamma(a + b);
}

// Numerical Recipes 6.4 continued-fraction for regularized incomplete beta.
static double beta_continued_fraction(double a, double b, double x) {
    constexpr int    MAX_ITER = 200;
    constexpr double EPS      = 3e-15;
    constexpr double FPMIN    = 1e-300;

    const double qab = a + b;
    const double qap = a + 1.0;
    const double qam = a - 1.0;
    double c = 1.0;
    double d = 1.0 - qab * x / qap;
    if (std::abs(d) < FPMIN) d = FPMIN;
    d = 1.0 / d;
    double h = d;

    for (int m = 1; m <= MAX_ITER; ++m) {
        const int    m2 = 2 * m;
        double aa = m * (b - m) * x / ((qam + m2) * (a + m2));
        d = 1.0 + aa * d; if (std::abs(d) < FPMIN) d = FPMIN;
        c = 1.0 + aa / c; if (std::abs(c) < FPMIN) c = FPMIN;
        d = 1.0 / d; h *= d * c;

        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
        d = 1.0 + aa * d; if (std::abs(d) < FPMIN) d = FPMIN;
        c = 1.0 + aa / c; if (std::abs(c) < FPMIN) c = FPMIN;
        d = 1.0 / d;
        const double delta = d * c;
        h *= delta;
        if (std::abs(delta - 1.0) < EPS) return h;
    }
    return h;
}

static double regularized_incomplete_beta(double a, double b, double x) {
    if (x < 0.0 || x > 1.0) throw std::out_of_range("x must be in [0, 1]");
    if (x == 0.0 || x == 1.0) return x;
    const double bt = std::exp(
        log_gamma(a + b) - log_gamma(a) - log_gamma(b)
        + a * std::log(x) + b * std::log(1.0 - x));
    if (x < (a + 1.0) / (a + b + 2.0))
        return bt * beta_continued_fraction(a, b, x) / a;
    return 1.0 - bt * beta_continued_fraction(b, a, 1.0 - x) / b;
}

static double beta_pdf_symmetric(double a, double x) {
    if (x <= 0.0 || x >= 1.0) return 0.0;
    const double log_pdf = (a - 1.0) * std::log(x)
                         + (a - 1.0) * std::log(1.0 - x)
                         - log_beta(a, a);
    return std::exp(log_pdf);
}

static double beta_cdf_symmetric(double a, double x) {
    if (x <= 0.0) return 0.0;
    if (x >= 1.0) return 1.0;
    return regularized_incomplete_beta(a, a, x);
}

// Adaptive Simpson — matches the C# recursive form.
template <typename F>
static double adaptive_simpson_rec(F&& f, double a, double b,
                                   double fa, double fb, double fm,
                                   double whole, double tol, int depth) {
    const double mid = (a + b) / 2.0;
    const double m1  = (a + mid) / 2.0;
    const double m2  = (mid + b) / 2.0;
    const double fm1 = f(m1), fm2 = f(m2);
    const double left  = (mid - a) / 6.0 * (fa + 4.0 * fm1 + fm);
    const double right = (b - mid) / 6.0 * (fm + 4.0 * fm2 + fb);
    const double refined = left + right;
    if (depth == 0 || std::abs(refined - whole) < 15.0 * tol)
        return refined + (refined - whole) / 15.0;
    return adaptive_simpson_rec(f, a, mid, fa, fm, fm1, left,  tol / 2.0, depth - 1)
         + adaptive_simpson_rec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1);
}

template <typename F>
static double adaptive_simpson(F&& f, double a, double b, double tol, int max_depth) {
    const double mid = (a + b) / 2.0;
    const double fa  = f(a), fb = f(b), fm = f(mid);
    const double whole = (b - a) / 6.0 * (fa + 4.0 * fm + fb);
    return adaptive_simpson_rec(f, a, b, fa, fb, fm, whole, tol, max_depth);
}

static BetaCodebook compute(int bits, int dim) {
    constexpr int    MAX_ITER = 200;
    constexpr double TOL      = 1e-12;
    const double a       = (dim - 1.0) / 2.0;
    const int    levels  = 1 << bits;

    // Initial centroids: evenly across +/- 3 stddev.
    const double std_dev = std::sqrt(2.0 * a / ((2.0 * a + 1.0) * 4.0 * a));
    const double spread  = 3.0 * std_dev;
    std::vector<double> centroids(levels);
    for (int i = 0; i < levels; ++i)
        centroids[i] = -spread + 2.0 * spread * i / (levels - 1);

    for (int iter = 0; iter < MAX_ITER; ++iter) {
        std::vector<double> boundaries(levels - 1);
        for (int i = 0; i < levels - 1; ++i)
            boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0;

        std::vector<double> edges(levels + 1);
        edges[0] = -1.0;
        for (int i = 0; i < levels - 1; ++i) edges[i + 1] = boundaries[i];
        edges[levels] = 1.0;

        std::vector<double> new_centroids(levels);
        for (int i = 0; i < levels; ++i) {
            const double lo = edges[i];
            const double hi = edges[i + 1];
            const double cdf_lo = beta_cdf_symmetric(a, (lo + 1.0) / 2.0);
            const double cdf_hi = beta_cdf_symmetric(a, (hi + 1.0) / 2.0);
            const double prob   = cdf_hi - cdf_lo;
            if (prob < 1e-15) {
                new_centroids[i] = centroids[i];
            } else {
                const double mean = adaptive_simpson(
                    [a](double x) { return x * beta_pdf_symmetric(a, (x + 1.0) / 2.0) / 2.0; },
                    lo, hi, 1e-14, 50);
                new_centroids[i] = mean / prob;
            }
        }

        double max_change = 0.0;
        for (int i = 0; i < levels; ++i)
            max_change = std::max(max_change, std::abs(centroids[i] - new_centroids[i]));
        centroids = std::move(new_centroids);
        if (max_change < TOL) break;
    }

    BetaCodebook cb;
    cb.boundaries.resize(levels - 1);
    cb.centroids.resize(levels);
    for (int i = 0; i < levels - 1; ++i)
        cb.boundaries[i] = static_cast<float>((centroids[i] + centroids[i + 1]) / 2.0);
    for (int i = 0; i < levels; ++i)
        cb.centroids[i] = static_cast<float>(centroids[i]);
    return cb;
}

// Cache keyed by (bits, dim).
static std::mutex g_cb_mutex;
struct PairHash {
    size_t operator()(const std::pair<int,int>& p) const noexcept {
        return std::hash<uint64_t>{}((uint64_t)p.first << 32 | (uint32_t)p.second);
    }
};
static std::unordered_map<std::pair<int,int>, BetaCodebook, PairHash> g_cb_cache;

const BetaCodebook& get(int bits, int dim) {
    if (bits < 1 || bits > 8) throw std::out_of_range("bits must be 1..8");
    if (dim <= 1)             throw std::out_of_range("dim must be > 1");
    std::lock_guard<std::mutex> lock(g_cb_mutex);
    const auto key = std::make_pair(bits, dim);
    auto it = g_cb_cache.find(key);
    if (it != g_cb_cache.end()) return it->second;
    auto [inserted, _] = g_cb_cache.emplace(key, compute(bits, dim));
    return inserted->second;
}

uint16_t bin_for(float value, const float* boundaries, int boundaries_len) {
    for (int i = 0; i < boundaries_len; ++i) {
        if (value < boundaries[i]) return static_cast<uint16_t>(i);
    }
    return static_cast<uint16_t>(boundaries_len);
}

} // namespace codebook

// ═══════════════════════════════════════════════════════════════════════
// TurboQuantCodec — encode / decode orchestrator
// ═══════════════════════════════════════════════════════════════════════

Payload encode(const float* vector, int dim, int bits_per_dim) {
    if (dim <= 1)                            throw std::out_of_range("dim must be > 1");
    if (bits_per_dim < 1 || bits_per_dim > 8) throw std::out_of_range("bits_per_dim must be 1..8");

    // 1. Norm in double for precision, narrow to float at the end.
    double sum_sq = 0.0;
    for (int i = 0; i < dim; ++i) sum_sq += (double)vector[i] * vector[i];
    const float norm = static_cast<float>(std::sqrt(sum_sq));

    // Zero-vector short circuit.
    if (norm < 1e-20f) {
        Payload zero{ 0.0f, std::vector<uint8_t>(payload_byte_count(dim, bits_per_dim), 0) };
        return zero;
    }

    // 2. Unit-normalise.
    std::vector<float> unit(dim);
    const float inv_norm = 1.0f / norm;
    for (int i = 0; i < dim; ++i) unit[i] = vector[i] * inv_norm;

    // 3. Rotate.
    std::vector<float> rotated(dim);
    rotation::rotate(dim, unit.data(), rotated.data());

    // 4. Quantize per-coordinate.
    const auto& cb = codebook::get(bits_per_dim, dim);
    std::vector<uint16_t> indices(dim);
    for (int i = 0; i < dim; ++i) {
        indices[i] = codebook::bin_for(rotated[i], cb.boundaries.data(),
                                       static_cast<int>(cb.boundaries.size()));
    }

    // 5. Pack.
    auto packed = bitpacker::pack(indices.data(), dim, bits_per_dim);
    return Payload{ norm, std::move(packed) };
}

void decode(const Payload& payload, int dim, int bits_per_dim, float* out) {
    if (dim <= 1)                            throw std::out_of_range("dim must be > 1");
    if (bits_per_dim < 1 || bits_per_dim > 8) throw std::out_of_range("bits_per_dim must be 1..8");

    if (payload.norm == 0.0f) {
        std::memset(out, 0, sizeof(float) * static_cast<size_t>(dim));
        return;
    }

    // 1. Unpack.
    auto indices = bitpacker::unpack(payload.packed_indices.data(),
                                     static_cast<int>(payload.packed_indices.size()),
                                     dim, bits_per_dim);

    // 2. Map indices -> centroids.
    const auto& cb = codebook::get(bits_per_dim, dim);
    std::vector<float> rotated(dim);
    for (int i = 0; i < dim; ++i) rotated[i] = cb.centroids[indices[i]];

    // 3. Inverse rotation.
    std::vector<float> unit(dim);
    rotation::unrotate(dim, rotated.data(), unit.data());

    // 4. Rescale by norm.
    const float scale = payload.norm;
    for (int i = 0; i < dim; ++i) out[i] = unit[i] * scale;
}

std::vector<float> round_trip(const float* vector, int dim, int bits_per_dim) {
    auto payload = encode(vector, dim, bits_per_dim);
    std::vector<float> out(dim);
    decode(payload, dim, bits_per_dim, out.data());
    return out;
}

} // namespace circleai::turboquant
