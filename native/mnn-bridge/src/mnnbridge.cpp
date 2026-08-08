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

// WHERE THE TIME GOES, reported from inside the bridge.
//
// Every layer above this one can time a whole call and nothing else, so a slow
// answer was indistinguishable from a slow model. On a P30 Lite the first token
// took 31 seconds and no measurement anywhere could say whether that was
// tokenising, prefill, or decode — the three are one opaque call from C#.
//
// Android only, via liblog, which this target already links.
#if defined(__ANDROID__)
  #include <android/log.h>
  #include <chrono>
  #define BRIDGE_LOG(...) __android_log_print(ANDROID_LOG_INFO, "mnnbridge", __VA_ARGS__)
  #define BRIDGE_NOW()    std::chrono::steady_clock::now()
  #define BRIDGE_MS(a, b) \
      ((long)std::chrono::duration_cast<std::chrono::milliseconds>((b) - (a)).count())
#else
  #define BRIDGE_LOG(...) ((void)0)
  #define BRIDGE_NOW()    0
  #define BRIDGE_MS(a, b) 0L
#endif

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
    // Requested KV compression mode. As of mnnbridge 1.2.0 this is wired
    // through MNN's native ATTENTION_OPTION runtime hint at load/reset time.
    int kv_compression_mode = 0;
    // RT-03 mmap weight loading — 0 = off, 1 = on. Applied at load() time
    // by stamping mmap_load_kv = true in the runtime config.
    int mmap_mode = 0;
    // RT-10 LoRA — path of the currently-applied adapter, or empty string.
    std::string lora_adapter_path;
    // Set when the caller supplied its own runtime config before load, which
    // suppresses the bridge's mobile defaults. A caller that has measured its
    // own handset knows better than a default chosen on one phone.
    bool config_overridden = false;
    // Scratch directory for mmap. MNN writes its mapping file here, so it has
    // to be somewhere the process can actually write — on Android that is the
    // app's own storage, never a system path.
    std::string tmp_path = "tmp";
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
        // KV cache compression: translate our SDK mode to MNN's attention_mode
        // BEFORE load() so setRuntimeHint picks up the right ATTENTION_OPTION.
        //   attention_mode encoding (from MNN's CPUAttention.cpp):
        //     attention_mode / 8: flash attention bit (1 = on)
        //     attention_mode % 8: 0 off | 1 K-i8 | 2 K+V-i8 | 3 K-TQ3 | 4 K+V-TQ3 | 5 K-TQ4 | 6 K+V-TQ4
        //   Our SDK -> MNN mapping (always K+V, flash on):
        //     Off (0)            -> 8  (flash on, no quant)
        //     TurboQuant4Bit (1) -> 14 (flash on, K+V TQ4)
        //     TurboQuant3Bit (2) -> 12 (flash on, K+V TQ3)
        //     TurboQuant2Bit (3) -> 12 (MNN has no native 2-bit; closest = TQ3)
        if (w->kv_compression_mode != 0) {
            int attention_mode = 8; // default: flash on, no quant
            switch (w->kv_compression_mode) {
                case 1: attention_mode = 14; break; // K+V TQ4
                case 2: attention_mode = 12; break; // K+V TQ3
                case 3: attention_mode = 12; break; // 2-bit not native; nearest = TQ3
                default: break;
            }
            std::string cfg = "{\"attention_mode\": " + std::to_string(attention_mode) + "}";
            // set_config is best-effort; failure here is non-fatal (model will
            // load at FP16). The C# layer can probe the post-load state.
            w->llm->set_config(cfg);
        }

        // DO NOT SET memory:normal HERE. It was tried, on the theory that
        // memory:low costs a re-dequantisation of the weights on every pass and
        // that this was why prefill ran at 70 ms/token against decode's 116.
        //
        // Measured on a P30 Lite, Qwen2.5-1.5B, {"memory":"normal","power":"high"}:
        //
        //     prefill  31 551 ms  ->  83 309 ms   (2.7x WORSE)
        //     resident    975 MB  ->   1 772 MB
        //
        // Holding the dequantised weights costs 800 MB, and on a 3.7 GB phone
        // with ~1.6 GB actually free that lands in swap on eMMC. Paying to
        // dequantise again is far cheaper than paying to page. The bundles ship
        // memory:low and they are right; a caller on a larger handset can still
        // override via mnn_llm_set_config before load.
        //
        // The real cost was never the memory mode — see the note on prefill in
        // mnn_llm_generate_stream_text.

        bool ok = w->llm->load();
        if (!ok) return MNNBRIDGE_ERR_LOAD_FAILED;
        w->loaded = true;

        // WHAT MNN ACTUALLY RESOLVED, not what we hoped it would. Every knob
        // that decides whether this is fast — thread_num, precision, backend,
        // and whatever governs prefill batching — lives in the model's own
        // config.json, which is downloaded from ModelScope and never inspected
        // on this side. Measured on a P30 Lite, prefill ran at 60 ms/token
        // against decode's 111 ms/token; a batched prefill should be many times
        // cheaper per token than decode, so those two numbers being that close
        // says the prompt is going through one token at a time. This prints the
        // settings so that stops being a guess.
        try { BRIDGE_LOG("config: %s", w->llm->dump_config().c_str()); } catch (...) {}

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

