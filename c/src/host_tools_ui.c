/*
 * host_tools_ui.c — CircleAI.Hosting.Tools + .GenerativeUI (C11 port).
 * See host_tools_ui.h.
 *
 * InMemoryToolCatalog mirrors the C# keyword-substring scorer + ordering.
 * JsonRenderParser is a small recursive-descent parser validating against a
 * UiCatalog (strict-mode rejects unknown kinds / undeclared properties /
 * disallowed children; lenient-mode maps unknown kinds to a textBlock).
 *
 * Pure C11 + libc.
 */

#include "circle_ai/host_tools_ui.h"

#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <ctype.h>

/* ── helpers ──────────────────────────────────────────────────────────── */

static char *t_strdup(const char *s) {
    if (!s) return NULL;
    size_t n = strlen(s) + 1;
    char *p = (char *)malloc(n);
    if (p) memcpy(p, s, n);
    return p;
}
static bool t_blank(const char *s) {
    if (!s) return true;
    for (const unsigned char *p = (const unsigned char *)s; *p; ++p)
        if (!isspace(*p)) return false;
    return true;
}
static int t_icmp(const char *a, const char *b) {
    if (!a) a = "";
    if (!b) b = "";
    while (*a && *b) {
        int ca = tolower((unsigned char)*a), cb = tolower((unsigned char)*b);
        if (ca != cb) return ca - cb;
        a++; b++;
    }
    return (unsigned char)tolower((unsigned char)*a) - (unsigned char)tolower((unsigned char)*b);
}
static bool contains_ci(const char *hay, const char *needle) {
    if (!hay || !needle || !*needle) return false;
    size_t nl = strlen(needle);
    for (const char *p = hay; *p; ++p) {
        size_t i = 0;
        while (i < nl && p[i] && tolower((unsigned char)p[i]) == tolower((unsigned char)needle[i])) i++;
        if (i == nl) return true;
    }
    return false;
}

typedef struct { char *data; size_t len, cap; } sb;
static void sb_reserve(sb *b, size_t extra) {
    if (b->len + extra + 1 <= b->cap) return;
    size_t nc = b->cap ? b->cap : 64;
    while (nc < b->len + extra + 1) nc *= 2;
    char *n = (char *)realloc(b->data, nc);
    if (n) { b->data = n; b->cap = nc; }
}
static void sb_add(sb *b, const char *s) { if (!s) return; size_t n = strlen(s); sb_reserve(b, n); memcpy(b->data + b->len, s, n); b->len += n; b->data[b->len] = 0; }
static void sb_addc(sb *b, char c) { sb_reserve(b, 1); b->data[b->len++] = c; b->data[b->len] = 0; }
static char *sb_take(sb *b) { return b->data ? b->data : t_strdup(""); }

/* ===========================================================================
 * ToolDescriptor
 * =========================================================================== */

static char **dup_str_array(char *const *src, size_t n) {
    if (n == 0) return NULL;
    char **a = (char **)calloc(n, sizeof(char *));
    if (!a) return NULL;
    for (size_t i = 0; i < n; ++i) a[i] = t_strdup(src[i]);
    return a;
}
static void free_str_array(char **a, size_t n) {
    if (!a) return;
    for (size_t i = 0; i < n; ++i) free(a[i]);
    free(a);
}

void ca_tool_descriptor_free(ca_tool_descriptor_t *d) {
    if (!d) return;
    free(d->name); free(d->description); free(d->provider);
    free(d->json_schema); free(d->auth_scheme);
    free_str_array(d->tags, d->tag_count);
    free_str_array(d->examples, d->example_count);
    memset(d, 0, sizeof(*d));
}
void ca_tool_descriptor_free_array(ca_tool_descriptor_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_tool_descriptor_free(&arr[i]);
    free(arr);
}
ca_tool_descriptor_t *ca_tool_descriptor_copy(ca_tool_descriptor_t *dst,
                                              const ca_tool_descriptor_t *src) {
    if (!dst || !src) return dst;
    dst->name        = t_strdup(src->name);
    dst->description = t_strdup(src->description);
    dst->provider    = t_strdup(src->provider);
    dst->json_schema = t_strdup(src->json_schema ? src->json_schema : "");
    dst->auth_scheme = t_strdup(src->auth_scheme ? src->auth_scheme : "none");
    dst->tags = dup_str_array(src->tags, src->tag_count); dst->tag_count = src->tag_count;
    dst->examples = dup_str_array(src->examples, src->example_count); dst->example_count = src->example_count;
    return dst;
}

