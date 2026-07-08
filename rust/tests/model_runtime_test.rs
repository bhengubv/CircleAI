//! model_runtime_test.rs
//!
//! Exercises the CircleAI.Core model-management runtime port: sources +
//! downloader, LocalModelLoader, LocalModelManager, CircleEngine + modules,
//! tenant context, audit sinks, and PlatformInterop / SafeModelHandle.

use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Arc;

use chrono::Utc;
use circle_ai::model_runtime::{
    outcomes, CircleAIAuditEntry, CircleAIAuditQuery, CircleAIAuditing, CircleEngine, ContentProvider,
    ICircleAIAuditLog, ICircleAITenantContext, ICircleModule, IModelDownloader, IModelLoader,
    IModelManager, IModelSource, InMemoryContentProvider, InMemoryNativeLoader, InteropError,
    LocalModelLoader, LocalModelManager, LoggerAuditLog, ModelDownloader, ModelEntry, ModelInfo,
    ModelScopeSource, NativeModelLoader, NoopAuditLog, NullTenantContext, PlatformInterop,
    SingleTenantContext,
};

// SHA-256("abc") — used for deterministic positive checksum verification.
const ABC_SHA256_HEX: &str = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
const ABC_SHA256_BYTES: [u8; 32] = [
    0xba, 0x78, 0x16, 0xbf, 0x8f, 0x01, 0xcf, 0xea, 0x41, 0x41, 0x40, 0xde, 0x5d, 0xae, 0x22, 0x23,
    0xb0, 0x03, 0x61, 0xa3, 0x96, 0x17, 0x7a, 0x9c, 0xb4, 0x10, 0xff, 0x61, 0xf2, 0x00, 0x15, 0xad,
];

fn temp_dir(label: &str) -> PathBuf {
    let mut p = std::env::temp_dir();
    let nonce = uuid::Uuid::new_v4().simple().to_string();
    p.push(format!("circleai-rust-mr-{label}-{nonce}"));
    std::fs::create_dir_all(&p).unwrap();
    p
}

// ── Sources ────────────────────────────────────────────────────────────────

#[test]
fn modelscope_source_rejects_non_modelscope_host() {
    let src = ModelScopeSource::new();
    let out = temp_dir("ms-host").join("f.bin");
    let err = src
        .download("https://huggingface.co/model/file.bin", &out, None)
        .unwrap_err();
    assert!(err.0.contains("modelscope.cn"), "got: {}", err.0);
}

#[test]
fn modelscope_source_downloads_registered_bytes() {
    let provider = InMemoryContentProvider::new();
    let url = "https://modelscope.cn/models/test/repo/file.bin";
    provider.insert(url, b"hello-model".to_vec());
    let src = ModelScopeSource::with_provider(Arc::new(provider));

    assert_eq!(src.name(), "ModelScope");
    let out = temp_dir("ms-dl").join("file.bin");
    src.download(url, &out, None).unwrap();
    assert_eq!(std::fs::read(&out).unwrap(), b"hello-model");
}

#[test]
fn modelscope_availability_follows_probe() {
    let provider = InMemoryContentProvider::new();
    let src = ModelScopeSource::with_provider(Arc::new(provider.clone()));
    assert!(!src.is_available());
    provider.insert("https://modelscope.cn/", b"ok".to_vec());
    assert!(src.is_available());
}

// ── Downloader ───────────────────────────────────────────────────────────────

#[test]
fn downloader_requires_at_least_one_source() {
    let empty: Vec<Box<dyn IModelSource + Send + Sync>> = Vec::new();
    assert!(ModelDownloader::new(empty).is_err());
}

#[test]
fn downloader_resolves_from_registry_and_falls_through() {
    let provider = Arc::new(InMemoryContentProvider::new());
    let primary = "https://modelscope.cn/models/x/primary.bin"; // missing → fails
    let fallback = "https://modelscope.cn/models/x/fallback.bin";
    provider.insert(fallback, b"fallback-bytes".to_vec());

    let src = ModelScopeSource::with_provider(provider.clone());
    let sources: Vec<Box<dyn IModelSource + Send + Sync>> = vec![Box::new(src)];

    let mut registry = HashMap::new();
    registry.insert(
        "demo".to_string(),
        ModelEntry {
            file_name: "weights.bin".to_string(),
            primary_url: Some(primary.to_string()),
            fallback_url: Some(fallback.to_string()),
            ..Default::default()
        },
    );

    let dl = ModelDownloader::with_registry(sources, registry).unwrap();
    let target = temp_dir("dl-fallthrough");
    dl.download_model("demo", &target).unwrap();
    assert_eq!(
        std::fs::read(target.join("weights.bin")).unwrap(),
        b"fallback-bytes"
    );
}

