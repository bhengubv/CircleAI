// mnnbridge.cpp
//
// C ABI implementation. Wraps MNN::Transformer::Llm into the flat C
// functions declared in include/mnnbridge.h.
//
// Built against MNN 3.5.x headers.

// MNNBRIDGE_EXPORTS is defined via the CMake target_compile_definitions
// flag — see CMakeLists.txt. Don't duplicate it here.
#include "mnnbridge.h"

// Windows API for GetTempPathA used in the multimodal helper. MUST come
// before any other system headers that might include winsock-related
// content (to avoid winsock conflict on older MSVC stacks).
#if defined(_WIN32) || defined(_WIN64)
  #define WIN32_LEAN_AND_MEAN
  #define NOMINMAX
  #include <windows.h>
#endif

// MNN's LLM public header. Alibaba ships this only in the macOS framework
// bundle (Versions/A/Headers/llm/llm.hpp); we vendor a copy under
// third_party/mnn-llm/include/llm/llm.hpp for use against the Windows /
// Linux MNN library binaries (which DO export the LLM symbols).
#include <llm/llm.hpp>

#include <cstring>
#include <fstream>
#include <memory>
#include <new>
#include <sstream>
#include <streambuf>
#include <string>
#include <vector>

namespace {

// ── Internal handle wrappers ──────────────────────────────────────────────
//
// We never expose MNN::Transformer::Llm* directly. The caller sees an
// opaque void* and the type-safe cast lives inside this file.

struct LlmWrapper {
    std::unique_ptr<MNN::Transformer::Llm,
                    void(*)(MNN::Transformer::Llm*)> llm{nullptr,
        +[](MNN::Transformer::Llm* p) { if (p) MNN::Transformer::Llm::destroy(p); }};
    bool loaded = false;
};

inline LlmWrapper* as_wrapper(mnn_llm_handle h) {
    return reinterpret_cast<LlmWrapper*>(h);
}

// Image buffer wrapper. MNN's multimodal API accepts raw bytes via
// MultimodalPrompt; we store the bytes and shape the prompt at call time.
struct ImageWrapper {
    std::vector<unsigned char> data;
    std::string mime;
};

inline ImageWrapper* as_image(mnn_image_handle h) {
    return reinterpret_cast<ImageWrapper*>(h);
}

// ── Streaming token-capture streambuf ─────────────────────────────────────
//
// MNN's Llm::response() writes decoded text to a std::ostream. We need
// per-token IDs, so we route the high-level path through tokenizer_decode
// after generation. The streambuf here captures any text written so
// callers using generate_ex (text out) still get correct UTF-8.

class CaptureStreambuf : public std::streambuf {
public:
    std::string& out;
    explicit CaptureStreambuf(std::string& dst) : out(dst) {}
protected:
    int_type overflow(int_type ch) override {
        if (ch != traits_type::eof()) out.push_back(static_cast<char>(ch));
        return ch;
    }
    std::streamsize xsputn(const char* s, std::streamsize n) override {
        out.append(s, static_cast<std::size_t>(n));
        return n;
    }
};

}  // namespace

// ── Lifecycle ────────────────────────────────────────────────────────────

MNNBRIDGE_API mnn_llm_handle mnn_llm_create(const char* config_path_utf8) {
    if (!config_path_utf8) return nullptr;
    try {
        auto wrapper = new (std::nothrow) LlmWrapper();
        if (!wrapper) return nullptr;
        MNN::Transformer::Llm* raw =
            MNN::Transformer::Llm::createLLM(std::string(config_path_utf8));
        if (!raw) {
            delete wrapper;
            return nullptr;
        }
        wrapper->llm.reset(raw);
        return wrapper;
    } catch (...) {
        return nullptr;
    }
}

MNNBRIDGE_API void mnn_llm_free(mnn_llm_handle handle) {
    if (!handle) return;
    auto w = as_wrapper(handle);
    try { delete w; } catch (...) { /* swallow */ }
}

MNNBRIDGE_API int mnn_llm_load(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    try {
        bool ok = w->llm->load();
        if (!ok) return MNNBRIDGE_ERR_LOAD_FAILED;
        w->loaded = true;
        return MNNBRIDGE_OK;
    } catch (...) {
        return MNNBRIDGE_ERR_LOAD_FAILED;
    }
}

// ── Inspection ───────────────────────────────────────────────────────────
//
// MNN doesn't expose context_size / vocab_size as direct getters on Llm;
// they live in the LlmConfig + the tokenizer. We pull what we can from
// the public surface and fall back to dump_config JSON parsing.
//
// For 1.0.0 of the bridge we keep this simple: parse the config JSON for
// the well-known fields. If that fails we return a defensive default.

