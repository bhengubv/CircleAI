/*
 * personality.c — CircleAI.Personality (C11 port).
 *
 * The user-declared Persona artefact, an in-memory provider keyed by userId, the
 * two conflict resolvers (formality clamp / pass-through), and the prompt builder
 * that JSON-quotes every user string. Deterministic. Pure C11 + libc. Consumes
 * memory_brain.h's ca_uuid_v4 + memory.h's ca_persona_state_t. No pthreads.
 */

#include "circle_ai/personality.h"
#include "circle_ai/security.h" /* ca_uuid_v4, CA_UUID_STR_LEN */
#include "board_common.h"

/* ── record helpers ─────────────────────────────────────────────────────── */

void ca_persona_free(ca_persona_t *p) {
    if (!p) return;
    free(p->id);
    free(p->display_name);
    free(p->pronouns);
    cab_strv_free(p->identity_tags, p->identity_tag_count);
    cab_strv_free(p->values, p->value_count);
    cab_strv_free(p->taboos, p->taboo_count);
    free(p->preferred_locale);
    free(p->voice_preference);
    free(p->formality.floor);
    free(p->formality.ceiling);
    memset(p, 0, sizeof(*p));
}
void ca_persona_free_array(ca_persona_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_persona_free(&arr[i]);
    free(arr);
}

bool ca_persona_copy(ca_persona_t *dst, const ca_persona_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->privacy       = src->privacy;
    dst->created_at_ms = src->created_at_ms;
    dst->updated_at_ms = src->updated_at_ms;
    dst->id               = cab_strdup_empty(src->id);
    dst->display_name     = cab_strdup_empty(src->display_name);
    dst->preferred_locale = cab_strdup_empty(src->preferred_locale);
    dst->pronouns         = src->pronouns ? cab_strdup(src->pronouns) : NULL;
    dst->voice_preference = src->voice_preference ? cab_strdup(src->voice_preference) : NULL;
    dst->formality.floor   = cab_strdup_empty(src->formality.floor);
    dst->formality.ceiling = cab_strdup_empty(src->formality.ceiling);
    if (!dst->id || !dst->display_name || !dst->preferred_locale ||
        !dst->formality.floor || !dst->formality.ceiling ||
        (src->pronouns && !dst->pronouns) ||
        (src->voice_preference && !dst->voice_preference)) {
        ca_persona_free(dst); return false;
    }
    if (!cab_strv_copy(&dst->identity_tags, src->identity_tags, src->identity_tag_count) ||
        !cab_strv_copy(&dst->values, src->values, src->value_count) ||
        !cab_strv_copy(&dst->taboos, src->taboos, src->taboo_count)) {
        ca_persona_free(dst); return false;
    }
    dst->identity_tag_count = src->identity_tag_count;
    dst->value_count = src->value_count;
    dst->taboo_count = src->taboo_count;
    return true;
}

bool ca_persona_create(const char *display_name, const char *locale,
                       int64_t now_ms, ca_persona_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(display_name) || cab_is_ws(locale)) return false;
    char uuid[CA_UUID_STR_LEN];
    ca_uuid_v4(uuid);
    out->id = cab_strdup_empty(uuid);
    out->display_name = cab_strdup_empty(display_name);
    out->preferred_locale = cab_strdup_empty(locale);
    out->pronouns = NULL;
    out->voice_preference = NULL;
    out->identity_tags = NULL; out->identity_tag_count = 0;
    out->values = NULL; out->value_count = 0;
    out->taboos = NULL; out->taboo_count = 0;
    out->formality.floor = cab_strdup_empty("casual");
    out->formality.ceiling = cab_strdup_empty("formal");
    out->privacy = CA_PRIVACY_BALANCED;
    out->created_at_ms = now_ms;
    out->updated_at_ms = now_ms;
    if (!out->id || !out->display_name || !out->preferred_locale ||
        !out->formality.floor || !out->formality.ceiling) {
        ca_persona_free(out); return false;
    }
    return true;
}

