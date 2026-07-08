/*
 * host_ai.c — CircleAI.Hosting core runtime (C11 port).
 *
 * See host_ai.h. Ports IAIService + AIService + FallbackAIService + AIApiClient
 * + IAIEndpoint (InProcess / HttpLoopback) + observers + memory-pressure
 * sources + ParseToolCall. Deterministic; external deps injected behind seams.
 *
 * Pure C11 + libc. No pthreads, no sockets.
 */

#include "circle_ai/host_ai.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

/* ── small string helpers ─────────────────────────────────────────────── */

static char *h_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool h_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}

/* Growable byte buffer. */
typedef struct { char *data; size_t len, cap; } h_sb;
static bool h_sb_reserve(h_sb *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return true;
    size_t nc = b->cap ? b->cap : 64;
    while (nc < b->len + extra + 1) nc *= 2;
    char *n = (char *)realloc(b->data, nc);
    if (!n) return false;
    b->data = n; b->cap = nc;
    return true;
}
static void h_sb_add(h_sb *b, const char *s) {
    if (!s) return;
    size_t n = strlen(s);
    if (!h_sb_reserve(b, n)) return;
    memcpy(b->data + b->len, s, n);
    b->len += n; b->data[b->len] = '\0';
}
static void h_sb_addc(h_sb *b, char c) {
    if (!h_sb_reserve(b, 1)) return;
    b->data[b->len++] = c; b->data[b->len] = '\0';
}
static char *h_sb_take(h_sb *b) {
    if (!b->data) return h_strdup("");
    return b->data; /* caller owns */
}
static void h_json_escape(h_sb *b, const char *s) {
    h_sb_addc(b, '"');
    for (const char *p = s ? s : ""; *p; p++) {
        unsigned char ch = (unsigned char)*p;
        switch (ch) {
            case '"':  h_sb_add(b, "\\\""); break;
            case '\\': h_sb_add(b, "\\\\"); break;
            case '\n': h_sb_add(b, "\\n");  break;
            case '\r': h_sb_add(b, "\\r");  break;
            case '\t': h_sb_add(b, "\\t");  break;
            default:
                if (ch < 0x20) { char u[8]; snprintf(u, sizeof(u), "\\u%04x", ch); h_sb_add(b, u); }
                else h_sb_addc(b, (char)ch);
        }
    }
    h_sb_addc(b, '"');
}

/* ── brownout reason ──────────────────────────────────────────────────── */

const char *ca_brownout_reason_name(ca_brownout_reason_t r) {
    switch (r) {
        case CA_BROWNOUT_MEMORY_PRESSURE:  return "MemoryPressure";
        case CA_BROWNOUT_BATTERY_FLOOR:    return "BatteryFloor";
        case CA_BROWNOUT_THERMAL_CRITICAL: return "ThermalCritical";
        case CA_BROWNOUT_MANUAL:           return "Manual";
    }
    return "Unknown";
}

/* ===========================================================================
 * ParseToolCall (AIService.ParseToolCall)
 * =========================================================================== */

/* Minimal helper: find a top-level string value for "key" in a JSON object
 * text. Returns malloc'd unescaped value or NULL. Only handles the flat shapes
 * the Qwen tool_call block uses ("name"/"tool_name"). */
static char *json_find_string(const char *json, const char *key) {
    if (!json || !key) return NULL;
    size_t klen = strlen(key);
    const char *p = json;
    while ((p = strchr(p, '"')) != NULL) {
        /* candidate key start */
        if (strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != ':') { p++; continue; }
            q++;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != '"') return NULL; /* not a string value */
            q++;
            h_sb out = {0};
            while (*q && *q != '"') {
                if (*q == '\\' && q[1]) {
                    q++;
                    switch (*q) {
                        case 'n': h_sb_addc(&out, '\n'); break;
                        case 't': h_sb_addc(&out, '\t'); break;
                        case 'r': h_sb_addc(&out, '\r'); break;
                        case '"': h_sb_addc(&out, '"');  break;
                        case '\\': h_sb_addc(&out, '\\'); break;
                        case '/': h_sb_addc(&out, '/');  break;
                        default:  h_sb_addc(&out, *q);   break;
                    }
                    q++;
                } else {
                    h_sb_addc(&out, *q++);
                }
            }
            return h_sb_take(&out);
        }
        p++;
    }
    return NULL;
}

/* Extract the raw text of the "arguments" object (balanced braces). Returns
 * malloc'd "{...}" or NULL when absent. */
static char *json_find_object(const char *json, const char *key) {
    if (!json || !key) return NULL;
    size_t klen = strlen(key);
    const char *p = json;
    while ((p = strchr(p, '"')) != NULL) {
        if (strncmp(p + 1, key, klen) == 0 && p[1 + klen] == '"') {
            const char *q = p + 1 + klen + 1;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != ':') { p++; continue; }
            q++;
            while (*q && isspace((unsigned char)*q)) q++;
            if (*q != '{') return NULL;
            const char *start = q;
            int depth = 0; bool instr = false;
            for (; *q; q++) {
                if (instr) {
                    if (*q == '\\' && q[1]) { q++; continue; }
                    if (*q == '"') instr = false;
                } else {
                    if (*q == '"') instr = true;
                    else if (*q == '{') depth++;
                    else if (*q == '}') { depth--; if (depth == 0) { q++; break; } }
                }
            }
            size_t n = (size_t)(q - start);
            char *r = (char *)malloc(n + 1);
            if (r) { memcpy(r, start, n); r[n] = '\0'; }
            return r;
        }
        p++;
    }
    return NULL;
}

bool ca_ai_parse_tool_call(const char *response, char **out_tool_name,
                           char **out_arguments_json) {
    if (h_blank(response) || !out_tool_name || !out_arguments_json) return false;
    static const char *OPEN = "<tool_call>";
    static const char *CLOSE = "</tool_call>";
    const char *start = strstr(response, OPEN);
    if (!start) return false;
    const char *cstart = start + strlen(OPEN);
    const char *end = strstr(cstart, CLOSE);
    if (!end) return false;

    size_t n = (size_t)(end - cstart);
    char *json = (char *)malloc(n + 1);
    if (!json) return false;
    memcpy(json, cstart, n); json[n] = '\0';
    /* trim */
    char *js = json;
    while (*js && isspace((unsigned char)*js)) js++;
    char *je = js + strlen(js);
    while (je > js && isspace((unsigned char)je[-1])) *--je = '\0';
    if (*js == '\0') { free(json); return false; }

    char *name = json_find_string(js, "name");
    if (!name) name = json_find_string(js, "tool_name");
    if (h_blank(name)) { free(name); free(json); return false; }

    char *args = json_find_object(js, "arguments");
    if (!args) args = h_strdup("{}");

    free(json);
    *out_tool_name = name;
    *out_arguments_json = args;
    return true;
}

/* ===========================================================================
 * IMemoryPressureSource
 * =========================================================================== */

typedef struct {
    int   token;
    ca_memory_pressure_handler_fn handler;
    void *user;
} h_mp_sub;

struct ca_memory_pressure_source {
    bool                        is_null;
    ca_memory_pressure_level_t  current;
    h_mp_sub                   *subs;
    size_t                      count, cap;
    int                         next_token;
};