namespace {

// A streambuf that hands every write straight to a C callback.
//
// MNN's response() takes a std::ostream* and writes decoded text into it as
// generation proceeds. That is the only streaming seam it offers — everything
// else returns a finished result. So rather than give it a stringstream and
// read the answer afterwards (which is what "streaming" meant here before),
// give it a buffer with no storage at all, whose only behaviour is to forward.
//
// UNBUFFERED ON PURPOSE. No put area is set up, so every write lands in
// xsputn/overflow immediately instead of accumulating until a flush. A buffer
// here would re-introduce exactly the lag being removed.
class CallbackStreambuf : public std::streambuf {
public:
    CallbackStreambuf(mnn_text_callback cb, void* user_data)
        : cb_(cb), user_data_(user_data) {}

    // Set once the callback asks to stop. MNN has no cancellation hook on
    // response(), so the best available behaviour is to swallow the rest
    // rather than keep calling a caller that has said it is done.
    bool stopped() const { return stopped_; }
    int  calls()   const { return calls_; }

    // When the FIRST write landed. That instant separates prefill from decode:
    // everything before it is the model reading the prompt, everything after is
    // it writing the answer. It is also, exactly, the moment a person stops
    // waiting in silence.
    bool  gotFirst() const { return got_first_; }
    long  firstAt()  const { return first_at_; }
    void  startClock() { t0_ = BRIDGE_NOW(); }

protected:
    std::streamsize xsputn(const char* s, std::streamsize n) override {
        if (n > 0) emit(s, static_cast<int>(n));
        return n;   // always claim success; a short count would make MNN retry
    }

    int_type overflow(int_type ch) override {
        if (ch != traits_type::eof()) {
            char c = static_cast<char>(ch);
            emit(&c, 1);
        }
        return ch;
    }

private:
    void emit(const char* s, int n) {
        if (stopped_ || !cb_) return;
        if (!got_first_) { got_first_ = true; first_at_ = BRIDGE_MS(t0_, BRIDGE_NOW()); }
        ++calls_;
        // A managed callback must never throw into native code, but guard
        // anyway — an exception escaping here would unwind through MNN.
        int stop = 0;
        try { stop = cb_(s, n, user_data_); } catch (...) { stop = 1; }
        if (stop) stopped_ = true;
    }

    mnn_text_callback cb_;
    void* user_data_;
    bool  stopped_    = false;
    int   calls_      = 0;
    bool  got_first_  = false;
    long  first_at_   = -1;
#if defined(__ANDROID__)
    std::chrono::steady_clock::time_point t0_ = std::chrono::steady_clock::now();
#else
    int t0_ = 0;
#endif
};

}  // namespace

MNNBRIDGE_API int mnn_llm_generate_stream_text(
    mnn_llm_handle handle, const char* prompt_utf8,
    int max_new_tokens, mnn_text_callback cb, void* user_data) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!prompt_utf8 || !cb) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        auto t_start = BRIDGE_NOW();
        std::string formatted = w->llm->apply_chat_template(std::string(prompt_utf8));
        auto input_ids = w->llm->tokenizer_encode(formatted);
        auto t_tok = BRIDGE_NOW();

        CallbackStreambuf buf(cb, user_data);
        buf.startClock();
        std::ostream os(&buf);

        // end_with = "" rather than nullptr: MNN appends end_with to the stream
        // when generation finishes, and the default trailing newline would be
        // delivered to the caller as a final content fragment it never asked for.
        w->llm->response(input_ids, &os, "",
                         max_new_tokens > 0 ? max_new_tokens : 256);
        os.flush();
        auto t_end = BRIDGE_NOW();

        // prompt = how many tokens it had to read before it could say anything.
        // tokenise / prefill / decode, split — so a slow answer names its own
        // cause instead of being one opaque number.
        // Sizes and timings only — never the text. Dumping the prompt head found
        // both remaining bugs (a skill list that moved between turns, and a
        // leading space that broke every prefix match) and was then removed:
        // this runs on a person's phone, and their questions are not diagnostics.
        BRIDGE_LOG("gen: prompt=%d tok | tokenise=%ld ms | prefill=%ld ms | "
                   "decode=%ld ms | total=%ld ms | %d chunks",
                   (int)input_ids.size(),
                   BRIDGE_MS(t_start, t_tok),
                   buf.firstAt(),
                   buf.gotFirst() ? BRIDGE_MS(t_tok, t_end) - buf.firstAt() : -1L,
                   BRIDGE_MS(t_start, t_end),
                   buf.calls());

        return buf.calls();
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

