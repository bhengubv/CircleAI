/*
 * compression.c — TurboQuant embedding compression + compressed store
 * decorators (C11 port).
 *
 * Ported EXACTLY from the C# reference (BitPacker / OrthogonalRotation /
 * SeededGaussian-SplitMix64 / BetaLloydMaxCodebook / TurboQuantCodec /
 * EmbeddingPayloadCodec) and mirroring the verified TypeScript reference 1:1.
 * The encoded wire payload is BYTE-IDENTICAL to C#.
 *
 * Fidelity: SplitMix64 runs in native uint64_t; every C# `float` store maps to a
 * C `float` at the same point (norm accumulates in double then casts to float;
 * rotate/unrotate use a float accumulator so each += truncates to fp32 exactly
 * like C#'s `float sum`); little-endian byte writes are explicit.
 *
 * Pure C11 + libc; -lm.
 */

#include "circle_ai/compression.h"

#include <stdlib.h>
#include <string.h>
#include <math.h>

/* ===========================================================================
 * BitPacker
 * =========================================================================== */

static bool bp_valid_width(int bits) { return bits >= 1 && bits <= 16; }

uint8_t *ca_bitpacker_pack(const uint16_t *indices, size_t count,
                           int bits_per_index, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!bp_valid_width(bits_per_index)) return NULL;
    size_t total_bits = count * (size_t)bits_per_index;
    size_t nbytes = (total_bits + 7) >> 3;
    uint8_t *packed = (uint8_t *)calloc(nbytes ? nbytes : 1, 1);
    if (!packed) return NULL;

    size_t bit_pos = 0;
    for (size_t i = 0; i < count; ++i) {
        uint32_t value = indices ? indices[i] : 0u;
        if (bits_per_index < 16 && value >= (1u << bits_per_index)) {
            free(packed);
            return NULL; /* overflow — matches C# throw */
        }
        int remaining = bits_per_index;
        size_t byte_idx = bit_pos >> 3;
        int bit_offset = (int)(bit_pos & 7);
        while (remaining > 0) {
            int take = remaining < (8 - bit_offset) ? remaining : (8 - bit_offset);
            int shift = bits_per_index - remaining;
            uint32_t chunk = (value >> shift) & ((1u << take) - 1u);
            packed[byte_idx] |= (uint8_t)((chunk << bit_offset) & 0xffu);
            remaining -= take;
            bit_offset = 0;
            byte_idx++;
        }
        bit_pos += (size_t)bits_per_index;
    }
    if (out_len) *out_len = nbytes;
    return packed;
}

uint16_t *ca_bitpacker_unpack(const uint8_t *packed, size_t packed_len,
                              size_t count, int bits_per_index) {
    if (!bp_valid_width(bits_per_index)) return NULL;
    size_t required = (count * (size_t)bits_per_index + 7) >> 3;
    if (packed_len < required) return NULL;
    uint16_t *result = (uint16_t *)calloc(count ? count : 1, sizeof(uint16_t));
    if (!result) return NULL;

    size_t bit_pos = 0;
    for (size_t i = 0; i < count; ++i) {
        int remaining = bits_per_index;
        size_t byte_idx = bit_pos >> 3;
        int bit_offset = (int)(bit_pos & 7);
        uint32_t value = 0;
        while (remaining > 0) {
            int take = remaining < (8 - bit_offset) ? remaining : (8 - bit_offset);
            int shift = bits_per_index - remaining;
            uint32_t chunk = ((uint32_t)packed[byte_idx] >> bit_offset) & ((1u << take) - 1u);
            value |= chunk << shift;
            remaining -= take;
            bit_offset = 0;
            byte_idx++;
        }
        result[i] = (uint16_t)(value & 0xffffu);
        bit_pos += (size_t)bits_per_index;
    }
    return result;
}

/* ===========================================================================
 * SeededGaussian — SplitMix64 + Box-Muller (native uint64_t)
 * =========================================================================== */

typedef struct {
    uint64_t state;
    bool     has_spare;
    double   spare;
} seeded_gaussian;

static void sg_init(seeded_gaussian *g, uint64_t seed) {
    g->state = (seed == 0) ? 0xDEADBEEFCAFEBABEULL : seed;
    g->has_spare = false;
    g->spare = 0.0;
}

static double sg_next_uniform(seeded_gaussian *g) {
    g->state += 0x9E3779B97F4A7C15ULL;
    uint64_t z = g->state;
    z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9ULL;
    z = (z ^ (z >> 27)) * 0x94D049BB133111EBULL;
    z = z ^ (z >> 31);
    /* top 53 bits → double in [0,1) */
    return (double)(z >> 11) * (1.0 / (double)(1ULL << 53));
}

static double sg_sample(seeded_gaussian *g) {
    if (g->has_spare) { g->has_spare = false; return g->spare; }
    double u, v;
    do { u = sg_next_uniform(g); } while (u <= 1e-300);
    v = sg_next_uniform(g);
    double magnitude = sqrt(-2.0 * log(u));
    double angle = 2.0 * M_PI * v;
    g->spare = magnitude * sin(angle);
    g->has_spare = true;
    return magnitude * cos(angle);
}

/* ===========================================================================
 * OrthogonalRotation — cached per-dimension matrix
 * =========================================================================== */

typedef struct rot_entry {
    int               dim;
    float            *matrix;   /* dim*dim row-major */
    struct rot_entry *next;
} rot_entry;

static rot_entry *g_rot_cache = NULL;

static double *mgs_qr(double *g, int dim) {
    double *q = (double *)calloc((size_t)dim * dim, sizeof(double));
    if (!q) return NULL;
    for (int j = 0; j < dim; ++j) {
        for (int i = 0; i < dim; ++i) q[(size_t)i*dim + j] = g[(size_t)i*dim + j];
        for (int k = 0; k < j; ++k) {
            double dot = 0.0;
            for (int i = 0; i < dim; ++i) dot += q[(size_t)i*dim + j] * q[(size_t)i*dim + k];
            for (int i = 0; i < dim; ++i) q[(size_t)i*dim + j] -= dot * q[(size_t)i*dim + k];
        }
        double norm = 0.0;
        for (int i = 0; i < dim; ++i) norm += q[(size_t)i*dim + j] * q[(size_t)i*dim + j];
        norm = sqrt(norm);
        if (norm < 1e-15) { free(q); return NULL; } /* statistically impossible */
        double inv = 1.0 / norm;
        for (int i = 0; i < dim; ++i) q[(size_t)i*dim + j] *= inv;
    }
    return q;
}