ca_memory_pressure_source_t *ca_null_memory_pressure_source(void) {
    ca_memory_pressure_source_t *s = (ca_memory_pressure_source_t *)calloc(1, sizeof(*s));
    if (s) { s->is_null = true; s->current = CA_MEM_PRESSURE_NORMAL; }
    return s;
}
ca_memory_pressure_source_t *ca_manual_memory_pressure_source_create(void) {
    ca_memory_pressure_source_t *s = (ca_memory_pressure_source_t *)calloc(1, sizeof(*s));
    if (s) { s->current = CA_MEM_PRESSURE_NORMAL; s->next_token = 1; }
    return s;
}
void ca_memory_pressure_source_destroy(ca_memory_pressure_source_t *s) {
    if (!s) return;
    free(s->subs);
    free(s);
}
ca_memory_pressure_level_t ca_memory_pressure_current(const ca_memory_pressure_source_t *s) {
    return s ? s->current : CA_MEM_PRESSURE_NORMAL;
}
int ca_memory_pressure_subscribe(ca_memory_pressure_source_t *s,
                                 ca_memory_pressure_handler_fn handler, void *user) {
    if (!s || s->is_null || !handler) return 0;
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 4;
        void *n = realloc(s->subs, nc * sizeof(*s->subs));
        if (!n) return 0;
        s->subs = (h_mp_sub *)n; s->cap = nc;
    }
    int tok = s->next_token++;
    s->subs[s->count].token = tok;
    s->subs[s->count].handler = handler;
    s->subs[s->count].user = user;
    s->count++;
    return tok;
}
void ca_memory_pressure_unsubscribe(ca_memory_pressure_source_t *s, int token) {
    if (!s || token <= 0) return;
    for (size_t i = 0; i < s->count; ++i)
        if (s->subs[i].token == token) {
            memmove(&s->subs[i], &s->subs[i + 1], (s->count - i - 1) * sizeof(*s->subs));
            s->count--;
            return;
        }
}
void ca_memory_pressure_raise(ca_memory_pressure_source_t *s,
                             ca_memory_pressure_level_t level) {
    if (!s || s->is_null) return;
    if (s->current == level) return; /* only transitions fire */
    ca_memory_pressure_level_t prev = s->current;
    s->current = level;
    /* snapshot to tolerate handlers that unsubscribe */
    size_t n = s->count;
    h_mp_sub *snap = NULL;
    if (n) { snap = (h_mp_sub *)malloc(n * sizeof(*snap)); if (snap) memcpy(snap, s->subs, n * sizeof(*snap)); }
    for (size_t i = 0; i < n && snap; ++i)
        snap[i].handler(snap[i].user, prev, level);
    free(snap);
}

/* ===========================================================================
 * PushAIObserver
 * =========================================================================== */

struct ca_push_observer {
    ca_push_send_fn send;
    void           *send_user;
    char           *device_token;
};

