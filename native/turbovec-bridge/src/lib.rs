//! turbovecbridge — C ABI over the turbovec crate.
//!
//! Exported as a `cdylib` so .NET P/Invoke can drive the index from
//! `CircleAI.Embeddings.Local`. Every entry point is `extern "C"`,
//! `#[no_mangle]`, takes raw pointers + lengths, and never panics across
//! the FFI boundary (panics are caught and reported as error codes).

use std::ffi::{c_char, CStr};
use std::panic::{catch_unwind, AssertUnwindSafe};
use std::path::PathBuf;
use std::ptr;
use std::slice;

use turbovec::TurboQuantIndex;

// ─────────────────────────────────────────────────────────────────────────────
// Status codes
// ─────────────────────────────────────────────────────────────────────────────

pub const TVB_OK: i32                 = 0;
pub const TVB_ERR_NULL_HANDLE: i32    = -1;
pub const TVB_ERR_INVALID_ARG: i32    = -2;
pub const TVB_ERR_PANIC: i32          = -3;
pub const TVB_ERR_CONSTRUCT: i32      = -4;
pub const TVB_ERR_ADD: i32            = -5;
pub const TVB_ERR_IO: i32             = -6;
pub const TVB_ERR_INVALID_UTF8: i32   = -7;

// ─────────────────────────────────────────────────────────────────────────────
// Opaque handle layout
// ─────────────────────────────────────────────────────────────────────────────

#[repr(C)]
pub struct TvbIndex {
    inner: TurboQuantIndex,
    /// Cached `dim` so callers can read it without re-entering Rust.
    dim: i32,
    /// Cached `bit_width` (2, 3, or 4).
    bit_width: i32,
}

impl TvbIndex {
    fn new(dim: usize, bit_width: usize) -> Result<Self, i32> {
        let inner = TurboQuantIndex::new(dim, bit_width).map_err(|_| TVB_ERR_CONSTRUCT)?;
        Ok(Self {
            inner,
            dim: dim as i32,
            bit_width: bit_width as i32,
        })
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Lifecycle
// ─────────────────────────────────────────────────────────────────────────────

/// Create a new index. `dim` must be > 0 and a multiple of 8.
/// `bit_width` must be 2, 3, or 4. Returns NULL on failure.
///
/// # Safety
/// The returned handle must be freed via `tvb_index_free`.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_new(dim: i32, bit_width: i32) -> *mut TvbIndex {
    if dim <= 0 || bit_width < 2 || bit_width > 4 {
        return ptr::null_mut();
    }
    catch_unwind(AssertUnwindSafe(|| {
        TvbIndex::new(dim as usize, bit_width as usize)
            .map(|tvb| Box::into_raw(Box::new(tvb)))
            .unwrap_or(ptr::null_mut())
    }))
    .unwrap_or(ptr::null_mut())
}

/// Free an index handle. Safe to call on NULL.
///
/// # Safety
/// `handle` must have come from `tvb_index_new` or `tvb_index_load`.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_free(handle: *mut TvbIndex) {
    if handle.is_null() {
        return;
    }
    let _ = catch_unwind(AssertUnwindSafe(|| {
        // Reconstruct + drop.
        drop(Box::from_raw(handle));
    }));
}

// ─────────────────────────────────────────────────────────────────────────────
// Accessors
// ─────────────────────────────────────────────────────────────────────────────

/// Return the vector count currently in the index. -1 on null handle.
///
/// # Safety
/// `handle` must be valid.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_len(handle: *const TvbIndex) -> i64 {
    if handle.is_null() {
        return -1;
    }
    catch_unwind(AssertUnwindSafe(|| (*handle).inner.len() as i64)).unwrap_or(-1)
}

/// Return the index dimensionality. -1 on null handle.
///
/// # Safety
/// `handle` must be valid.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_dim(handle: *const TvbIndex) -> i32 {
    if handle.is_null() {
        return -1;
    }
    (*handle).dim
}

/// Return the index bit_width (2 / 3 / 4). -1 on null handle.
///
/// # Safety
/// `handle` must be valid.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_bit_width(handle: *const TvbIndex) -> i32 {
    if handle.is_null() {
        return -1;
    }
    (*handle).bit_width
}

// ─────────────────────────────────────────────────────────────────────────────
// Add / Search
// ─────────────────────────────────────────────────────────────────────────────

