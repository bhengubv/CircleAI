//! embeddings — CircleAI.Embeddings (Rust port).
//!
//! Port of:
//!   - `CircleAI.Embeddings.ITextEmbedder`
//!   - `CircleAI.Embeddings.TextEmbedder` (+ internal `IEmbeddingBackend`)
//!
//! `TextEmbedder` resolves + verifies an embedding model via an [`IModelManager`],
//! then embeds text through an injectable backend, returning an L2-normalised
//! `Vec<f32>`. The production C# backend is MNN (native); per the porting brief
//! that native dependency is replaced with a deterministic default backend
//! ([`HashingEmbeddingBackend`]) behind the same [`IEmbeddingBackend`] seam.
//!
//! Sync per the crate convention — `GenerateAsync` maps to
//! [`ITextEmbedder::generate`]. Lazy, once-only backend init is guarded by a
//! `Mutex` (the analogue of the C# `SemaphoreSlim` init gate).

use std::sync::{Mutex, OnceLock};

use crate::model_runtime::manager::IModelManager;
use crate::model_runtime::loader::ModelLoaderError;

/// On-device text embedder contract. Mirrors `CircleAI.Embeddings.ITextEmbedder`
/// (sync per crate convention).
pub trait ITextEmbedder {
    /// Generate an embedding vector for `text`.
    fn generate(&self, text: &str) -> Result<Vec<f32>, EmbedderError>;
}

/// Errors surfaced by [`TextEmbedder`].
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum EmbedderError {
    /// `ArgumentException` — empty text.
    Argument(String),
    /// `InvalidDataException` — checksum verification failed.
    InvalidData(String),
    /// A model-manager / resolution failure.
    Model(String),
    /// Backend embedding failure.
    Backend(String),
}

impl std::fmt::Display for EmbedderError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            EmbedderError::Argument(m)
            | EmbedderError::InvalidData(m)
            | EmbedderError::Model(m)
            | EmbedderError::Backend(m) => f.write_str(m),
        }
    }
}

impl std::error::Error for EmbedderError {}

impl From<ModelLoaderError> for EmbedderError {
    fn from(e: ModelLoaderError) -> Self {
        EmbedderError::Model(e.to_string())
    }
}

/// Internal embedding-backend abstraction — lets callers inject a fake without a
/// native library. Mirrors the C# `IEmbeddingBackend`.
pub trait IEmbeddingBackend: Send + Sync {
    /// Number of floats returned by [`IEmbeddingBackend::embed`].
    fn dimension(&self) -> usize;

    /// Embed `text` and return an L2-normalised vector.
    fn embed(&self, text: &str) -> Result<Vec<f32>, EmbedderError>;
}

/// Deterministic default backend: a hashing embedder that maps text into a
/// fixed-dimension bag-of-tokens vector, then L2-normalises — the stand-in for
/// the native MNN backend. Same output shape/semantics (unit-norm `f32[dim]`) so
/// downstream cosine similarity reduces to a dot product.
pub struct HashingEmbeddingBackend {
    dimension: usize,
}

impl HashingEmbeddingBackend {
    /// Construct with the given vector dimension (must be > 0).
    pub fn new(dimension: usize) -> Self {
        assert!(dimension > 0, "dimension must be > 0");
        Self { dimension }
    }

    /// FNV-1a 64-bit hash of a byte slice — deterministic across platforms.
    fn fnv1a(bytes: &[u8]) -> u64 {
        let mut hash: u64 = 0xcbf29ce484222325;
        for &b in bytes {
            hash ^= b as u64;
            hash = hash.wrapping_mul(0x0000_0100_0000_01b3);
        }
        hash
    }
}

impl IEmbeddingBackend for HashingEmbeddingBackend {
    fn dimension(&self) -> usize {
        self.dimension
    }