ca_push_observer_t *ca_push_observer_create(ca_push_send_fn send, void *send_user,
                                            const char *device_token) {
    if (h_blank(device_token)) return NULL;
    ca_push_observer_t *o = (ca_push_observer_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->send = send; o->send_user = send_user;
    o->device_token = h_strdup(device_token);
    return o;
}
void ca_push_observer_destroy(ca_push_observer_t *o) {
    if (!o) return;
    free(o->device_token);
    free(o);
}

/* Truncate body to 100 chars + "…" (matches MaxBodyLength). Returns malloc'd. */
static char *truncate_body(const char *full) {
    const char *s = full ? full : "";
    size_t n = strlen(s);
    if (n <= 100) return h_strdup(s);
    h_sb b = {0};
    for (size_t i = 0; i < 100; ++i) h_sb_addc(&b, s[i]);
    h_sb_add(&b, "\xE2\x80\xA6"); /* … */
    return h_sb_take(&b);
}

static void push_on_chat(void *user, const ca_ai_chat_event_t *ev) {
    ca_push_observer_t *o = (ca_push_observer_t *)user;
    if (!o || !o->send) return;
    char *body = truncate_body(ev ? ev->response : "");
    o->send(o->send_user, o->device_token, "B!", body);
    free(body);
}
ca_ai_observer_v2_t ca_push_observer_as_observer(ca_push_observer_t *o) {
    ca_ai_observer_v2_t v; memset(&v, 0, sizeof(v));
    v.on_chat_completed = push_on_chat;
    v.user = o;
    return v;
}
void ca_push_observer_on_error(ca_push_observer_t *o, const char *message) {
    if (!o || !o->send) return;
    char *body = truncate_body(message);
    o->send(o->send_user, o->device_token, "B! Error", body);
    free(body);
}

/* ===========================================================================
 * AetherAIObserver
 * =========================================================================== */

struct ca_aether_observer {
    ca_aether_publish_fn publish;
    void                *publish_user;
};

ca_aether_observer_t *ca_aether_observer_create(ca_aether_publish_fn publish, void *publish_user) {
    ca_aether_observer_t *o = (ca_aether_observer_t *)calloc(1, sizeof(*o));
    if (!o) return NULL;
    o->publish = publish; o->publish_user = publish_user;
    return o;
}
void ca_aether_observer_destroy(ca_aether_observer_t *o) { free(o); }

static void aether_on_chat(void *user, const ca_ai_chat_event_t *ev) {
    ca_aether_observer_t *o = (ca_aether_observer_t *)user;
    if (!o || !o->publish) return;
    h_sb b = {0};
    h_sb_add(&b, "{\"response\":");
    h_json_escape(&b, ev ? ev->response : "");
    h_sb_addc(&b, '}');
    o->publish(o->publish_user, "butler/response", (const uint8_t *)b.data, b.len);
    free(b.data);
}
ca_ai_observer_v2_t ca_aether_observer_as_observer(ca_aether_observer_t *o) {
    ca_ai_observer_v2_t v; memset(&v, 0, sizeof(v));
    v.on_chat_completed = aether_on_chat;
    v.user = o;
    return v;
}
void ca_aether_observer_on_error(ca_aether_observer_t *o, const char *error_name,
                                 const char *message) {
    if (!o || !o->publish) return;
    h_sb b = {0};
    h_sb_add(&b, "{\"error\":");   h_json_escape(&b, error_name ? error_name : "");
    h_sb_add(&b, ",\"message\":"); h_json_escape(&b, message ? message : "");
    h_sb_addc(&b, '}');
    o->publish(o->publish_user, "butler/error", (const uint8_t *)b.data, b.len);
    free(b.data);
}

/* ===========================================================================
 * AIOptions
 * =========================================================================== */

bool ca_ai_options_init(ca_ai_options_t2 *opts) {
    if (!opts) return false;
    memset(opts, 0, sizeof(*opts));
    opts->system_prompt = h_strdup("You are B!, a helpful on-device AI butler.");
    opts->persona_user_id = h_strdup("default");
    if (!opts->system_prompt || !opts->persona_user_id) { ca_ai_options_free(opts); return false; }
    opts->context_size = 4096;
    opts->thread_count = 0;
    opts->warm_on_start = false;
    opts->agentic_max_iterations = 1;
    ca_generation_options_init(&opts->default_generation_options);
    return true;
}
void ca_ai_options_free(ca_ai_options_t2 *opts) {
    if (!opts) return;
    free(opts->model_id); free(opts->system_prompt); free(opts->persona_user_id);
    opts->model_id = opts->system_prompt = opts->persona_user_id = NULL;
}

/* ===========================================================================
 * IAIService dispatchers
 * =========================================================================== */

bool ca_ai_service_is_ready(ca_ai_service_t *svc) {
    return (svc && svc->vt && svc->vt->is_ready) ? svc->vt->is_ready(svc->self) : false;
}
bool ca_ai_service_start(ca_ai_service_t *svc) {
    return (svc && svc->vt && svc->vt->start) ? svc->vt->start(svc->self) : false;
}
bool ca_ai_service_stop(ca_ai_service_t *svc) {
    return (svc && svc->vt && svc->vt->stop) ? svc->vt->stop(svc->self) : false;
}
char *ca_ai_service_ask(ca_ai_service_t *svc, const char *question) {
    return (svc && svc->vt && svc->vt->ask) ? svc->vt->ask(svc->self, question) : NULL;
}
char *ca_ai_service_chat(ca_ai_service_t *svc, const ca_chat_msg_t *messages,
                         size_t count, const ca_generation_options_t *opts) {
    return (svc && svc->vt && svc->vt->chat) ? svc->vt->chat(svc->self, messages, count, opts) : NULL;
}
char *ca_ai_service_agentic_chat(ca_ai_service_t *svc, const char *prompt,
                                 const ca_generation_options_t *opts) {
    return (svc && svc->vt && svc->vt->agentic_chat) ? svc->vt->agentic_chat(svc->self, prompt, opts) : NULL;
}
long ca_ai_service_stream(ca_ai_service_t *svc, const ca_chat_msg_t *messages,
                          size_t count, const ca_generation_options_t *opts,
                          ca_ai_stream_piece_fn on_piece, void *piece_user) {
    return (svc && svc->vt && svc->vt->stream)
        ? svc->vt->stream(svc->self, messages, count, opts, on_piece, piece_user) : -1;
}
bool ca_ai_service_invoke_tool(ca_ai_service_t *svc, const char *tool_name,
                               const char *arguments_json,
                               char **out_result_json, char **out_error) {
    return (svc && svc->vt && svc->vt->invoke_tool)
        ? svc->vt->invoke_tool(svc->self, tool_name, arguments_json, out_result_json, out_error) : false;
}
void ca_ai_service_submit_feedback(ca_ai_service_t *svc, const ca_feedback_signal_rec_t *signal) {
    if (svc && svc->vt && svc->vt->submit_feedback) svc->vt->submit_feedback(svc->self, signal);
}
void ca_ai_service_prewarm(ca_ai_service_t *svc) {
    if (svc && svc->vt && svc->vt->prewarm) svc->vt->prewarm(svc->self);
}

/* ===========================================================================
 * AIService — deterministic default impl
 * =========================================================================== */

struct ca_ai_service_impl {
    ca_ai_options_t2         *options;    /* borrowed */
    ca_local_chat_generator_t *generator; /* owned */
    bool                      owns_generator;
    bool                      started;
    bool                      disposed;

    char                     *resolved_model;   /* owned */
    char                     *fallback_model;    /* owned, or NULL */

    /* pressure subscription */
    int                       pressure_token;

    /* persona counters (PersonaState subset) */
    int                       positive_signals;
    int                       negative_signals;
    int                       total_interactions;

    ca_ai_service_t           view;
};

static uint64_t h_now_ms_counter = 1000; /* deterministic monotonic clock */
static int64_t h_now_ms(void) { return (int64_t)(h_now_ms_counter += 7); }

/* Build the enriched message list: prepend the configured system prompt unless
 * the caller already supplied a system message. Returns a malloc'd array of
 * ca_chat_msg_t whose `content`/`role` point into a companion string arena the
 * caller frees via free(arena). To keep lifetimes simple we borrow the caller's
 * message pointers and add one system message pointing at options->system_prompt.
 */
static ca_chat_msg_t *prepare_messages(ca_ai_service_impl_t *s,
                                       const ca_chat_msg_t *messages, size_t count,
                                       size_t *out_count) {
    bool has_system = false;
    for (size_t i = 0; i < count; ++i)
        if (messages[i].role && strcmp(messages[i].role, "system") == 0) { has_system = true; break; }

    const char *sysp = s->options->system_prompt;
    bool inject = !has_system && !h_blank(sysp);
    size_t n = count + (inject ? 1 : 0);
    ca_chat_msg_t *arr = (ca_chat_msg_t *)calloc(n ? n : 1, sizeof(ca_chat_msg_t));
    if (!arr) { *out_count = 0; return NULL; }
    size_t k = 0;
    if (inject) {
        arr[k].role = "system";
        arr[k].content = sysp;
        arr[k].image_bytes = NULL; arr[k].image_len = 0;
        k++;
    }
    for (size_t i = 0; i < count; ++i) arr[k++] = messages[i];
    *out_count = n;
    return arr;
}

static void fire_chat_completed(ca_ai_service_impl_t *s, const ca_chat_msg_t *prepared,
                                size_t pcount, const char *response, double elapsed_ms) {
    ca_ai_observer_v2_t *o = s->options->observer;
    if (!o || !o->on_chat_completed) return;
    ca_ai_chat_event_t ev;
    ev.correlation_id = "00000000000000000000000000000000";
    ev.messages = prepared; ev.message_count = pcount;
    ev.response = response; ev.elapsed_ms = elapsed_ms;
    ev.timestamp_ms = h_now_ms();
    o->on_chat_completed(o->user, &ev);
}

static bool impl_start(void *self);

static char *impl_chat_core(ca_ai_service_impl_t *s, const ca_chat_msg_t *messages,
                            size_t count, const ca_generation_options_t *opts) {
    if (!s || s->disposed) return NULL;
    if (!s->started) { if (!impl_start(s)) return NULL; }
    if (!s->generator) return NULL;

    size_t pcount = 0;
    ca_chat_msg_t *prepared = prepare_messages(s, messages, count, &pcount);
    if (!prepared) return NULL;

    const ca_generation_options_t *eff = opts ? opts : &s->options->default_generation_options;
    int64_t t0 = h_now_ms();
    char *response = ca_local_chat_generator_generate(s->generator, prepared, pcount, eff);
    int64_t t1 = h_now_ms();
    if (!response) response = h_strdup("");

    fire_chat_completed(s, prepared, pcount, response, (double)(t1 - t0));
    free(prepared);
    return response;
}

/* vtable impls */
static bool impl_is_ready(void *self) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    return s && s->started && s->generator && !s->disposed;
}

/* brownout handler (pressure Critical) */
static void impl_pressure_handler(void *user, ca_memory_pressure_level_t old_lvl,
                                  ca_memory_pressure_level_t new_lvl) {
    (void)old_lvl;
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)user;
    if (new_lvl == CA_MEM_PRESSURE_CRITICAL)
        ca_ai_service_impl_brownout(s, CA_BROWNOUT_MEMORY_PRESSURE);
}

