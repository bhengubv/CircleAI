// llamabridge.cpp
//
// Implementation of the C shim declared in llamabridge.h.
//
// PINNED, NOT FLOATING. llama.cpp's C API churns (llama_kv_self_clear ->
// llama_memory_clear, llama_new_context_with_model -> llama_init_from_model,
// the vocab split out of the model). CMakeLists pins a tag for that reason;
// bump it deliberately and fix this file, rather than tracking master and
// discovering the break on a phone.

#include "llamabridge.h"
#include "llama.h"

#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

// One-time global backend init. It must outlive every handle, so it is done
// once and never torn down — process exit is the teardown.
std::once_flag g_backend_once;
void ensure_backend() {
    std::call_once(g_backend_once, [] { llama_backend_init(); });
}

struct Bridge {
    llama_model*   model = nullptr;
    llama_context* ctx   = nullptr;
    std::string    path;
    int            n_ctx     = 0;
    int            n_threads = 0;
    std::string    arch;   // filled on load, from the GGUF's own metadata
};

int copy_out(const std::string& s, char* buf, int cap) {
    if (buf == nullptr || cap <= 0) return static_cast<int>(s.size());
    const size_t room = static_cast<size_t>(cap - 1);
    const int n = static_cast<int>(s.size() < room ? s.size() : room);
    std::memcpy(buf, s.data(), static_cast<size_t>(n));
    buf[n] = 0;
    return n;
}

} // namespace

extern "C" {

llama_bridge_handle llama_bridge_create(const char* gguf_path_utf8, int n_ctx, int n_threads) {
    if (gguf_path_utf8 == nullptr || gguf_path_utf8[0] == 0) return nullptr;
    ensure_backend();
    Bridge* b = new (std::nothrow) Bridge();
    if (b == nullptr) return nullptr;
    b->path      = gguf_path_utf8;
    b->n_ctx     = n_ctx;
    b->n_threads = n_threads;
    return b;
}

void llama_bridge_free(llama_bridge_handle handle) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr) return;
    if (b->ctx   != nullptr) llama_free(b->ctx);
    if (b->model != nullptr) llama_model_free(b->model);
    delete b;
}

int llama_bridge_load(llama_bridge_handle handle) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr) return -1;
    if (b->model != nullptr) return 0;   // idempotent

    llama_model_params mp = llama_model_default_params();
    b->model = llama_model_load_from_file(b->path.c_str(), mp);
    if (b->model == nullptr) return -2;

    llama_context_params cp = llama_context_default_params();
    if (b->n_ctx > 0) cp.n_ctx = static_cast<uint32_t>(b->n_ctx);
    if (b->n_threads > 0) {
        cp.n_threads       = b->n_threads;
        cp.n_threads_batch = b->n_threads;
    }

    b->ctx = llama_init_from_model(b->model, cp);
    if (b->ctx == nullptr) {
        llama_model_free(b->model);
        b->model = nullptr;
        return -3;
    }

    // The GGUF's own general.architecture — the fact that distinguishes an
    // unsupported BUILD from an unsupported MODEL.
    char buf[128];
    buf[0] = 0;
    if (llama_model_meta_val_str(b->model, "general.architecture", buf, sizeof(buf)) > 0) {
        b->arch = buf;
    }
    return 0;
}

int llama_bridge_get_context_size(llama_bridge_handle handle) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->ctx == nullptr) return -1;
    return static_cast<int>(llama_n_ctx(b->ctx));
}

int llama_bridge_get_vocab_size(llama_bridge_handle handle) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->model == nullptr) return -1;
    return llama_vocab_n_tokens(llama_model_get_vocab(b->model));
}

int llama_bridge_get_arch(llama_bridge_handle handle, char* out_buf_utf8, int buf_size) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr) return -1;
    return copy_out(b->arch, out_buf_utf8, buf_size);
}