/* ── InMemoryPersonaProvider ────────────────────────────────────────────── */

typedef struct { char *user_id; ca_persona_t persona; } persona_entry_t;

struct ca_persona_provider {
    persona_entry_t *items; size_t count, cap;
};

ca_persona_provider_t *ca_persona_provider_create(void) {
    return (ca_persona_provider_t *)calloc(1, sizeof(ca_persona_provider_t));
}
void ca_persona_provider_destroy(ca_persona_provider_t *p) {
    if (!p) return;
    for (size_t i = 0; i < p->count; ++i) {
        free(p->items[i].user_id);
        ca_persona_free(&p->items[i].persona);
    }
    free(p->items);
    free(p);
}

bool ca_persona_provider_get(const ca_persona_provider_t *p, const char *user_id,
                             ca_persona_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || cab_is_ws(user_id) || !out) return false;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ord_eq(p->items[i].user_id, user_id))
            return ca_persona_copy(out, &p->items[i].persona);
    return false;
}

int ca_persona_provider_save(ca_persona_provider_t *p, const char *user_id,
                             const ca_persona_t *persona, int64_t now_ms,
                             ca_persona_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!p || cab_is_ws(user_id) || !persona) return -1;
    ca_persona_t refreshed;
    if (!ca_persona_copy(&refreshed, persona)) return -1;
    refreshed.updated_at_ms = now_ms;

    for (size_t i = 0; i < p->count; ++i) {
        if (cab_ord_eq(p->items[i].user_id, user_id)) {
            ca_persona_free(&p->items[i].persona);
            /* store a copy, keep `refreshed` for the return */
            if (!ca_persona_copy(&p->items[i].persona, &refreshed)) {
                ca_persona_free(&refreshed); return -1;
            }
            if (out) { if (!ca_persona_copy(out, &refreshed)) { ca_persona_free(&refreshed); return -1; } }
            ca_persona_free(&refreshed);
            return 0;
        }
    }
    if (p->count == p->cap) {
        size_t nc = p->cap ? p->cap * 2 : 4;
        void *n = realloc(p->items, nc * sizeof(persona_entry_t));
        if (!n) { ca_persona_free(&refreshed); return -1; }
        p->items = (persona_entry_t *)n; p->cap = nc;
    }
    char *uid = cab_strdup_empty(user_id);
    if (!uid) { ca_persona_free(&refreshed); return -1; }
    p->items[p->count].user_id = uid;
    if (!ca_persona_copy(&p->items[p->count].persona, &refreshed)) {
        free(uid); ca_persona_free(&refreshed); return -1;
    }
    p->count++;
    if (out) { if (!ca_persona_copy(out, &refreshed)) { ca_persona_free(&refreshed); return -1; } }
    ca_persona_free(&refreshed);
    return 0;
}

bool ca_persona_provider_exists(const ca_persona_provider_t *p, const char *user_id) {
    if (!p || cab_is_ws(user_id)) return false;
    for (size_t i = 0; i < p->count; ++i)
        if (cab_ord_eq(p->items[i].user_id, user_id)) return true;
    return false;
}

ca_persona_t *ca_persona_provider_export_all(const ca_persona_provider_t *p,
                                             size_t *out_count) {
    if (!out_count) return NULL;
    if (!p) { *out_count = (size_t)-1; return NULL; }
    if (p->count == 0) { *out_count = 0; return NULL; }
    ca_persona_t *out = (ca_persona_t *)calloc(p->count, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < p->count; ++i) {
        if (!ca_persona_copy(&out[i], &p->items[i].persona)) {
            ca_persona_free_array(out, i);
            *out_count = (size_t)-1; return NULL;
        }
    }
    *out_count = p->count;
    return out;
}

/* ── conflict resolvers ─────────────────────────────────────────────────── */