void ca_tool_execution_result_free(ca_tool_execution_result_t *r) {
    if (!r) return;
    free(r->result); free(r->error);
    r->result = r->error = NULL;
}

/* ===========================================================================
 * InMemoryToolCatalog
 * =========================================================================== */

struct ca_tool_catalog {
    ca_tool_descriptor_t *items;
    size_t                count, cap;
};

ca_tool_catalog_t *ca_tool_catalog_create(void) {
    return (ca_tool_catalog_t *)calloc(1, sizeof(ca_tool_catalog_t));
}
void ca_tool_catalog_destroy(ca_tool_catalog_t *c) {
    if (!c) return;
    for (size_t i = 0; i < c->count; ++i) ca_tool_descriptor_free(&c->items[i]);
    free(c->items);
    free(c);
}
int ca_tool_catalog_count(const ca_tool_catalog_t *c) { return c ? (int)c->count : 0; }

static ca_tool_descriptor_t *cat_find(ca_tool_catalog_t *c, const char *name) {
    for (size_t i = 0; i < c->count; ++i)
        if (t_icmp(c->items[i].name, name) == 0) return &c->items[i];
    return NULL;
}
bool ca_tool_catalog_upsert(ca_tool_catalog_t *c, const ca_tool_descriptor_t *d) {
    if (!c || !d || t_blank(d->name)) return false;
    ca_tool_descriptor_t *existing = cat_find(c, d->name);
    if (existing) {
        ca_tool_descriptor_t copy; memset(&copy, 0, sizeof(copy));
        ca_tool_descriptor_copy(&copy, d);
        ca_tool_descriptor_free(existing);
        *existing = copy;
        return true;
    }
    if (c->count == c->cap) {
        size_t nc = c->cap ? c->cap * 2 : 8;
        void *n = realloc(c->items, nc * sizeof(*c->items));
        if (!n) return false;
        c->items = (ca_tool_descriptor_t *)n; c->cap = nc;
    }
    ca_tool_descriptor_copy(&c->items[c->count], d);
    c->count++;
    return true;
}
bool ca_tool_catalog_remove(ca_tool_catalog_t *c, const char *name) {
    if (!c || t_blank(name)) return false;
    for (size_t i = 0; i < c->count; ++i)
        if (t_icmp(c->items[i].name, name) == 0) {
            ca_tool_descriptor_free(&c->items[i]);
            memmove(&c->items[i], &c->items[i + 1], (c->count - i - 1) * sizeof(*c->items));
            c->count--;
            return true;
        }
    return false;
}
bool ca_tool_catalog_get(ca_tool_catalog_t *c, const char *name, ca_tool_descriptor_t *out) {
    if (!c || t_blank(name) || !out) return false;
    ca_tool_descriptor_t *d = cat_find(c, name);
    if (!d) return false;
    ca_tool_descriptor_copy(out, d);
    return true;
}

/* qsort comparator by name (ordinal-ignore-case) */
static int cmp_by_name(const void *a, const void *b) {
    const ca_tool_descriptor_t *x = (const ca_tool_descriptor_t *)a;
    const ca_tool_descriptor_t *y = (const ca_tool_descriptor_t *)b;
    return t_icmp(x->name, y->name);
}

ca_tool_descriptor_t *ca_tool_catalog_list(ca_tool_catalog_t *c, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!c || c->count == 0) return NULL;
    ca_tool_descriptor_t *res = (ca_tool_descriptor_t *)calloc(c->count, sizeof(*res));
    if (!res) return NULL;
    for (size_t i = 0; i < c->count; ++i) ca_tool_descriptor_copy(&res[i], &c->items[i]);
    qsort(res, c->count, sizeof(*res), cmp_by_name);
    if (out_count) *out_count = c->count;
    return res;
}