static void sign_correct_columns(double *q, int dim) {
    for (int j = 0; j < dim; ++j) {
        double diag = q[(size_t)j*dim + j];
        if (diag < 0.0)
            for (int i = 0; i < dim; ++i) q[(size_t)i*dim + j] = -q[(size_t)i*dim + j];
    }
}

static float *build_rotation_matrix(int dim) {
    double *gauss = (double *)malloc((size_t)dim * dim * sizeof(double));
    if (!gauss) return NULL;
    seeded_gaussian rng;
    sg_init(&rng, CA_ROTATION_SEED);
    for (size_t i = 0; i < (size_t)dim * dim; ++i) gauss[i] = sg_sample(&rng);

    double *q = mgs_qr(gauss, dim);
    free(gauss);
    if (!q) return NULL;
    sign_correct_columns(q, dim);

    float *result = (float *)malloc((size_t)dim * dim * sizeof(float));
    if (!result) { free(q); return NULL; }
    for (size_t i = 0; i < (size_t)dim * dim; ++i) result[i] = (float)q[i];
    free(q);
    return result;
}

const float *ca_orthogonal_rotation_matrix(int dim) {
    if (dim <= 0) return NULL;
    for (rot_entry *e = g_rot_cache; e; e = e->next)
        if (e->dim == dim) return e->matrix;
    float *m = build_rotation_matrix(dim);
    if (!m) return NULL;
    rot_entry *e = (rot_entry *)malloc(sizeof(*e));
    if (!e) { free(m); return NULL; }
    e->dim = dim;
    e->matrix = m;
    e->next = g_rot_cache;
    g_rot_cache = e;
    return m;
}

void ca_orthogonal_rotation_rotate(int dim, const float *vector, float *output) {
    const float *matrix = ca_orthogonal_rotation_matrix(dim);
    if (!matrix) return;
    for (int i = 0; i < dim; ++i) {
        float sum = 0.0f;                 /* fp32 accumulator — matches C# */
        int row = i * dim;
        for (int j = 0; j < dim; ++j) sum += matrix[row + j] * vector[j];
        output[i] = sum;
    }
}

void ca_orthogonal_rotation_unrotate(int dim, const float *vector, float *output) {
    const float *matrix = ca_orthogonal_rotation_matrix(dim);
    if (!matrix) return;
    for (int i = 0; i < dim; ++i) {
        float sum = 0.0f;
        for (int j = 0; j < dim; ++j) sum += matrix[j * dim + i] * vector[j];
        output[i] = sum;
    }
}

void ca_orthogonal_rotation_clear_cache(void) {
    rot_entry *e = g_rot_cache;
    while (e) { rot_entry *n = e->next; free(e->matrix); free(e); e = n; }
    g_rot_cache = NULL;
}

/* ===========================================================================
 * BetaLloydMaxCodebook — Lanczos logΓ, regularized incomplete beta, adaptive
 * Simpson, Lloyd-Max
 * =========================================================================== */

static const double LANCZOS_G7[9] = {
    0.99999999999980993, 676.5203681218851, -1259.1392167224028,
    771.32342877765313, -176.61502916214059, 12.507343278686905,
    -0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7
};

static double log_gamma(double x) {
    if (x < 0.5)
        return log(M_PI / sin(M_PI * x)) - log_gamma(1.0 - x);
    x -= 1.0;
    double t = x + 7.5;
    double sum = LANCZOS_G7[0];
    for (int i = 1; i < 9; ++i) sum += LANCZOS_G7[i] / (x + i);
    return 0.5 * log(2.0 * M_PI) + (x + 0.5) * log(t) - t + log(sum);
}

static double log_beta(double a, double b) {
    return log_gamma(a) + log_gamma(b) - log_gamma(a + b);
}

static double beta_continued_fraction(double a, double b, double x) {
    const int maxIter = 200;
    const double eps = 3e-15;
    const double fpmin = 1e-300;
    double qab = a + b, qap = a + 1.0, qam = a - 1.0;
    double c = 1.0;
    double d = 1.0 - qab * x / qap;
    if (fabs(d) < fpmin) d = fpmin;
    d = 1.0 / d;
    double h = d;
    for (int m = 1; m <= maxIter; ++m) {
        int m2 = 2 * m;
        double aa = (double)m * (b - m) * x / ((qam + m2) * (a + m2));
        d = 1.0 + aa * d; if (fabs(d) < fpmin) d = fpmin;
        c = 1.0 + aa / c; if (fabs(c) < fpmin) c = fpmin;
        d = 1.0 / d;
        h *= d * c;
        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
        d = 1.0 + aa * d; if (fabs(d) < fpmin) d = fpmin;
        c = 1.0 + aa / c; if (fabs(c) < fpmin) c = fpmin;
        d = 1.0 / d;
        double delta = d * c;
        h *= delta;
        if (fabs(delta - 1.0) < eps) return h;
    }
    return h;
}

static double regularized_incomplete_beta(double a, double b, double x) {
    if (x < 0.0 || x > 1.0) return 0.0; /* guarded by callers */
    if (x == 0.0 || x == 1.0) return x;
    double bt = exp(log_gamma(a + b) - log_gamma(a) - log_gamma(b)
                    + a * log(x) + b * log(1.0 - x));
    if (x < (a + 1.0) / (a + b + 2.0))
        return bt * beta_continued_fraction(a, b, x) / a;
    return 1.0 - bt * beta_continued_fraction(b, a, 1.0 - x) / b;
}

static double beta_pdf_symmetric(double a, double x) {
    if (x <= 0.0 || x >= 1.0) return 0.0;
    double logPdf = (a - 1.0) * log(x) + (a - 1.0) * log(1.0 - x) - log_beta(a, a);
    return exp(logPdf);
}