namespace {

int extract_int_from_config(MNN::Transformer::Llm* llm, const char* key, int defaultVal) {
    if (!llm) return -1;
    try {
        std::string json = llm->dump_config();
        std::string needle = std::string("\"") + key + "\"";
        auto pos = json.find(needle);
        if (pos == std::string::npos) return defaultVal;
        pos = json.find(':', pos);
        if (pos == std::string::npos) return defaultVal;
        ++pos;
        while (pos < json.size() && (json[pos] == ' ' || json[pos] == '\t')) ++pos;
        int value = 0;
        bool any = false;
        while (pos < json.size() && json[pos] >= '0' && json[pos] <= '9') {
            value = value * 10 + (json[pos] - '0');
            ++pos; any = true;
        }
        return any ? value : defaultVal;
    } catch (...) {
        return defaultVal;
    }
}

}  // namespace

MNNBRIDGE_API int mnn_llm_get_context_size(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    return extract_int_from_config(w->llm.get(), "max_position_embeddings", 4096);
}

MNNBRIDGE_API int mnn_llm_get_vocab_size(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    return extract_int_from_config(w->llm.get(), "vocab_size", 151936);
}

MNNBRIDGE_API int mnn_llm_get_model_type(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    try {
        std::string json = w->llm->dump_config();
        if (json.find("qwen3") != std::string::npos) return 1;
        if (json.find("qwen2") != std::string::npos) return 0;
        if (json.find("kimi")  != std::string::npos) return 2;
        if (json.find("llama") != std::string::npos) return 2;
        return 99;
    } catch (...) {
        return 99;
    }
}

// ── Tokenization ─────────────────────────────────────────────────────────

MNNBRIDGE_API int mnn_llm_tokenize(
    mnn_llm_handle handle, const char* text_utf8,
    int* out_tokens, int max_tokens) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!text_utf8 || !out_tokens || max_tokens <= 0) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        auto ids = w->llm->tokenizer_encode(std::string(text_utf8));
        int n = static_cast<int>(ids.size());
        int copy = (n < max_tokens) ? n : max_tokens;
        for (int i = 0; i < copy; ++i) out_tokens[i] = ids[i];
        return copy;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
}

MNNBRIDGE_API int mnn_llm_token_to_text(
    mnn_llm_handle handle, int token, char* out_buf_utf8, int buf_size) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!out_buf_utf8 || buf_size <= 0) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        std::string text = w->llm->tokenizer_decode(token);
        int n = static_cast<int>(text.size());
        if (n + 1 > buf_size) {
            int copy = buf_size - 1;
            std::memcpy(out_buf_utf8, text.data(), copy);
            out_buf_utf8[copy] = '\0';
            return -static_cast<int>(text.size());  // negative size signals truncation
        }
        std::memcpy(out_buf_utf8, text.data(), n);
        out_buf_utf8[n] = '\0';
        return n;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
}

// ── Generation ───────────────────────────────────────────────────────────

MNNBRIDGE_API int mnn_llm_generate_ex(
    mnn_llm_handle handle, const char* prompt_utf8,
    int max_new_tokens, char* out_buf_utf8, int buf_size) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!prompt_utf8 || !out_buf_utf8 || buf_size <= 0) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        std::string captured;
        CaptureStreambuf buf(captured);
        std::ostream os(&buf);
        w->llm->response(std::string(prompt_utf8), &os, nullptr,
                         max_new_tokens > 0 ? max_new_tokens : -1);
        int n = static_cast<int>(captured.size());
        if (n + 1 > buf_size) {
            int copy = buf_size - 1;
            std::memcpy(out_buf_utf8, captured.data(), copy);
            out_buf_utf8[copy] = '\0';
            return -static_cast<int>(captured.size());
        }
        std::memcpy(out_buf_utf8, captured.data(), n);
        out_buf_utf8[n] = '\0';
        return n;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
}

// For streaming we drive generation token-by-token, calling the user
// callback after each decoded token id. MNN's high-level Llm::response
// writes text to an ostream; we instead use the chat template + manual
// generate loop so the callback sees integer token IDs (the contract
// MnnInterop.mnn_token_callback expects).

MNNBRIDGE_API int mnn_llm_generate_stream_ex(
    mnn_llm_handle handle, const char* prompt_utf8,
    int max_new_tokens, mnn_token_callback cb, void* user_data) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!prompt_utf8 || !cb) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        std::string formatted = w->llm->apply_chat_template(std::string(prompt_utf8));
        auto input_ids = w->llm->tokenizer_encode(formatted);
        auto generated = w->llm->generate(
            input_ids,
            max_new_tokens > 0 ? max_new_tokens : 256);
        int emitted = 0;
        for (int t : generated) {
            int stop = cb(t, user_data);
            ++emitted;
            if (stop) break;
        }
        return emitted;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
}