#[test]
fn downloader_unknown_model_errors_with_known_keys() {
    let src = ModelScopeSource::new();
    let sources: Vec<Box<dyn IModelSource + Send + Sync>> = vec![Box::new(src)];
    let mut registry = HashMap::new();
    registry.insert("alpha".to_string(), ModelEntry::default());
    let dl = ModelDownloader::with_registry(sources, registry).unwrap();
    let err = dl.download_model("missing", &temp_dir("dl-unknown")).unwrap_err();
    assert!(err.0.contains("alpha"), "got: {}", err.0);
}

#[test]
fn downloader_bundle_entry_steers_to_bundle_path() {
    use circle_ai::model_runtime::BundleFileEntry;
    let src = ModelScopeSource::new();
    let sources: Vec<Box<dyn IModelSource + Send + Sync>> = vec![Box::new(src)];
    let mut registry = HashMap::new();
    registry.insert(
        "bundle".to_string(),
        ModelEntry {
            repo: Some("org/repo".to_string()),
            bundle_files: Some(vec![BundleFileEntry {
                name: "llm.mnn.weight".to_string(),
                sha256: "deadbeef".to_string(),
                size_bytes: 10,
            }]),
            ..Default::default()
        },
    );
    let dl = ModelDownloader::with_registry(sources, registry).unwrap();
    let err = dl.download_model("bundle", &temp_dir("dl-bundle")).unwrap_err();
    assert!(err.0.contains("multi-file MNN bundle"), "got: {}", err.0);
}

// ── LocalModelLoader ─────────────────────────────────────────────────────────

fn loader_with(model: &str, info: ModelInfo, provider: Arc<dyn ContentProvider>) -> (LocalModelLoader, PathBuf) {
    let dir = temp_dir("loader");
    let mut reg = HashMap::new();
    reg.insert(model.to_string(), info);
    let loader = LocalModelLoader::with_provider(&dir, reg, provider).unwrap();
    (loader, dir)
}

#[test]
fn loader_unsupported_model_errors() {
    let (loader, _dir) = loader_with(
        "known",
        ModelInfo {
            file_name: Some("k.bin".to_string()),
            ..Default::default()
        },
        Arc::new(InMemoryContentProvider::new()),
    );
    let err = loader.download_model("unknown", None).unwrap_err();
    assert!(err.to_string().contains("not supported"));
}

#[test]
fn loader_downloads_and_verifies_checksum() {
    let provider = InMemoryContentProvider::new();
    let url = "https://modelscope.cn/x/abc.bin";
    provider.insert(url, b"abc".to_vec());
    let (loader, _dir) = loader_with(
        "abc",
        ModelInfo {
            file_name: Some("abc.bin".to_string()),
            primary_url: Some(url.to_string()),
            checksum: Some(format!("sha256:{ABC_SHA256_HEX}")),
            ..Default::default()
        },
        Arc::new(provider),
    );
    let path = loader.download_model("abc", None).unwrap();
    assert_eq!(std::fs::read(&path).unwrap(), b"abc");
    // The model now "exists" (present + checksum verifies).
    assert!(loader.model_exists("abc"));
}

#[test]
fn loader_checksum_mismatch_is_reported() {
    let provider = InMemoryContentProvider::new();
    let url = "https://modelscope.cn/x/bad.bin";
    provider.insert(url, b"not-abc".to_vec());
    let (loader, _dir) = loader_with(
        "bad",
        ModelInfo {
            file_name: Some("bad.bin".to_string()),
            primary_url: Some(url.to_string()),
            checksum: Some(format!("sha256:{ABC_SHA256_HEX}")),
            ..Default::default()
        },
        Arc::new(provider),
    );
    let err = loader.download_model("bad", None).unwrap_err();
    assert!(err.to_string().contains("checksum"), "got: {err}");
}