static bool impl_start(void *self) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed) return false;
    if (s->started) return true;

    /* resolve model id */
    const char *mid = s->options->model_id;
    free(s->resolved_model);
    s->resolved_model = h_strdup(mid ? mid : "local-default");

    ca_ai_observer_v2_t *o = s->options->observer;
    if (o && o->on_model_fetching)
        o->on_model_fetching(o->user, s->resolved_model, s->options->model_id == NULL);

    if (!s->generator) {
        int ctx = s->options->context_size > 0 ? s->options->context_size : 4096;
        s->generator = ca_local_chat_generator_create(s->resolved_model, ctx);
        s->owns_generator = true;
        if (!s->generator) return false;
    }

    /* optional warm-up (deterministic no-op generation) */
    if (s->options->warm_on_start) {
        ca_chat_msg_t warm[2] = {
            { "system", s->options->system_prompt, NULL, 0 },
            { "user", ".", NULL, 0 },
        };
        ca_generation_options_t wo; ca_generation_options_init(&wo);
        wo.max_tokens = 1; wo.temperature = 0.0f;
        char *w = ca_local_chat_generator_generate(s->generator, warm, 2, &wo);
        free(w);
    }

    s->started = true;

    /* subscribe to pressure -> brownout */
    if (s->options->pressure_source && s->pressure_token == 0)
        s->pressure_token = ca_memory_pressure_subscribe(
            s->options->pressure_source, impl_pressure_handler, s);

    if (o && o->on_started) o->on_started(o->user);
    return true;
}

static bool impl_stop(void *self) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed) return true;
    if (s->owns_generator && s->generator) { ca_local_chat_generator_destroy(s->generator); s->generator = NULL; }
    else s->generator = NULL;
    s->started = false;
    ca_ai_observer_v2_t *o = s->options->observer;
    if (o && o->on_stopped) o->on_stopped(o->user);
    return true;
}

static char *impl_ask(void *self, const char *question) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (h_blank(question)) return NULL;
    ca_chat_msg_t m = { "user", question, NULL, 0 };
    return impl_chat_core(s, &m, 1, &s->options->default_generation_options);
}

static char *impl_chat(void *self, const ca_chat_msg_t *messages, size_t count,
                       const ca_generation_options_t *opts) {
    return impl_chat_core((ca_ai_service_impl_t *)self, messages, count, opts);
}

static long impl_stream(void *self, const ca_chat_msg_t *messages, size_t count,
                        const ca_generation_options_t *opts,
                        ca_ai_stream_piece_fn on_piece, void *piece_user) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed) return -1;
    if (!s->started) { if (!impl_start(s)) return -1; }
    if (!s->generator) return -1;

    size_t pcount = 0;
    ca_chat_msg_t *prepared = prepare_messages(s, messages, count, &pcount);
    if (!prepared) return -1;
    const ca_generation_options_t *eff = opts ? opts : &s->options->default_generation_options;

    /* The deterministic generator has no token streaming; emit the full reply
     * as a single piece (mirrors the loopback endpoint's single-frame case and
     * still exercises the stream-started/completed observer path). */
    char *full = ca_local_chat_generator_generate(s->generator, prepared, pcount, eff);
    if (!full) full = h_strdup("");

    ca_ai_observer_v2_t *o = s->options->observer;
    if (o && o->on_stream_started) {
        ca_ai_stream_event_t ev = { "00000000000000000000000000000000", prepared, pcount, 0.0, 0, h_now_ms() };
        o->on_stream_started(o->user, &ev);
    }
    long pieces = 0;
    bool cont = true;
    if (full[0] != '\0') { cont = on_piece ? on_piece(piece_user, full) : true; pieces = 1; }
    (void)cont;
    if (o && o->on_stream_completed) {
        ca_ai_stream_event_t ev = { "00000000000000000000000000000000", prepared, pcount, 0.0, (int)pieces, h_now_ms() };
        o->on_stream_completed(o->user, &ev);
    }
    free(full);
    free(prepared);
    return pieces;
}

static void fire_tool_event(ca_ai_service_impl_t *s, const char *name,
                            const char *args_json, bool success,
                            const char *result_json, const char *error) {
    ca_ai_observer_v2_t *o = s->options->observer;
    if (!o || !o->on_tool_invoked) return;
    ca_ai_tool_event_t ev;
    ev.correlation_id = "00000000000000000000000000000000";
    ev.tool_name = name; ev.arguments_json = args_json;
    ev.success = success; ev.result_json = result_json; ev.error_message = error;
    ev.elapsed_ms = 0.0; ev.timestamp_ms = h_now_ms();
    o->on_tool_invoked(o->user, &ev);
}

static bool impl_invoke_tool(void *self, const char *tool_name, const char *arguments_json,
                             char **out_result_json, char **out_error) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed || !out_result_json || !out_error) return false;
    *out_result_json = NULL; *out_error = NULL;

    if (!s->options->tool_bridge || !s->options->tool_bridge->invoke) {
        *out_error = h_strdup("No tool bridge configured.");
        fire_tool_event(s, tool_name, arguments_json, false, NULL, *out_error);
        return false;
    }
    char *res = NULL, *err = NULL;
    bool ok = s->options->tool_bridge->invoke(s->options->tool_bridge->user,
                                              tool_name, arguments_json, &res, &err);
    *out_result_json = res; *out_error = err;
    fire_tool_event(s, tool_name, arguments_json, ok, res, err);
    return ok;
}

static char *impl_agentic_chat(void *self, const char *prompt,
                               const ca_generation_options_t *opts) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed || h_blank(prompt)) return NULL;
    if (!s->started) { if (!impl_start(s)) return NULL; }
    if (!s->generator) return NULL;

    int max_iter = s->options->agentic_max_iterations > 0 ? s->options->agentic_max_iterations : 1;
    const ca_generation_options_t *eff = opts ? opts : &s->options->default_generation_options;

    /* Growable history of owned messages (role/content strdup'd). */
    typedef struct { char *role; char *content; } owned_msg;
    owned_msg *hist = NULL; size_t hcount = 0, hcap = 0;
    #define PUSH_MSG(R, C) do { \
        if (hcount == hcap) { size_t nc = hcap ? hcap * 2 : 4; \
            void *n = realloc(hist, nc * sizeof(*hist)); if (!n) goto done; hist = (owned_msg *)n; hcap = nc; } \
        hist[hcount].role = h_strdup(R); hist[hcount].content = h_strdup(C); hcount++; \
    } while (0)

    PUSH_MSG("user", prompt);

    char *last_response = h_strdup("");

    for (int it = 0; it < max_iter; ++it) {
        /* build ca_chat_msg_t array from history (prepend system via prepare) */
        ca_chat_msg_t *raw = (ca_chat_msg_t *)calloc(hcount, sizeof(ca_chat_msg_t));
        if (!raw) break;
        for (size_t i = 0; i < hcount; ++i) { raw[i].role = hist[i].role; raw[i].content = hist[i].content; }
        size_t pcount = 0;
        ca_chat_msg_t *prepared = prepare_messages(s, raw, hcount, &pcount);
        free(raw);
        if (!prepared) break;

        int64_t t0 = h_now_ms();
        char *response = ca_local_chat_generator_generate(s->generator, prepared, pcount, eff);
        int64_t t1 = h_now_ms();
        if (!response) response = h_strdup("");
        fire_chat_completed(s, prepared, pcount, response, (double)(t1 - t0));
        free(prepared);

        free(last_response);
        last_response = h_strdup(response);
        PUSH_MSG("assistant", response);

        char *tname = NULL, *targs = NULL;
        bool has_call = ca_ai_parse_tool_call(response, &tname, &targs);
        free(response);
        if (!has_call) break;

        if (!s->options->tool_bridge || !s->options->tool_bridge->invoke) {
            h_sb tc = {0};
            h_sb_add(&tc, "{\"tool\": ");   h_json_escape(&tc, tname);
            h_sb_add(&tc, ", \"error\": \"No tool bridge configured.\"}");
            PUSH_MSG("tool", tc.data ? tc.data : "{}");
            free(tc.data);
            free(tname); free(targs);
            continue;
        }
        char *res = NULL, *err = NULL;
        bool ok = impl_invoke_tool(s, tname, targs, &res, &err);
        h_sb tc = {0};
        if (ok) {
            h_sb_add(&tc, "{\"tool\": ");   h_json_escape(&tc, tname);
            h_sb_add(&tc, ", \"result\": "); h_sb_add(&tc, res ? res : "null");
            h_sb_addc(&tc, '}');
        } else {
            h_sb_add(&tc, "{\"tool\": ");   h_json_escape(&tc, tname);
            h_sb_add(&tc, ", \"error\": "); h_json_escape(&tc, err ? err : "");
            h_sb_addc(&tc, '}');
        }
        PUSH_MSG("tool", tc.data ? tc.data : "{}");
        free(tc.data);
        free(res); free(err);
        free(tname); free(targs);
    }