static int score_match(const ca_tool_descriptor_t *d, char *const *terms, size_t nterms) {
    const char *name = d->name ? d->name : "";
    const char *desc = d->description ? d->description : "";
    /* tag blob */
    sb blob = {0};
    for (size_t i = 0; i < d->tag_count; ++i) { if (i) sb_addc(&blob, ' '); sb_add(&blob, d->tags[i]); }
    const char *tagblob = blob.data ? blob.data : "";
    int score = 0;
    for (size_t i = 0; i < nterms; ++i) {
        if (contains_ci(name, terms[i])) score += 5;
        if (contains_ci(desc, terms[i])) score += 2;
        if (contains_ci(tagblob, terms[i])) score += 3;
    }
    free(blob.data);
    return score;
}

ca_tool_descriptor_t *ca_tool_catalog_search(ca_tool_catalog_t *c, const char *query,
                                             int top_k, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!c || t_blank(query) || top_k <= 0) return NULL;

    /* split query into terms */
    char *qcopy = t_strdup(query);
    char **terms = NULL; size_t nterms = 0, tcap = 0;
    char *save = NULL;
    for (char *tok = strtok_r(qcopy, " \t\r\n", &save); tok; tok = strtok_r(NULL, " \t\r\n", &save)) {
        if (nterms == tcap) { tcap = tcap ? tcap * 2 : 4; terms = (char **)realloc(terms, tcap * sizeof(char *)); }
        terms[nterms++] = tok;
    }

    /* score all, keep >0 */
    typedef struct { size_t idx; int score; } scored;
    scored *sc = (scored *)calloc(c->count, sizeof(scored));
    size_t nsc = 0;
    for (size_t i = 0; i < c->count; ++i) {
        int s = score_match(&c->items[i], terms, nterms);
        if (s > 0) { sc[nsc].idx = i; sc[nsc].score = s; nsc++; }
    }
    /* sort: score desc, then name asc (stable-ish via insertion) */
    for (size_t i = 1; i < nsc; ++i) {
        scored key = sc[i]; size_t j = i;
        while (j > 0) {
            bool greater;
            if (sc[j - 1].score != key.score) greater = sc[j - 1].score < key.score;
            else greater = t_icmp(c->items[sc[j - 1].idx].name, c->items[key.idx].name) > 0;
            if (!greater) break;
            sc[j] = sc[j - 1]; j--;
        }
        sc[j] = key;
    }
    size_t take = nsc < (size_t)top_k ? nsc : (size_t)top_k;
    ca_tool_descriptor_t *res = take ? (ca_tool_descriptor_t *)calloc(take, sizeof(*res)) : NULL;
    for (size_t i = 0; i < take; ++i) ca_tool_descriptor_copy(&res[i], &c->items[sc[i].idx]);

    free(sc); free(terms); free(qcopy);
    if (out_count) *out_count = take;
    return res;
}

ca_tool_descriptor_t *ca_tool_catalog_list_by_provider(ca_tool_catalog_t *c,
                                                       const char *provider, size_t *out_count) {
    if (out_count) *out_count = 0;
    if (!c || t_blank(provider)) return NULL;
    size_t n = 0;
    for (size_t i = 0; i < c->count; ++i)
        if (t_icmp(c->items[i].provider, provider) == 0) n++;
    if (n == 0) return NULL;
    ca_tool_descriptor_t *res = (ca_tool_descriptor_t *)calloc(n, sizeof(*res));
    size_t k = 0;
    for (size_t i = 0; i < c->count; ++i)
        if (t_icmp(c->items[i].provider, provider) == 0)
            ca_tool_descriptor_copy(&res[k++], &c->items[i]);
    qsort(res, n, sizeof(*res), cmp_by_name);
    if (out_count) *out_count = n;
    return res;
}

