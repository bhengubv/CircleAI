//! compression.rs
//!
//! TurboQuant embedding compression + the compressed store decorators.
//!
//! Ported EXACTLY from the C# reference (and the verified TypeScript pilot,
//! memory/compression.ts) so a payload encoded by any language in the SDK decodes
//! byte-identically in every other:
//!   - CircleAI.Core.Compression.BitPacker
//!   - CircleAI.Core.Compression.OrthogonalRotation (+ SeededGaussian)
//!   - CircleAI.Core.Compression.BetaLloydMaxCodebook
//!   - CircleAI.Core.Compression.TurboQuantCodec (+ TurboQuantPayload)
//!   - CircleAI.Memory.Compression.EmbeddingPayloadCodec
//!   - CircleAI.Memory.Compression.CompressedEpisodicMemoryStore
//!   - CircleAI.Memory.Compression.CompressedMultimodalMemoryStore
//!
//! TurboQuant is Google Research's data-oblivious vector quantizer
//! (arxiv:2504.19874). Per-vector: norm → unit-normalise → fixed orthogonal
//! rotation → per-coordinate Lloyd-Max quantise (codebook optimal for the
//! Beta((d-1)/2,(d-1)/2) coordinate distribution of a rotated unit vector) →
//! bit-pack. Decode reverses it.
//!
//! Numeric fidelity notes (why this round-trips bit-for-bit with C#):
//!   - The SplitMix64 PRNG state is a native `u64` (wrapping arithmetic) — no
//!     BigInt needed, unlike JS.
//!   - Every place C# stores a `float` (norm, matrix cells, centroids, deltas) we
//!     use native Rust `f32`, so the FP32 rounding matches exactly. Where C#
//!     accumulates in `double` (norm sum-of-squares, matrix build, codebook
//!     integration) we accumulate in `f64` and narrow to `f32` at the same points.
//!   - The wire format writes float32 little-endian, same as
//!     BinaryPrimitives.WriteSingleLittleEndian.

use std::collections::HashMap;
use std::sync::Arc;

use super::episodic::InMemoryEpisodicStore;
use super::multimodal::{
    IMultimodalMemoryStore, InMemoryMultimodalMemoryStore, MultimodalMemoryEntry,
};
use super::stores::EpisodicMemoryEntry;
use crate::brain::BrainError;

// ─────────────────────────────────────────────────────────────────────────────
// BitPacker — CircleAI.Core.Compression.BitPacker
// ─────────────────────────────────────────────────────────────────────────────

/// Bit-packing primitives for arbitrary widths (1..16 bits/index).
pub struct BitPacker;

impl BitPacker {
    /// Packs `indices` at `bits_per_index` into a new byte vector. Indices are
    /// written least-significant-bit first.
    pub fn pack(indices: &[u16], bits_per_index: u32) -> Result<Vec<u8>, BrainError> {
        validate_width(bits_per_index)?;
        let total_bits = indices.len() * bits_per_index as usize;
        let mut packed = vec![0u8; total_bits.div_ceil(8)];

        let mut bit_pos = 0usize;
        for (i, &idx) in indices.iter().enumerate() {
            let value = idx as u32;
            if bits_per_index < 16 && value >= (1u32 << bits_per_index) {
                return Err(BrainError::new(format!(
                    "Index {value} at position {i} exceeds {bits_per_index}-bit range."
                )));
            }

            let mut remaining = bits_per_index as i32;
            let mut byte_idx = bit_pos >> 3;
            let mut bit_offset = (bit_pos & 7) as i32;

            while remaining > 0 {
                let take = remaining.min(8 - bit_offset);
                let shift = bits_per_index as i32 - remaining;
                let chunk = ((value >> shift) & ((1u32 << take) - 1)) as u8;
                packed[byte_idx] |= (chunk << bit_offset) & 0xff;

                remaining -= take;
                bit_offset = 0;
                byte_idx += 1;
            }
            bit_pos += bits_per_index as usize;
        }
        Ok(packed)
    }

    /// Unpacks `count` indices of `bits_per_index` each from `packed`.
    pub fn unpack(
        packed: &[u8],
        count: usize,
        bits_per_index: u32,
    ) -> Result<Vec<u16>, BrainError> {
        validate_width(bits_per_index)?;
        let required_bytes = (count * bits_per_index as usize).div_ceil(8);
        if packed.len() < required_bytes {
            return Err(BrainError::new(format!(
                "Packed buffer too small: need {required_bytes} bytes, got {}.",
                packed.len()
            )));
        }

        let mut result = vec![0u16; count];
        let mut bit_pos = 0usize;
        for slot in result.iter_mut() {
            let mut remaining = bits_per_index as i32;
            let mut byte_idx = bit_pos >> 3;
            let mut bit_offset = (bit_pos & 7) as i32;
            let mut value = 0u32;

            while remaining > 0 {
                let take = remaining.min(8 - bit_offset);
                let shift = bits_per_index as i32 - remaining;
                let chunk = ((packed[byte_idx] as u32) >> bit_offset) & ((1u32 << take) - 1);
                value |= chunk << shift;

                remaining -= take;
                bit_offset = 0;
                byte_idx += 1;
            }
            *slot = (value & 0xffff) as u16;
            bit_pos += bits_per_index as usize;
        }
        Ok(result)
    }
}

fn validate_width(bits_per_index: u32) -> Result<(), BrainError> {
    if !(1..=16).contains(&bits_per_index) {
        return Err(BrainError::new("Bits per index must be 1..16."));
    }
    Ok(())
}

// ─────────────────────────────────────────────────────────────────────────────
// SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)
// ─────────────────────────────────────────────────────────────────────────────