done:
    for (size_t i = 0; i < hcount; ++i) { free(hist[i].role); free(hist[i].content); }
    free(hist);
    #undef PUSH_MSG
    return last_response;
}

static void impl_submit_feedback(void *self, const ca_feedback_signal_rec_t *signal) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed || !signal) return;
    if (signal->polarity == CA_FEEDBACK_POLARITY_POSITIVE) s->positive_signals++;
    else if (signal->polarity == CA_FEEDBACK_POLARITY_NEGATIVE) s->negative_signals++;
    s->total_interactions++;
}

static void impl_prewarm(void *self) {
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)self;
    if (!s || s->disposed) return;
    if (!s->started) { impl_start(s); return; }
    /* warm-up generation */
    if (s->generator) {
        ca_chat_msg_t warm[2] = {
            { "system", s->options->system_prompt, NULL, 0 },
            { "user", ".", NULL, 0 },
        };
        ca_generation_options_t wo; ca_generation_options_init(&wo);
        wo.max_tokens = 1; wo.temperature = 0.0f;
        char *w = ca_local_chat_generator_generate(s->generator, warm, 2, &wo);
        free(w);
    }
}

static const ca_ai_service_vtable_t IMPL_VT = {
    impl_is_ready, impl_start, impl_stop, impl_ask, impl_chat,
    impl_agentic_chat, impl_stream, impl_invoke_tool, impl_submit_feedback, impl_prewarm,
};

static ca_ai_service_impl_t *impl_new(ca_ai_options_t2 *options,
                                      ca_local_chat_generator_t *generator, bool owns) {
    if (!options) return NULL;
    ca_ai_service_impl_t *s = (ca_ai_service_impl_t *)calloc(1, sizeof(*s));
    if (!s) return NULL;
    s->options = options;
    s->generator = generator;
    s->owns_generator = owns;
    s->view.vt = &IMPL_VT;
    s->view.self = s;
    return s;
}
ca_ai_service_impl_t *ca_ai_service_impl_create(ca_ai_options_t2 *options) {
    return impl_new(options, NULL, false);
}
ca_ai_service_impl_t *ca_ai_service_impl_create_with_generator(
    ca_ai_options_t2 *options, ca_local_chat_generator_t *generator) {
    if (!generator) return NULL;
    return impl_new(options, generator, true);
}
void ca_ai_service_impl_destroy(ca_ai_service_impl_t *s) {
    if (!s) return;
    s->disposed = true;
    if (s->options->pressure_source && s->pressure_token)
        ca_memory_pressure_unsubscribe(s->options->pressure_source, s->pressure_token);
    if (s->owns_generator && s->generator) ca_local_chat_generator_destroy(s->generator);
    free(s->resolved_model);
    free(s->fallback_model);
    free(s);
}
ca_ai_service_t *ca_ai_service_impl_as_service(ca_ai_service_impl_t *s) {
    return s ? &s->view : NULL;
}
void ca_ai_service_impl_set_fallback_model(ca_ai_service_impl_t *s, const char *fallback_model_id) {
    if (!s) return;
    free(s->fallback_model);
    s->fallback_model = h_strdup(fallback_model_id);
}
const char *ca_ai_service_impl_resolved_model(const ca_ai_service_impl_t *s) {
    return s ? s->resolved_model : NULL;
}
bool ca_ai_service_impl_brownout(ca_ai_service_impl_t *s, ca_brownout_reason_t reason) {
    if (!s || s->disposed || !s->started || !s->generator) return false;
    if (h_blank(s->fallback_model)) return false;
    if (s->resolved_model && strcmp(s->resolved_model, s->fallback_model) == 0) return false;

    char *from = h_strdup(s->resolved_model);

    /* swap generator to the fallback model */
    ca_local_chat_generator_t *ng =
        ca_local_chat_generator_create(s->fallback_model,
                                       s->options->context_size > 0 ? s->options->context_size : 4096);
    if (!ng) { free(from); return false; }
    if (s->owns_generator && s->generator) ca_local_chat_generator_destroy(s->generator);
    s->generator = ng; s->owns_generator = true;

    free(s->resolved_model);
    s->resolved_model = h_strdup(s->fallback_model);

    ca_ai_observer_v2_t *o = s->options->observer;
    if (o && o->on_brownout)
        o->on_brownout(o->user, from, s->resolved_model, reason);
    free(from);
    return true;
}
int ca_ai_service_impl_positive_signals(const ca_ai_service_impl_t *s) { return s ? s->positive_signals : 0; }
int ca_ai_service_impl_negative_signals(const ca_ai_service_impl_t *s) { return s ? s->negative_signals : 0; }
int ca_ai_service_impl_total_interactions(const ca_ai_service_impl_t *s) { return s ? s->total_interactions : 0; }

/* ===========================================================================
 * AIApiClient — remote proxy over an injected transport
 * =========================================================================== */

struct ca_ai_api_client {
    ca_http_transport_t transport; /* copied */
    bool                ready;
    ca_ai_service_t     view;
};

/* Extract {"text": "..."} from a JSON body. */
static char *extract_text_field(const char *body) {
    char *t = json_find_string(body, "text");
    return t ? t : h_strdup("");
}

static bool api_start(void *self) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    char *body = NULL;
    bool ok = c->transport.request(c->transport.user, "GET", "api/butler/health", NULL, &body);
    free(body);
    c->ready = ok;
    return ok;
}
static bool api_stop(void *self) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    c->ready = false;
    return true;
}
static bool api_is_ready(void *self) { return ((ca_ai_api_client_t *)self)->ready; }

static char *api_ask(void *self, const char *question) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    h_sb b = {0};
    h_sb_add(&b, "{\"question\":"); h_json_escape(&b, question ? question : ""); h_sb_addc(&b, '}');
    char *body = NULL;
    bool ok = c->transport.request(c->transport.user, "POST", "api/butler/ask", b.data, &body);
    free(b.data);
    if (!ok) { free(body); return NULL; }
    char *text = extract_text_field(body);
    free(body);
    return text;
}

/* Serialize a chat request body {"messages":[{role,content}...],"options":...}.
 * options JSON kept minimal (max_tokens/temperature/top_p) — the loopback
 * endpoint tolerates it. */
