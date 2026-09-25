// llamabridge.h
//
// A C shim over llama.cpp, shaped DELIBERATELY like mnnbridge.h so the managed
// side of CircleAI does not have to learn a second set of habits: same opaque
// void* handle, same return-code convention, same one-handle-one-thread rule,
// same text callback signature.
//
// WHY A SECOND BACKEND AT ALL. CircleAI has been MNN-only. MNN cannot run
// several things the open ecosystem now publishes: measured on 2026-09-25,
// Bonsai 2 27B (prism-ml/Ternary-Bonsai-2-27B-gguf) has architecture "qwen35",
// which no MNN family covers, and 402 of its tensors carry a custom ternary
// type (143) MNN has no dequant for. Splitting the graph so the unsupported
// GatedDeltaNet blocks run outside MNN was measured and rejected: 48 of its 64
// blocks carry SSM tensors, so a split means 49 graph segments and 96
// managed/native crossings marshalling ~1.97 MB PER TOKEN. The ToucanTTS
// precedent that made splitting work elsewhere had ONE crossing per utterance.
// GGUF is where most open models ship, so the honest answer is to run GGUF
// rather than to keep converting it.
//
// SCOPE OF THIS FIRST CUT: load a GGUF, tokenize, stream text out, stop. No
// LoRA, no image input, no session save/restore. Those exist in mnnbridge and
// can follow; shipping a small surface that actually runs beats a wide one
// that does not.
//
// Return-code convention (identical to mnnbridge):
//   0   = success
//   <0  = error (negative errno-style code)
//   >0  = caller-defined / data-bearing (e.g. token count from tokenize)
//
// Thread safety (identical to mnnbridge):
//   Each handle is single-threaded. Concurrent calls on the SAME handle are
//   undefined behaviour. Different handles are independent.

#ifndef CIRCLEAI_LLAMABRIDGE_H
#define CIRCLEAI_LLAMABRIDGE_H

#if defined(_WIN32)
  #ifdef LLAMABRIDGE_BUILD
    #define LLAMABRIDGE_API __declspec(dllexport)
  #else
    #define LLAMABRIDGE_API __declspec(dllimport)
  #endif
#elif defined(__GNUC__)
  #define LLAMABRIDGE_API __attribute__((visibility("default")))
#else
  #define LLAMABRIDGE_API
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef void* llama_bridge_handle;

// Mirrors mnn_text_callback exactly. Return 0 to continue generating, non-zero
// to stop — that is how the managed side cancels without tearing the handle
// down mid-token.
typedef int (*llama_text_callback)(const char* text, int len, void* user_data);

// ---- lifecycle -------------------------------------------------------------

// Creates a handle for a GGUF file. Does NOT load weights — call
// llama_bridge_load, mirroring mnn_llm_create/mnn_llm_load, so the caller can
// set configuration between the two.
//   n_ctx     : context window, 0 = take the model's own training context
//   n_threads : CPU threads, 0 = let the bridge choose
LLAMABRIDGE_API llama_bridge_handle llama_bridge_create(
    const char* gguf_path_utf8, int n_ctx, int n_threads);

LLAMABRIDGE_API void llama_bridge_free(llama_bridge_handle handle);

// Loads weights. Separate from create for the reason above.
LLAMABRIDGE_API int llama_bridge_load(llama_bridge_handle handle);

// ---- model facts -----------------------------------------------------------

LLAMABRIDGE_API int llama_bridge_get_context_size(llama_bridge_handle handle);
LLAMABRIDGE_API int llama_bridge_get_vocab_size(llama_bridge_handle handle);

// The GGUF's own general.architecture string (e.g. "qwen3", "qwen35"). The
// managed catalogue needs this to tell an unsupported build apart from an
// unsupported MODEL — a distinction MNN never had, and the reason a ternary
// Bonsai looked like a conversion problem for longer than it should have.
LLAMABRIDGE_API int llama_bridge_get_arch(
    llama_bridge_handle handle, char* out_buf_utf8, int buf_size);

// ---- tokens ----------------------------------------------------------------

// Returns the token count written, or <0 on error. Pass out_tokens=NULL to ask
// for the count only.
LLAMABRIDGE_API int llama_bridge_tokenize(
    llama_bridge_handle handle, const char* text_utf8,
    int* out_tokens, int max_tokens, int add_special);

LLAMABRIDGE_API int llama_bridge_token_to_text(
    llama_bridge_handle handle, int token_id, char* out_buf_utf8, int buf_size);

// ---- generation ------------------------------------------------------------

// Streams generated text through the callback. Returns the number of tokens
// produced, or <0 on error. Stops on EOS, on max_tokens, or when the callback
// returns non-zero.
LLAMABRIDGE_API int llama_bridge_generate_stream_text(
    llama_bridge_handle handle, const char* prompt_utf8, int max_tokens,
    llama_text_callback callback, void* user_data);

// Clears the KV cache so the next prompt starts clean.
LLAMABRIDGE_API void llama_bridge_reset(llama_bridge_handle handle);

// ---- diagnostics -----------------------------------------------------------

// Human-readable, for logs and the capability manifest. Includes the llama.cpp
// build it was compiled against, because a ternary model needs a fork and
// "which llama.cpp is this" then stops being a rhetorical question.
LLAMABRIDGE_API const char* llama_bridge_version(void);

#ifdef __cplusplus
}
#endif

#endif // CIRCLEAI_LLAMABRIDGE_H
