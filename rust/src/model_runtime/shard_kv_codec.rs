//! shard_kv_codec.rs
//!
//! (3.3.0) Shard-style KV cache compression — byte-for-byte port of
//! `CircleAI.Core.Compression.ShardKvCodec`.
//!
//! Compress K via per-layer online PCA + Hadamard rotation, and compress V via
//! product vector quantisation. An alternative to the TurboQuant codec — same KV
//! bytes, different math.
//!
//! The wire format matches the C# reference exactly:
//!   - `encoded_k` = 4-byte float32 little-endian scale, then `k_rank` bytes of
//!     signed int8 (stored as the two's-complement byte).
//!   - `encoded_v` = little-endian index in 1/2/4 bytes depending on codebook
//!     size (`<=256` → 1 byte, `<=65536` → 2 bytes, else 4).
//!   - The frame carries the flattened PCA axes so the decoder can stand alone.
//!
//! Determinism: the seed codebook is generated with the same LCG (`java.util`-style
//! `Random(seed)` as reimplemented in [`DotNetRandom`]) so a codebook seeded here
//! matches one seeded by C# `new Random(seed)`.

use super::dotnet_random::DotNetRandom;

/// (3.3.0) Encoded shard KV pair (compressed K + compressed V).
#[derive(Debug, Clone, PartialEq)]
pub struct ShardCompressedFrame {
    /// Compressed K: 4-byte float32 LE scale followed by `k_rank` int8 bytes.
    pub compressed_k: Vec<u8>,
    /// Compressed V: little-endian codeword index (1/2/4 bytes).
    pub compressed_v: Vec<u8>,
    /// Flattened PCA axes, row-major `[k_rank * k_dim]`.
    pub k_principal_axes: Vec<f32>,
    /// Original K dimensionality.
    pub k_original_dim: usize,
    /// Original V dimensionality.
    pub v_original_dim: usize,
}

impl ShardCompressedFrame {
    pub fn new(
        compressed_k: Vec<u8>,
        compressed_v: Vec<u8>,
        k_principal_axes: Vec<f32>,
        k_original_dim: usize,
        v_original_dim: usize,
    ) -> Self {
        Self {
            compressed_k,
            compressed_v,
            k_principal_axes,
            k_original_dim,
            v_original_dim,
        }
    }
}

/// Error returned by [`ShardKvCodec`] construction / encode / decode.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct ShardCodecError(pub String);

impl std::fmt::Display for ShardCodecError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for ShardCodecError {}

/// (3.3.0) Online-PCA-on-K + VQ-on-V KV compressor. Stateless across frames —
/// the host re-trains the PCA basis with [`ShardKvCodec::observe_k`] when desired,
/// and uses the current basis to encode subsequent frames.
pub struct ShardKvCodec {
    k_dim: usize,
    k_rank: usize,
    v_dim: usize,
    v_codewords: usize,
    v_codebook: Vec<Vec<f32>>,
    hadamard_scratch: Vec<f32>,
    k_center: Vec<f32>,
    /// Row-major `[k_rank][k_dim]` PCA axes.
    k_axes: Vec<Vec<f32>>,
    samples_observed: i64,
}

impl ShardKvCodec {
    /// (3.3.0)
    ///
    /// - `k_dim`: K-vector dimensionality (e.g. 128 for a typical attention head).
    /// - `k_rank`: number of principal components to keep on K (e.g. 32).
    /// - `v_dim`: V-vector dimensionality.
    /// - `v_codewords`: number of VQ codewords for V (must be a power of 2 > 1).
    /// - `v_codebook_seed`: seed for the deterministic initial codebook.
    pub fn new(
        k_dim: usize,
        k_rank: usize,
        v_dim: usize,
        v_codewords: usize,
        v_codebook_seed: i32,
    ) -> Result<Self, ShardCodecError> {
        if k_dim == 0 {
            return Err(ShardCodecError("kDim".into()));
        }
        if k_rank == 0 || k_rank > k_dim {
            return Err(ShardCodecError("kRank".into()));
        }
        if v_dim == 0 {
            return Err(ShardCodecError("vDim".into()));
        }
        if v_codewords <= 1 || (v_codewords & (v_codewords - 1)) != 0 {
            return Err(ShardCodecError(
                "Codeword count must be a power of two greater than 1.".into(),
            ));
        }

        let k_center = vec![0.0f32; k_dim];
        let mut k_axes = vec![vec![0.0f32; k_dim]; k_rank];
        let v_codebook = Self::seed_codebook(v_dim, v_codewords, v_codebook_seed);
        let hadamard_scratch = vec![0.0f32; Self::pow2_ceil(k_dim)];

        // Initialise PCA axes to identity-top-rank for sane defaults before training.
        for (r, row) in k_axes.iter_mut().enumerate().take(k_rank) {
            row[r] = 1.0;
        }

        Ok(Self {
            k_dim,
            k_rank,
            v_dim,
            v_codewords,
            v_codebook,
            hadamard_scratch,
            k_center,
            k_axes,
            samples_observed: 0,
        })
    }