static char *serialize_chat_body(const ca_chat_msg_t *messages, size_t count,
                                 const ca_generation_options_t *opts) {
    h_sb b = {0};
    h_sb_add(&b, "{\"messages\":[");
    for (size_t i = 0; i < count; ++i) {
        if (i) h_sb_addc(&b, ',');
        h_sb_add(&b, "{\"role\":");    h_json_escape(&b, messages[i].role ? messages[i].role : "user");
        h_sb_add(&b, ",\"content\":"); h_json_escape(&b, messages[i].content ? messages[i].content : "");
        h_sb_addc(&b, '}');
    }
    h_sb_addc(&b, ']');
    if (opts) {
        char num[64];
        h_sb_add(&b, ",\"options\":{");
        snprintf(num, sizeof(num), "%d", opts->max_tokens);       h_sb_add(&b, "\"maxTokens\":"); h_sb_add(&b, num);
        snprintf(num, sizeof(num), "%g", (double)opts->temperature); h_sb_add(&b, ",\"temperature\":"); h_sb_add(&b, num);
        snprintf(num, sizeof(num), "%g", (double)opts->top_p);    h_sb_add(&b, ",\"topP\":"); h_sb_add(&b, num);
        h_sb_addc(&b, '}');
    }
    h_sb_addc(&b, '}');
    return h_sb_take(&b);
}

static char *api_chat(void *self, const ca_chat_msg_t *messages, size_t count,
                      const ca_generation_options_t *opts) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    char *body_req = serialize_chat_body(messages, count, opts);
    char *body = NULL;
    bool ok = c->transport.request(c->transport.user, "POST", "api/butler/chat", body_req, &body);
    free(body_req);
    if (!ok) { free(body); return NULL; }
    char *text = extract_text_field(body);
    free(body);
    return text;
}

static char *api_agentic(void *self, const char *prompt, const ca_generation_options_t *opts) {
    (void)opts;
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    h_sb b = {0};
    h_sb_add(&b, "{\"prompt\":"); h_json_escape(&b, prompt ? prompt : ""); h_sb_addc(&b, '}');
    char *body = NULL;
    bool ok = c->transport.request(c->transport.user, "POST", "api/butler/agentic", b.data, &body);
    free(b.data);
    if (!ok) { free(body); return NULL; }
    char *text = extract_text_field(body);
    free(body);
    return text;
}

static long api_stream(void *self, const ca_chat_msg_t *messages, size_t count,
                       const ca_generation_options_t *opts,
                       ca_ai_stream_piece_fn on_piece, void *piece_user) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    if (!c->transport.stream) return -1;
    char *body_req = serialize_chat_body(messages, count, opts);
    long r = c->transport.stream(c->transport.user, "api/butler/stream", body_req, on_piece, piece_user);
    free(body_req);
    return r;
}

static bool api_invoke_tool(void *self, const char *tool_name, const char *arguments_json,
                            char **out_result_json, char **out_error) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    if (!out_result_json || !out_error) return false;
    *out_result_json = NULL; *out_error = NULL;
    h_sb b = {0};
    h_sb_add(&b, "{\"name\":");        h_json_escape(&b, tool_name ? tool_name : "");
    h_sb_add(&b, ",\"arguments\":");   h_sb_add(&b, (arguments_json && arguments_json[0]) ? arguments_json : "{}");
    h_sb_addc(&b, '}');
    char *body = NULL;
    bool ok = c->transport.request(c->transport.user, "POST", "api/butler/tool", b.data, &body);
    free(b.data);
    if (!ok || !body) {
        free(body);
        *out_error = h_strdup("Empty response from cloud");
        return false;
    }
    /* body is a ToolResult JSON; extract success + result/error */
    bool success = strstr(body, "\"success\":true") != NULL || strstr(body, "\"Success\":true") != NULL;
    if (success) {
        char *r = json_find_object(body, "result");
        if (!r) r = json_find_string(body, "result");
        *out_result_json = r ? r : h_strdup("null");
    } else {
        char *e = json_find_string(body, "error");
        *out_error = e ? e : h_strdup("tool failed");
    }
    free(body);
    return success;
}

static void api_submit_feedback(void *self, const ca_feedback_signal_rec_t *signal) {
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)self;
    if (!signal) return;
    h_sb b = {0};
    h_sb_add(&b, "{\"id\":");            h_json_escape(&b, signal->id ? signal->id : "");
    { char num[32]; snprintf(num, sizeof(num), "%d", (int)signal->polarity);
      h_sb_add(&b, ",\"polarity\":"); h_sb_add(&b, num); }
    h_sb_add(&b, ",\"userText\":");      h_json_escape(&b, signal->user_text ? signal->user_text : "");
    h_sb_add(&b, ",\"assistantText\":"); h_json_escape(&b, signal->assistant_text ? signal->assistant_text : "");
    h_sb_addc(&b, '}');
    char *body = NULL;
    c->transport.request(c->transport.user, "POST", "api/butler/feedback", b.data, &body);
    free(b.data); free(body);
}

static void api_prewarm(void *self) { api_start(self); }

static const ca_ai_service_vtable_t API_VT = {
    api_is_ready, api_start, api_stop, api_ask, api_chat,
    api_agentic, api_stream, api_invoke_tool, api_submit_feedback, api_prewarm,
};

ca_ai_api_client_t *ca_ai_api_client_create(const ca_http_transport_t *transport) {
    if (!transport || !transport->request) return NULL;
    ca_ai_api_client_t *c = (ca_ai_api_client_t *)calloc(1, sizeof(*c));
    if (!c) return NULL;
    c->transport = *transport;
    c->view.vt = &API_VT; c->view.self = c;
    return c;
}
void ca_ai_api_client_destroy(ca_ai_api_client_t *c) { free(c); }
ca_ai_service_t *ca_ai_api_client_as_service(ca_ai_api_client_t *c) { return c ? &c->view : NULL; }

/* ===========================================================================
 * IAIEndpoint + InProcessEndpoint + HttpLoopbackEndpoint
 * =========================================================================== */

typedef enum { EP_INPROCESS, EP_LOOPBACK } h_ep_kind;

struct ca_ai_endpoint {
    h_ep_kind        kind;
    ca_ai_service_t *service;   /* borrowed */
    bool             started;
    bool             disposed;
    /* loopback */
    char            *token;
    int              bound_port;
};

/* --- InProcess --- */
ca_ai_endpoint_t *ca_inprocess_endpoint_create(void) {
    ca_ai_endpoint_t *e = (ca_ai_endpoint_t *)calloc(1, sizeof(*e));
    if (e) e->kind = EP_INPROCESS;
    return e;
}
bool ca_ai_endpoint_start(ca_ai_endpoint_t *e, ca_ai_service_t *service) {
    if (!e || e->disposed) return false;
    if (e->started) return true;
    if (!service) return false;
    e->service = service;
    e->started = true;
    if (e->kind == EP_LOOPBACK && !e->token)
        e->token = h_strdup("butler-token");
    return true;
}
bool ca_ai_endpoint_stop(ca_ai_endpoint_t *e) {
    if (!e) return true;
    e->started = false;
    e->service = NULL;
    return true;
}
void ca_ai_endpoint_destroy(ca_ai_endpoint_t *e) {
    if (!e) return;
    e->disposed = true;
    free(e->token);
    free(e);
}
ca_ai_service_t *ca_inprocess_endpoint_service(ca_ai_endpoint_t *e) {
    return (e && e->kind == EP_INPROCESS) ? e->service : NULL;
}