static double beta_cdf_symmetric(double a, double x) {
    if (x <= 0.0) return 0.0;
    if (x >= 1.0) return 1.0;
    return regularized_incomplete_beta(a, a, x);
}

/* Adaptive Simpson — must match the C# recursion exactly (integrand captured via
 * the shape parameter a). */
static double simpson_integrand(double a, double x) {
    return x * beta_pdf_symmetric(a, (x + 1.0) / 2.0) / 2.0;
}

static double adaptive_simpson_rec(double a, double lo, double hi,
                                   double fa, double fb, double fm,
                                   double whole, double tol, int depth) {
    double mid = (lo + hi) / 2.0;
    double m1 = (lo + mid) / 2.0;
    double m2 = (mid + hi) / 2.0;
    double fm1 = simpson_integrand(a, m1);
    double fm2 = simpson_integrand(a, m2);
    double left = (mid - lo) / 6.0 * (fa + 4.0 * fm1 + fm);
    double right = (hi - mid) / 6.0 * (fm + 4.0 * fm2 + fb);
    double refined = left + right;
    if (depth == 0 || fabs(refined - whole) < 15.0 * tol)
        return refined + (refined - whole) / 15.0;
    return adaptive_simpson_rec(a, lo, mid, fa, fm, fm1, left, tol / 2.0, depth - 1)
         + adaptive_simpson_rec(a, mid, hi, fm, fb, fm2, right, tol / 2.0, depth - 1);
}

static double adaptive_simpson(double a, double lo, double hi, double tol, int maxDepth) {
    double mid = (lo + hi) / 2.0;
    double fa = simpson_integrand(a, lo);
    double fb = simpson_integrand(a, hi);
    double fm = simpson_integrand(a, mid);
    double whole = (hi - lo) / 6.0 * (fa + 4.0 * fm + fb);
    return adaptive_simpson_rec(a, lo, hi, fa, fb, fm, whole, tol, maxDepth);
}

typedef struct cb_entry {
    int              bits, dim;
    float           *boundaries; size_t boundaries_len;
    float           *centroids;  size_t centroids_len;
    struct cb_entry *next;
} cb_entry;

static cb_entry *g_cb_cache = NULL;

static cb_entry *compute_codebook(int bits, int dim) {
    const int maxIter = 200;
    const double tol = 1e-12;
    double a = (dim - 1.0) / 2.0;
    int nLevels = 1 << bits;

    double std = sqrt(2.0 * a / ((2.0 * a + 1.0) * 4.0 * a));
    double spread = 3.0 * std;
    double *centroids = (double *)malloc((size_t)nLevels * sizeof(double));
    if (!centroids) return NULL;
    for (int i = 0; i < nLevels; ++i)
        centroids[i] = -spread + 2.0 * spread * i / (nLevels - 1);

    double *boundaries = (double *)malloc((size_t)(nLevels - 1) * sizeof(double));
    double *edges = (double *)malloc((size_t)(nLevels + 1) * sizeof(double));
    double *newCentroids = (double *)malloc((size_t)nLevels * sizeof(double));
    if (!boundaries || !edges || !newCentroids) {
        free(centroids); free(boundaries); free(edges); free(newCentroids); return NULL;
    }

    for (int iter = 0; iter < maxIter; ++iter) {
        for (int i = 0; i < nLevels - 1; ++i)
            boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0;
        edges[0] = -1.0;
        for (int i = 0; i < nLevels - 1; ++i) edges[i + 1] = boundaries[i];
        edges[nLevels] = 1.0;

        for (int i = 0; i < nLevels; ++i) {
            double lo = edges[i], hi = edges[i + 1];
            double cdfLo = beta_cdf_symmetric(a, (lo + 1.0) / 2.0);
            double cdfHi = beta_cdf_symmetric(a, (hi + 1.0) / 2.0);
            double prob = cdfHi - cdfLo;
            if (prob < 1e-15) {
                newCentroids[i] = centroids[i];
            } else {
                double mean = adaptive_simpson(a, lo, hi, 1e-14, 50);
                newCentroids[i] = mean / prob;
            }
        }

        double maxChange = 0.0;
        for (int i = 0; i < nLevels; ++i) {
            double ch = fabs(centroids[i] - newCentroids[i]);
            if (ch > maxChange) maxChange = ch;
            centroids[i] = newCentroids[i];
        }
        if (maxChange < tol) break;
    }

    cb_entry *e = (cb_entry *)malloc(sizeof(*e));
    if (!e) { free(centroids); free(boundaries); free(edges); free(newCentroids); return NULL; }
    e->bits = bits; e->dim = dim;
    e->boundaries_len = (size_t)(nLevels - 1);
    e->centroids_len = (size_t)nLevels;
    e->boundaries = (float *)malloc(e->boundaries_len * sizeof(float));
    e->centroids  = (float *)malloc(e->centroids_len * sizeof(float));
    if (!e->boundaries || !e->centroids) {
        free(e->boundaries); free(e->centroids); free(e);
        free(centroids); free(boundaries); free(edges); free(newCentroids);
        return NULL;
    }
    for (int i = 0; i < nLevels - 1; ++i)
        e->boundaries[i] = (float)((centroids[i] + centroids[i + 1]) / 2.0);
    for (int i = 0; i < nLevels; ++i)
        e->centroids[i] = (float)centroids[i];

    free(centroids); free(boundaries); free(edges); free(newCentroids);
    e->next = g_cb_cache;
    g_cb_cache = e;
    return e;
}

bool ca_beta_codebook_get(int bits, int dim, ca_beta_codebook_t *out) {
    if (!out) return false;
    if (bits < 1 || bits > 8) return false;
    if (dim <= 1) return false;
    for (cb_entry *e = g_cb_cache; e; e = e->next) {
        if (e->bits == bits && e->dim == dim) {
            out->boundaries = e->boundaries; out->boundaries_len = e->boundaries_len;
            out->centroids = e->centroids;   out->centroids_len = e->centroids_len;
            return true;
        }
    }
    cb_entry *e = compute_codebook(bits, dim);
    if (!e) return false;
    out->boundaries = e->boundaries; out->boundaries_len = e->boundaries_len;
    out->centroids = e->centroids;   out->centroids_len = e->centroids_len;
    return true;
}