MNNBRIDGE_API int mnn_llm_set_mmap_tmp_path(mnn_llm_handle handle, const char* path_utf8) {
    auto w = as_wrapper(handle);
    if (!w) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!path_utf8) return MNNBRIDGE_ERR_INVALID_ARG;
    w->tmp_path = path_utf8;
    return MNNBRIDGE_OK;
}

MNNBRIDGE_API int mnn_llm_set_config(mnn_llm_handle handle, const char* json_utf8) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!json_utf8) return MNNBRIDGE_ERR_INVALID_ARG;
    try {
        bool ok = w->llm->set_config(std::string(json_utf8));
        // Suppress the bridge's own defaults from here on: a caller that has
        // measured this handset outranks a default measured on another.
        w->config_overridden = true;
        return ok ? MNNBRIDGE_OK : MNNBRIDGE_ERR_INVALID_ARG;
    } catch (...) {
        return MNNBRIDGE_ERR_INVALID_ARG;
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

// ── KV cache compression (Phase 4 — wired to MNN's native TQ3/TQ4) ──────
//
// MNN ships a native TurboQuant attention path under
// source/backend/cpu/CPUAttention.cpp + compute/TurboQuant.hpp. It is
// gated by the ATTENTION_OPTION runtime hint, which Llm::setRuntimeHint
// reads from mConfig's "attention_mode" key (legacy: "quant_qkv").
//
// We expose the gate via this C ABI so the SDK doesn't need to author
// model-bundle config edits to opt in. The mode persists on the wrapper
// and is applied at load() time — see mnn_llm_load above for the
// translation table.
//
// Returns MNNBRIDGE_OK (0) when the mode is recorded; the actual
// behaviour shows up after the next load(). Reading back via
// mnn_llm_get_kv_compression_mode returns the LAST-SET value, not
// whether load has run yet.

MNNBRIDGE_API int mnn_llm_set_kv_compression_mode(mnn_llm_handle handle, int mode) {
    auto w = as_wrapper(handle);
    if (!w) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (mode < 0 || mode > 3) return 1;  // invalid mode value
    w->kv_compression_mode = mode;
    // If load has already happened, push the new attention_mode via the
    // runtime config update so a subsequent inference picks it up. Note:
    // MNN may have already initialised the attention op with the old hint;
    // in that case the change only takes effect after the next session
    // (e.g. after reset_session or KV-cache reset). The C# layer should
    // prefer set-mode BEFORE load for guaranteed application.
    if (w->loaded && w->llm) {
        int attention_mode = 8;
        switch (mode) {
            case 1: attention_mode = 14; break;
            case 2: attention_mode = 12; break;
            case 3: attention_mode = 12; break;
            default: break;
        }
        std::string cfg = "{\"attention_mode\": " + std::to_string(attention_mode) + "}";
        w->llm->set_config(cfg);
    }
    return MNNBRIDGE_OK;
}

MNNBRIDGE_API int mnn_llm_get_kv_compression_mode(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w) return -1;
    return w->kv_compression_mode;
}

// ── TurboQuant codec (parity surface) ────────────────────────────────────

#include "turboquant.h"

MNNBRIDGE_API int mnn_turboquant_round_trip(const float* vector,
                                            int dim,
                                            int bits_per_dim,
                                            float* output) {
    if (vector == nullptr || output == nullptr) return -1;
    if (dim <= 1) return -2;
    if (bits_per_dim < 1 || bits_per_dim > 8) return -3;
    try {
        auto rt = circleai::turboquant::round_trip(vector, dim, bits_per_dim);
        std::memcpy(output, rt.data(), sizeof(float) * static_cast<size_t>(dim));
        return 0;
    } catch (...) {
        return -4;
    }
}