int ca_tool_catalog_import_from(ca_tool_catalog_t *c, const ca_tool_provider_t *provider) {
    if (!c || !provider || !provider->discover) return 0;
    size_t n = 0;
    ca_tool_descriptor_t *tools = provider->discover(provider->user, &n);
    int count = 0;
    for (size_t i = 0; i < n; ++i)
        if (ca_tool_catalog_upsert(c, &tools[i])) count++;
    ca_tool_descriptor_free_array(tools, n);
    return count;
}

void ca_tool_executor_execute(const ca_tool_executor_t *ex, const ca_tool_descriptor_t *tool,
                              const char *arguments_json, ca_tool_execution_result_t *out) {
    if (!out) return;
    memset(out, 0, sizeof(*out));
    if (!ex || !ex->execute) { out->success = false; out->error = t_strdup("No executor configured."); return; }
    ex->execute(ex->user, tool, arguments_json, out);
}

/* ===========================================================================
 * Generative UI
 * =========================================================================== */

/* Free a component's owned internals (kind, properties, children array +
 * recurse), but NOT the struct itself. Children are stored inline. */
static void ui_component_free_internals(ca_ui_component_t *c) {
    if (!c) return;
    free(c->kind);
    for (size_t i = 0; i < c->property_count; ++i) {
        free(c->properties[i].key);
        free(c->properties[i].s);
    }
    free(c->properties);
    for (size_t i = 0; i < c->child_count; ++i)
        ui_component_free_internals(&c->children[i]);
    free(c->children);
    memset(c, 0, sizeof(*c));
}

void ca_ui_component_free(ca_ui_component_t *c) {
    if (!c) return;
    ui_component_free_internals(c);
    free(c);
}

/* Default catalog (UiCatalogs.Default). */
static const ca_ui_allowed_prop_t CARD_PROPS[]  = { {"title","string"}, {"caption","string?"} };
static const ca_ui_allowed_prop_t LIST_PROPS[]  = { {"ordered","boolean"} };
static const ca_ui_allowed_prop_t BTN_PROPS[]   = { {"label","string"}, {"action","string"}, {"style","string?"} };
static const ca_ui_allowed_prop_t TEXT_PROPS[]  = { {"text","string"}, {"markdown","boolean?"} };
static const ca_ui_allowed_prop_t IMG_PROPS[]   = { {"src","string"}, {"alt","string?"} };

static const ca_ui_catalog_entry_t DEFAULT_CATALOG[] = {
    { "card", "A bordered container with a title and body. May contain children.", CARD_PROPS, 2, true },
    { "list", "An ordered or unordered list. Children are the list items.", LIST_PROPS, 1, true },
    { "button", "A tappable button. Emit an action identifier when clicked.", BTN_PROPS, 3, false },
    { "textBlock", "Inline text content, optionally markdown.", TEXT_PROPS, 2, false },
    { "image", "An image displayed from a URL or data-URI.", IMG_PROPS, 2, false },
};

const ca_ui_catalog_entry_t *ca_ui_catalog_default(size_t *out_count) {
    if (out_count) *out_count = sizeof(DEFAULT_CATALOG) / sizeof(DEFAULT_CATALOG[0]);
    return DEFAULT_CATALOG;
}

/* --- minimal JSON parser (objects/arrays/strings/numbers/bool/null) --- */

typedef struct { const char *p; bool err; } jp;

static void jp_ws(jp *j) { while (*j->p && isspace((unsigned char)*j->p)) j->p++; }

/* parse a JSON string into a malloc'd unescaped C string; advances past it. */
static char *jp_string(jp *j) {
    jp_ws(j);
    if (*j->p != '"') { j->err = true; return NULL; }
    j->p++;
    sb out = {0};
    while (*j->p && *j->p != '"') {
        if (*j->p == '\\' && j->p[1]) {
            j->p++;
            switch (*j->p) {
                case 'n': sb_addc(&out, '\n'); break;
                case 't': sb_addc(&out, '\t'); break;
                case 'r': sb_addc(&out, '\r'); break;
                case 'b': sb_addc(&out, '\b'); break;
                case 'f': sb_addc(&out, '\f'); break;
                case '/': sb_addc(&out, '/'); break;
                case '"': sb_addc(&out, '"'); break;
                case '\\': sb_addc(&out, '\\'); break;
                case 'u': {
                    /* minimal \uXXXX -> keep ASCII, drop others */
                    char hex[5] = {0};
                    for (int k = 0; k < 4 && j->p[1]; ++k) hex[k] = *++j->p;
                    long cp = strtol(hex, NULL, 16);
                    if (cp < 0x80) sb_addc(&out, (char)cp);
                    break;
                }
                default: sb_addc(&out, *j->p); break;
            }
            j->p++;
        } else {
            sb_addc(&out, *j->p++);
        }
    }
    if (*j->p != '"') { free(out.data); j->err = true; return NULL; }
    j->p++;
    return sb_take(&out);
}