/* --- HttpLoopback --- */
ca_ai_endpoint_t *ca_http_loopback_endpoint_create(const char *token, int bound_port) {
    ca_ai_endpoint_t *e = (ca_ai_endpoint_t *)calloc(1, sizeof(*e));
    if (!e) return NULL;
    e->kind = EP_LOOPBACK;
    e->token = h_blank(token) ? h_strdup("butler-token") : h_strdup(token);
    e->bound_port = bound_port > 0 ? bound_port : 5199; /* deterministic "picked" port */
    return e;
}
const char *ca_http_loopback_endpoint_token(const ca_ai_endpoint_t *e) {
    return (e && e->kind == EP_LOOPBACK) ? e->token : NULL;
}
int ca_http_loopback_endpoint_port(const ca_ai_endpoint_t *e) {
    return (e && e->kind == EP_LOOPBACK) ? e->bound_port : 0;
}

/* constant-time compare (HttpLoopbackEndpoint.CryptographicEquals) */
static bool ct_equals(const char *a, const char *b) {
    if (!a || !b) return false;
    size_t la = strlen(a), lb = strlen(b);
    if (la != lb) return false;
    int diff = 0;
    for (size_t i = 0; i < la; ++i) diff |= (unsigned char)a[i] ^ (unsigned char)b[i];
    return diff == 0;
}

/* Parse a chat body ("messages" array of {role,content}) into owned arrays. */
typedef struct { char **roles; char **contents; size_t count; } h_msgs;
static void h_msgs_free(h_msgs *m) {
    for (size_t i = 0; i < m->count; ++i) { free(m->roles[i]); free(m->contents[i]); }
    free(m->roles); free(m->contents);
    m->roles = m->contents = NULL; m->count = 0;
}
/* Extremely small parser: walks objects inside "messages":[...] pulling
 * "role"/"content" string values. Adequate for our own serializer's output. */
static bool parse_chat_messages(const char *body, h_msgs *out) {
    memset(out, 0, sizeof(*out));
    if (!body) return false;
    const char *arr = strstr(body, "\"messages\"");
    if (!arr) return false;
    const char *lb = strchr(arr, '[');
    if (!lb) return false;
    const char *p = lb + 1;
    size_t cap = 0;
    while (*p) {
        while (*p && *p != '{' && *p != ']') p++;
        if (*p != '{') break;
        /* find matching close */
        const char *obj = p;
        int depth = 0; bool instr = false;
        for (; *p; p++) {
            if (instr) { if (*p == '\\' && p[1]) { p++; continue; } if (*p == '"') instr = false; }
            else { if (*p == '"') instr = true; else if (*p == '{') depth++; else if (*p == '}') { depth--; if (depth == 0) { p++; break; } } }
        }
        size_t olen = (size_t)(p - obj);
        char *objs = (char *)malloc(olen + 1);
        if (!objs) break;
        memcpy(objs, obj, olen); objs[olen] = '\0';
        char *role = json_find_string(objs, "role");
        char *content = json_find_string(objs, "content");
        free(objs);
        if (out->count == cap) {
            size_t nc = cap ? cap * 2 : 4;
            void *nr = realloc(out->roles, nc * sizeof(char *));
            void *nco = realloc(out->contents, nc * sizeof(char *));
            if (!nr || !nco) { free(nr ? nr : out->roles); free(role); free(content); break; }
            out->roles = (char **)nr; out->contents = (char **)nco; cap = nc;
        }
        out->roles[out->count] = role ? role : h_strdup("user");
        out->contents[out->count] = content ? content : h_strdup("");
        out->count++;
        while (*p && (*p == ',' || isspace((unsigned char)*p))) p++;
        if (*p == ']') break;
    }
    return out->count > 0;
}

bool ca_http_loopback_endpoint_dispatch(ca_ai_endpoint_t *e,
                                        const char *token, const char *method,
                                        const char *path, const char *body_json,
                                        int *out_status, char **out_body) {
    if (!e || e->kind != EP_LOOPBACK || !out_status || !out_body) return false;
    *out_body = NULL; *out_status = 0;

    if (!ct_equals(token, e->token)) { *out_status = 401; *out_body = h_strdup("unauthorised"); return true; }
    if (!method || strcmp(method, "POST") != 0) { *out_status = 405; *out_body = h_strdup("method not allowed"); return true; }
    if (!e->service) { *out_status = 500; *out_body = h_strdup("internal error"); return true; }

    if (path && strcmp(path, "/butler/ask") == 0) {
        char *q = json_find_string(body_json, "question");
        if (h_blank(q)) { free(q); q = json_find_string(body_json, "prompt"); } /* agentic maps here */
        if (h_blank(q)) { free(q); *out_status = 400; *out_body = h_strdup("missing 'question'"); return true; }
        char *ans = ca_ai_service_ask(e->service, q);
        free(q);
        *out_status = 200; *out_body = ans ? ans : h_strdup("");
        return true;
    }
    if (path && strcmp(path, "/butler/chat") == 0) {
        h_msgs m;
        if (!parse_chat_messages(body_json, &m)) { h_msgs_free(&m); *out_status = 400; *out_body = h_strdup("missing 'messages'"); return true; }
        ca_chat_msg_t *cm = (ca_chat_msg_t *)calloc(m.count, sizeof(ca_chat_msg_t));
        for (size_t i = 0; i < m.count; ++i) { cm[i].role = m.roles[i]; cm[i].content = m.contents[i]; }
        char *content = ca_ai_service_chat(e->service, cm, m.count, NULL);
        free(cm);
        h_msgs_free(&m);
        h_sb b = {0};
        h_sb_add(&b, "{\"content\":"); h_json_escape(&b, content ? content : ""); h_sb_addc(&b, '}');
        free(content);
        *out_status = 200; *out_body = h_sb_take(&b);
        return true;
    }
    if (path && strcmp(path, "/butler/tool") == 0) {
        char *tn = json_find_string(body_json, "toolName");
        if (!tn) tn = json_find_string(body_json, "name");
        if (h_blank(tn)) { free(tn); *out_status = 400; *out_body = h_strdup("missing 'toolName'"); return true; }
        char *args = json_find_object(body_json, "arguments");
        char *res = NULL, *err = NULL;
        bool ok = ca_ai_service_invoke_tool(e->service, tn, args ? args : "{}", &res, &err);
        h_sb b = {0};
        h_sb_add(&b, "{\"toolName\":");    h_json_escape(&b, tn);
        h_sb_add(&b, ",\"success\":");     h_sb_add(&b, ok ? "true" : "false");
        if (ok) { h_sb_add(&b, ",\"result\":"); h_sb_add(&b, res ? res : "null"); }
        else    { h_sb_add(&b, ",\"error\":");  h_json_escape(&b, err ? err : ""); }
        h_sb_addc(&b, '}');
        free(tn); free(args); free(res); free(err);
        *out_status = ok ? 200 : 502; *out_body = h_sb_take(&b);
        return true;
    }
    if (path && strcmp(path, "/butler/stream") == 0) {
        /* streaming should go through dispatch_stream; here return 400 hint */
        *out_status = 400; *out_body = h_strdup("use stream dispatch"); return true;
    }
    *out_status = 404; *out_body = h_strdup("not found");
    return true;
}