uint16_t ca_beta_codebook_bin_for(float value, const float *boundaries, size_t n) {
    for (size_t i = 0; i < n; ++i)
        if (value < boundaries[i]) return (uint16_t)i;
    return (uint16_t)n;
}

void ca_beta_codebook_clear_cache(void) {
    cb_entry *e = g_cb_cache;
    while (e) { cb_entry *n = e->next; free(e->boundaries); free(e->centroids); free(e); e = n; }
    g_cb_cache = NULL;
}

/* ===========================================================================
 * TurboQuantCodec
 * =========================================================================== */

void ca_turboquant_payload_free(ca_turboquant_payload_t *p) {
    if (!p) return;
    free(p->packed_indices);
    p->packed_indices = NULL;
    p->packed_len = 0;
    p->norm = 0.0f;
}

bool ca_turboquant_encode(const float *vector, size_t dim, int bits_per_dim,
                          ca_turboquant_payload_t *out) {
    if (!out) return false;
    out->norm = 0.0f; out->packed_indices = NULL; out->packed_len = 0;
    if (!vector || dim <= 1) return false;
    if (bits_per_dim < 1 || bits_per_dim > 8) return false;

    /* 1. Norm — accumulate in double, cast to float (C# parity). */
    double sumSq = 0.0;
    for (size_t i = 0; i < dim; ++i) sumSq += (double)vector[i] * vector[i];
    float norm = (float)sqrt(sumSq);

    /* Edge case — zero vector. */
    if (norm < 1e-20f) {
        size_t nbytes = (dim * (size_t)bits_per_dim + 7) >> 3;
        out->packed_indices = (uint8_t *)calloc(nbytes ? nbytes : 1, 1);
        if (!out->packed_indices) return false;
        out->packed_len = nbytes;
        out->norm = 0.0f;
        return true;
    }

    /* 2. Unit-normalise (fp32). */
    float *unit = (float *)malloc(dim * sizeof(float));
    if (!unit) return false;
    float invNorm = 1.0f / norm;
    for (size_t i = 0; i < dim; ++i) unit[i] = vector[i] * invNorm;

    /* 3. Rotate. */
    float *rotated = (float *)malloc(dim * sizeof(float));
    if (!rotated) { free(unit); return false; }
    ca_orthogonal_rotation_rotate((int)dim, unit, rotated);
    free(unit);

    /* 4. Quantize per-coordinate. */
    ca_beta_codebook_t cb;
    if (!ca_beta_codebook_get(bits_per_dim, (int)dim, &cb)) { free(rotated); return false; }
    uint16_t *indices = (uint16_t *)malloc(dim * sizeof(uint16_t));
    if (!indices) { free(rotated); return false; }
    for (size_t i = 0; i < dim; ++i)
        indices[i] = ca_beta_codebook_bin_for(rotated[i], cb.boundaries, cb.boundaries_len);
    free(rotated);

    /* 5. Pack. */
    size_t packed_len = 0;
    uint8_t *packed = ca_bitpacker_pack(indices, dim, bits_per_dim, &packed_len);
    free(indices);
    if (!packed) return false;

    out->norm = norm;
    out->packed_indices = packed;
    out->packed_len = packed_len;
    return true;
}

float *ca_turboquant_decode(const ca_turboquant_payload_t *payload,
                            int dim, int bits_per_dim) {
    if (!payload || dim <= 1) return NULL;
    if (bits_per_dim < 1 || bits_per_dim > 8) return NULL;

    float *result = (float *)calloc((size_t)dim, sizeof(float));
    if (!result) return NULL;
    if (payload->norm == 0.0f) return result; /* all zeros */

    uint16_t *indices = ca_bitpacker_unpack(payload->packed_indices, payload->packed_len,
                                            (size_t)dim, bits_per_dim);
    if (!indices) { free(result); return NULL; }

    ca_beta_codebook_t cb;
    if (!ca_beta_codebook_get(bits_per_dim, dim, &cb)) { free(indices); free(result); return NULL; }

    float *rotated = (float *)malloc((size_t)dim * sizeof(float));
    if (!rotated) { free(indices); free(result); return NULL; }
    for (int i = 0; i < dim; ++i) rotated[i] = cb.centroids[indices[i]];
    free(indices);

    float *unit = (float *)malloc((size_t)dim * sizeof(float));
    if (!unit) { free(rotated); free(result); return NULL; }
    ca_orthogonal_rotation_unrotate(dim, rotated, unit);
    free(rotated);

    float scale = payload->norm;
    for (int i = 0; i < dim; ++i) result[i] = unit[i] * scale;
    free(unit);
    return result;
}

size_t ca_turboquant_payload_byte_count(int dim, int bits_per_dim) {
    return ((size_t)dim * (size_t)bits_per_dim + 7) >> 3;
}

double ca_turboquant_compression_ratio(int dim, int bits_per_dim) {
    double raw = (double)dim * 4.0;
    double encoded = (double)ca_turboquant_payload_byte_count(dim, bits_per_dim) + 4.0;
    return raw / encoded;
}

/* ===========================================================================
 * EmbeddingPayloadCodec + base64
 * =========================================================================== */

const uint8_t CA_TQ_MAGIC[4] = { 0x54, 0x51, 0x33, 0x01 };

static void write_u32_le(uint8_t *p, uint32_t v) {
    p[0] = (uint8_t)(v & 0xff);
    p[1] = (uint8_t)((v >> 8) & 0xff);
    p[2] = (uint8_t)((v >> 16) & 0xff);
    p[3] = (uint8_t)((v >> 24) & 0xff);
}

static uint32_t read_u32_le(const uint8_t *p) {
    return (uint32_t)p[0] | ((uint32_t)p[1] << 8) |
           ((uint32_t)p[2] << 16) | ((uint32_t)p[3] << 24);
}

static void write_f32_le(uint8_t *p, float f) {
    uint32_t bits;
    memcpy(&bits, &f, 4);
    write_u32_le(p, bits);
}