// ── Session persistence ──────────────────────────────────────────────────

MNNBRIDGE_API int mnn_llm_save_session(mnn_llm_handle handle, const char* path_utf8) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!path_utf8) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        // MNN exposes setPrefixCacheFile (write side). The flag arg picks
        // the cache mode; 1 = write.
        bool ok = w->llm->setPrefixCacheFile(std::string(path_utf8), 1);
        return ok ? MNNBRIDGE_OK : MNNBRIDGE_ERR_IO;
    } catch (...) {
        return MNNBRIDGE_ERR_IO;
    }
}

MNNBRIDGE_API int mnn_llm_load_session(mnn_llm_handle handle, const char* path_utf8) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!path_utf8) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        // flag = 0 = read.
        bool ok = w->llm->setPrefixCacheFile(std::string(path_utf8), 0);
        return ok ? MNNBRIDGE_OK : MNNBRIDGE_ERR_IO;
    } catch (...) {
        return MNNBRIDGE_ERR_IO;
    }
}

MNNBRIDGE_API void mnn_llm_reset_session(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return;
    try { w->llm->reset(); } catch (...) { /* swallow */ }
}

// ── Vision / multimodal ──────────────────────────────────────────────────

MNNBRIDGE_API mnn_image_handle mnn_llm_image_from_bytes(
    const unsigned char* data, int size, const char* mime_utf8) {
    if (!data || size <= 0) return nullptr;
    try {
        auto img = new (std::nothrow) ImageWrapper();
        if (!img) return nullptr;
        img->data.assign(data, data + size);
        if (mime_utf8) img->mime = mime_utf8;
        return img;
    } catch (...) {
        return nullptr;
    }
}

MNNBRIDGE_API void mnn_llm_image_free(mnn_image_handle handle) {
    if (!handle) return;
    try { delete as_image(handle); } catch (...) {}
}

MNNBRIDGE_API int mnn_llm_generate_with_image_stream_ex(
    mnn_llm_handle handle, mnn_image_handle image,
    const char* prompt_utf8, int max_new_tokens,
    mnn_token_callback cb, void* user_data) {
    auto w = as_wrapper(handle);
    auto img = as_image(image);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!img || !prompt_utf8 || !cb) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        // Build a MultimodalPrompt and route through the same token-stream
        // path. MNN's MultimodalPrompt API is in flux across releases — for
        // 3.5.x we encode the image as a special URL token in the prompt
        // and rely on the model's chat template to splice it in. This
        // matches the example/llm_demo path Alibaba ships.
        //
        // Tmp file path strategy: write the bytes to a temp file and
        // reference it. Production callers can replace this with a direct
        // VARP if they need lower overhead.
        std::string tmpPath;
        {
#if defined(_WIN32) || defined(_WIN64)
            char buf[260];
            DWORD n = GetTempPathA(static_cast<DWORD>(sizeof(buf)), buf);
            tmpPath.assign(buf, n);
            tmpPath += "circleai_mnn_image_";
            tmpPath += std::to_string(reinterpret_cast<uintptr_t>(img));
            tmpPath += ".bin";
#else
            tmpPath = "/tmp/circleai_mnn_image_";
            tmpPath += std::to_string(reinterpret_cast<uintptr_t>(img));
            tmpPath += ".bin";
#endif
            std::ofstream ofs(tmpPath, std::ios::binary);
            if (!ofs) return MNNBRIDGE_ERR_IO;
            ofs.write(reinterpret_cast<const char*>(img->data.data()),
                      static_cast<std::streamsize>(img->data.size()));
        }
        std::string promptWithImage = "<image>" + tmpPath + "</image>" + std::string(prompt_utf8);
        std::string formatted = w->llm->apply_chat_template(promptWithImage);
        auto input_ids = w->llm->tokenizer_encode(formatted);
        auto generated = w->llm->generate(
            input_ids,
            max_new_tokens > 0 ? max_new_tokens : 256);
        int emitted = 0;
        for (int t : generated) {
            int stop = cb(t, user_data);
            ++emitted;
            if (stop) break;
        }
        std::remove(tmpPath.c_str());
        return emitted;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
}

// ── Embeddings ───────────────────────────────────────────────────────────

MNNBRIDGE_API int mnn_embed_get_dim(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    return extract_int_from_config(w->llm.get(), "hidden_size", 0);
}

// ── Version ──────────────────────────────────────────────────────────────

MNNBRIDGE_API const char* mnn_bridge_version(void) {
    return "1.0.0-mnn3.5.0";
}

// (Windows-specific includes moved to the top of this file so they're
// available in mnn_llm_generate_with_image_stream_ex.)