/* skip any JSON value (for values we don't materialise into props deeply). */
static void jp_skip_value(jp *j);
static void jp_skip_container(jp *j, char open, char close) {
    j->p++; /* open */
    int depth = 1; bool instr = false;
    while (*j->p && depth > 0) {
        if (instr) { if (*j->p == '\\' && j->p[1]) { j->p += 2; continue; } if (*j->p == '"') instr = false; j->p++; }
        else { if (*j->p == '"') instr = true; else if (*j->p == open) depth++; else if (*j->p == close) depth--; j->p++; }
    }
    (void)close;
}
static void jp_skip_value(jp *j) {
    jp_ws(j);
    if (*j->p == '"') { char *s = jp_string(j); free(s); }
    else if (*j->p == '{') jp_skip_container(j, '{', '}');
    else if (*j->p == '[') jp_skip_container(j, '[', ']');
    else { while (*j->p && *j->p != ',' && *j->p != '}' && *j->p != ']') j->p++; }
}

/* set a property value from the current JSON scalar (ToManaged mapping). */
static void jp_read_prop_value(jp *j, ca_ui_property_t *prop) {
    jp_ws(j);
    if (*j->p == '"') {
        prop->kind = CA_UI_VAL_STRING;
        prop->s = jp_string(j);
    } else if (*j->p == 't' && strncmp(j->p, "true", 4) == 0) {
        prop->kind = CA_UI_VAL_BOOL; prop->b = true; j->p += 4;
    } else if (*j->p == 'f' && strncmp(j->p, "false", 5) == 0) {
        prop->kind = CA_UI_VAL_BOOL; prop->b = false; j->p += 5;
    } else if (*j->p == 'n' && strncmp(j->p, "null", 4) == 0) {
        prop->kind = CA_UI_VAL_NULL; j->p += 4;
    } else if (*j->p == '-' || isdigit((unsigned char)*j->p)) {
        char *endp = NULL;
        const char *start = j->p;
        /* detect int vs double: presence of '.', 'e', 'E' => double */
        bool is_double = false;
        for (const char *q = start; *q && *q != ',' && *q != '}' && *q != ']' && !isspace((unsigned char)*q); ++q)
            if (*q == '.' || *q == 'e' || *q == 'E') is_double = true;
        if (is_double) { prop->kind = CA_UI_VAL_DOUBLE; prop->d = strtod(start, &endp); }
        else { prop->kind = CA_UI_VAL_INT; prop->i = (int64_t)strtoll(start, &endp, 10); }
        j->p = endp;
    } else {
        /* array/object property value -> record as NULL (host rarely reads
         * these; ToManaged would build nested structures) */
        prop->kind = CA_UI_VAL_NULL;
        jp_skip_value(j);
    }
}

static ca_ui_component_t *parse_element(jp *j,
                                        const ca_ui_catalog_entry_t *catalog, size_t ncat,
                                        bool strict);

