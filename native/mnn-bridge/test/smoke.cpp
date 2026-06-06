// smoke.cpp
//
// Minimum end-to-end smoke test for mnnbridge. Builds when
// MNNBRIDGE_BUILD_TEST=ON in the CMake config.
//
// Usage:
//   mnnbridge_smoke                  → load-only sanity (no model)
//   mnnbridge_smoke <config.json>    → create + load + report

#include "mnnbridge.h"

#include <cstdio>
#include <cstdlib>
#include <cstring>

static int print_token(int token_id, void* user_data) {
    int* counter = static_cast<int*>(user_data);
    if (counter) (*counter)++;
    std::fprintf(stdout, "token=%d ", token_id);
    return 0;
}

int main(int argc, char** argv) {
    std::fprintf(stdout, "mnnbridge version: %s\n", mnn_bridge_version());

    if (argc < 2) {
        std::fprintf(stdout, "No config.json supplied. "
                              "Load-only sanity passed.\n");
        return 0;
    }

    const char* config_path = argv[1];
    std::fprintf(stdout, "Creating Llm from: %s\n", config_path);

    mnn_llm_handle h = mnn_llm_create(config_path);
    if (!h) {
        std::fprintf(stderr, "ERROR: mnn_llm_create returned NULL\n");
        return 1;
    }
    std::fprintf(stdout, "Created. Loading weights...\n");

    int rc = mnn_llm_load(h);
    if (rc != MNNBRIDGE_OK) {
        std::fprintf(stderr, "ERROR: mnn_llm_load returned %d\n", rc);
        mnn_llm_free(h);
        return 2;
    }
    std::fprintf(stdout, "Loaded.\n");

    int ctx_sz   = mnn_llm_get_context_size(h);
    int vocab_sz = mnn_llm_get_vocab_size(h);
    int mtype    = mnn_llm_get_model_type(h);
    std::fprintf(stdout, "  context_size = %d\n", ctx_sz);
    std::fprintf(stdout, "  vocab_size   = %d\n", vocab_sz);
    std::fprintf(stdout, "  model_type   = %d\n", mtype);

    if (argc >= 3) {
        const char* prompt = argv[2];
        std::fprintf(stdout, "Streaming generate (max 16 tokens) for: %s\n", prompt);
        int counter = 0;
        int emitted = mnn_llm_generate_stream_ex(h, prompt, 16, print_token, &counter);
        std::fprintf(stdout, "\nemitted=%d  (callback fired %d times)\n",
                     emitted, counter);
    }

    mnn_llm_free(h);
    std::fprintf(stdout, "Freed. OK.\n");
    return 0;
}
