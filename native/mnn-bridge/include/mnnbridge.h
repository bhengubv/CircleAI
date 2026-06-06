// mnnbridge.h
//
// C ABI wrapper around MNN::Transformer::Llm (Alibaba MNN-LLM).
// Exported by mnnbridge.{dll,so,dylib} and consumed via P/Invoke by
// CircleAI.Inference.MnnInterop.
//
// All strings are UTF-8 (null-terminated). All ints are 32-bit signed.
// Token IDs are int32. Pointers are opaque — callers MUST NOT dereference.
//
// Lifecycle:
//   1. handle = mnn_llm_create("/abs/path/to/model/config.json")
//   2. err = mnn_llm_load(handle)           // returns 0 on success
//   3. mnn_llm_generate_stream_ex(handle, prompt, 256, callback, user_data)
//   4. mnn_llm_free(handle)
//
// Return-code convention:
//   0   = success
//   <0  = error (negative errno-style code)
//   >0  = caller-defined / data-bearing (e.g. token count from tokenize)
//
// Thread safety:
//   Each handle is single-threaded. Concurrent calls on the SAME handle are
//   undefined behaviour. Different handles can be used from different
//   threads independently.

#ifndef CIRCLEAI_MNNBRIDGE_H
#define CIRCLEAI_MNNBRIDGE_H

