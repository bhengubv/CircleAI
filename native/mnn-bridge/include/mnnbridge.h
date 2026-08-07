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

// ── Text streaming callback ──────────────────────────────────────────────
//
// Called with each piece of decoded UTF-8 text AS THE MODEL PRODUCES IT.
// `text` is NOT null-terminated; use `len`. The bytes are only valid for the
// duration of the call — copy what you need. Return value as above.
//
// WHY A SECOND CALLBACK SHAPE EXISTS. mnn_llm_generate_stream_ex reports token
// IDs, and the only way to get token IDs out of MNN is its blocking
// generate(), which returns the finished vector. Reporting IDs and streaming
// are mutually exclusive against this API — so the streaming entry point
// reports text, which is what every caller was converting the IDs into anyway.
typedef int (*mnn_text_callback)(const char* text, int len, void* user_data);

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

// NOT ACTUALLY STREAMING — see mnn_llm_generate_stream_text below.
//
// Invokes cb once per generated token, but only AFTER the whole answer has
// been generated: MNN's generate() blocks to completion and returns the token
// vector, which this then replays through the callback. Measured on a P30 Lite
// with Qwen2.5-1.5B, a 34-character answer arrived as 8 callbacks spanning
// 4 milliseconds, 33.5 seconds after the question — the caller heard nothing
// for 33 seconds and then everything at once.
//
// Kept for callers that genuinely need token IDs, which this is the only way
// to get. If you want the answer as it is produced, use the text variant.
MNNBRIDGE_API int mnn_llm_generate_stream_ex(
    mnn_llm_handle handle,
    const char* prompt_utf8,
    int max_new_tokens,
    mnn_token_callback cb,
    void* user_data);

// Streaming, properly: invokes cb with decoded UTF-8 text as each piece is
// produced. Returns the number of callbacks made (>=0), negative on error.
// A callback returning non-zero stops generation early and is treated as
// success.
//
// Built on MNN's own response(input_ids, ostream*, ...) overload, which writes
// through as it decodes. The blocking generate() cannot do this — by the time
// it returns there is nothing left to stream.
MNNBRIDGE_API int mnn_llm_generate_stream_text(
    mnn_llm_handle handle,
    const char* prompt_utf8,
    int max_new_tokens,
    mnn_text_callback cb,
    void* user_data);

// Applies MNN runtime settings as a JSON object, e.g.
//   {"memory":"normal","power":"high","thread_num":4}
// Must be called BEFORE mnn_llm_load — these are read during load and ignored
// afterwards. Returns 0 on success.
//
// Calling this also suppresses the bridge's own mobile defaults (memory:normal,
// power:high), on the principle that a caller which has measured its handset
// outranks a default measured on a different one.
MNNBRIDGE_API int mnn_llm_set_config(mnn_llm_handle handle, const char* json_utf8);

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

// ── KV cache compression (RT-01 — TurboQuant via MNN ATTENTION_OPTION) ──
//
// Mode encoding:
//   0 = off (full FP16 KV cache, default)
//   1 = TurboQuant 4-bit per channel
//   2 = TurboQuant 3-bit per channel
//   3 = TurboQuant 2-bit per channel
//
// As of mnnbridge 1.2.0 this is wired to MNN's native ATTENTION_OPTION
// runtime hint and applied at load() / reset_session() time.
//
// Returns:
//   0  = success
//   1  = invalid mode value
//   <0 = handle invalid
//
// The MNNBRIDGE_KV_NOT_IMPLEMENTED constant is retained as 2 for ABI
// compatibility with 1.1.x callers but is no longer returned by the 1.2.0+
// implementation.
#define MNNBRIDGE_KV_NOT_IMPLEMENTED 2
MNNBRIDGE_API int mnn_llm_set_kv_compression_mode(
    mnn_llm_handle handle,
    int mode);

// Returns the currently-set KV compression mode (0..3), or -1 on invalid
// handle. Reports the LAST value set by mnn_llm_set_kv_compression_mode
// regardless of whether the native path actually honoured it.
MNNBRIDGE_API int mnn_llm_get_kv_compression_mode(mnn_llm_handle handle);

