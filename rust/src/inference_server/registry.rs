//! registry.rs
//!
//! In-process registry mapping logical model IDs to the [`IInferenceBridge`]
//! that serves them (chat) plus [`ITextEmbedder`]s (embeddings). Ported from
//! `CircleAI.Inference.Server/Models/ModelRegistry.cs`.
//!
//! The host populates this at startup (one bridge per loaded model) and the
//! endpoints look up by `request.model`. Thread-safe via a mutex over
//! copy-on-read maps.

use std::collections::BTreeMap;
use std::sync::{Arc, Mutex};

use crate::embeddings::ITextEmbedder;
use crate::inference_server::bridge::IInferenceBridge;

/// Shared bridge handle stored in the registry.
pub type SharedBridge = Arc<dyn IInferenceBridge>;
/// Shared embedder handle stored in the registry.
pub type SharedEmbedder = Arc<dyn ITextEmbedder + Send + Sync>;

/// In-process registry of bridge instances keyed by logical model ID. Sync port
/// of `IInferenceServerModelRegistry`.
pub trait IInferenceServerModelRegistry: Send + Sync {
    /// Register a chat bridge under `model_id`.
    fn register(&self, model_id: &str, bridge: SharedBridge);

    /// Register an embedder under `model_id`.
    fn register_embedder(&self, model_id: &str, embedder: SharedEmbedder);

    /// Remove the bridge under `model_id`. Returns `true` when one was removed.
    fn deregister(&self, model_id: &str) -> bool;

    /// Look up a chat bridge. `None` when the model is not registered.
    fn resolve(&self, model_id: &str) -> Option<SharedBridge>;

    /// Look up an embedder.
    fn resolve_embedder(&self, model_id: &str) -> Option<SharedEmbedder>;

    /// Every model ID currently served (chat + embedding), de-duplicated.
    fn all_model_ids(&self) -> Vec<String>;

    /// Chat-capable model IDs only.
    fn chat_model_ids(&self) -> Vec<String>;
}

/// Default thread-safe registry. Mirrors `InferenceServerModelRegistry`.
#[derive(Default)]
pub struct InferenceServerModelRegistry {
    chat: Mutex<BTreeMap<String, SharedBridge>>,
    embed: Mutex<BTreeMap<String, SharedEmbedder>>,
}

impl InferenceServerModelRegistry {
    /// Constructs an empty registry.
    pub fn new() -> Self {
        Self {
            chat: Mutex::new(BTreeMap::new()),
            embed: Mutex::new(BTreeMap::new()),
        }
    }
}

impl IInferenceServerModelRegistry for InferenceServerModelRegistry {
    fn register(&self, model_id: &str, bridge: SharedBridge) {
        assert!(!model_id.trim().is_empty(), "modelId required");
        self.chat.lock().unwrap().insert(model_id.to_string(), bridge);
    }

    fn register_embedder(&self, model_id: &str, embedder: SharedEmbedder) {
        assert!(!model_id.trim().is_empty(), "modelId required");
        self.embed
            .lock()
            .unwrap()
            .insert(model_id.to_string(), embedder);
    }

    fn deregister(&self, model_id: &str) -> bool {
        self.chat.lock().unwrap().remove(model_id).is_some()
    }

    fn resolve(&self, model_id: &str) -> Option<SharedBridge> {
        self.chat.lock().unwrap().get(model_id).cloned()
    }

    fn resolve_embedder(&self, model_id: &str) -> Option<SharedEmbedder> {
        self.embed.lock().unwrap().get(model_id).cloned()
    }

    fn all_model_ids(&self) -> Vec<String> {
        let mut ids: Vec<String> = Vec::new();
        {
            let chat = self.chat.lock().unwrap();
            for k in chat.keys() {
                ids.push(k.clone());
            }
        }
        {
            let embed = self.embed.lock().unwrap();
            for k in embed.keys() {
                if !ids.contains(k) {
                    ids.push(k.clone());
                }
            }
        }
        ids
    }

    fn chat_model_ids(&self) -> Vec<String> {
        self.chat.lock().unwrap().keys().cloned().collect()
    }
}