/* Returns malloc'd component or NULL (sets j->err). */
static ca_ui_component_t *parse_element(jp *j,
                                        const ca_ui_catalog_entry_t *catalog, size_t ncat,
                                        bool strict) {
    jp_ws(j);
    if (*j->p != '{') { j->err = true; return NULL; }
    /* We need two passes: find "kind" first (order-independent), then props. To
     * keep it single-pass we scan the object collecting a raw kind, a props
     * object span, and a children array span. */
    const char *obj_start = j->p;
    j->p++; /* { */

    char *kind = NULL;
    const char *props_span = NULL;
    const char *children_span = NULL;

    while (1) {
        jp_ws(j);
        if (*j->p == '}') { j->p++; break; }
        char *key = jp_string(j);
        if (j->err) { free(key); free(kind); return NULL; }
        jp_ws(j);
        if (*j->p != ':') { free(key); free(kind); j->err = true; return NULL; }
        j->p++;
        jp_ws(j);
        if (strcmp(key, "kind") == 0) {
            kind = jp_string(j);
        } else if (strcmp(key, "properties") == 0 && *j->p == '{') {
            props_span = j->p;
            jp_skip_value(j);
        } else if (strcmp(key, "children") == 0 && *j->p == '[') {
            children_span = j->p;
            jp_skip_value(j);
        } else {
            jp_skip_value(j);
        }
        free(key);
        jp_ws(j);
        if (*j->p == ',') { j->p++; continue; }
        if (*j->p == '}') { j->p++; break; }
        j->err = true; free(kind); return NULL;
    }
    (void)obj_start;

    if (t_blank(kind)) { free(kind); j->err = true; return NULL; }

    /* find catalog entry */
    const ca_ui_catalog_entry_t *entry = NULL;
    for (size_t i = 0; i < ncat; ++i)
        if (t_icmp(catalog[i].kind, kind) == 0) { entry = &catalog[i]; break; }

    if (!entry) {
        if (strict) { free(kind); j->err = true; return NULL; }
        /* lenient: textBlock with the unknown-kind text */
        ca_ui_component_t *tb = (ca_ui_component_t *)calloc(1, sizeof(*tb));
        tb->kind = t_strdup("textBlock");
        tb->properties = (ca_ui_property_t *)calloc(2, sizeof(ca_ui_property_t));
        tb->property_count = 2;
        tb->properties[0].key = t_strdup("text");
        tb->properties[0].kind = CA_UI_VAL_STRING;
        { sb m = {0}; sb_add(&m, "[unknown kind '"); sb_add(&m, kind); sb_add(&m, "']"); tb->properties[0].s = sb_take(&m); }
        tb->properties[1].key = t_strdup("markdown");
        tb->properties[1].kind = CA_UI_VAL_BOOL; tb->properties[1].b = false;
        free(kind);
        return tb;
    }

    ca_ui_component_t *comp = (ca_ui_component_t *)calloc(1, sizeof(*comp));
    comp->kind = kind;

    /* parse properties from props_span */
    if (props_span) {
        jp pj = { props_span, false };
        pj.p++; /* { */
        ca_ui_property_t *props = NULL; size_t np = 0, pcap = 0;
        while (1) {
            jp_ws(&pj);
            if (*pj.p == '}') { break; }
            char *pkey = jp_string(&pj);
            if (pj.err) { free(pkey); break; }
            jp_ws(&pj);
            if (*pj.p != ':') { free(pkey); break; }
            pj.p++;
            if (strict) {
                bool allowed = false;
                for (size_t i = 0; i < entry->allowed_property_count; ++i)
                    if (strcmp(entry->allowed_properties[i].name, pkey) == 0) { allowed = true; break; }
                if (!allowed) {
                    free(pkey);
                    /* strict violation */
                    for (size_t i = 0; i < np; ++i) { free(props[i].key); free(props[i].s); }
                    free(props);
                    ca_ui_component_free(comp);
                    j->err = true;
                    return NULL;
                }
            }
            if (np == pcap) { pcap = pcap ? pcap * 2 : 4; props = (ca_ui_property_t *)realloc(props, pcap * sizeof(*props)); }
            memset(&props[np], 0, sizeof(props[np]));
            props[np].key = pkey;
            jp_read_prop_value(&pj, &props[np]);
            np++;
            jp_ws(&pj);
            if (*pj.p == ',') { pj.p++; continue; }
            if (*pj.p == '}') break;
            break;
        }
        comp->properties = props; comp->property_count = np;
    }

    /* parse children from children_span */
    if (children_span) {
        if (!entry->allows_children) {
            if (strict) { ca_ui_component_free(comp); j->err = true; return NULL; }
            /* lenient: ignore children */
        } else {
            jp cj = { children_span, false };
            cj.p++; /* [ */
            ca_ui_component_t *kids = NULL; size_t nk = 0, kcap = 0;
            while (1) {
                jp_ws(&cj);
                if (*cj.p == ']') break;
                ca_ui_component_t *child = parse_element(&cj, catalog, ncat, strict);
                if (cj.err || !child) {
                    for (size_t i = 0; i < nk; ++i) ui_component_free_internals(&kids[i]);
                    free(kids);
                    if (child) ca_ui_component_free(child);
                    ca_ui_component_free(comp);
                    j->err = true;
                    return NULL;
                }
                if (nk == kcap) { kcap = kcap ? kcap * 2 : 4; kids = (ca_ui_component_t *)realloc(kids, kcap * sizeof(*kids)); }
                kids[nk++] = *child;
                free(child); /* move contents into array */
                jp_ws(&cj);
                if (*cj.p == ',') { cj.p++; continue; }
                if (*cj.p == ']') break;
                break;
            }
            comp->children = kids; comp->child_count = nk;
        }
    }

    return comp;
}