int llama_bridge_tokenize(llama_bridge_handle handle, const char* text_utf8,
                          int* out_tokens, int max_tokens, int add_special) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->model == nullptr || text_utf8 == nullptr) return -1;
    const llama_vocab* v = llama_model_get_vocab(b->model);
    const int len = static_cast<int>(std::strlen(text_utf8));

    if (out_tokens == nullptr || max_tokens <= 0) {
        // Count-only probe: llama_tokenize returns -needed when the buffer is
        // too small, so ask with no buffer and flip the sign.
        const int need = llama_tokenize(v, text_utf8, len, nullptr, 0, add_special != 0, true);
        return need < 0 ? -need : need;
    }
    return llama_tokenize(v, text_utf8, len, out_tokens, max_tokens, add_special != 0, true);
}

int llama_bridge_token_to_text(llama_bridge_handle handle, int token_id,
                               char* out_buf_utf8, int buf_size) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->model == nullptr) return -1;
    if (out_buf_utf8 == nullptr || buf_size <= 0) return -2;
    const llama_vocab* v = llama_model_get_vocab(b->model);
    const int n = llama_token_to_piece(v, token_id, out_buf_utf8, buf_size - 1, 0, true);
    if (n < 0) return n;
    out_buf_utf8[n] = 0;
    return n;
}

int llama_bridge_generate_stream_text(llama_bridge_handle handle, const char* prompt_utf8,
                                      int max_tokens, llama_text_callback callback,
                                      void* user_data) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->ctx == nullptr || prompt_utf8 == nullptr) return -1;
    if (max_tokens <= 0) max_tokens = 512;

    const llama_vocab* v = llama_model_get_vocab(b->model);
    const int plen = static_cast<int>(std::strlen(prompt_utf8));

    int need = llama_tokenize(v, prompt_utf8, plen, nullptr, 0, true, true);
    if (need < 0) need = -need;
    if (need <= 0) return -4;

    std::vector<llama_token> toks(static_cast<size_t>(need) + 1);
    const int ntok = llama_tokenize(v, prompt_utf8, plen, toks.data(),
                                    static_cast<int>(toks.size()), true, true);
    if (ntok <= 0) return -4;
    toks.resize(static_cast<size_t>(ntok));

    // Prefill.
    llama_batch batch = llama_batch_get_one(toks.data(), ntok);
    if (llama_decode(b->ctx, batch) != 0) return -5;

    // Greedy for this first cut — it proves the round trip. Sampling knobs
    // stay on the managed side until there is something to tune against.
    llama_sampler* smpl = llama_sampler_chain_init(llama_sampler_chain_default_params());
    llama_sampler_chain_add(smpl, llama_sampler_init_greedy());

    int  produced = 0;
    char piece[512];

    for (int i = 0; i < max_tokens; ++i) {
        llama_token tok = llama_sampler_sample(smpl, b->ctx, -1);
        if (llama_vocab_is_eog(v, tok)) break;

        const int n = llama_token_to_piece(v, tok, piece,
                                           static_cast<int>(sizeof(piece)) - 1, 0, true);
        if (n > 0) {
            piece[n] = 0;
            // Non-zero from the callback is the caller cancelling. Break rather
            // than tear the handle down mid-token.
            if (callback != nullptr && callback(piece, n, user_data) != 0) break;
        }
        ++produced;

        llama_batch next = llama_batch_get_one(&tok, 1);
        if (llama_decode(b->ctx, next) != 0) break;
    }

    llama_sampler_free(smpl);
    return produced;
}

void llama_bridge_reset(llama_bridge_handle handle) {
    Bridge* b = static_cast<Bridge*>(handle);
    if (b == nullptr || b->ctx == nullptr) return;
    llama_memory_clear(llama_get_memory(b->ctx), true);
}

const char* llama_bridge_version(void) {
    // Carries the llama.cpp build number so "which llama.cpp is this" is
    // answerable from a log line — which matters the moment a fork is in play.
    static std::string s = std::string("circleai-llamabridge/0.1.0 llama.cpp/")
                         + std::to_string(LLAMA_BUILD_NUMBER);
    return s.c_str();
}

} // extern "C"