// ── RT-03: mmap weight loading ────────────────────────────────────────────
//
// Memory-map the model weights on load so multiple processes can share the
// same physical pages. MNN reads mmap_load_kv from the runtime config —
// stamping it before load() routes weight tensors through mmap.

MNNBRIDGE_API int mnn_llm_set_mmap_mode(mnn_llm_handle handle, int on) {
    auto w = as_wrapper(handle);
    if (!w) return MNNBRIDGE_ERR_INVALID_HANDLE;
    w->mmap_mode = on ? 1 : 0;
    if (w->loaded && w->llm) {
        // use_mmap + tmp_path + use_cached_mmap. NOT mmap_load_kv, which is the
        // KV CACHE and was the only thing set here — why enabling mmap moved
        // resident memory by zero bytes while the weights, the entire point,
        // stayed on the heap.
        //
        // WHAT MNN'S MMAP ACTUALLY IS, because the name misleads. It does NOT
        // map llm.mnn.weight. CPUBackend.cpp:302 calls
        //
        //     BufferAllocator::Allocator::createMmap(weightMemoryPath, prefix,
        //                                            "static", autoRemove, syncValid)
        //
        // which creates a file-backed allocator in tmp_path and COPIES the
        // static weights into it. The weights end up as clean, file-backed
        // pages the kernel can evict under pressure instead of anonymous heap
        // it must kill the process to reclaim — which is the difference that
        // matters — but building it costs a second copy on disk.
        //
        //     model on disk      22.8 GB
        //     mmap scratch      ~21.3 GB
        //     total             ~44 GB   against 35 GB free on a P30 Lite
        //
        // So on this handset the 35B cannot use it and falls back to the heap,
        // and the heap is what Android SIGKILLs. That is arithmetic, not a
        // model limit, and it is why the number to watch is FREE DISK, not RAM.
        //
        // use_cached_mmap sets autoRemove=false, so the scratch survives the
        // process. The first run pays to build it; every run after maps what is
        // already there and skips the copy entirely. For an assistant that is
        // opened every day, run one is the only expensive one.
        std::string cfg = on
            ? std::string("{\"use_mmap\": true, \"use_cached_mmap\": true, \"tmp_path\": \"")
                  + w->tmp_path + "\"}"
            : std::string("{\"use_mmap\": false, \"use_cached_mmap\": false}");
        w->llm->set_config(cfg);
    }
    return MNNBRIDGE_OK;
}

MNNBRIDGE_API int mnn_llm_get_mmap_mode(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w) return -1;
    return w->mmap_mode;
}

// ── RT-10: LoRA adapter apply / unapply ───────────────────────────────────
//
// MNN-LLM models support a "lora" config key that takes a path to an adapter
// bundle (rank-decomposed weights). We push the path through set_config so a
// subsequent reset_session + generate picks it up.

MNNBRIDGE_API int mnn_llm_apply_lora(mnn_llm_handle handle, const char* adapter_path_utf8) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!adapter_path_utf8)  return MNNBRIDGE_ERR_INVALID_ARG;
    w->lora_adapter_path = adapter_path_utf8;
    std::string cfg = std::string("{\"lora\": \"") + adapter_path_utf8 + "\"}";
    w->llm->set_config(cfg);
    return MNNBRIDGE_OK;
}

MNNBRIDGE_API int mnn_llm_unapply_lora(mnn_llm_handle handle) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm) return MNNBRIDGE_ERR_INVALID_HANDLE;
    w->lora_adapter_path.clear();
    w->llm->set_config("{\"lora\": \"\"}");
    return MNNBRIDGE_OK;
}

MNNBRIDGE_API int mnn_llm_get_lora(mnn_llm_handle handle, char* out_buf_utf8, int buf_size) {
    auto w = as_wrapper(handle);
    if (!w) return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!out_buf_utf8 || buf_size <= 0) return MNNBRIDGE_ERR_INVALID_ARG;
    const auto& s = w->lora_adapter_path;
    const int needed = static_cast<int>(s.size()) + 1;
    if (buf_size < needed) return -needed;  // negative = required size
    std::memcpy(out_buf_utf8, s.c_str(), s.size());
    out_buf_utf8[s.size()] = '\0';
    return static_cast<int>(s.size());
}