static int formality_rank(const char *f) {
    if (cab_ord_eq(f, "casual")) return 0;
    if (cab_ord_eq(f, "neutral")) return 1;
    if (cab_ord_eq(f, "formal")) return 2;
    return 1; /* unknown -> neutral */
}
static const char *formality_state_str(ca_formality_t f) {
    switch (f) {
        case CA_FORMALITY_CASUAL: return "casual";
        case CA_FORMALITY_FORMAL: return "formal";
        default: return "neutral";
    }
}
/* Clamp `learned` into [floor, ceiling]; if inverted, return floor. */
static const char *clamp_formality(const char *learned,
                                   const char *floor, const char *ceiling) {
    int lr = formality_rank(learned);
    int fr = formality_rank(floor);
    int cr = formality_rank(ceiling);
    if (fr > cr) return floor;
    if (lr < fr) return floor;
    if (lr > cr) return ceiling;
    return learned;
}

bool ca_persona_resolve_declared_wins(const ca_persona_t *declared,
                                      const ca_persona_state_t *learned,
                                      ca_persona_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!declared || !learned || !out) return false;
    if (!ca_persona_copy(out, declared)) return false;

    const char *lstr = formality_state_str(learned->formality);
    const char *clamped = clamp_formality(lstr, declared->formality.floor,
                                          declared->formality.ceiling);
    if (cab_ord_eq(clamped, lstr)) {
        /* learned was within bounds — declared unchanged */
        return true;
    }
    /* surface the clamped value by adjusting floor/ceiling */
    if (cab_ord_eq(clamped, "casual")) {
        char *nf = cab_strdup_empty("casual");
        if (!nf) { ca_persona_free(out); return false; }
        free(out->formality.floor);
        out->formality.floor = nf;
        /* ceiling stays declared.Ceiling (already copied) */
    } else if (cab_ord_eq(clamped, "formal")) {
        char *nc = cab_strdup_empty("formal");
        if (!nc) { ca_persona_free(out); return false; }
        free(out->formality.ceiling);
        out->formality.ceiling = nc;
    }
    return true;
}

bool ca_persona_resolve_learned_wins(const ca_persona_t *declared,
                                     const ca_persona_state_t *learned,
                                     ca_persona_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!declared || !learned || !out) return false;
    /* passes the declared persona through unchanged */
    return ca_persona_copy(out, declared);
}

/* ── PersonaPromptBuilder ───────────────────────────────────────────────── */

/* True when the persona carries nothing beyond Create() defaults. */
static bool persona_is_default(const ca_persona_t *p) {
    return cab_is_ws(p->pronouns) &&
           p->identity_tag_count == 0 &&
           p->value_count == 0 &&
           p->taboo_count == 0 &&
           cab_is_ws(p->voice_preference) &&
           p->privacy == CA_PRIVACY_BALANCED &&
           cab_ord_eq(p->formality.floor, "casual") &&
           cab_ord_eq(p->formality.ceiling, "formal");
}

/* Growable char buffer. */
typedef struct { char *buf; size_t len, cap; } sb_t;
static bool sb_reserve(sb_t *sb, size_t extra) {
    if (sb->len + extra + 1 > sb->cap) {
        size_t nc = sb->cap ? sb->cap : 64;
        while (nc < sb->len + extra + 1) nc *= 2;
        char *nb = (char *)realloc(sb->buf, nc);
        if (!nb) return false;
        sb->buf = nb; sb->cap = nc;
    }
    return true;
}
static bool sb_puts(sb_t *sb, const char *s) {
    size_t n = strlen(s);
    if (!sb_reserve(sb, n)) return false;
    memcpy(sb->buf + sb->len, s, n);
    sb->len += n;
    sb->buf[sb->len] = '\0';
    return true;
}
/* Append `raw` as a JSON string literal (relaxed escaping: quote + backslash +
 * control chars). */