static float read_f32_le(const uint8_t *p) {
    uint32_t bits = read_u32_le(p);
    float f;
    memcpy(&f, &bits, 4);
    return f;
}

uint8_t *ca_embedding_payload_encode(const float *vector, size_t dim,
                                     int bits_per_dim, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!vector || dim <= 1) return NULL;

    ca_turboquant_payload_t payload;
    if (!ca_turboquant_encode(vector, dim, bits_per_dim, &payload)) return NULL;

    size_t total = 4 + 4 + 4 + 4 + payload.packed_len;
    uint8_t *buf = (uint8_t *)malloc(total);
    if (!buf) { ca_turboquant_payload_free(&payload); return NULL; }

    size_t o = 0;
    memcpy(buf, CA_TQ_MAGIC, 4); o += 4;
    write_u32_le(buf + o, (uint32_t)bits_per_dim); o += 4;
    write_u32_le(buf + o, (uint32_t)dim); o += 4;
    write_f32_le(buf + o, payload.norm); o += 4;
    if (payload.packed_len) memcpy(buf + o, payload.packed_indices, payload.packed_len);

    ca_turboquant_payload_free(&payload);
    if (out_len) *out_len = total;
    return buf;
}

bool ca_embedding_payload_is_encoded(const uint8_t *bytes, size_t len) {
    return bytes && len >= 4 &&
           bytes[0] == CA_TQ_MAGIC[0] && bytes[1] == CA_TQ_MAGIC[1] &&
           bytes[2] == CA_TQ_MAGIC[2] && bytes[3] == CA_TQ_MAGIC[3];
}

float *ca_embedding_payload_decode(const uint8_t *bytes, size_t len, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!bytes || len < 4 + 12) return NULL;      /* too short */
    if (!ca_embedding_payload_is_encoded(bytes, len)) return NULL; /* bad magic */

    size_t o = 4;
    int bits_per_dim = (int)read_u32_le(bytes + o); o += 4;
    int dim = (int)read_u32_le(bytes + o); o += 4;
    float norm = read_f32_le(bytes + o); o += 4;

    ca_turboquant_payload_t payload;
    payload.norm = norm;
    payload.packed_len = len - o;
    payload.packed_indices = (uint8_t *)malloc(payload.packed_len ? payload.packed_len : 1);
    if (!payload.packed_indices) return NULL;
    if (payload.packed_len) memcpy(payload.packed_indices, bytes + o, payload.packed_len);

    float *decoded = ca_turboquant_decode(&payload, dim, bits_per_dim);
    ca_turboquant_payload_free(&payload);
    if (!decoded) return NULL;
    if (out_len) *out_len = (size_t)dim;
    return decoded;
}

/* ── standard base64 (RFC 4648) ── */

static const char B64_ENC[] =
    "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

char *ca_base64_encode(const uint8_t *data, size_t len) {
    size_t olen = 4 * ((len + 2) / 3);
    char *out = (char *)malloc(olen + 1);
    if (!out) return NULL;
    size_t i = 0, j = 0;
    while (i + 3 <= len) {
        uint32_t n = ((uint32_t)data[i] << 16) | ((uint32_t)data[i+1] << 8) | data[i+2];
        out[j++] = B64_ENC[(n >> 18) & 63];
        out[j++] = B64_ENC[(n >> 12) & 63];
        out[j++] = B64_ENC[(n >> 6) & 63];
        out[j++] = B64_ENC[n & 63];
        i += 3;
    }
    size_t rem = len - i;
    if (rem == 1) {
        uint32_t n = (uint32_t)data[i] << 16;
        out[j++] = B64_ENC[(n >> 18) & 63];
        out[j++] = B64_ENC[(n >> 12) & 63];
        out[j++] = '=';
        out[j++] = '=';
    } else if (rem == 2) {
        uint32_t n = ((uint32_t)data[i] << 16) | ((uint32_t)data[i+1] << 8);
        out[j++] = B64_ENC[(n >> 18) & 63];
        out[j++] = B64_ENC[(n >> 12) & 63];
        out[j++] = B64_ENC[(n >> 6) & 63];
        out[j++] = '=';
    }
    out[j] = '\0';
    return out;
}

static int b64_val(char c) {
    if (c >= 'A' && c <= 'Z') return c - 'A';
    if (c >= 'a' && c <= 'z') return c - 'a' + 26;
    if (c >= '0' && c <= '9') return c - '0' + 52;
    if (c == '+') return 62;
    if (c == '/') return 63;
    return -1;
}

uint8_t *ca_base64_decode(const char *b64, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!b64) return NULL;
    size_t slen = strlen(b64);
    /* gather valid symbols (ignore whitespace); count padding at the end */
    uint8_t *out = (uint8_t *)malloc(slen / 4 * 3 + 3);
    if (!out) return NULL;
    int quad[4];
    int qn = 0;
    size_t j = 0;
    for (size_t i = 0; i < slen; ++i) {
        char c = b64[i];
        if (c == '=' ) { break; }
        if (c == '\r' || c == '\n' || c == ' ' || c == '\t') continue;
        int v = b64_val(c);
        if (v < 0) { free(out); return NULL; } /* malformed */
        quad[qn++] = v;
        if (qn == 4) {
            uint32_t n = ((uint32_t)quad[0] << 18) | ((uint32_t)quad[1] << 12) |
                         ((uint32_t)quad[2] << 6) | (uint32_t)quad[3];
            out[j++] = (uint8_t)((n >> 16) & 0xff);
            out[j++] = (uint8_t)((n >> 8) & 0xff);
            out[j++] = (uint8_t)(n & 0xff);
            qn = 0;
        }
    }
    if (qn == 1) { free(out); return NULL; } /* invalid trailing symbol */
    if (qn == 2) {
        uint32_t n = ((uint32_t)quad[0] << 18) | ((uint32_t)quad[1] << 12);
        out[j++] = (uint8_t)((n >> 16) & 0xff);
    } else if (qn == 3) {
        uint32_t n = ((uint32_t)quad[0] << 18) | ((uint32_t)quad[1] << 12) |
                     ((uint32_t)quad[2] << 6);
        out[j++] = (uint8_t)((n >> 16) & 0xff);
        out[j++] = (uint8_t)((n >> 8) & 0xff);
    }
    if (out_len) *out_len = j;
    return out;
}