/// Append vectors to the index.
///
/// `vectors` is a flat array of length `count * dim`. Returns one of the
/// `TVB_*` status codes.
///
/// # Safety
/// `handle` must be valid; `vectors` must point to at least `count * dim`
/// f32 values.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_add(
    handle: *mut TvbIndex,
    vectors: *const f32,
    count: i32,
) -> i32 {
    if handle.is_null() {
        return TVB_ERR_NULL_HANDLE;
    }
    if count < 0 {
        return TVB_ERR_INVALID_ARG;
    }
    if count == 0 {
        return TVB_OK;
    }
    if vectors.is_null() {
        return TVB_ERR_INVALID_ARG;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let tvb = &mut *handle;
        let len = (count as usize) * (tvb.dim as usize);
        let slice = slice::from_raw_parts(vectors, len);
        tvb.inner.add(slice);
        TVB_OK
    }));

    match result {
        Ok(code) => code,
        Err(_) => TVB_ERR_ADD,
    }
}

/// Search the index for the top-`k` nearest neighbours of one query
/// vector.
///
/// `query` must point to `dim` f32 values. `out_indices` receives `k`
/// i64 ids; `out_scores` receives `k` f32 scores (higher = closer).
/// Returns a status code.
///
/// # Safety
/// `handle`, `query`, `out_indices`, `out_scores` must all be valid.
/// `out_indices` and `out_scores` must each hold at least `k` slots.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_search(
    handle: *const TvbIndex,
    query: *const f32,
    k: i32,
    out_indices: *mut i64,
    out_scores: *mut f32,
) -> i32 {
    if handle.is_null() {
        return TVB_ERR_NULL_HANDLE;
    }
    if query.is_null() || out_indices.is_null() || out_scores.is_null() {
        return TVB_ERR_INVALID_ARG;
    }
    if k <= 0 {
        return TVB_ERR_INVALID_ARG;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let tvb = &*handle;
        let q = slice::from_raw_parts(query, tvb.dim as usize);
        let res = tvb.inner.search(q, k as usize);

        // Single-query path: res.indices/scores are flat of length k.
        let idx_dst = slice::from_raw_parts_mut(out_indices, k as usize);
        let scr_dst = slice::from_raw_parts_mut(out_scores, k as usize);
        idx_dst.copy_from_slice(&res.indices);
        scr_dst.copy_from_slice(&res.scores);
        TVB_OK
    }));

    match result {
        Ok(code) => code,
        Err(_) => TVB_ERR_PANIC,
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// Persistence
// ─────────────────────────────────────────────────────────────────────────────

/// Persist the index to `path`. Path is UTF-8, null-terminated.
///
/// # Safety
/// `handle` and `path` must be valid; `path` must be null-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_save(
    handle: *const TvbIndex,
    path: *const c_char,
) -> i32 {
    if handle.is_null() {
        return TVB_ERR_NULL_HANDLE;
    }
    if path.is_null() {
        return TVB_ERR_INVALID_ARG;
    }

    let result = catch_unwind(AssertUnwindSafe(|| {
        let c_str = CStr::from_ptr(path);
        let str_path = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return TVB_ERR_INVALID_UTF8,
        };
        match (*handle).inner.write(PathBuf::from(str_path)) {
            Ok(_) => TVB_OK,
            Err(_) => TVB_ERR_IO,
        }
    }));

    match result {
        Ok(code) => code,
        Err(_) => TVB_ERR_PANIC,
    }
}

/// Load an index from `path`. Returns NULL on failure.
///
/// # Safety
/// Caller must free the returned handle via `tvb_index_free`.
/// `path` must be null-terminated UTF-8.
#[no_mangle]
pub unsafe extern "C" fn tvb_index_load(path: *const c_char) -> *mut TvbIndex {
    if path.is_null() {
        return ptr::null_mut();
    }
    catch_unwind(AssertUnwindSafe(|| {
        let c_str = CStr::from_ptr(path);
        let str_path = match c_str.to_str() {
            Ok(s) => s,
            Err(_) => return ptr::null_mut(),
        };
        let inner = match TurboQuantIndex::load(PathBuf::from(str_path)) {
            Ok(i) => i,
            Err(_) => return ptr::null_mut(),
        };
        let dim = inner.dim() as i32;
        let bit_width = inner.bit_width() as i32;
        let tvb = TvbIndex {
            inner,
            dim,
            bit_width,
        };
        Box::into_raw(Box::new(tvb))
    }))
    .unwrap_or(ptr::null_mut())
}

// ─────────────────────────────────────────────────────────────────────────────
// Version
// ─────────────────────────────────────────────────────────────────────────────

/// Bridge ABI version. Bump on every breaking ABI change.
#[no_mangle]
pub extern "C" fn tvb_abi_version() -> i32 {
    1
}
