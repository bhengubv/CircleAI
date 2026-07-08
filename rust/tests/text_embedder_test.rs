//! text_embedder_test.rs
//!
//! Verifies CircleAI.Embeddings.TextEmbedder port: model resolve + verify gating,
//! empty-text rejection, deterministic hashing backend, and L2-normalised output.

use std::path::{Path, PathBuf};

use circle_ai::embeddings::{
    EmbedderError, HashingEmbeddingBackend, IEmbeddingBackend, ITextEmbedder, TextEmbedder,
};
use circle_ai::model_runtime::{IModelManager, ModelLoaderError};

/// Mock manager: returns a fixed path and a caller-chosen verification verdict.
struct MockManager {
    verify_result: bool,
}
impl IModelManager for MockManager {
    fn get_model_path(&self, _model_id: &str) -> Result<PathBuf, ModelLoaderError> {
        Ok(PathBuf::from("embedding/model.gguf"))
    }
    fn verify_model(
        &self,
        _model_path: &Path,
        _expected_checksum: &[u8],
    ) -> Result<bool, ModelLoaderError> {
        Ok(self.verify_result)
    }
}

fn l2(v: &[f32]) -> f32 {
    v.iter().map(|&x| x as f64 * x as f64).sum::<f64>().sqrt() as f32
}

#[test]
fn hashing_backend_is_deterministic_and_unit_norm() {
    let b = HashingEmbeddingBackend::new(64);
    assert_eq!(b.dimension(), 64);
    let a = b.embed("the quick brown fox").unwrap();
    let c = b.embed("the quick brown fox").unwrap();
    assert_eq!(a, c);
    assert_eq!(a.len(), 64);
    assert!((l2(&a) - 1.0).abs() < 1e-5, "not unit norm: {}", l2(&a));
}

#[test]
fn hashing_backend_distinguishes_texts() {
    let b = HashingEmbeddingBackend::new(128);
    let a = b.embed("apples").unwrap();
    let c = b.embed("oranges").unwrap();
    assert_ne!(a, c);
}

#[test]
fn embedder_generates_unit_vector() {
    let mgr = MockManager { verify_result: true };
    let embedder = TextEmbedder::new(mgr, vec![1, 2, 3]);
    let v = embedder.generate("hello world").unwrap();
    assert_eq!(v.len(), 384);
    assert!((l2(&v) - 1.0).abs() < 1e-5);
}

#[test]
fn embedder_rejects_empty_text() {
    let mgr = MockManager { verify_result: true };
    let embedder = TextEmbedder::new(mgr, vec![1]);
    assert!(matches!(
        embedder.generate("   "),
        Err(EmbedderError::Argument(_))
    ));
}

#[test]
fn embedder_fails_when_checksum_unverified() {
    let mgr = MockManager {
        verify_result: false,
    };
    let embedder = TextEmbedder::new(mgr, vec![9, 9]);
    assert!(matches!(
        embedder.generate("anything"),
        Err(EmbedderError::InvalidData(_))
    ));
}

#[test]
fn embedder_uses_injected_backend_factory() {
    // Inject a backend factory producing a fixed-dim backend and confirm the
    // resolved model path is threaded through.
    let mgr = MockManager { verify_result: true };
    let embedder = TextEmbedder::with_backend_factory(
        mgr,
        vec![0],
        Box::new(|path: &str| {
            assert!(path.contains("model.gguf"));
            Box::new(HashingEmbeddingBackend::new(16)) as Box<dyn IEmbeddingBackend>
        }),
    );
    let v = embedder.generate("token one two").unwrap();
    assert_eq!(v.len(), 16);
}

#[test]
fn embedder_initialises_backend_once() {
    // Two calls reuse the same backend (init gate); both succeed and match.
    let mgr = MockManager { verify_result: true };
    let embedder = TextEmbedder::new(mgr, vec![1]);
    let a = embedder.generate("stable text").unwrap();
    let b = embedder.generate("stable text").unwrap();
    assert_eq!(a, b);
}