#[test]
fn loader_tbd_checksum_skips_verification() {
    let provider = InMemoryContentProvider::new();
    let url = "https://modelscope.cn/x/tbd.bin";
    provider.insert(url, b"whatever".to_vec());
    let (loader, _dir) = loader_with(
        "tbd",
        ModelInfo {
            file_name: Some("tbd.bin".to_string()),
            primary_url: Some(url.to_string()),
            checksum: Some("sha256:TBD".to_string()),
            ..Default::default()
        },
        Arc::new(provider),
    );
    let path = loader.download_model("tbd", None).unwrap();
    assert!(path.exists());
}

#[test]
fn loader_bundle_download_is_rejected() {
    use circle_ai::model_runtime::BundleFileInfo;
    let (loader, _dir) = loader_with(
        "b",
        ModelInfo {
            bundle_files: Some(vec![BundleFileInfo {
                name: "llm.mnn.weight".to_string(),
                sha256: "x".to_string(),
                size_bytes: 1,
            }]),
            ..Default::default()
        },
        Arc::new(InMemoryContentProvider::new()),
    );
    let err = loader.download_model("b", None).unwrap_err();
    assert!(err.to_string().contains("multi-file bundle"));
    // Bundle path layout: <dir>/<model>/llm.mnn.weight.
    let p = loader.get_model_path("b").unwrap();
    assert!(p.ends_with("llm.mnn.weight"));
}

#[test]
fn loader_critical_update_probe() {
    let provider = InMemoryContentProvider::new();
    provider.insert(
        "https://raw.githubusercontent.com/BhenguAI/models/main/versions.txt",
        b"v1.0 [CRITICAL] security fix".to_vec(),
    );
    let (loader, _dir) = loader_with(
        "any",
        ModelInfo {
            file_name: Some("a.bin".to_string()),
            ..Default::default()
        },
        Arc::new(provider),
    );
    assert!(loader.check_for_critical_update());
}

#[test]
fn loader_progress_callback_fires() {
    let provider = InMemoryContentProvider::new();
    let url = "https://modelscope.cn/x/p.bin";
    provider.insert(url, b"abc".to_vec());
    let (loader, _dir) = loader_with(
        "p",
        ModelInfo {
            file_name: Some("p.bin".to_string()),
            primary_url: Some(url.to_string()),
            checksum: None,
            ..Default::default()
        },
        Arc::new(provider),
    );
    let mut seen = 0.0f32;
    {
        let mut cb = |f: f32| seen = f;
        loader.download_model("p", Some(&mut cb)).unwrap();
    }
    assert_eq!(seen, 1.0);
}

// ── LocalModelManager ────────────────────────────────────────────────────────

/// A source that writes `pytorch_model.bin` with a caller-chosen payload when the
/// downloader asks — lets the manager tests exercise the download+verify path.
struct SeedingDownloader {
    payload: Vec<u8>,
}

impl IModelDownloader for SeedingDownloader {
    fn download_model(
        &self,
        _model_id: &str,
        local_path: &std::path::Path,
    ) -> Result<(), circle_ai::model_runtime::SourceError> {
        std::fs::create_dir_all(local_path).unwrap();
        std::fs::write(local_path.join("pytorch_model.bin"), &self.payload).unwrap();
        Ok(())
    }
    fn download_from_candidates(
        &self,
        _candidate_urls: &[String],
        _local_file_path: &std::path::Path,
        _progress: Option<&mut circle_ai::model_runtime::ProgressSink<'_>>,
    ) -> Result<String, circle_ai::model_runtime::SourceError> {
        Ok("Seeding".to_string())
    }
}