char *ca_embedding_payload_encode_base64(const float *vector, size_t dim, int bits_per_dim) {
    size_t len = 0;
    uint8_t *bytes = ca_embedding_payload_encode(vector, dim, bits_per_dim, &len);
    if (!bytes) return NULL;
    char *b64 = ca_base64_encode(bytes, len);
    free(bytes);
    return b64;
}

float *ca_embedding_payload_decode_base64(const char *base64, size_t *out_len) {
    if (out_len) *out_len = 0;
    if (!base64) return NULL;
    size_t len = 0;
    uint8_t *bytes = ca_base64_decode(base64, &len);
    if (!bytes) return NULL;
    float *decoded = ca_embedding_payload_decode(bytes, len, out_len);
    free(bytes);
    return decoded;
}

/* ===========================================================================
 * Shared cosine for the decorators (matches C#/TS store CosineSimilarity.Score)
 * =========================================================================== */

static double comp_cosine(const float *a, size_t alen, const float *b, size_t blen) {
    if (alen != blen || alen == 0) return 0.0;
    double dot = 0.0, ma = 0.0, mb = 0.0;
    for (size_t i = 0; i < alen; ++i) {
        dot += (double)a[i] * b[i];
        ma  += (double)a[i] * a[i];
        mb  += (double)b[i] * b[i];
    }
    double denom = sqrt(ma) * sqrt(mb);
    if (denom < 2.220446049250313e-16) return 0.0;
    return dot / denom;
}

/* ===========================================================================
 * CompressedEpisodicMemoryStore — decorator
 * =========================================================================== */

struct ca_compressed_episodic_store {
    ca_episodic_store_t *inner; /* borrowed */
    int                  bits_per_dim;
};

ca_compressed_episodic_store_t *ca_compressed_episodic_store_create(
    ca_episodic_store_t *inner, int bits_per_dim) {
    if (!inner) return NULL;
    if (bits_per_dim < 1 || bits_per_dim > 8) return NULL;
    ca_compressed_episodic_store_t *s =
        (ca_compressed_episodic_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->inner = inner;
    s->bits_per_dim = bits_per_dim;
    return s;
}

void ca_compressed_episodic_store_destroy(ca_compressed_episodic_store_t *s) {
    free(s);
}

/* Build a copy of `src` entry with the embedding dropped and a compressed-tag
 * added (upsert on the tag key). Ownership: fills *dst which the caller frees
 * with ca_episodic_entry_free. Requires src->embedding with len > 1. */
static char *ep_dup(const char *x) {
    if (!x) return NULL;
    size_t n = strlen(x) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, x, n);
    return p;
}

static bool episodic_rewrite_compressed(const ca_episodic_entry_t *src, int bits,
                                        ca_episodic_entry_t *dst) {
    memset(dst, 0, sizeof(*dst));
    dst->id             = ep_dup(src->id);
    dst->recorded_at_ms = src->recorded_at_ms;
    dst->user_text      = ep_dup(src->user_text);
    dst->assistant_text = ep_dup(src->assistant_text);
    dst->app_context    = ep_dup(src->app_context);
    dst->embedding      = NULL;         /* dropped — lives in tags */
    dst->embedding_len  = 0;

    char *b64 = ca_embedding_payload_encode_base64(src->embedding, src->embedding_len, bits);
    if (!b64) return false;

    /* copy existing tags, replacing/adding the compressed key */
    size_t base = src->tag_count;
    bool has_key = false;
    for (size_t i = 0; i < base; ++i)
        if (src->tag_keys[i] && strcmp(src->tag_keys[i], CA_COMPRESSED_TAG_KEY) == 0) { has_key = true; break; }
    size_t total = base + (has_key ? 0 : 1);
    dst->tag_keys   = (char **)calloc(total, sizeof(char *));
    dst->tag_values = (char **)calloc(total, sizeof(char *));
    dst->tag_count  = total;
    size_t w = 0;
    for (size_t i = 0; i < base; ++i) {
        if (src->tag_keys[i] && strcmp(src->tag_keys[i], CA_COMPRESSED_TAG_KEY) == 0) {
            dst->tag_keys[w]   = ep_dup(CA_COMPRESSED_TAG_KEY);
            dst->tag_values[w] = b64; /* take ownership */
            b64 = NULL;
        } else {
            dst->tag_keys[w]   = ep_dup(src->tag_keys[i]);
            dst->tag_values[w] = ep_dup(src->tag_values[i]);
        }
        w++;
    }
    if (!has_key) {
        dst->tag_keys[w]   = ep_dup(CA_COMPRESSED_TAG_KEY);
        dst->tag_values[w] = b64; /* take ownership */
        b64 = NULL;
        w++;
    }
    free(b64); /* NULL if consumed */
    return true;
}

bool ca_compressed_episodic_store_add(ca_compressed_episodic_store_t *s,
                                      const ca_episodic_entry_t *entry) {
    if (!s || !entry) return false;
    if (entry->embedding && entry->embedding_len > 1) {
        ca_episodic_entry_t rewritten;
        if (!episodic_rewrite_compressed(entry, s->bits_per_dim, &rewritten)) return false;
        bool ok = ca_episodic_store_add(s->inner, &rewritten);
        ca_episodic_entry_free(&rewritten);
        return ok;
    }
    return ca_episodic_store_add(s->inner, entry);
}

/* Rehydrate: if an entry has no embedding but carries the compressed tag, decode
 * it into entry->embedding (mutates the deep copy in place). */
static void episodic_rehydrate(ca_episodic_entry_t *e) {
    if (e->embedding && e->embedding_len > 0) return;
    const char *b64 = ca_episodic_entry_get_tag(e, CA_COMPRESSED_TAG_KEY);
    if (!b64) return;
    size_t len = 0;
    float *floats = ca_embedding_payload_decode_base64(b64, &len);
    if (!floats) return; /* malformed — leave as-is */
    e->embedding = floats;
    e->embedding_len = len;
}