long ca_http_loopback_endpoint_dispatch_stream(ca_ai_endpoint_t *e,
                                              const char *token, const char *method,
                                              const char *path, const char *body_json,
                                              ca_ai_stream_piece_fn on_piece,
                                              void *piece_user, int *out_status) {
    if (!e || e->kind != EP_LOOPBACK || !out_status) return -1;
    *out_status = 0;
    if (!ct_equals(token, e->token)) { *out_status = 401; return -1; }
    if (!method || strcmp(method, "POST") != 0) { *out_status = 405; return -1; }
    if (!e->service) { *out_status = 500; return -1; }
    if (!path || strcmp(path, "/butler/stream") != 0) { *out_status = 404; return -1; }

    h_msgs m;
    if (!parse_chat_messages(body_json, &m)) { h_msgs_free(&m); *out_status = 400; return -1; }
    ca_chat_msg_t *cm = (ca_chat_msg_t *)calloc(m.count, sizeof(ca_chat_msg_t));
    for (size_t i = 0; i < m.count; ++i) { cm[i].role = m.roles[i]; cm[i].content = m.contents[i]; }
    long r = ca_ai_service_stream(e->service, cm, m.count, NULL, on_piece, piece_user);
    free(cm);
    h_msgs_free(&m);
    *out_status = 200;
    return r;
}

/* --- loopback transport adapter (client <-> endpoint) --- */

static bool loopback_request(void *user, const char *method, const char *path,
                             const char *body_json, char **out_body) {
    ca_ai_endpoint_t *e = (ca_ai_endpoint_t *)user;
    /* Map client 'api/butler/...' paths to endpoint '/butler/...' routes. A GET
     * health check is a special-case OK. */
    if (strcmp(method, "GET") == 0 && strstr(path, "health")) {
        *out_body = h_strdup("ok");
        return true;
    }
    /* The loopback endpoint speaks the loopback wire (ask -> plain text,
     * chat -> {"content":...}); the AIApiClient expects the ButlerAPI wire
     * ({"text":...}). Adapt the response shapes here. */
    bool is_ask = strstr(path, "/ask") != NULL;
    bool is_chat = strstr(path, "/chat") != NULL;
    const char *route = "/butler/unknown";
    if (is_ask)                        route = "/butler/ask";
    else if (is_chat)                  route = "/butler/chat";
    else if (strstr(path, "/tool"))    route = "/butler/tool";
    else if (strstr(path, "/agentic")) route = "/butler/ask"; /* endpoint has no agentic route -> map to ask */
    int status = 0;
    char *body = NULL;
    ca_http_loopback_endpoint_dispatch(e, e->token, "POST", route, body_json, &status, &body);
    if (status == 200 || status == 502) {
        if (is_ask && status == 200) {
            /* wrap plain-text answer as {"text":"..."} */
            h_sb b = {0};
            h_sb_add(&b, "{\"text\":"); h_json_escape(&b, body ? body : ""); h_sb_addc(&b, '}');
            free(body);
            *out_body = h_sb_take(&b);
            return true;
        }
        if (is_chat && status == 200) {
            /* re-key {"content":...} to {"text":...} */
            char *content = json_find_string(body, "content");
            h_sb b = {0};
            h_sb_add(&b, "{\"text\":"); h_json_escape(&b, content ? content : ""); h_sb_addc(&b, '}');
            free(content); free(body);
            *out_body = h_sb_take(&b);
            return true;
        }
        *out_body = body ? body : h_strdup("");
        return status == 200;
    }
    free(body);
    *out_body = NULL;
    return false;
}
static long loopback_stream(void *user, const char *path, const char *body_json,
                            ca_ai_stream_piece_fn on_piece, void *piece_user) {
    (void)path;
    ca_ai_endpoint_t *e = (ca_ai_endpoint_t *)user;
    int status = 0;
    return ca_http_loopback_endpoint_dispatch_stream(e, e->token, "POST", "/butler/stream",
                                                     body_json, on_piece, piece_user, &status);
}

bool ca_http_loopback_transport(ca_ai_endpoint_t *endpoint, ca_http_transport_t *out) {
    if (!endpoint || endpoint->kind != EP_LOOPBACK || !out) return false;
    out->request = loopback_request;
    out->stream = loopback_stream;
    out->user = endpoint;
    return true;
}

/* ===========================================================================
 * FallbackAIService
 * =========================================================================== */

struct ca_fallback_ai_service {
    ca_ai_service_t *local;   /* borrowed */
    ca_ai_service_t *cloud;   /* borrowed */
    int64_t          ram_threshold;
    ca_ram_probe_fn  ram_probe;
    void            *ram_probe_user;
    ca_ai_service_t *active;   /* borrowed (== local or cloud) */
    bool             using_cloud;
    ca_ai_service_t  view;
};

static bool fb_start(void *self) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    int64_t ram = f->ram_probe ? f->ram_probe(f->ram_probe_user) : 0;
    if (ram >= f->ram_threshold) {
        if (ca_ai_service_start(f->local)) { f->active = f->local; f->using_cloud = false; return true; }
        /* local failed -> fall through to cloud */
    }
    bool ok = ca_ai_service_start(f->cloud);
    f->active = f->cloud; f->using_cloud = true;
    return ok;
}
static bool fb_stop(void *self) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_stop(f->active) : true;
}
static bool fb_is_ready(void *self) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_is_ready(f->active) : false;
}
static char *fb_ask(void *self, const char *q) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_ask(f->active, q) : NULL;
}
static char *fb_chat(void *self, const ca_chat_msg_t *m, size_t n, const ca_generation_options_t *o) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_chat(f->active, m, n, o) : NULL;
}
static char *fb_agentic(void *self, const char *p, const ca_generation_options_t *o) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_agentic_chat(f->active, p, o) : NULL;
}
static long fb_stream(void *self, const ca_chat_msg_t *m, size_t n, const ca_generation_options_t *o,
                      ca_ai_stream_piece_fn cb, void *u) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_stream(f->active, m, n, o, cb, u) : -1;
}
static bool fb_invoke_tool(void *self, const char *tn, const char *aj, char **or_, char **oe) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    return f->active ? ca_ai_service_invoke_tool(f->active, tn, aj, or_, oe) : false;
}
static void fb_feedback(void *self, const ca_feedback_signal_rec_t *s) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    if (f->active) ca_ai_service_submit_feedback(f->active, s);
}
static void fb_prewarm(void *self) {
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)self;
    if (f->active) ca_ai_service_prewarm(f->active);
}
static const ca_ai_service_vtable_t FB_VT = {
    fb_is_ready, fb_start, fb_stop, fb_ask, fb_chat,
    fb_agentic, fb_stream, fb_invoke_tool, fb_feedback, fb_prewarm,
};

ca_fallback_ai_service_t *ca_fallback_ai_service_create(
    ca_ai_service_t *local, ca_ai_service_t *cloud,
    int64_t ram_threshold_bytes, ca_ram_probe_fn ram_probe, void *ram_probe_user) {
    if (!local || !cloud) return NULL;
    ca_fallback_ai_service_t *f = (ca_fallback_ai_service_t *)calloc(1, sizeof(*f));
    if (!f) return NULL;
    f->local = local; f->cloud = cloud;
    f->ram_threshold = ram_threshold_bytes > 0 ? ram_threshold_bytes : (2LL * 1024 * 1024 * 1024);
    f->ram_probe = ram_probe; f->ram_probe_user = ram_probe_user;
    f->view.vt = &FB_VT; f->view.self = f;
    return f;
}
void ca_fallback_ai_service_destroy(ca_fallback_ai_service_t *f) { free(f); }
ca_ai_service_t *ca_fallback_ai_service_as_service(ca_fallback_ai_service_t *f) { return f ? &f->view : NULL; }
bool ca_fallback_ai_service_using_cloud(const ca_fallback_ai_service_t *f) { return f ? f->using_cloud : false; }