#[test]
fn manager_downloads_when_missing_then_verifies() {
    let dir = temp_dir("mgr-dl");
    let downloader = Arc::new(SeedingDownloader {
        payload: b"abc".to_vec(),
    });
    let mgr = LocalModelManager::with_downloader(downloader, &dir).unwrap();

    // Resolve (downloads pytorch_model.bin).
    let path = mgr.get_model_path("org/model").unwrap();
    assert!(path.join("pytorch_model.bin").exists());
    // Sanitised id: "/" → "_".
    assert!(path.ends_with("org_model"));

    // Verify against the correct SHA-256 of "abc".
    assert!(mgr.verify_model(&path, &ABC_SHA256_BYTES).unwrap());
    // Wrong checksum → false.
    assert!(!mgr.verify_model(&path, &[0u8; 32]).unwrap());
}

#[test]
fn manager_verified_path_rejects_bad_checksum() {
    let dir = temp_dir("mgr-bad");
    let downloader = Arc::new(SeedingDownloader {
        payload: b"not-abc".to_vec(),
    });
    let mgr = LocalModelManager::with_downloader(downloader, &dir).unwrap();
    let err = mgr
        .get_model_path_verified("m", Some(&ABC_SHA256_BYTES))
        .unwrap_err();
    assert!(err.to_string().contains("checksum verification failed"));
}

#[test]
fn manager_without_downloader_errors_when_missing() {
    let dir = temp_dir("mgr-none");
    let mgr = LocalModelManager::new(None, &dir).unwrap();
    let err = mgr.get_model_path("x").unwrap_err();
    assert!(err.to_string().contains("no downloader configured"));
}

#[test]
fn manager_with_url_builds_modelscope_downloader() {
    let dir = temp_dir("mgr-url");
    // Just constructs — no network is touched until a missing model is requested.
    let mgr = LocalModelManager::new(Some("https://modelscope.cn/"), &dir).unwrap();
    // A model whose files already exist is returned without download.
    let model_dir = dir.join("present");
    std::fs::create_dir_all(&model_dir).unwrap();
    std::fs::write(model_dir.join("pytorch_model.bin"), b"abc").unwrap();
    let path = mgr.get_model_path("present").unwrap();
    assert_eq!(path, model_dir);
}

// ── CircleEngine + modules ───────────────────────────────────────────────────

struct DummyLoader;
impl IModelLoader for DummyLoader {
    fn download_model(
        &self,
        _model_name: &str,
        _progress: Option<&mut dyn FnMut(f32)>,
    ) -> Result<PathBuf, circle_ai::model_runtime::ModelLoaderError> {
        Ok(PathBuf::from("dummy"))
    }
    fn get_model_path(
        &self,
        _model_name: &str,
    ) -> Result<PathBuf, circle_ai::model_runtime::ModelLoaderError> {
        Ok(PathBuf::from("dummy"))
    }
    fn model_exists(&self, _model_name: &str) -> bool {
        true
    }
    fn check_for_critical_update(&self) -> bool {
        false
    }
}

struct DummyModule {
    loaded: bool,
}
impl ICircleModule for DummyModule {
    fn module_name(&self) -> &str {
        "DummyModule"
    }
    fn init(&mut self, _engine: &CircleEngine) {
        self.loaded = true;
    }
    fn is_model_loaded(&self) -> bool {
        self.loaded
    }
}

#[test]
fn engine_registers_and_retrieves_modules_by_type() {
    let loader: Arc<dyn IModelLoader + Send + Sync> = Arc::new(DummyLoader);
    let mut engine = CircleEngine::new(loader);
    assert!(!engine.has_module::<DummyModule>());
    engine.register_module(DummyModule { loaded: false });
    assert!(engine.has_module::<DummyModule>());
    let m = engine.get_module::<DummyModule>().unwrap();
    assert_eq!(m.module_name(), "DummyModule");
    assert!(!m.is_model_loaded());
    // Missing type → None.
    assert!(engine.get_module::<String>().is_none());
}

#[test]
fn engine_embedding_service_slot() {
    let loader: Arc<dyn IModelLoader + Send + Sync> = Arc::new(DummyLoader);
    let mut engine = CircleEngine::new(loader);
    assert!(!engine.has_embedding_service());
    engine.set_embedding_service(384usize);
    assert!(engine.has_embedding_service());
    assert_eq!(engine.embedding_service::<usize>().copied(), Some(384));
}

// ── Tenant context ───────────────────────────────────────────────────────────

#[test]
fn null_tenant_context_throws() {
    let ctx = NullTenantContext::new();
    assert!(!ctx.has_tenant());
    assert!(ctx.current_tenant_id().is_err());
}