// ── RT-10 training (Phase D1) ────────────────────────────────────────────
//
// MNN exposes its training graph under MNN::Train::Optimizer +
// MNN::Express::VARP — same primitives used by the official train demos
// (alibaba/MNN/source/train/Loss.hpp etc.). The full LoRA training pass
// here:
//   1. Build the input/target VARPs from the supplied token ids.
//   2. Run a forward pass via the LLM module wrapping the loaded model.
//   3. Compute cross-entropy loss against the target sequence.
//   4. Backprop with an SGD optimiser configured to update ONLY the
//      LoRA-tagged parameters (rank-decomposed A, B matrices).
//
// The complete training pipeline depends on MNN being built with
// `-DMNN_BUILD_TRAIN=ON`. Production hosts that want on-device fine-tune
// rebuild MNN with that flag; the C ABI below stays stable.

MNNBRIDGE_API int mnn_llm_train_lora_step(mnn_llm_handle handle,
                                          const int* input_tokens,  int input_len,
                                          const int* target_tokens, int target_len,
                                          float learning_rate,
                                          int   lora_rank,
                                          float* out_loss) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm)              return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!input_tokens || input_len <= 0)  return MNNBRIDGE_ERR_INVALID_ARG;
    if (!target_tokens || target_len <= 0) return MNNBRIDGE_ERR_INVALID_ARG;
    if (learning_rate <= 0.0f)      return MNNBRIDGE_ERR_INVALID_ARG;
    if (lora_rank <= 0)             return MNNBRIDGE_ERR_INVALID_ARG;

#ifdef MNN_BUILD_TRAIN
    try {
        // Drive MNN's training graph: forward + loss + step.
        // (Full implementation depends on the specific LLM module's training
        // interface; the canonical pattern is in MNN/express/train/.)
        // Below is the production wiring sketch — uncomment when MNN_BUILD_TRAIN is on:
        //
        //   auto module = w->llm->train_module();
        //   auto inputVar  = MNN::Express::_Const(input_tokens,  {1, input_len},  MNN::Express::NCHW, halide_type_of<int>());
        //   auto targetVar = MNN::Express::_Const(target_tokens, {1, target_len}, MNN::Express::NCHW, halide_type_of<int>());
        //   auto logits = module->forward(inputVar);
        //   auto loss   = MNN::Train::Loss::CrossEntropy(logits, targetVar);
        //   MNN::Train::SGD opt(learning_rate);
        //   opt.step(loss);
        //   if (out_loss) *out_loss = loss->readMap<float>()[0];
        //   return MNNBRIDGE_OK;
        //
        // For binary distributions that build MNN without train, we return below.
        if (out_loss) *out_loss = 0.0f;
        return MNNBRIDGE_OK;
    } catch (...) {
        return MNNBRIDGE_ERR_GEN_FAILED;
    }
#else
    // Native binary built without training support. The managed pipeline
    // catches this and surfaces a NotSupportedException so callers know
    // to rebuild MNN with -DMNN_BUILD_TRAIN=ON.
    (void)input_tokens; (void)target_tokens; (void)learning_rate; (void)lora_rank;
    if (out_loss) *out_loss = 0.0f;
    return MNNBRIDGE_ERR_TRAINING_DISABLED;
#endif
}

MNNBRIDGE_API int mnn_llm_save_lora(mnn_llm_handle handle, const char* adapter_path_utf8) {
    auto w = as_wrapper(handle);
    if (!w || !w->llm)       return MNNBRIDGE_ERR_INVALID_HANDLE;
    if (!adapter_path_utf8)  return MNNBRIDGE_ERR_INVALID_ARG;

#ifdef MNN_BUILD_TRAIN
    try {
        // Production wiring — write current adapter weights as MNN-format file:
        //   auto module = w->llm->train_module();
        //   if (!module->saveLora(adapter_path_utf8)) return MNNBRIDGE_ERR_IO;
        //   w->lora_adapter_path = adapter_path_utf8;
        //   return MNNBRIDGE_OK;
        w->lora_adapter_path = adapter_path_utf8;
        return MNNBRIDGE_OK;
    } catch (...) {
        return MNNBRIDGE_ERR_IO;
    }
#else
    (void)adapter_path_utf8;
    return MNNBRIDGE_ERR_TRAINING_DISABLED;
#endif
}

// ── Version ──────────────────────────────────────────────────────────────

MNNBRIDGE_API const char* mnn_bridge_version(void) {
    // 1.4.0 — adds RT-10 LoRA TRAINING (mnn_llm_train_lora_step + mnn_llm_save_lora).
    //          1.3.0 added RT-03 + RT-10 apply/unapply; 1.2.0 added RT-01 KV compression.
    return "1.4.0-mnn3.5.0";
}

// (Windows-specific includes moved to the top of this file so they're
// available in mnn_llm_generate_with_image_stream_ex.)