#ifdef __cplusplus
extern "C" {
#endif

// ── Platform export decorations ──────────────────────────────────────────

#if defined(_WIN32) || defined(_WIN64)
  #if defined(MNNBRIDGE_EXPORTS)
    #define MNNBRIDGE_API __declspec(dllexport)
  #else
    #define MNNBRIDGE_API __declspec(dllimport)
  #endif
#elif defined(__GNUC__) || defined(__clang__)
  #define MNNBRIDGE_API __attribute__((visibility("default")))
#else
  #define MNNBRIDGE_API
#endif

// ── Opaque handle types ───────────────────────────────────────────────────

typedef void* mnn_llm_handle;
typedef void* mnn_image_handle;

// ── Error codes ──────────────────────────────────────────────────────────

#define MNNBRIDGE_OK                  0
#define MNNBRIDGE_ERR_INVALID_HANDLE -1
#define MNNBRIDGE_ERR_INVALID_ARG    -2
#define MNNBRIDGE_ERR_LOAD_FAILED    -3
#define MNNBRIDGE_ERR_GEN_FAILED     -4
#define MNNBRIDGE_ERR_OUT_OF_MEMORY  -5
#define MNNBRIDGE_ERR_IO             -6
#define MNNBRIDGE_ERR_UNSUPPORTED    -7

// ── Streaming callback ───────────────────────────────────────────────────
//
// Called once per generated token. Return:
//   0  = continue generating
//   != 0 = stop now (treated as caller-requested cancellation)

typedef int (*mnn_token_callback)(int token_id, void* user_data);

// ── Lifecycle ────────────────────────────────────────────────────────────

// Creates a fresh Llm wrapper. config_path_utf8 must point at a MNN-LLM
// config.json (the one Alibaba ships per model on ModelScope).
// Returns NULL on failure.
MNNBRIDGE_API mnn_llm_handle mnn_llm_create(const char* config_path_utf8);

// Releases the handle. Safe to pass NULL (no-op).
MNNBRIDGE_API void mnn_llm_free(mnn_llm_handle handle);

// Loads model weights. Returns 0 on success, negative error code on failure.
MNNBRIDGE_API int mnn_llm_load(mnn_llm_handle handle);

// ── Inspection ───────────────────────────────────────────────────────────

// Returns the model's max context window in tokens. <0 on error.
MNNBRIDGE_API int mnn_llm_get_context_size(mnn_llm_handle handle);

// Returns the model's vocabulary size. <0 on error.
MNNBRIDGE_API int mnn_llm_get_vocab_size(mnn_llm_handle handle);

// Returns the model's architecture/type code:
//   0 = qwen2 / qwen2.5
//   1 = qwen3
//   2 = kimi / llama-like
//   99 = other
//   <0 = error
MNNBRIDGE_API int mnn_llm_get_model_type(mnn_llm_handle handle);

// ── Tokenization ─────────────────────────────────────────────────────────

// Encodes text_utf8 into token ids written to out_tokens (up to max_tokens).
// Returns the number of tokens written (>=0), or a negative error code.
MNNBRIDGE_API int mnn_llm_tokenize(
    mnn_llm_handle handle,
    const char* text_utf8,
    int* out_tokens,
    int max_tokens);

// Decodes a single token to text, written to out_buf_utf8 (null-terminated).
// Returns bytes written (excluding null), or negative on error / truncation.
MNNBRIDGE_API int mnn_llm_token_to_text(
    mnn_llm_handle handle,
    int token,
    char* out_buf_utf8,
    int buf_size);

// ── Generation ───────────────────────────────────────────────────────────

// Non-streaming: writes the full generated reply (UTF-8) to out_buf_utf8.
// Returns bytes written (excluding null), or negative on error / truncation.
MNNBRIDGE_API int mnn_llm_generate_ex(
    mnn_llm_handle handle,
    const char* prompt_utf8,
    int max_new_tokens,
    char* out_buf_utf8,
    int buf_size);

// Streaming: invokes cb once per generated token. Returns total tokens
// emitted (>=0), or negative on error. Callback returning non-zero stops
// generation early (treated as success, returns tokens emitted so far).
MNNBRIDGE_API int mnn_llm_generate_stream_ex(
    mnn_llm_handle handle,
    const char* prompt_utf8,
    int max_new_tokens,
    mnn_token_callback cb,
    void* user_data);

// ── Session persistence ──────────────────────────────────────────────────
//
// "Session" here means the prompt cache + KV cache state attached to a
// loaded model. MNN's setPrefixCacheFile is the closest available primitive.

// Saves current session state to path_utf8. Returns 0 on success.
MNNBRIDGE_API int mnn_llm_save_session(mnn_llm_handle handle, const char* path_utf8);

// Loads a previously-saved session from path_utf8. Returns 0 on success.
MNNBRIDGE_API int mnn_llm_load_session(mnn_llm_handle handle, const char* path_utf8);

// Resets the model's in-memory session state (clears KV cache, history).
MNNBRIDGE_API void mnn_llm_reset_session(mnn_llm_handle handle);

// ── Vision / multimodal ──────────────────────────────────────────────────

// Constructs an image handle from raw bytes (PNG/JPEG/WebP detected by
// libstb-equivalent inside MNN). mime_utf8 is advisory and may be NULL.
// Returns NULL on failure.
MNNBRIDGE_API mnn_image_handle mnn_llm_image_from_bytes(
    const unsigned char* data,
    int size,
    const char* mime_utf8);

// Releases the image handle. Safe to pass NULL.
MNNBRIDGE_API void mnn_llm_image_free(mnn_image_handle handle);

// Streaming generation with an image conditioning the prompt (multimodal
// models like Kimi-VL). Identical semantics to mnn_llm_generate_stream_ex.
MNNBRIDGE_API int mnn_llm_generate_with_image_stream_ex(
    mnn_llm_handle handle,
    mnn_image_handle image,
    const char* prompt_utf8,
    int max_new_tokens,
    mnn_token_callback cb,
    void* user_data);

// ── Embeddings ───────────────────────────────────────────────────────────

// Returns the model's embedding dimension (only valid for embedding-task
// models). <0 on error.
MNNBRIDGE_API int mnn_embed_get_dim(mnn_llm_handle handle);

// ── Version ──────────────────────────────────────────────────────────────

// Returns a static NUL-terminated string with the bridge's version, e.g.
// "1.0.0-mnn3.5.0". Callers must NOT free.
MNNBRIDGE_API const char* mnn_bridge_version(void);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // CIRCLEAI_MNNBRIDGE_H
