//! platform_interop.rs
//!
//! Port of:
//!   - `CircleAI.Core.SafeModelHandle`
//!   - `CircleAI.Core.PlatformInterop`
//!
//! `SafeModelHandle` wraps an opaque native model pointer with a release callback
//! invoked exactly once when the handle is dropped (the analogue of
//! `SafeHandle.ReleaseHandle`). `PlatformInterop` loads a model and returns such a
//! handle.
//!
//! The real C# path P/Invokes llama.cpp. Per the porting brief the native library
//! is replaced with an injected loader ([`NativeModelLoader`]) — the *contract*
//! (validate path, validate existence, produce a handle, free-on-drop) is
//! preserved. A default deterministic in-memory loader is provided so tests need
//! no native dependency.

use std::fs;
use std::path::Path;
use std::sync::atomic::{AtomicUsize, Ordering};
use std::sync::Arc;

/// A release callback — the analogue of the managed free callback the C# loader
/// supplies to `SafeModelHandle`.
pub type ReleaseCallback = dyn Fn(usize) + Send + Sync;

/// SafeHandle wrapper around an opaque native model pointer. The release callback
/// is supplied by the loader so this type stays free of native imports. The
/// callback runs at most once — either on explicit [`SafeModelHandle::release`]
/// or on `Drop`. Mirrors `CircleAI.Core.SafeModelHandle`.
pub struct SafeModelHandle {
    handle: usize,
    release_callback: Option<Arc<ReleaseCallback>>,
    released: bool,
}

impl SafeModelHandle {
    /// Constructs a wrapper around a known native pointer with an explicit release
    /// callback. Mirrors `SafeModelHandle(IntPtr, Action<IntPtr>)`.
    pub fn new(native_handle: usize, release_callback: Arc<ReleaseCallback>) -> Self {
        Self {
            handle: native_handle,
            release_callback: Some(release_callback),
            released: false,
        }
    }

    /// An invalid handle (null pointer, no callback). Mirrors the default ctor.
    pub fn invalid() -> Self {
        Self {
            handle: 0,
            release_callback: None,
            released: false,
        }
    }

    /// Wires up (or replaces) the release callback after construction. Mirrors
    /// `WithReleaseCallback`.
    pub fn with_release_callback(mut self, release_callback: Arc<ReleaseCallback>) -> Self {
        self.release_callback = Some(release_callback);
        self
    }

    /// True when the handle is a null pointer. Mirrors `IsInvalid`.
    pub fn is_invalid(&self) -> bool {
        self.handle == 0
    }

    /// The raw native pointer value.
    pub fn raw(&self) -> usize {
        self.handle
    }

    /// Release the handle now (idempotent). Mirrors `ReleaseHandle`.
    pub fn release(&mut self) -> bool {
        if !self.released && self.handle != 0 {
            if let Some(cb) = &self.release_callback {
                cb(self.handle);
            }
            self.handle = 0;
            self.released = true;
        }
        true
    }
}

impl Drop for SafeModelHandle {
    fn drop(&mut self) {
        self.release();
    }
}

impl std::fmt::Debug for SafeModelHandle {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("SafeModelHandle")
            .field("handle", &self.handle)
            .field("released", &self.released)
            .finish()
    }
}

/// Errors from [`PlatformInterop::load_model`]. Mirrors the C# exception set.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum InteropError {
    /// `ArgumentException` — path null/empty.
    Argument(String),
    /// `FileNotFoundException`.
    NotFound(String),
    /// `InvalidOperationException` — native load failed.
    InvalidOperation(String),
}

impl std::fmt::Display for InteropError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            InteropError::Argument(m) | InteropError::NotFound(m) | InteropError::InvalidOperation(m) => {
                f.write_str(m)
            }
        }
    }
}

impl std::error::Error for InteropError {}

/// The native loader seam. Production wires a real llama.cpp binding; the default
/// [`InMemoryNativeLoader`] is deterministic and dependency-free.
pub trait NativeModelLoader: Send + Sync {
    /// Load the model at `path`, returning a non-zero native pointer, or `0` on
    /// failure (mirrors `llama_model_load_from_file` returning `IntPtr.Zero`).
    fn load(&self, path: &str) -> usize;

    /// Free a previously-loaded native pointer.
    fn free(&self, handle: usize);
}

/// Deterministic in-memory [`NativeModelLoader`]. Hands out monotonically
/// increasing non-zero "pointers" and tracks the live set so tests can assert
/// free-on-drop.
#[derive(Default)]
pub struct InMemoryNativeLoader {
    next: AtomicUsize,
    live: std::sync::Mutex<std::collections::HashSet<usize>>,
}

impl InMemoryNativeLoader {
    pub fn new() -> Self {
        Self {
            next: AtomicUsize::new(1),
            live: std::sync::Mutex::new(std::collections::HashSet::new()),
        }
    }

    /// How many handles are currently live (loaded but not yet freed).
    pub fn live_count(&self) -> usize {
        self.live.lock().unwrap().len()
    }
}

impl NativeModelLoader for InMemoryNativeLoader {
    fn load(&self, _path: &str) -> usize {
        let h = self.next.fetch_add(1, Ordering::SeqCst);
        self.live.lock().unwrap().insert(h);
        h
    }

    fn free(&self, handle: usize) {
        self.live.lock().unwrap().remove(&handle);
    }
}

/// Loads native models and returns opaque [`SafeModelHandle`]s. Mirrors
/// `PlatformInterop`. The native library is injected via [`NativeModelLoader`].
pub struct PlatformInterop {
    native: Arc<dyn NativeModelLoader>,
}

impl PlatformInterop {
    /// Construct with the default deterministic in-memory native loader.
    pub fn new() -> Self {
        Self {
            native: Arc::new(InMemoryNativeLoader::new()),
        }
    }

    /// Construct with an injected native loader.
    pub fn with_native(native: Arc<dyn NativeModelLoader>) -> Self {
        Self { native }
    }

    /// Loads a GGUF model from `path`. Mirrors `PlatformInterop.LoadModel`:
    /// - errors if `path` is null/empty,
    /// - errors if the file does not exist,
    /// - errors if the native load returns a null pointer,
    /// - otherwise returns a [`SafeModelHandle`] whose drop frees the model.
    pub fn load_model(&self, path: &str) -> Result<SafeModelHandle, InteropError> {
        if path.trim().is_empty() {
            return Err(InteropError::Argument("Model path is required.".into()));
        }
        if !Path::new(path).exists() {
            return Err(InteropError::NotFound(format!(
                "GGUF model file not found. {path}"
            )));
        }

        let native_handle = self.native.load(path);
        if native_handle == 0 {
            return Err(InteropError::InvalidOperation(format!(
                "llama.cpp failed to load model at '{path}'. \
                 Verify the file is a valid GGUF and that the native llama \
                 library is on the search path."
            )));
        }

        let native = Arc::clone(&self.native);
        let free_cb: Arc<ReleaseCallback> = Arc::new(move |h: usize| native.free(h));
        Ok(SafeModelHandle::new(native_handle, free_cb))
    }

    /// The native loader backing this interop (for inspection in tests).
    pub fn native(&self) -> &Arc<dyn NativeModelLoader> {
        &self.native
    }
}

impl Default for PlatformInterop {
    fn default() -> Self {
        Self::new()
    }
}

/// Convenience helper to check a file exists (the C# path also validates this).
pub(crate) fn file_exists(path: &str) -> bool {
    fs::metadata(path).is_ok()
}