ca_episodic_entry_t *ca_compressed_episodic_store_get_recent(
    ca_compressed_episodic_store_t *s, int count, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s) return NULL;
    size_t n = 0;
    ca_episodic_entry_t *arr = ca_episodic_store_get_recent(s->inner, count, &n);
    for (size_t i = 0; i < n; ++i) episodic_rehydrate(&arr[i]);
    if (out_count) *out_count = n;
    return arr;
}

ca_episodic_entry_t *ca_compressed_episodic_store_search(
    ca_compressed_episodic_store_t *s, const float *query, size_t query_len,
    int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s) return NULL;
    if (top_k <= 0) top_k = 5;

    /* Inner store never has embeddings (they live in tags), so we cannot defer to
     * its cosine. Pull ALL, rehydrate, then rank here — mirrors the C#/TS
     * decorator. */
    size_t n = 0;
    ca_episodic_entry_t *all = ca_episodic_store_get_recent(s->inner, (int)ca_episodic_store_count(s->inner), &n);
    if (n == 0) { if (out_count) *out_count = 0; ca_episodic_entry_free_array(all, n); return NULL; }
    for (size_t i = 0; i < n; ++i) episodic_rehydrate(&all[i]);

    size_t take;
    ca_episodic_entry_t *out;

    if (!query || query_len == 0) {
        /* get_recent already newest-first; take top_k */
        take = (size_t)top_k < n ? (size_t)top_k : n;
        out = (ca_episodic_entry_t *)malloc(take * sizeof(*out));
        if (!out) { ca_episodic_entry_free_array(all, n); return NULL; }
        for (size_t i = 0; i < take; ++i) out[i] = all[i];       /* move */
        for (size_t i = take; i < n; ++i) ca_episodic_entry_free(&all[i]);
        free(all);
        if (out_count) *out_count = take;
        return out;
    }

    /* cosine rank among entries with an embedding */
    typedef struct { size_t idx; double score; } scored_t;
    scored_t *sc = (scored_t *)malloc(n * sizeof(*sc));
    if (!sc) { ca_episodic_entry_free_array(all, n); return NULL; }
    size_t m = 0;
    for (size_t i = 0; i < n; ++i) {
        if (all[i].embedding && all[i].embedding_len > 0) {
            sc[m].idx = i;
            sc[m].score = comp_cosine(query, query_len, all[i].embedding, all[i].embedding_len);
            m++;
        }
    }
    /* stable insertion sort desc */
    for (size_t i = 1; i < m; ++i) {
        scored_t key = sc[i]; size_t j = i;
        while (j > 0 && sc[j-1].score < key.score) { sc[j] = sc[j-1]; --j; }
        sc[j] = key;
    }
    take = (size_t)top_k < m ? (size_t)top_k : m;
    out = take ? (ca_episodic_entry_t *)malloc(take * sizeof(*out)) : NULL;
    /* mark which we keep */
    bool *kept = (bool *)calloc(n, sizeof(bool));
    for (size_t i = 0; i < take; ++i) { out[i] = all[sc[i].idx]; kept[sc[i].idx] = true; }
    for (size_t i = 0; i < n; ++i) if (!kept[i]) ca_episodic_entry_free(&all[i]);
    free(kept);
    free(sc);
    free(all);
    if (out_count) *out_count = take;
    return out;
}

size_t ca_compressed_episodic_store_count(const ca_compressed_episodic_store_t *s) {
    return s ? ca_episodic_store_count(s->inner) : 0;
}

size_t ca_compressed_episodic_store_prune_older_than(ca_compressed_episodic_store_t *s,
                                                     int64_t cutoff_ms) {
    return s ? ca_episodic_store_prune_older_than(s->inner, cutoff_ms) : 0;
}

/* ===========================================================================
 * CompressedMultimodalMemoryStore — decorator
 * =========================================================================== */

struct ca_compressed_multimodal_store {
    ca_multimodal_store_t *inner; /* borrowed */
    int                    bits_per_dim;
};