static bool sb_put_quoted(sb_t *sb, const char *raw) {
    if (!raw) raw = "";
    if (!sb_reserve(sb, strlen(raw) * 6 + 2)) return false;
    char *p = sb->buf + sb->len;
    *p++ = '"';
    for (const unsigned char *c = (const unsigned char *)raw; *c; ++c) {
        switch (*c) {
            case '"':  *p++ = '\\'; *p++ = '"';  break;
            case '\\': *p++ = '\\'; *p++ = '\\'; break;
            case '\b': *p++ = '\\'; *p++ = 'b';  break;
            case '\f': *p++ = '\\'; *p++ = 'f';  break;
            case '\n': *p++ = '\\'; *p++ = 'n';  break;
            case '\r': *p++ = '\\'; *p++ = 'r';  break;
            case '\t': *p++ = '\\'; *p++ = 't';  break;
            default:
                if (*c < 0x20) {
                    static const char hex[] = "0123456789abcdef";
                    *p++ = '\\'; *p++ = 'u'; *p++ = '0'; *p++ = '0';
                    *p++ = hex[(*c >> 4) & 0xF]; *p++ = hex[*c & 0xF];
                } else {
                    *p++ = (char)*c;
                }
        }
    }
    *p++ = '"';
    *p = '\0';
    sb->len = (size_t)(p - sb->buf);
    return true;
}
static bool sb_put_quoted_list(sb_t *sb, char *const *items, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        if (i > 0 && !sb_puts(sb, ", ")) return false;
        if (!sb_put_quoted(sb, items[i])) return false;
    }
    return true;
}

char *ca_persona_build_system_hint(const ca_persona_t *persona) {
    if (!persona) return NULL;
    if (persona_is_default(persona)) return cab_strdup_empty("");

    sb_t sb = {0};
    bool ok = true;
    ok = ok && sb_puts(&sb, "[Persona]");
    ok = ok && sb_puts(&sb, "\nYou are speaking with ");
    ok = ok && sb_put_quoted(&sb, persona->display_name);
    ok = ok && sb_puts(&sb, ".");

    if (ok && !cab_is_ws(persona->pronouns)) {
        ok = sb_puts(&sb, " They identify as ") &&
             sb_put_quoted(&sb, persona->pronouns) &&
             sb_puts(&sb, ".");
    }

    ok = ok && sb_puts(&sb, "\nThey prefer responses in ");
    ok = ok && sb_put_quoted(&sb, persona->preferred_locale);
    ok = ok && sb_puts(&sb, ", tone between ");
    ok = ok && sb_put_quoted(&sb, persona->formality.floor);
    ok = ok && sb_puts(&sb, " and ");
    ok = ok && sb_put_quoted(&sb, persona->formality.ceiling);
    ok = ok && sb_puts(&sb, ".");

    if (ok && persona->identity_tag_count > 0) {
        ok = sb_puts(&sb, "\nIdentity tags: ") &&
             sb_put_quoted_list(&sb, persona->identity_tags, persona->identity_tag_count) &&
             sb_puts(&sb, ".");
    }
    if (ok && persona->value_count > 0) {
        ok = sb_puts(&sb, "\nTheir declared values: ") &&
             sb_put_quoted_list(&sb, persona->values, persona->value_count) &&
             sb_puts(&sb, ".");
    }
    if (ok && persona->taboo_count > 0) {
        ok = sb_puts(&sb, "\nAvoid: ") &&
             sb_put_quoted_list(&sb, persona->taboos, persona->taboo_count) &&
             sb_puts(&sb, ".");
    }
    if (ok && !cab_is_ws(persona->voice_preference)) {
        ok = sb_puts(&sb, "\nPreferred voice tag: ") &&
             sb_put_quoted(&sb, persona->voice_preference) &&
             sb_puts(&sb, ".");
    }
    if (ok && persona->privacy == CA_PRIVACY_STRICT) {
        ok = sb_puts(&sb, "\nPrivacy: strict — minimize stored signals, do not "
                          "surface personal context proactively, and never share "
                          "personal context across surfaces without explicit prompt.");
    } else if (ok && persona->privacy == CA_PRIVACY_OPEN) {
        ok = sb_puts(&sb, "\nPrivacy: open — the user has authorised broader "
                          "retention and proactive surfacing.");
    }

    if (!ok) { free(sb.buf); return NULL; }
    if (!sb.buf) return cab_strdup_empty("");
    return sb.buf;
}