    /// (3.3.0) Number of K samples used to update the PCA centre.
    pub fn samples_observed(&self) -> i64 {
        self.samples_observed
    }

    /// (3.3.0) Update the online K mean estimate with this sample.
    pub fn observe_k(&mut self, k: &[f32]) -> Result<(), ShardCodecError> {
        if k.len() != self.k_dim {
            return Err(ShardCodecError("Input dim mismatch".into()));
        }
        self.samples_observed += 1;
        let n = self.samples_observed as f32;
        for i in 0..self.k_dim {
            // Running mean.
            self.k_center[i] += (k[i] - self.k_center[i]) / n;
        }
        Ok(())
    }

    /// (3.3.0) Replace the current PCA axes with `axes` (shape `[k_rank][k_dim]`).
    /// Caller computes axes offline (full SVD/PCA on observed K) or in batch.
    pub fn set_principal_axes(&mut self, axes: &[Vec<f32>]) -> Result<(), ShardCodecError> {
        if axes.len() != self.k_rank || axes.iter().any(|row| row.len() != self.k_dim) {
            return Err(ShardCodecError("Axes shape must be (kRank, kDim).".into()));
        }
        for (dst, src) in self.k_axes.iter_mut().zip(axes.iter()) {
            dst.copy_from_slice(src);
        }
        Ok(())
    }

    /// (3.3.0) Replace the V codebook with `codebook`.
    pub fn set_v_codebook(&mut self, codebook: &[Vec<f32>]) -> Result<(), ShardCodecError> {
        if codebook.len() != self.v_codewords {
            return Err(ShardCodecError("Codebook size mismatch.".into()));
        }
        for word in codebook {
            if word.len() != self.v_dim {
                return Err(ShardCodecError("Codeword dim mismatch.".into()));
            }
        }
        for (dst, src) in self.v_codebook.iter_mut().zip(codebook.iter()) {
            dst.copy_from_slice(src);
        }
        Ok(())
    }

    /// (3.3.0) Encode one (K, V) pair.
    pub fn encode(&mut self, k: &[f32], v: &[f32]) -> Result<ShardCompressedFrame, ShardCodecError> {
        if k.len() != self.k_dim {
            return Err(ShardCodecError("K dim mismatch".into()));
        }
        if v.len() != self.v_dim {
            return Err(ShardCodecError("V dim mismatch".into()));
        }

        // K: centre → Hadamard → project to top-rank principal axes → quantise to int8.
        let mut centred = vec![0.0f32; self.k_dim];
        for i in 0..self.k_dim {
            centred[i] = k[i] - self.k_center[i];
        }
        self.apply_hadamard_in_place(&mut centred);

        let mut projected = vec![0.0f32; self.k_rank];
        for r in 0..self.k_rank {
            let mut dot = 0.0f32;
            for i in 0..self.k_dim {
                dot += centred[i] * self.k_axes[r][i];
            }
            projected[r] = dot;
        }

        // Find scale that fits all components into int8 dynamic range.
        let mut max_abs = 1e-9f32;
        for r in 0..self.k_rank {
            max_abs = max_abs.max(projected[r].abs());
        }
        let scale = max_abs / 127.0;

        let mut encoded_k = vec![0u8; self.k_rank + 4]; // +4 for float32 LE scale
        encoded_k[0..4].copy_from_slice(&scale.to_le_bytes());
        for r in 0..self.k_rank {
            // C#: (int)Math.Round(projected[r] / scale) — banker's rounding.
            let q = round_half_to_even(projected[r] / scale);
            let q = q.clamp(-127, 127);
            encoded_k[4 + r] = (q as i8) as u8;
        }

        // V: nearest-codeword VQ → encode index in ⌈log2(codewords)⌉ bytes.
        let mut best_idx = 0usize;
        let mut best_dist = f32::MAX;
        for c in 0..self.v_codewords {
            let mut d = 0.0f32;
            let word = &self.v_codebook[c];
            for i in 0..self.v_dim {
                let diff = v[i] - word[i];
                d += diff * diff;
            }
            if d < best_dist {
                best_dist = d;
                best_idx = c;
            }
        }

        // Encode index as little-endian uint (1, 2, or 4 bytes depending on size).
        let idx_bytes = Self::idx_bytes(self.v_codewords);
        let mut encoded_v = vec![0u8; idx_bytes];
        match idx_bytes {
            1 => encoded_v[0] = best_idx as u8,
            2 => encoded_v.copy_from_slice(&(best_idx as u16).to_le_bytes()),
            4 => encoded_v.copy_from_slice(&(best_idx as u32).to_le_bytes()),
            _ => {}
        }

        // Materialise the PCA axes once in the frame so the decoder can stand alone.
        let mut axes_flat = vec![0.0f32; self.k_rank * self.k_dim];
        for r in 0..self.k_rank {
            for i in 0..self.k_dim {
                axes_flat[r * self.k_dim + i] = self.k_axes[r][i];
            }
        }

        Ok(ShardCompressedFrame::new(
            encoded_k,
            encoded_v,
            axes_flat,
            self.k_dim,
            self.v_dim,
        ))
    }