ca_ui_component_t *ca_ui_parse(const char *json,
                               const ca_ui_catalog_entry_t *catalog, size_t catalog_count,
                               bool strict) {
    if (t_blank(json) || !catalog) return NULL;
    jp j = { json, false };
    ca_ui_component_t *root = parse_element(&j, catalog, catalog_count, strict);
    if (j.err) { if (root) ca_ui_component_free(root); return NULL; }
    return root;
}

char *ca_ui_describe_catalog_for_prompt(const ca_ui_catalog_entry_t *catalog,
                                        size_t catalog_count) {
    if (!catalog) return NULL;
    sb b = {0};
    sb_add(&b, "You may respond with a single JSON object describing one UI component.\n");
    sb_add(&b, "Allowed shape: { \"kind\": string, \"properties\": { ... }, \"children\"?: [ ... ] }\n");
    sb_add(&b, "\n");
    sb_add(&b, "Allowed kinds:\n");
    for (size_t i = 0; i < catalog_count; ++i) {
        sb_add(&b, "- "); sb_add(&b, catalog[i].kind); sb_add(&b, " \xE2\x80\x94 "); sb_add(&b, catalog[i].description); sb_add(&b, "\n");
        for (size_t k = 0; k < catalog[i].allowed_property_count; ++k) {
            sb_add(&b, "    - "); sb_add(&b, catalog[i].allowed_properties[k].name);
            sb_add(&b, ": "); sb_add(&b, catalog[i].allowed_properties[k].type); sb_add(&b, "\n");
        }
        if (catalog[i].allows_children) sb_add(&b, "    - children: array of components\n");
    }
    return sb_take(&b);
}

/* Recording renderer */
struct ca_recording_ui_renderer {
    int   count;
    char *last_kind;
};
ca_recording_ui_renderer_t *ca_recording_ui_renderer_create(void) {
    return (ca_recording_ui_renderer_t *)calloc(1, sizeof(ca_recording_ui_renderer_t));
}
void ca_recording_ui_renderer_destroy(ca_recording_ui_renderer_t *r) {
    if (!r) return;
    free(r->last_kind);
    free(r);
}
static void recording_render(void *user, const ca_ui_component_t *root) {
    ca_recording_ui_renderer_t *r = (ca_recording_ui_renderer_t *)user;
    if (!r) return;
    free(r->last_kind);
    r->last_kind = t_strdup(root ? root->kind : NULL);
    r->count++;
}
ca_ui_renderer_t ca_recording_ui_renderer_as_renderer(ca_recording_ui_renderer_t *r) {
    ca_ui_renderer_t v; v.render = recording_render; v.user = r; return v;
}
int ca_recording_ui_renderer_count(const ca_recording_ui_renderer_t *r) { return r ? r->count : 0; }
const char *ca_recording_ui_renderer_last_kind(const ca_recording_ui_renderer_t *r) {
    return r ? r->last_kind : NULL;
}