ca_compressed_multimodal_store_t *ca_compressed_multimodal_store_create(
    ca_multimodal_store_t *inner, int bits_per_dim) {
    if (!inner) return NULL;
    if (bits_per_dim < 1 || bits_per_dim > 8) return NULL;
    ca_compressed_multimodal_store_t *s =
        (ca_compressed_multimodal_store_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->inner = inner;
    s->bits_per_dim = bits_per_dim;
    return s;
}

void ca_compressed_multimodal_store_destroy(ca_compressed_multimodal_store_t *s) {
    free(s);
}

static bool multimodal_rewrite_compressed(const ca_multimodal_entry_t *src, int bits,
                                          ca_multimodal_entry_t *dst) {
    /* shallow-copy scalar/string fields, drop embedding, add compressed tag */
    memset(dst, 0, sizeof(*dst));
    dst->id                = ep_dup(src->id);
    dst->recorded_at_ms    = src->recorded_at_ms;
    dst->modality          = src->modality;
    dst->caption           = ep_dup(src->caption);
    dst->embedding         = NULL;
    dst->embedding_len     = 0;
    dst->source_sha256     = ep_dup(src->source_sha256);
    dst->source_mime_type  = ep_dup(src->source_mime_type);
    dst->source_byte_count = src->source_byte_count;
    dst->source_uri        = ep_dup(src->source_uri);
    dst->has_width  = src->has_width;  dst->width_px  = src->width_px;
    dst->has_height = src->has_height; dst->height_px = src->height_px;
    dst->has_duration = src->has_duration; dst->duration_ms = src->duration_ms;
    dst->reference_count   = src->reference_count;

    char *b64 = ca_embedding_payload_encode_base64(src->embedding, src->embedding_len, bits);
    if (!b64) return false;

    size_t base = src->tag_count;
    bool has_key = false;
    for (size_t i = 0; i < base; ++i)
        if (src->tag_keys[i] && strcmp(src->tag_keys[i], CA_COMPRESSED_TAG_KEY) == 0) { has_key = true; break; }
    size_t total = base + (has_key ? 0 : 1);
    dst->tag_keys   = (char **)calloc(total, sizeof(char *));
    dst->tag_values = (char **)calloc(total, sizeof(char *));
    dst->tag_count  = total;
    size_t w = 0;
    for (size_t i = 0; i < base; ++i) {
        if (src->tag_keys[i] && strcmp(src->tag_keys[i], CA_COMPRESSED_TAG_KEY) == 0) {
            dst->tag_keys[w]   = ep_dup(CA_COMPRESSED_TAG_KEY);
            dst->tag_values[w] = b64; b64 = NULL;
        } else {
            dst->tag_keys[w]   = ep_dup(src->tag_keys[i]);
            dst->tag_values[w] = ep_dup(src->tag_values[i]);
        }
        w++;
    }
    if (!has_key) {
        dst->tag_keys[w]   = ep_dup(CA_COMPRESSED_TAG_KEY);
        dst->tag_values[w] = b64; b64 = NULL;
        w++;
    }
    free(b64);
    return true;
}

bool ca_compressed_multimodal_store_add(ca_compressed_multimodal_store_t *s,
                                        const ca_multimodal_entry_t *entry) {
    if (!s || !entry) return false;
    if (entry->embedding && entry->embedding_len > 1) {
        ca_multimodal_entry_t rewritten;
        if (!multimodal_rewrite_compressed(entry, s->bits_per_dim, &rewritten)) return false;
        bool ok = ca_multimodal_store_add(s->inner, &rewritten);
        ca_multimodal_entry_free(&rewritten);
        return ok;
    }
    return ca_multimodal_store_add(s->inner, entry);
}

static void multimodal_rehydrate(ca_multimodal_entry_t *e) {
    if (e->embedding && e->embedding_len > 0) return;
    const char *b64 = ca_multimodal_entry_get_tag(e, CA_COMPRESSED_TAG_KEY);
    if (!b64) return;
    size_t len = 0;
    float *floats = ca_embedding_payload_decode_base64(b64, &len);
    if (!floats) return;
    e->embedding = floats;
    e->embedding_len = len;
}

bool ca_compressed_multimodal_store_get_by_hash(ca_compressed_multimodal_store_t *s,
                                                const char *source_sha256,
                                                ca_multimodal_entry_t *out) {
    if (!s || !out) return false;
    if (!ca_multimodal_store_get_by_hash(s->inner, source_sha256, out)) return false;
    multimodal_rehydrate(out);
    return true;
}

void ca_compressed_multimodal_store_reinforce(ca_compressed_multimodal_store_t *s,
                                              const char *source_sha256) {
    if (s) ca_multimodal_store_reinforce(s->inner, source_sha256);
}

ca_multimodal_entry_t *ca_compressed_multimodal_store_get_recent(
    ca_compressed_multimodal_store_t *s, int count, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s) return NULL;
    size_t n = 0;
    ca_multimodal_entry_t *arr = ca_multimodal_store_get_recent(s->inner, count, &n);
    for (size_t i = 0; i < n; ++i) multimodal_rehydrate(&arr[i]);
    if (out_count) *out_count = n;
    return arr;
}

ca_multimodal_entry_t *ca_compressed_multimodal_store_search(
    ca_compressed_multimodal_store_t *s, const float *query, size_t query_len,
    int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!s) return NULL;
    if (top_k <= 0) top_k = 5;

    size_t n = 0;
    ca_multimodal_entry_t *all =
        ca_multimodal_store_get_recent(s->inner, (int)ca_multimodal_store_count(s->inner), &n);
    if (n == 0) { ca_multimodal_entry_free_array(all, n); return NULL; }
    for (size_t i = 0; i < n; ++i) multimodal_rehydrate(&all[i]);

    if (!query || query_len == 0) {
        size_t take = (size_t)top_k < n ? (size_t)top_k : n;
        ca_multimodal_entry_t *out = (ca_multimodal_entry_t *)malloc(take * sizeof(*out));
        if (!out) { ca_multimodal_entry_free_array(all, n); return NULL; }
        for (size_t i = 0; i < take; ++i) out[i] = all[i];
        for (size_t i = take; i < n; ++i) ca_multimodal_entry_free(&all[i]);
        free(all);
        if (out_count) *out_count = take;
        return out;
    }

    typedef struct { size_t idx; double score; } scored_t;
    scored_t *sc = (scored_t *)malloc(n * sizeof(*sc));
    if (!sc) { ca_multimodal_entry_free_array(all, n); return NULL; }
    size_t m = 0;
    for (size_t i = 0; i < n; ++i) {
        if (all[i].embedding && all[i].embedding_len > 0) {
            sc[m].idx = i;
            sc[m].score = comp_cosine(query, query_len, all[i].embedding, all[i].embedding_len);
            m++;
        }
    }
    for (size_t i = 1; i < m; ++i) {
        scored_t key = sc[i]; size_t j = i;
        while (j > 0 && sc[j-1].score < key.score) { sc[j] = sc[j-1]; --j; }
        sc[j] = key;
    }
    size_t take = (size_t)top_k < m ? (size_t)top_k : m;
    ca_multimodal_entry_t *out = take ? (ca_multimodal_entry_t *)malloc(take * sizeof(*out)) : NULL;
    bool *kept = (bool *)calloc(n, sizeof(bool));
    for (size_t i = 0; i < take; ++i) { out[i] = all[sc[i].idx]; kept[sc[i].idx] = true; }
    for (size_t i = 0; i < n; ++i) if (!kept[i]) ca_multimodal_entry_free(&all[i]);
    free(kept);
    free(sc);
    free(all);
    if (out_count) *out_count = take;
    return out;
}

size_t ca_compressed_multimodal_store_count(const ca_compressed_multimodal_store_t *s) {
    return s ? ca_multimodal_store_count(s->inner) : 0;
}

size_t ca_compressed_multimodal_store_prune_older_than(ca_compressed_multimodal_store_t *s,
                                                       int64_t cutoff_ms) {
    return s ? ca_multimodal_store_prune_older_than(s->inner, cutoff_ms) : 0;
}