    /// (3.3.0) Decode a frame back to approximate K and V.
    pub fn decode(
        &mut self,
        frame: &ShardCompressedFrame,
    ) -> Result<(Vec<f32>, Vec<f32>), ShardCodecError> {
        if frame.k_original_dim != self.k_dim {
            return Err(ShardCodecError("Codec K-dim does not match frame.".into()));
        }
        if frame.v_original_dim != self.v_dim {
            return Err(ShardCodecError("Codec V-dim does not match frame.".into()));
        }

        // K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recenter.
        let mut scale_bytes = [0u8; 4];
        scale_bytes.copy_from_slice(&frame.compressed_k[0..4]);
        let scale = f32::from_le_bytes(scale_bytes);

        let mut projected = vec![0.0f32; self.k_rank];
        for r in 0..self.k_rank {
            let q = frame.compressed_k[4 + r] as i8;
            projected[r] = q as f32 * scale;
        }

        let mut k = vec![0.0f32; self.k_dim];
        for i in 0..self.k_dim {
            let mut acc = 0.0f32;
            for r in 0..self.k_rank {
                acc += projected[r] * frame.k_principal_axes[r * self.k_dim + i];
            }
            k[i] = acc;
        }
        self.apply_hadamard_in_place(&mut k); // Hadamard is self-inverse up to 1/n.
        for i in 0..self.k_dim {
            k[i] = k[i] / self.k_dim as f32 + self.k_center[i];
        }

        // V decode: read index, copy codeword.
        let idx_bytes = Self::idx_bytes(self.v_codewords);
        let idx: usize = match idx_bytes {
            1 => frame.compressed_v[0] as usize,
            2 => {
                let mut b = [0u8; 2];
                b.copy_from_slice(&frame.compressed_v[0..2]);
                u16::from_le_bytes(b) as usize
            }
            4 => {
                let mut b = [0u8; 4];
                b.copy_from_slice(&frame.compressed_v[0..4]);
                u32::from_le_bytes(b) as usize
            }
            _ => 0,
        };
        let mut v = vec![0.0f32; self.v_dim];
        v.copy_from_slice(&self.v_codebook[idx]);
        Ok((k, v))
    }

    fn apply_hadamard_in_place(&mut self, buffer: &mut [f32]) {
        // Fast Walsh-Hadamard transform on the next-power-of-two-sized scratch.
        let n = self.hadamard_scratch.len();
        for x in self.hadamard_scratch.iter_mut() {
            *x = 0.0;
        }
        let copy_len = buffer.len().min(n);
        self.hadamard_scratch[..copy_len].copy_from_slice(&buffer[..copy_len]);

        let mut h = 1usize;
        while h < n {
            let mut i = 0usize;
            while i < n {
                for j in i..(i + h) {
                    let x = self.hadamard_scratch[j];
                    let y = self.hadamard_scratch[j + h];
                    self.hadamard_scratch[j] = x + y;
                    self.hadamard_scratch[j + h] = x - y;
                }
                i += h * 2;
            }
            h <<= 1;
        }

        buffer[..copy_len].copy_from_slice(&self.hadamard_scratch[..copy_len]);
    }

    fn pow2_ceil(v: usize) -> usize {
        let mut p = 1usize;
        while p < v {
            p <<= 1;
        }
        p
    }

    fn idx_bytes(codewords: usize) -> usize {
        if codewords <= 256 {
            1
        } else if codewords <= 65536 {
            2
        } else {
            4
        }
    }

    fn seed_codebook(dim: usize, count: usize, seed: i32) -> Vec<Vec<f32>> {
        let mut rng = DotNetRandom::new(seed);
        let mut cb = Vec::with_capacity(count);
        for _ in 0..count {
            let mut word = vec![0.0f32; dim];
            for x in word.iter_mut() {
                // uniform [-1, 1] — matches C# (float)(rng.NextDouble()*2.0 - 1.0).
                *x = (rng.next_double() * 2.0 - 1.0) as f32;
            }
            cb.push(word);
        }
        cb
    }
}

/// Round-half-to-even (banker's rounding) matching C# `Math.Round(double)` default
/// mode, returning an `i32`. C# rounds a `float` promoted to `double`.
fn round_half_to_even(x: f32) -> i32 {
    let d = x as f64;
    let r = d.round(); // round-half-away-from-zero
    // Adjust the .5 case to round-half-to-even like C#.
    if (d - d.floor() - 0.5).abs() < f64::EPSILON {
        let floor = d.floor();
        let even = if (floor as i64) % 2 == 0 {
            floor
        } else {
            floor + 1.0
        };
        even as i32
    } else {
        r as i32
    }
}
