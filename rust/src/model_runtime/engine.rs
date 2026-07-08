//! engine.rs
//!
//! Port of:
//!   - `CircleAI.Core.CircleEngine`
//!   - `CircleAI.Core.ICircleModule`
//!   - `CircleAI.Core.IEmbeddingService`
//!
//! `CircleEngine` is the top-level facade: it holds an [`IModelLoader`] and a
//! type-keyed module bag. In C# the bag is `Dictionary<Type, object>`; the
//! idiomatic Rust equivalent is a `TypeId`-keyed map of `Box<dyn Any>`.

use std::any::{Any, TypeId};
use std::collections::HashMap;
use std::sync::Arc;

use super::loader::IModelLoader;

/// A CircleAI module — an attachable service (embeddings, search, chat, tools).
/// Mirrors `CircleAI.Core.ICircleModule` (sync per crate convention;
/// `IDisposable` maps to `Drop`).
pub trait ICircleModule {
    /// Human-readable module name.
    fn module_name(&self) -> &str;

    /// Initialise against the engine.
    fn init(&mut self, engine: &CircleEngine);

    /// True once the module's model is loaded.
    fn is_model_loaded(&self) -> bool;
}

/// An on-device text embedding service. Mirrors `CircleAI.Core.IEmbeddingService`
/// (extends [`ICircleModule`]).
pub trait IEmbeddingService: ICircleModule {
    /// Generate an embedding vector for `text`.
    fn generate_embedding(&self, text: &str) -> Vec<f32>;

    /// Dimensionality of the vectors this service produces.
    fn embedding_size(&self) -> usize;
}

/// Top-level facade for the CircleAI on-device stack. Holds the model loader and
/// a small registry of attached modules keyed by type. Mirrors
/// `CircleAI.Core.CircleEngine`.
pub struct CircleEngine {
    model_loader: Arc<dyn IModelLoader + Send + Sync>,
    modules: HashMap<TypeId, Box<dyn Any + Send + Sync>>,
    /// Optional embedding service, wired in by downstream extensions. Kept as an
    /// opaque handle so Core does not depend on embedding implementations.
    embedding_service: Option<Box<dyn Any + Send + Sync>>,
}

impl CircleEngine {
    /// Construct with the model loader used to acquire/cache model files.
    pub fn new(model_loader: Arc<dyn IModelLoader + Send + Sync>) -> Self {
        Self {
            model_loader,
            modules: HashMap::new(),
            embedding_service: None,
        }
    }

    /// The model loader used to acquire and cache model files.
    pub fn model_loader(&self) -> &Arc<dyn IModelLoader + Send + Sync> {
        &self.model_loader
    }

    /// Register a module instance keyed by its concrete type `T`. Returns `&mut
    /// self` for chaining (mirrors the fluent C# `RegisterModule<T>`).
    pub fn register_module<T: Any + Send + Sync>(&mut self, module: T) -> &mut Self {
        self.modules.insert(TypeId::of::<T>(), Box::new(module));
        self
    }

    /// Retrieve a previously registered module of type `T`, or `None`.
    pub fn get_module<T: Any + Send + Sync>(&self) -> Option<&T> {
        self.modules
            .get(&TypeId::of::<T>())
            .and_then(|b| b.downcast_ref::<T>())
    }

    /// True if a module of type `T` has been registered.
    pub fn has_module<T: Any + Send + Sync>(&self) -> bool {
        self.modules.contains_key(&TypeId::of::<T>())
    }

    /// Set the optional embedding service (opaque, like the C# settable
    /// `object? EmbeddingService`).
    pub fn set_embedding_service<T: Any + Send + Sync>(&mut self, service: T) {
        self.embedding_service = Some(Box::new(service));
    }

    /// Retrieve the embedding service downcast to `T`, or `None`.
    pub fn embedding_service<T: Any + Send + Sync>(&self) -> Option<&T> {
        self.embedding_service
            .as_ref()
            .and_then(|b| b.downcast_ref::<T>())
    }

    /// True when an embedding service has been wired.
    pub fn has_embedding_service(&self) -> bool {
        self.embedding_service.is_some()
    }
}