    fn embed(&self, text: &str) -> Result<Vec<f32>, EmbedderError> {
        let mut v = vec![0.0f32; self.dimension];
        // Split on ASCII whitespace; hash each token into a bin with a signed
        // increment so distinct token sets diverge.
        for token in text.split_whitespace() {
            let lower = token.to_ascii_lowercase();
            let h = Self::fnv1a(lower.as_bytes());
            let bin = (h % self.dimension as u64) as usize;
            let sign = if (h >> 63) & 1 == 1 { -1.0 } else { 1.0 };
            v[bin] += sign;
        }
        l2_normalize(&mut v);
        Ok(v)
    }
}

/// A backend factory — the analogue of `Func<string, IEmbeddingBackend>`. Given a
/// resolved model path, produce a backend.
pub type BackendFactory = dyn Fn(&str) -> Box<dyn IEmbeddingBackend> + Send + Sync;

/// On-device text embedder. Mirrors `CircleAI.Embeddings.TextEmbedder`.
pub struct TextEmbedder<M: IModelManager> {
    model_manager: M,
    expected_checksum: Vec<u8>,
    backend_factory: Box<BackendFactory>,
    backend: OnceLock<Box<dyn IEmbeddingBackend>>,
    init_gate: Mutex<()>,
}

impl<M: IModelManager> TextEmbedder<M> {
    /// Production constructor: default [`HashingEmbeddingBackend`] with a 384-dim
    /// vector (a common sentence-embedding size). Mirrors the C# production ctor
    /// that defaults to the MNN backend.
    pub fn new(model_manager: M, expected_checksum: Vec<u8>) -> Self {
        Self::with_backend_factory(
            model_manager,
            expected_checksum,
            Box::new(|_path: &str| {
                Box::new(HashingEmbeddingBackend::new(384)) as Box<dyn IEmbeddingBackend>
            }),
        )
    }

    /// Testing/advanced constructor: inject the backend factory. Mirrors the C#
    /// internal ctor taking `Func<string, IEmbeddingBackend>`.
    pub fn with_backend_factory(
        model_manager: M,
        expected_checksum: Vec<u8>,
        backend_factory: Box<BackendFactory>,
    ) -> Self {
        Self {
            model_manager,
            expected_checksum,
            backend_factory,
            backend: OnceLock::new(),
            init_gate: Mutex::new(()),
        }
    }

    /// Lazily resolve + verify the model and construct the backend, once.
    fn ensure_backend(&self) -> Result<&dyn IEmbeddingBackend, EmbedderError> {
        if let Some(b) = self.backend.get() {
            return Ok(b.as_ref());
        }
        let _guard = self.init_gate.lock().unwrap();
        if let Some(b) = self.backend.get() {
            return Ok(b.as_ref());
        }

        // Resolve + verify model path via the IModelManager contract.
        let path = self.model_manager.get_model_path("embedding")?;
        let path_str = path.to_string_lossy().into_owned();

        let verified = self
            .model_manager
            .verify_model(&path, &self.expected_checksum)?;
        if !verified {
            return Err(EmbedderError::InvalidData(
                "Embedding model checksum verification failed. \
                 The file may be corrupt or tampered with."
                    .into(),
            ));
        }

        let backend = (self.backend_factory)(&path_str);
        // OnceLock::set can only fail if another thread won the race; either way a
        // value is now present.
        let _ = self.backend.set(backend);
        Ok(self.backend.get().unwrap().as_ref())
    }
}

impl<M: IModelManager> ITextEmbedder for TextEmbedder<M> {
    fn generate(&self, text: &str) -> Result<Vec<f32>, EmbedderError> {
        if text.trim().is_empty() {
            return Err(EmbedderError::Argument("Text cannot be empty.".into()));
        }
        let backend = self.ensure_backend()?;
        backend.embed(text)
    }
}

/// L2-normalise a vector in place (no-op for a zero vector), matching the C#
/// backend's normalisation.
pub(crate) fn l2_normalize(v: &mut [f32]) {
    let mut norm = 0.0f64;
    for &x in v.iter() {
        norm += x as f64 * x as f64;
    }
    let norm = norm.sqrt();
    if norm < 1e-12 {
        return;
    }
    let scale = (1.0 / norm) as f32;
    for x in v.iter_mut() {
        *x *= scale;
    }
}