#[test]
fn single_tenant_context_returns_fixed_id() {
    let ctx = SingleTenantContext::new("acme").unwrap();
    assert!(ctx.has_tenant());
    assert_eq!(ctx.current_tenant_id().unwrap(), "acme");
    // Whitespace id rejected.
    assert!(SingleTenantContext::new("   ").is_err());
}

// ── Auditing ─────────────────────────────────────────────────────────────────

#[test]
fn noop_audit_drops_and_returns_empty() {
    let log = NoopAuditLog::new();
    log.record(&CircleAIAuditEntry::new(
        Utc::now(),
        "Comp",
        "Op",
        outcomes::SUCCESS,
    ));
    assert!(log.query(&CircleAIAuditQuery::default()).is_empty());
}

#[test]
fn logger_audit_captures_structured_line() {
    let (log, buf) = LoggerAuditLog::capturing();
    let mut entry = CircleAIAuditEntry::new(Utc::now(), "JsonPersonaProvider", "GetAsync", outcomes::ERROR);
    entry.tenant_id = Some("t1".to_string());
    entry.error_type = Some("InvalidOperationException".to_string());
    entry.duration_ms = 12.5;
    log.record(&entry);
    let lines = buf.lock().unwrap();
    assert_eq!(lines.len(), 1);
    let line = &lines[0];
    assert!(line.contains("JsonPersonaProvider.GetAsync"));
    assert!(line.contains("error"));
    assert!(line.contains("tenant=t1"));
    assert!(line.contains("InvalidOperationException"));
    // Query is always empty for the logger sink.
    assert!(log.query(&CircleAIAuditQuery::default()).is_empty());
}

#[test]
fn ambient_auditing_default_is_noop_and_settable() {
    CircleAIAuditing::reset_to_noop();
    // Default sink records without panicking.
    CircleAIAuditing::default_sink().record(&CircleAIAuditEntry::new(
        Utc::now(),
        "C",
        "O",
        outcomes::SUCCESS,
    ));
    let (log, buf) = LoggerAuditLog::capturing();
    CircleAIAuditing::set_default(Arc::new(log));
    CircleAIAuditing::default_sink().record(&CircleAIAuditEntry::new(
        Utc::now(),
        "C2",
        "O2",
        outcomes::SUCCESS,
    ));
    assert_eq!(buf.lock().unwrap().len(), 1);
    CircleAIAuditing::reset_to_noop();
}

// ── PlatformInterop + SafeModelHandle ────────────────────────────────────────

#[test]
fn platform_interop_rejects_empty_and_missing_paths() {
    let interop = PlatformInterop::new();
    assert!(matches!(
        interop.load_model("  "),
        Err(InteropError::Argument(_))
    ));
    assert!(matches!(
        interop.load_model("C:/no/such/model.gguf"),
        Err(InteropError::NotFound(_))
    ));
}

#[test]
fn platform_interop_loads_and_frees_on_drop() {
    let native = Arc::new(InMemoryNativeLoader::new());
    let interop = PlatformInterop::with_native(native.clone());

    let dir = temp_dir("interop");
    let model = dir.join("model.gguf");
    std::fs::write(&model, b"gguf-bytes").unwrap();

    {
        let handle = interop.load_model(model.to_str().unwrap()).unwrap();
        assert!(!handle.is_invalid());
        assert_eq!(native.live_count(), 1);
        // Handle drops at end of scope → native free runs.
    }
    assert_eq!(native.live_count(), 0);
}

#[test]
fn native_load_failure_surfaces_invalid_operation() {
    struct FailingLoader;
    impl NativeModelLoader for FailingLoader {
        fn load(&self, _path: &str) -> usize {
            0 // null pointer → failure
        }
        fn free(&self, _handle: usize) {}
    }
    let interop = PlatformInterop::with_native(Arc::new(FailingLoader));
    let dir = temp_dir("interop-fail");
    let model = dir.join("m.gguf");
    std::fs::write(&model, b"x").unwrap();
    assert!(matches!(
        interop.load_model(model.to_str().unwrap()),
        Err(InteropError::InvalidOperation(_))
    ));
}