// ── TurboQuant codec (parity surface) ────────────────────────────────────
//
// The pure-C++ TurboQuantCodec port lives next to mnnbridge so the SDK
// can validate that the managed CircleAI.Core.Compression.TurboQuantCodec
// produces identical numerical results to the native implementation.
// Exposed here so a P/Invoke test can drive a vector through the native
// codec and compare against the managed reconstruction.
//
// This is NOT how the KV cache attention path runs — MNN has its own
// native TurboQuant operator that the ATTENTION_OPTION runtime hint
// activates (see mnn_llm_set_kv_compression_mode). These exports exist
// purely for parity testing and possible future native embedding paths.
//
// Round-trips `vector` (length `dim`) through encode + decode at
// `bits_per_dim` bits per dimension. Writes `dim` floats into `output`.
// Returns 0 on success, negative on validation failure.
MNNBRIDGE_API int mnn_turboquant_round_trip(
    const float* vector,
    int dim,
    int bits_per_dim,
    float* output);

// ── RT-03: mmap weight loading ────────────────────────────────────────────
//
// Set to 1 to memory-map model weights on load (lets multiple processes
// share weight pages). Apply BEFORE mnn_llm_load. Returns 0 on success,
// negative on invalid handle.
MNNBRIDGE_API int mnn_llm_set_mmap_mode(mnn_llm_handle handle, int on);
MNNBRIDGE_API int mnn_llm_get_mmap_mode(mnn_llm_handle handle);

// ── RT-10: LoRA adapter apply / unapply ───────────────────────────────────
//
// Apply a LoRA adapter (rank-decomposed weight delta) on top of the loaded
// base model. The adapter takes effect on the NEXT generation. Returns 0
// on success, MNNBRIDGE_ERR_INVALID_HANDLE / MNNBRIDGE_ERR_INVALID_ARG on
// failure.
MNNBRIDGE_API int mnn_llm_apply_lora(mnn_llm_handle handle, const char* adapter_path_utf8);

// Remove any applied LoRA adapter. Safe to call when no adapter is active.
MNNBRIDGE_API int mnn_llm_unapply_lora(mnn_llm_handle handle);

// Read the currently-applied LoRA adapter path. Returns bytes written
// (excluding NUL), 0 if no adapter is applied, or a negative number whose
// absolute value is the required buffer size when buf_size is too small.
MNNBRIDGE_API int mnn_llm_get_lora(mnn_llm_handle handle, char* out_buf_utf8, int buf_size);

// ── RT-10 training (Phase D1, mnnbridge 1.4.0+) ─────────────────────────
//
// Run one gradient step against the LoRA adapter weights using a single
// (input_tokens, target_tokens) batch. Requires MNN compiled with
// MNN_BUILD_TRAIN=ON; without it returns MNNBRIDGE_ERR_TRAINING_DISABLED.
//
// out_loss receives the scalar cross-entropy loss for the batch.

#define MNNBRIDGE_ERR_TRAINING_DISABLED -12

MNNBRIDGE_API int mnn_llm_train_lora_step(
    mnn_llm_handle handle,
    const int*     input_tokens,  int input_len,
    const int*     target_tokens, int target_len,
    float          learning_rate,
    int            lora_rank,
    float*         out_loss);

// Persist the currently-attached LoRA adapter weights to disk so a future
// mnn_llm_apply_lora() call can reload them.
MNNBRIDGE_API int mnn_llm_save_lora(mnn_llm_handle handle, const char* adapter_path_utf8);

// ── Version ──────────────────────────────────────────────────────────────

// Returns a static NUL-terminated string with the bridge's version, e.g.
// "1.0.0-mnn3.5.0". Callers must NOT free.
MNNBRIDGE_API const char* mnn_bridge_version(void);

#ifdef __cplusplus
}  // extern "C"
#endif

#endif  // CIRCLEAI_MNNBRIDGE_H