const SPLITMIX_GAMMA: u64 = 0x9e3779b97f4a7c15;
const SPLITMIX_M1: u64 = 0xbf58476d1ce4e5b9;
const SPLITMIX_M2: u64 = 0x94d049bb133111eb;

/// Deterministic Gaussian sampler — Box-Muller over a seeded SplitMix64 PRNG.
/// Hand-rolled (not the stdlib RNG) so output is reproducible across platforms
/// and byte-identical with the C# `SeededGaussian`. State is a native `u64` with
/// wrapping arithmetic (no BigInt masking, unlike JS).
struct SeededGaussian {
    state: u64,
    has_spare: bool,
    spare: f64,
}

impl SeededGaussian {
    fn new(seed: u64) -> Self {
        Self {
            state: if seed == 0 {
                0xdeadbeefcafebabe
            } else {
                seed
            },
            has_spare: false,
            spare: 0.0,
        }
    }

    fn sample(&mut self) -> f64 {
        if self.has_spare {
            self.has_spare = false;
            return self.spare;
        }

        // Two uniforms in (0, 1].
        let mut u;
        loop {
            u = self.next_uniform();
            if u > 1e-300 {
                break;
            }
        }
        let v = self.next_uniform();
        let magnitude = (-2.0 * u.ln()).sqrt();
        let angle = 2.0 * std::f64::consts::PI * v;
        self.spare = magnitude * angle.sin();
        self.has_spare = true;
        magnitude * angle.cos()
    }

    fn next_uniform(&mut self) -> f64 {
        // SplitMix64 step (wrapping u64 arithmetic).
        self.state = self.state.wrapping_add(SPLITMIX_GAMMA);
        let mut z = self.state;
        z = (z ^ (z >> 30)).wrapping_mul(SPLITMIX_M1);
        z = (z ^ (z >> 27)).wrapping_mul(SPLITMIX_M2);
        z ^= z >> 31;
        // Convert top 53 bits to a double in [0, 1).
        (z >> 11) as f64 * (1.0 / ((1u64 << 53) as f64))
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation
// ─────────────────────────────────────────────────────────────────────────────

/// Fixed seed shared across every CircleAI process so the rotation is portable:
/// compress on device A, decode on device B works identically.
pub const ROTATION_SEED: u64 = 0xc1c1ea10c1c1ea10;

/// Deterministic random orthogonal rotation matrix for a given dimension.
/// Constructed via QR (modified Gram-Schmidt) of a seeded Gaussian matrix, then
/// sign-corrected. Cached per dimension (construction is O(d^3)).
pub struct OrthogonalRotation;

thread_local! {
    static ROTATION_CACHE: std::cell::RefCell<HashMap<usize, Arc<Vec<f32>>>> =
        std::cell::RefCell::new(HashMap::new());
}

impl OrthogonalRotation {
    /// Returns the dim×dim orthogonal matrix in row-major layout (length dim*dim).
    /// Cached after the first call for a given dimension.
    pub fn get_matrix(dim: usize) -> Arc<Vec<f32>> {
        assert!(dim > 0, "dim must be positive");
        ROTATION_CACHE.with(|cache| {
            if let Some(m) = cache.borrow().get(&dim) {
                return m.clone();
            }
            let m = Arc::new(build_matrix(dim));
            cache.borrow_mut().insert(dim, m.clone());
            m
        })
    }

    /// `output[i] = Σ R[i,j] * vector[j]`.
    pub fn rotate(dim: usize, vector: &[f32], output: &mut [f32]) {
        assert_eq!(vector.len(), dim, "vector length must equal dim.");
        assert_eq!(output.len(), dim, "output length must equal dim.");
        let matrix = Self::get_matrix(dim);
        for i in 0..dim {
            let mut sum = 0.0f32;
            let row_start = i * dim;
            for j in 0..dim {
                sum += matrix[row_start + j] * vector[j];
            }
            output[i] = sum;
        }
    }

    /// Inverse rotation — multiplies the TRANSPOSE of the rotation matrix by
    /// `vector`. The transpose of an orthogonal matrix is its inverse.
    pub fn unrotate(dim: usize, vector: &[f32], output: &mut [f32]) {
        assert_eq!(vector.len(), dim, "vector length must equal dim.");
        assert_eq!(output.len(), dim, "output length must equal dim.");
        let matrix = Self::get_matrix(dim);
        for i in 0..dim {
            let mut sum = 0.0f32;
            for j in 0..dim {
                sum += matrix[j * dim + i] * vector[j];
            }
            output[i] = sum;
        }
    }
}

fn build_matrix(dim: usize) -> Vec<f32> {
    // 1. Generate a seeded Gaussian matrix G (dim × dim).
    let mut gauss = vec![0.0f64; dim * dim];
    let mut rng = SeededGaussian::new(ROTATION_SEED);
    for g in gauss.iter_mut() {
        *g = rng.sample();
    }

    // 2. QR decomposition via modified Gram-Schmidt.
    let mut q = modified_gram_schmidt(&gauss, dim);

    // 3. Sign-correct columns so Q is deterministic.
    sign_correct_columns(&mut q, dim);

    // 4. Convert to row-major float32.
    q.iter().map(|&x| x as f32).collect()
}

/// Modified Gram-Schmidt QR. Returns Q (orthonormal columns) in row-major flat
/// layout. `g` is the input Gaussian matrix (row-major, length dim*dim).
fn modified_gram_schmidt(g: &[f64], dim: usize) -> Vec<f64> {
    let mut q = vec![0.0f64; dim * dim];

    for j in 0..dim {
        // Copy column j of g into the working column.
        for i in 0..dim {
            q[i * dim + j] = g[i * dim + j];
        }

        // Subtract projections onto already-processed columns.
        for k in 0..j {
            let mut dot = 0.0f64;
            for i in 0..dim {
                dot += q[i * dim + j] * q[i * dim + k];
            }
            for i in 0..dim {
                q[i * dim + j] -= dot * q[i * dim + k];
            }
        }

        // Normalise column j.
        let mut norm = 0.0f64;
        for i in 0..dim {
            norm += q[i * dim + j] * q[i * dim + j];
        }
        norm = norm.sqrt();
        assert!(
            norm >= 1e-15,
            "Gram-Schmidt produced a near-zero column at j={j} (dim={dim}). \
             This is statistically impossible for a Gaussian matrix; check the RNG seed."
        );
        let inv = 1.0 / norm;
        for i in 0..dim {
            q[i * dim + j] *= inv;
        }
    }
    q
}

fn sign_correct_columns(q: &mut [f64], dim: usize) {
    for j in 0..dim {
        // Diagonal-based sign convention: ensure q[j,j] >= 0.
        let diag = q[j * dim + j];
        if diag < 0.0 {
            for i in 0..dim {
                q[i * dim + j] = -q[i * dim + j];
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook
// ─────────────────────────────────────────────────────────────────────────────

/// A Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1].
/// `boundaries` has length 2^bits-1; `centroids` has length 2^bits.
#[derive(Debug, Clone)]
pub struct BetaCodebook {
    /// Length 2^bits - 1. boundaries[i] separates centroid i from centroid i+1.
    pub boundaries: Vec<f32>,
    /// Length 2^bits. centroids[i] is the reconstruction value for bin i.
    pub centroids: Vec<f32>,
}

/// Computes / caches Lloyd-Max codebooks for Beta((d-1)/2,(d-1)/2).
pub struct BetaLloydMaxCodebook;

thread_local! {
    static CODEBOOK_CACHE: std::cell::RefCell<HashMap<(u32, usize), Arc<BetaCodebook>>> =
        std::cell::RefCell::new(HashMap::new());
}

impl BetaLloydMaxCodebook {
    /// Returns the codebook for the given bit width and dimension, computing it on
    /// first request. Cached by (bits, dim).
    pub fn get(bits: u32, dim: usize) -> Result<Arc<BetaCodebook>, BrainError> {
        if !(1..=8).contains(&bits) {
            return Err(BrainError::new("bits must be in 1..8."));
        }
        if dim <= 1 {
            return Err(BrainError::new("dim must be > 1."));
        }
        Ok(CODEBOOK_CACHE.with(|cache| {
            if let Some(cb) = cache.borrow().get(&(bits, dim)) {
                return cb.clone();
            }
            let cb = Arc::new(compute_codebook(bits, dim, 200, 1e-12));
            cache.borrow_mut().insert((bits, dim), cb.clone());
            cb
        }))
    }

    /// Returns the bin index for `value` against `boundaries` (linear scan — for
    /// small codebooks this beats a branch-heavy binary search).
    pub fn bin_for(value: f32, boundaries: &[f32]) -> u16 {
        for (i, &b) in boundaries.iter().enumerate() {
            if value < b {
                return i as u16;
            }
        }
        boundaries.len() as u16
    }
}

fn compute_codebook(bits: u32, dim: usize, max_iter: usize, tol: f64) -> BetaCodebook {
    let a = (dim as f64 - 1.0) / 2.0;
    let n_levels = 1usize << bits;

    // Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
    let std = (2.0 * a / ((2.0 * a + 1.0) * 4.0 * a)).sqrt();
    let spread = 3.0 * std;
    let mut centroids = vec![0.0f64; n_levels];
    for (i, c) in centroids.iter_mut().enumerate() {
        *c = -spread + 2.0 * spread * i as f64 / (n_levels as f64 - 1.0);
    }

    for _iter in 0..max_iter {
        // Boundaries = midpoints between adjacent centroids.
        let mut boundaries = vec![0.0f64; n_levels - 1];
        for i in 0..n_levels - 1 {
            boundaries[i] = (centroids[i] + centroids[i + 1]) / 2.0;
        }

        let mut edges = vec![0.0f64; n_levels + 1];
        edges[0] = -1.0;
        for (i, &b) in boundaries.iter().enumerate() {
            edges[i + 1] = b;
        }
        edges[n_levels] = 1.0;

        let mut new_centroids = vec![0.0f64; n_levels];
        for i in 0..n_levels {
            let lo = edges[i];
            let hi = edges[i + 1];
            let cdf_lo = beta_cdf_symmetric(a, (lo + 1.0) / 2.0);
            let cdf_hi = beta_cdf_symmetric(a, (hi + 1.0) / 2.0);
            let prob = cdf_hi - cdf_lo;

            if prob < 1e-15 {
                new_centroids[i] = centroids[i];
            } else {
                let mean = adaptive_simpson(
                    &|x: f64| x * beta_pdf_symmetric(a, (x + 1.0) / 2.0) / 2.0,
                    lo,
                    hi,
                    1e-14,
                    50,
                );
                new_centroids[i] = mean / prob;
            }
        }

        let mut max_change = 0.0f64;
        for i in 0..n_levels {
            max_change = max_change.max((centroids[i] - new_centroids[i]).abs());
        }
        centroids = new_centroids;

        if max_change < tol {
            break;
        }
    }

    let mut final_boundaries = vec![0.0f32; n_levels - 1];
    for i in 0..n_levels - 1 {
        final_boundaries[i] = ((centroids[i] + centroids[i + 1]) / 2.0) as f32;
    }
    let final_centroids: Vec<f32> = centroids.iter().map(|&c| c as f32).collect();
    BetaCodebook {
        boundaries: final_boundaries,
        centroids: final_centroids,
    }
}

// ── Beta(a, a) PDF / CDF on [0, 1] ─────────────────────────────────────────
// The "symmetric" suffix is a reminder that we always use shape Beta(a, a).

fn beta_pdf_symmetric(a: f64, x: f64) -> f64 {
    if x <= 0.0 || x >= 1.0 {
        return 0.0;
    }
    // f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
    let log_pdf = (a - 1.0) * x.ln() + (a - 1.0) * (1.0 - x).ln() - log_beta(a, a);
    log_pdf.exp()
}

fn beta_cdf_symmetric(a: f64, x: f64) -> f64 {
    if x <= 0.0 {
        return 0.0;
    }
    if x >= 1.0 {
        return 1.0;
    }
    regularized_incomplete_beta(a, a, x)
}

fn log_beta(a: f64, b: f64) -> f64 {
    log_gamma(a) + log_gamma(b) - log_gamma(a + b)
}

/// Lanczos coefficients for g = 7.
const LANCZOS_G7: [f64; 9] = [
    0.99999999999980993,
    676.5203681218851,
    -1259.1392167224028,
    771.32342877765313,
    -176.61502916214059,
    12.507343278686905,
    -0.13857109526572012,
    9.9843695780195716e-6,
    1.5056327351493116e-7,
];

/// log Γ(x) for x > 0 via the Lanczos approximation (g = 7, n = 9).
fn log_gamma(x: f64) -> f64 {
    if x < 0.5 {
        // Reflection: Γ(x)Γ(1-x) = π/sin(πx)
        return (std::f64::consts::PI / (std::f64::consts::PI * x).sin()).ln() - log_gamma(1.0 - x);
    }
    let x = x - 1.0;
    let t = x + 7.5;
    let mut sum = LANCZOS_G7[0];
    for (i, &c) in LANCZOS_G7.iter().enumerate().skip(1) {
        sum += c / (x + i as f64);
    }
    0.5 * (2.0 * std::f64::consts::PI).ln() + (x + 0.5) * t.ln() - t + sum.ln()
}

/// Regularised incomplete beta function I_x(a, b) (Numerical Recipes 6.4).
fn regularized_incomplete_beta(a: f64, b: f64, x: f64) -> f64 {
    if !(0.0..=1.0).contains(&x) {
        // Mirrors the C#/TS RangeError; unreachable for our inputs.
        return f64::NAN;
    }
    if x == 0.0 || x == 1.0 {
        return x;
    }

    let bt =
        (log_gamma(a + b) - log_gamma(a) - log_gamma(b) + a * x.ln() + b * (1.0 - x).ln()).exp();
    if x < (a + 1.0) / (a + b + 2.0) {
        bt * beta_continued_fraction(a, b, x) / a
    } else {
        1.0 - bt * beta_continued_fraction(b, a, 1.0 - x) / b
    }
}

fn beta_continued_fraction(a: f64, b: f64, x: f64) -> f64 {
    const MAX_ITER: usize = 200;
    const EPS: f64 = 3e-15;
    const FPMIN: f64 = 1e-300;

    let qab = a + b;
    let qap = a + 1.0;
    let qam = a - 1.0;
    let mut c = 1.0;
    let mut d = 1.0 - qab * x / qap;
    if d.abs() < FPMIN {
        d = FPMIN;
    }
    d = 1.0 / d;
    let mut h = d;

    for m in 1..=MAX_ITER {
        let m = m as f64;
        let m2 = 2.0 * m;
        let mut aa = m * (b - m) * x / ((qam + m2) * (a + m2));
        d = 1.0 + aa * d;
        if d.abs() < FPMIN {
            d = FPMIN;
        }
        c = 1.0 + aa / c;
        if c.abs() < FPMIN {
            c = FPMIN;
        }
        d = 1.0 / d;
        h *= d * c;

        aa = -(a + m) * (qab + m) * x / ((a + m2) * (qap + m2));
        d = 1.0 + aa * d;
        if d.abs() < FPMIN {
            d = FPMIN;
        }
        c = 1.0 + aa / c;
        if c.abs() < FPMIN {
            c = FPMIN;
        }
        d = 1.0 / d;
        let delta = d * c;
        h *= delta;
        if (delta - 1.0).abs() < EPS {
            return h;
        }
    }
    h // best effort if no convergence
}

// ── Adaptive Simpson integration ───────────────────────────────────────────

fn adaptive_simpson(f: &dyn Fn(f64) -> f64, a: f64, b: f64, tol: f64, max_depth: i32) -> f64 {
    let mid = (a + b) / 2.0;
    let fa = f(a);
    let fb = f(b);
    let fm = f(mid);
    let whole = (b - a) / 6.0 * (fa + 4.0 * fm + fb);
    adaptive_simpson_rec(f, a, b, fa, fb, fm, whole, tol, max_depth)
}

#[allow(clippy::too_many_arguments)]
fn adaptive_simpson_rec(
    f: &dyn Fn(f64) -> f64,
    a: f64,
    b: f64,
    fa: f64,
    fb: f64,
    fm: f64,
    whole: f64,
    tol: f64,
    depth: i32,
) -> f64 {
    let mid = (a + b) / 2.0;
    let m1 = (a + mid) / 2.0;
    let m2 = (mid + b) / 2.0;
    let fm1 = f(m1);
    let fm2 = f(m2);
    let left = (mid - a) / 6.0 * (fa + 4.0 * fm1 + fm);
    let right = (b - mid) / 6.0 * (fm + 4.0 * fm2 + fb);
    let refined = left + right;

    if depth == 0 || (refined - whole).abs() < 15.0 * tol {
        return refined + (refined - whole) / 15.0;
    }
    adaptive_simpson_rec(f, a, mid, fa, fm, fm1, left, tol / 2.0, depth - 1)
        + adaptive_simpson_rec(f, mid, b, fm, fb, fm2, right, tol / 2.0, depth - 1)
}

// ─────────────────────────────────────────────────────────────────────────────
// TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec
// ─────────────────────────────────────────────────────────────────────────────

/// Output of [`TurboQuantCodec::encode`].
/// - `norm`: L2 norm of the original vector — needed to reconstruct magnitude.
/// - `packed_indices`: bit-packed Lloyd-Max bin indices, one per dimension.
#[derive(Debug, Clone)]
pub struct TurboQuantPayload {
    /// L2 norm of the original vector.
    pub norm: f32,
    /// Bit-packed Lloyd-Max bin indices, one per dimension.
    pub packed_indices: Vec<u8>,
}

/// TurboQuant encoder / decoder.
pub struct TurboQuantCodec;

impl TurboQuantCodec {
    /// Encodes a float vector at `bits_per_dim` bits per dimension. Higher bits =
    /// better fidelity, larger payload. Typical: 2 bits (16×), 3 bits (~10×).
    pub fn encode(vector: &[f32], bits_per_dim: u32) -> Result<TurboQuantPayload, BrainError> {
        if vector.len() <= 1 {
            return Err(BrainError::new("Vector must have length > 1."));
        }
        if !(1..=8).contains(&bits_per_dim) {
            return Err(BrainError::new("bitsPerDim must be 1..8."));
        }

        let dim = vector.len();

        // 1. Norm. Accumulate in f64 then narrow to f32 (matches C#
        //    `sumSq += (double)v[i]*v[i]; norm = (float)Math.Sqrt(sumSq)`).
        let mut sum_sq = 0.0f64;
        for &x in vector {
            sum_sq += x as f64 * x as f64;
        }
        let norm = sum_sq.sqrt() as f32;

        // Edge case — zero vector. Round-trip preserves the all-zero shape.
        if norm < 1e-20 {
            let all_zeros = vec![0u8; (dim * bits_per_dim as usize).div_ceil(8)];
            return Ok(TurboQuantPayload {
                norm: 0.0,
                packed_indices: all_zeros,
            });
        }

        // 2. Unit-normalise (f32, matching the C# `float` unit vector).
        let inv_norm = 1.0f32 / norm;
        let unit: Vec<f32> = vector.iter().map(|&x| x * inv_norm).collect();

        // 3. Rotate.
        let mut rotated = vec![0.0f32; dim];
        OrthogonalRotation::rotate(dim, &unit, &mut rotated);

        // 4. Quantize per-coordinate.
        let codebook = BetaLloydMaxCodebook::get(bits_per_dim, dim)?;
        let mut indices = vec![0u16; dim];
        for i in 0..dim {
            indices[i] = BetaLloydMaxCodebook::bin_for(rotated[i], &codebook.boundaries);
        }

        // 5. Pack.
        let packed = BitPacker::pack(&indices, bits_per_dim)?;
        Ok(TurboQuantPayload {
            norm,
            packed_indices: packed,
        })
    }

    /// Decodes a TurboQuant payload back into the original-magnitude vector
    /// (modulo quantization error).
    pub fn decode(
        payload: &TurboQuantPayload,
        dim: usize,
        bits_per_dim: u32,
    ) -> Result<Vec<f32>, BrainError> {
        if dim <= 1 {
            return Err(BrainError::new("dim must be > 1"));
        }
        if !(1..=8).contains(&bits_per_dim) {
            return Err(BrainError::new("bitsPerDim must be 1..8"));
        }

        let mut result = vec![0.0f32; dim];
        if payload.norm == 0.0 {
            return Ok(result); // all zeros
        }

        // 1. Unpack indices.
        let indices = BitPacker::unpack(&payload.packed_indices, dim, bits_per_dim)?;

        // 2. Map indices → centroids (rotated-space reconstruction).
        let codebook = BetaLloydMaxCodebook::get(bits_per_dim, dim)?;
        let mut rotated = vec![0.0f32; dim];
        for i in 0..dim {
            rotated[i] = codebook.centroids[indices[i] as usize];
        }

        // 3. Inverse rotation.
        let mut unit = vec![0.0f32; dim];
        OrthogonalRotation::unrotate(dim, &rotated, &mut unit);

        // 4. Scale by stored norm.
        let scale = payload.norm;
        for i in 0..dim {
            result[i] = unit[i] * scale;
        }
        Ok(result)
    }

    /// Convenience: encode then decode, returning the reconstruction.
    pub fn round_trip(vector: &[f32], bits_per_dim: u32) -> Result<Vec<f32>, BrainError> {
        let encoded = Self::encode(vector, bits_per_dim)?;
        Self::decode(&encoded, vector.len(), bits_per_dim)
    }

    /// Bytes-per-vector required at the given dim and bits_per_dim (excluding the
    /// 4-byte norm header).
    pub fn payload_byte_count(dim: usize, bits_per_dim: u32) -> usize {
        (dim * bits_per_dim as usize).div_ceil(8)
    }

    /// Compression ratio vs raw FP32 (vector bytes / encoded bytes incl. norm).
    pub fn compression_ratio(dim: usize, bits_per_dim: u32) -> f64 {
        let raw = dim * 4;
        let encoded = Self::payload_byte_count(dim, bits_per_dim) + 4; // norm
        raw as f64 / encoded as f64
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
// ─────────────────────────────────────────────────────────────────────────────
//
// Wire format (binary):
//   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
//   bytes [4..7]   = bit-width as uint32 little-endian
//   bytes [8..11]  = dimension as uint32 little-endian
//   bytes [12..15] = norm as float32 little-endian
//   bytes [16..]   = packed indices
// Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
// decode without out-of-band metadata.

/// Magic header bytes that identify a TurboQuant-encoded blob ("TQ3\1").
pub const MAGIC: [u8; 4] = [0x54, 0x51, 0x33, 0x01];

/// Encodes and decodes TurboQuant-compressed embeddings as binary blobs suitable
/// for persistence (e.g. in a tag value).
pub struct EmbeddingPayloadCodec;

impl EmbeddingPayloadCodec {
    /// Encodes `vector` at `bits_per_dim` bits per coordinate into a
    /// self-describing byte payload.
    pub fn encode(vector: &[f32], bits_per_dim: u32) -> Result<Vec<u8>, BrainError> {
        if vector.len() <= 1 {
            return Err(BrainError::new("Vector must have length > 1."));
        }

        let payload = TurboQuantCodec::encode(vector, bits_per_dim)?;
        let mut buf = Vec::with_capacity(MAGIC.len() + 12 + payload.packed_indices.len());
        buf.extend_from_slice(&MAGIC);
        buf.extend_from_slice(&bits_per_dim.to_le_bytes());
        buf.extend_from_slice(&(vector.len() as u32).to_le_bytes());
        buf.extend_from_slice(&payload.norm.to_le_bytes());
        buf.extend_from_slice(&payload.packed_indices);
        Ok(buf)
    }

    /// Decodes a byte payload produced by [`encode`](Self::encode) back into a
    /// float vector.
    pub fn decode(bytes: &[u8]) -> Result<Vec<f32>, BrainError> {
        if bytes.len() < MAGIC.len() + 12 {
            return Err(BrainError::new("Payload too short."));
        }
        if !has_magic(bytes) {
            return Err(BrainError::new(
                "Magic header missing — not a TurboQuant payload.",
            ));
        }

        let mut o = MAGIC.len();
        let bits_per_dim = u32::from_le_bytes([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]);
        o += 4;
        let dim = u32::from_le_bytes([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]) as usize;
        o += 4;
        let norm = f32::from_le_bytes([bytes[o], bytes[o + 1], bytes[o + 2], bytes[o + 3]]);
        o += 4;
        let packed = bytes[o..].to_vec();
        let payload = TurboQuantPayload {
            norm,
            packed_indices: packed,
        };
        TurboQuantCodec::decode(&payload, dim, bits_per_dim)
    }

    /// True when the byte span begins with the TurboQuant magic header.
    pub fn is_encoded(bytes: &[u8]) -> bool {
        bytes.len() >= MAGIC.len() && has_magic(bytes)
    }

    /// Convenience: encode + base64-stringify for tag-style storage.
    pub fn encode_base64(vector: &[f32], bits_per_dim: u32) -> Result<String, BrainError> {
        Ok(base64_encode(&Self::encode(vector, bits_per_dim)?))
    }

    /// Convenience: base64-decode + decode.
    pub fn decode_base64(base64: &str) -> Result<Vec<f32>, BrainError> {
        let bytes = base64_decode(base64)?;
        Self::decode(&bytes)
    }
}

fn has_magic(bytes: &[u8]) -> bool {
    bytes.len() >= 4 && bytes[0..4] == MAGIC
}

// ── Standard base64 (RFC 4648, '+/' alphabet, '=' padding) ──────────────────
// Matches System.Convert.ToBase64String / Buffer.toString('base64').

const B64_ALPHABET: &[u8; 64] = b"ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";

fn base64_encode(data: &[u8]) -> String {
    let mut out = String::with_capacity(data.len().div_ceil(3) * 4);
    for chunk in data.chunks(3) {
        let b0 = chunk[0] as u32;
        let b1 = *chunk.get(1).unwrap_or(&0) as u32;
        let b2 = *chunk.get(2).unwrap_or(&0) as u32;
        let triple = (b0 << 16) | (b1 << 8) | b2;

        out.push(B64_ALPHABET[((triple >> 18) & 0x3f) as usize] as char);
        out.push(B64_ALPHABET[((triple >> 12) & 0x3f) as usize] as char);
        if chunk.len() > 1 {
            out.push(B64_ALPHABET[((triple >> 6) & 0x3f) as usize] as char);
        } else {
            out.push('=');
        }
        if chunk.len() > 2 {
            out.push(B64_ALPHABET[(triple & 0x3f) as usize] as char);
        } else {
            out.push('=');
        }
    }
    out
}

fn base64_decode(s: &str) -> Result<Vec<u8>, BrainError> {
    fn val(c: u8) -> Option<u32> {
        match c {
            b'A'..=b'Z' => Some((c - b'A') as u32),
            b'a'..=b'z' => Some((c - b'a' + 26) as u32),
            b'0'..=b'9' => Some((c - b'0' + 52) as u32),
            b'+' => Some(62),
            b'/' => Some(63),
            _ => None,
        }
    }

    let bytes: Vec<u8> = s.bytes().filter(|b| !b.is_ascii_whitespace()).collect();
    let mut out = Vec::with_capacity(bytes.len() / 4 * 3);
    for chunk in bytes.chunks(4) {
        if chunk.is_empty() {
            break;
        }
        let mut acc = 0u32;
        let mut pad = 0;
        for i in 0..4 {
            acc <<= 6;
            match chunk.get(i) {
                Some(&b'=') | None => {
                    pad += 1;
                }
                Some(&c) => {
                    let v = val(c)
                        .ok_or_else(|| BrainError::new("Invalid base64 character."))?;
                    acc |= v;
                }
            }
        }
        out.push((acc >> 16) as u8);
        if pad < 2 {
            out.push((acc >> 8) as u8);
        }
        if pad < 1 {
            out.push(acc as u8);
        }
    }
    Ok(out)
}

// ─────────────────────────────────────────────────────────────────────────────
// Shared cosine — matches the C# stores' internal CosineSimilarity.Score
// ─────────────────────────────────────────────────────────────────────────────

fn cosine_score(a: &[f32], b: &[f32]) -> f32 {
    if a.len() != b.len() {
        return 0.0;
    }
    let mut dot = 0.0f32;
    let mut mag_a = 0.0f32;
    let mut mag_b = 0.0f32;
    for i in 0..a.len() {
        dot += a[i] * b[i];
        mag_a += a[i] * a[i];
        mag_b += b[i] * b[i];
    }
    let denom = mag_a.sqrt() * mag_b.sqrt();
    if denom < f32::EPSILON {
        0.0
    } else {
        dot / denom
    }
}

/// Tag key under which the compressed embedding is stored.
pub const COMPRESSED_TAG_KEY: &str = "x-tq-embedding";

// ─────────────────────────────────────────────────────────────────────────────
// CompressedEpisodicMemoryStore — CircleAI.Memory.Compression
// ─────────────────────────────────────────────────────────────────────────────

/// Wraps an [`InMemoryEpisodicStore`] and stores its embeddings in
/// TurboQuant-compressed form. Default 2 bits per dim (~16× shrink).
///
/// The inner store sees `embedding = None`; the compressed base64 payload lives
/// in the entry's tags under [`COMPRESSED_TAG_KEY`]. Reads rehydrate the
/// embedding by decoding the tag, and search rebuilds embeddings on the read path
/// so cosine ranking works against the reconstructed vectors.
pub struct CompressedEpisodicMemoryStore {
    inner: Arc<InMemoryEpisodicStore>,
    bits_per_dim: u32,
}

impl CompressedEpisodicMemoryStore {
    /// Tag key under which the compressed embedding is stored.
    pub const COMPRESSED_TAG_KEY: &'static str = COMPRESSED_TAG_KEY;

    /// Creates a decorator over `inner` at `bits_per_dim` (1..8).
    pub fn new(inner: Arc<InMemoryEpisodicStore>, bits_per_dim: u32) -> Result<Self, BrainError> {
        if !(1..=8).contains(&bits_per_dim) {
            return Err(BrainError::new("bitsPerDim must be 1..8"));
        }
        Ok(Self {
            inner,
            bits_per_dim,
        })
    }

    /// Creates a decorator with the default 2 bits per dim.
    pub fn with_default_bits(inner: Arc<InMemoryEpisodicStore>) -> Self {
        Self {
            inner,
            bits_per_dim: 2,
        }
    }

    /// Adds an entry. When it carries an embedding of length > 1 the embedding is
    /// compressed into a tag and dropped from the entry; otherwise it passes
    /// through unchanged.
    pub fn add(&self, entry: EpisodicMemoryEntry) -> Result<(), BrainError> {
        let rewritten = match &entry.embedding {
            Some(emb) if emb.len() > 1 => {
                let b64 = EmbeddingPayloadCodec::encode_base64(emb, self.bits_per_dim)?;
                let mut tags = entry.tags.clone().unwrap_or_default();
                tags.insert(COMPRESSED_TAG_KEY.to_string(), b64);
                EpisodicMemoryEntry {
                    id: entry.id,
                    recorded_at_utc: entry.recorded_at_utc,
                    user_text: entry.user_text,
                    assistant_text: entry.assistant_text,
                    app_context: entry.app_context,
                    embedding: None, // dropped — lives in tags
                    tags: Some(tags),
                }
            }
            _ => entry,
        };
        self.inner.add_shared(rewritten)
    }

    /// Ranks recent entries by cosine similarity to `query_embedding` (through
    /// compression), or returns recency when the query is `None`.
    pub fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        // The inner store sees embedding = None on every entry, so we cannot
        // defer to its cosine ranking. Load recent, rehydrate, then rank here.
        let all = self.inner.get_recent_shared(usize::MAX)?;
        let rehydrated: Vec<EpisodicMemoryEntry> =
            all.into_iter().map(rehydrate_episodic).collect();

        let query = match query_embedding {
            Some(q) => q,
            None => {
                let mut out = rehydrated;
                out.truncate(top_k);
                return Ok(out);
            }
        };

        let mut scored: Vec<(EpisodicMemoryEntry, f32)> = rehydrated
            .into_iter()
            .filter_map(|e| match &e.embedding {
                Some(emb) if !emb.is_empty() => {
                    let score = cosine_score(query, emb);
                    Some((e, score))
                }
                _ => None,
            })
            .collect();
        scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        scored.truncate(top_k);
        Ok(scored.into_iter().map(|(e, _)| e).collect())
    }

    /// Returns the most recent `count` entries, rehydrating embeddings.
    pub fn get_recent(&self, count: usize) -> Result<Vec<EpisodicMemoryEntry>, BrainError> {
        let recent = self.inner.get_recent_shared(count)?;
        Ok(recent.into_iter().map(rehydrate_episodic).collect())
    }

    /// Total entries in the inner store.
    pub fn count(&self) -> Result<usize, BrainError> {
        self.inner.count_shared()
    }

    /// Delegates prune to the inner store.
    pub fn prune_older_than(
        &self,
        cutoff: &chrono::DateTime<chrono::Utc>,
    ) -> Result<usize, BrainError> {
        self.inner.prune_older_than_shared(cutoff)
    }
}

fn rehydrate_episodic(e: EpisodicMemoryEntry) -> EpisodicMemoryEntry {
    if let Some(emb) = &e.embedding {
        if !emb.is_empty() {
            return e; // never compressed
        }
    }
    let b64 = e
        .tags
        .as_ref()
        .and_then(|t| t.get(COMPRESSED_TAG_KEY))
        .cloned();
    let b64 = match b64 {
        Some(v) => v,
        None => return e,
    };
    match EmbeddingPayloadCodec::decode_base64(&b64) {
        Ok(floats) => EpisodicMemoryEntry {
            id: e.id,
            recorded_at_utc: e.recorded_at_utc,
            user_text: e.user_text,
            assistant_text: e.assistant_text,
            app_context: e.app_context,
            embedding: Some(floats),
            tags: e.tags,
        },
        // Malformed tag — return entry as-is so the caller can still see it.
        Err(_) => e,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// CompressedMultimodalMemoryStore — CircleAI.Memory.Compression
// ─────────────────────────────────────────────────────────────────────────────

/// Wraps an [`InMemoryMultimodalMemoryStore`] and stores its embeddings in
/// TurboQuant-compressed form. Same wire format + tag key as the episodic
/// decorator.
pub struct CompressedMultimodalMemoryStore {
    inner: Arc<InMemoryMultimodalMemoryStore>,
    bits_per_dim: u32,
}

impl CompressedMultimodalMemoryStore {
    /// Tag key under which the compressed embedding is stored.
    pub const COMPRESSED_TAG_KEY: &'static str = COMPRESSED_TAG_KEY;

    /// Creates a decorator over `inner` at `bits_per_dim` (1..8).
    pub fn new(
        inner: Arc<InMemoryMultimodalMemoryStore>,
        bits_per_dim: u32,
    ) -> Result<Self, BrainError> {
        if !(1..=8).contains(&bits_per_dim) {
            return Err(BrainError::new("bitsPerDim must be 1..8"));
        }
        Ok(Self {
            inner,
            bits_per_dim,
        })
    }

    /// Creates a decorator with the default 2 bits per dim.
    pub fn with_default_bits(inner: Arc<InMemoryMultimodalMemoryStore>) -> Self {
        Self {
            inner,
            bits_per_dim: 2,
        }
    }

    /// Adds an entry, compressing its embedding into a tag when present (len > 1).
    pub fn add(&self, entry: MultimodalMemoryEntry) -> Result<(), BrainError> {
        let rewritten = match &entry.embedding {
            Some(emb) if emb.len() > 1 => self.compress(entry)?,
            _ => entry,
        };
        self.inner.add(rewritten)
    }

    /// Returns the entry with the given hash, rehydrating its embedding.
    pub fn get_by_hash(
        &self,
        source_sha256: &str,
    ) -> Result<Option<MultimodalMemoryEntry>, BrainError> {
        Ok(self
            .inner
            .get_by_hash(source_sha256)?
            .map(rehydrate_multimodal))
    }

    /// Delegates reinforce to the inner store.
    pub fn reinforce(&self, source_sha256: &str) -> Result<(), BrainError> {
        self.inner.reinforce(source_sha256)
    }

    /// Ranks recent entries by cosine through compression, or recency when the
    /// query is `None`.
    pub fn search(
        &self,
        query_embedding: Option<&[f32]>,
        top_k: usize,
    ) -> Result<Vec<MultimodalMemoryEntry>, BrainError> {
        let all = self.inner.get_recent(usize::MAX)?;
        let rehydrated: Vec<MultimodalMemoryEntry> =
            all.into_iter().map(rehydrate_multimodal).collect();

        let query = match query_embedding {
            Some(q) => q,
            None => {
                let mut out = rehydrated;
                out.truncate(top_k);
                return Ok(out);
            }
        };

        let mut scored: Vec<(MultimodalMemoryEntry, f32)> = rehydrated
            .into_iter()
            .filter_map(|e| match &e.embedding {
                Some(emb) if !emb.is_empty() => {
                    let score = cosine_score(query, emb);
                    Some((e, score))
                }
                _ => None,
            })
            .collect();
        scored.sort_by(|a, b| b.1.partial_cmp(&a.1).unwrap_or(std::cmp::Ordering::Equal));
        scored.truncate(top_k);
        Ok(scored.into_iter().map(|(e, _)| e).collect())
    }

    /// Returns the most recent `count` entries, rehydrating embeddings.
    pub fn get_recent(&self, count: usize) -> Result<Vec<MultimodalMemoryEntry>, BrainError> {
        let recent = self.inner.get_recent(count)?;
        Ok(recent.into_iter().map(rehydrate_multimodal).collect())
    }

    /// Delegates prune to the inner store.
    pub fn prune_older_than(
        &self,
        cutoff: &chrono::DateTime<chrono::Utc>,
    ) -> Result<usize, BrainError> {
        self.inner.prune_older_than(cutoff)
    }

    /// Total entries in the inner store.
    pub fn count(&self) -> Result<usize, BrainError> {
        self.inner.count()
    }

    fn compress(&self, entry: MultimodalMemoryEntry) -> Result<MultimodalMemoryEntry, BrainError> {
        let emb = entry.embedding.as_ref().expect("checked by caller");
        let b64 = EmbeddingPayloadCodec::encode_base64(emb, self.bits_per_dim)?;
        let mut tags = entry.tags.clone().unwrap_or_default();
        tags.insert(COMPRESSED_TAG_KEY.to_string(), b64);

        Ok(MultimodalMemoryEntry {
            embedding: None,
            tags: Some(tags),
            ..entry
        })
    }
}

fn rehydrate_multimodal(e: MultimodalMemoryEntry) -> MultimodalMemoryEntry {
    if let Some(emb) = &e.embedding {
        if !emb.is_empty() {
            return e;
        }
    }
    let b64 = e
        .tags
        .as_ref()
        .and_then(|t| t.get(COMPRESSED_TAG_KEY))
        .cloned();
    let b64 = match b64 {
        Some(v) => v,
        None => return e,
    };
    match EmbeddingPayloadCodec::decode_base64(&b64) {
        Ok(floats) => MultimodalMemoryEntry {
            embedding: Some(floats),
            ..e
        },
        Err(_) => e,
    }
}
