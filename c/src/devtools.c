/*
 * devtools.c — CircleAI.DevTools (C11 port).
 *
 * Every tool operates over an in-memory buffer store (host loads path -> text;
 * the filesystem is the injected boundary). The algorithms port faithfully:
 * cursor-partial completion, word-boundary (\bX\b) rename, remove-line, append,
 * extract-constant, and the built-in agent executor. Deterministic. Pure C11 +
 * libc. No pthreads.
 */

#include "circle_ai/devtools.h"
#include "board_common.h"
#include <stdio.h>

/* ── FileEdit ───────────────────────────────────────────────────────────── */

void ca_file_edit_free(ca_file_edit_t *e) {
    if (!e) return;
    free(e->path);
    free(e->replacement);
    e->path = e->replacement = NULL;
}
void ca_file_edit_free_array(ca_file_edit_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_file_edit_free(&arr[i]);
    free(arr);
}
static bool edit_init(ca_file_edit_t *e, const char *path, int start, int end,
                      const char *replacement) {
    memset(e, 0, sizeof(*e));
    e->range_start = start;
    e->range_end = end;
    e->path = cab_strdup_empty(path);
    e->replacement = cab_strdup_empty(replacement);
    if (!e->path || !e->replacement) { ca_file_edit_free(e); return false; }
    return true;
}
static bool edit_copy(ca_file_edit_t *dst, const ca_file_edit_t *src) {
    return edit_init(dst, src->path, src->range_start, src->range_end,
                     src->replacement);
}

/* growable edit vector */
typedef struct { ca_file_edit_t *v; size_t n, cap; } edit_vec_t;
static bool edit_vec_push(edit_vec_t *ev, const char *path, int start, int end,
                          const char *replacement) {
    if (ev->n == ev->cap) {
        size_t nc = ev->cap ? ev->cap * 2 : 8;
        void *n = realloc(ev->v, nc * sizeof(ca_file_edit_t));
        if (!n) return false;
        ev->v = (ca_file_edit_t *)n;
        ev->cap = nc;
    }
    if (!edit_init(&ev->v[ev->n], path, start, end, replacement)) return false;
    ev->n++;
    return true;
}

/* ── InlineSuggestion ───────────────────────────────────────────────────── */

void ca_inline_suggestion_free(ca_inline_suggestion_t *s) {
    if (!s) return;
    free(s->text);
    s->text = NULL;
}

/* ── AgentTurn ──────────────────────────────────────────────────────────── */

void ca_agent_turn_free(ca_agent_turn_t *t) {
    if (!t) return;
    free(t->turn_id);
    free(t->user_prompt);
    free(t->response);
    ca_file_edit_free_array(t->edits, t->edit_count);
    memset(t, 0, sizeof(*t));
}
void ca_agent_turn_free_array(ca_agent_turn_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_agent_turn_free(&arr[i]);
    free(arr);
}
static bool turn_copy(ca_agent_turn_t *dst, const ca_agent_turn_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->turn_id     = cab_strdup_empty(src->turn_id);
    dst->user_prompt = cab_strdup_empty(src->user_prompt);
    dst->response    = cab_strdup_empty(src->response);
    if (!dst->turn_id || !dst->user_prompt || !dst->response) {
        ca_agent_turn_free(dst); return false;
    }
    if (src->edit_count > 0) {
        dst->edits = (ca_file_edit_t *)calloc(src->edit_count, sizeof(ca_file_edit_t));
        if (!dst->edits) { ca_agent_turn_free(dst); return false; }
        for (size_t i = 0; i < src->edit_count; ++i) {
            if (!edit_copy(&dst->edits[i], &src->edits[i])) {
                dst->edit_count = i;
                ca_agent_turn_free(dst); return false;
            }
        }
        dst->edit_count = src->edit_count;
    }
    return true;
}

/* ── PatchPlan ──────────────────────────────────────────────────────────── */

void ca_patch_plan_free(ca_patch_plan_t *p) {
    if (!p) return;
    free(p->goal);
    cab_strv_free(p->steps, p->step_count);
    ca_file_edit_free_array(p->proposed_edits, p->edit_count);
    memset(p, 0, sizeof(*p));
}

/* ── BufferCodeEditor ───────────────────────────────────────────────────── */

typedef struct { char *path; char *text; } buffer_t;

struct ca_code_editor {
    buffer_t *items;
    size_t    count, cap;
};

ca_code_editor_t *ca_code_editor_create(void) {
    return (ca_code_editor_t *)calloc(1, sizeof(ca_code_editor_t));
}
void ca_code_editor_destroy(ca_code_editor_t *e) {
    if (!e) return;
    for (size_t i = 0; i < e->count; ++i) { free(e->items[i].path); free(e->items[i].text); }
    free(e->items);
    free(e);
}
const char *ca_code_editor_backend_id(const ca_code_editor_t *e) {
    (void)e; return "buffer";
}

static buffer_t *editor_find(const ca_code_editor_t *e, const char *path) {
    for (size_t i = 0; i < e->count; ++i)
        if (cab_ord_eq(e->items[i].path, path)) return &e->items[i];
    return NULL;
}

int ca_code_editor_put_buffer(ca_code_editor_t *e, const char *path,
                              const char *text) {
    if (!e || cab_is_ws(path) || !text) return -1;
    buffer_t *b = editor_find(e, path);
    if (b) {
        char *nt = cab_strdup(text);
        if (!nt) return -1;
        free(b->text);
        b->text = nt;
        return 0;
    }
    if (e->count == e->cap) {
        size_t nc = e->cap ? e->cap * 2 : 4;
        void *n = realloc(e->items, nc * sizeof(buffer_t));
        if (!n) return -1;
        e->items = (buffer_t *)n;
        e->cap = nc;
    }
    char *p = cab_strdup_empty(path);
    char *t = cab_strdup(text);
    if (!p || !t) { free(p); free(t); return -1; }
    e->items[e->count].path = p;
    e->items[e->count].text = t;
    e->count++;
    return 0;
}

char *ca_code_editor_read(const ca_code_editor_t *e, const char *path) {
    if (!e || cab_is_ws(path)) return NULL;
    buffer_t *b = editor_find(e, path);
    return b ? cab_strdup(b->text) : NULL;
}

/* Splice: remove [start,end) and insert replacement into a heap string. */
static char *splice(const char *text, int start, int end, const char *repl) {
    size_t tlen = strlen(text), rlen = strlen(repl);
    size_t out_len = tlen - (size_t)(end - start) + rlen;
    char *out = (char *)malloc(out_len + 1);
    if (!out) return NULL;
    memcpy(out, text, (size_t)start);
    memcpy(out + start, repl, rlen);
    memcpy(out + start + rlen, text + end, tlen - (size_t)end);
    out[out_len] = '\0';
    return out;
}

int ca_code_editor_apply(ca_code_editor_t *e, const ca_file_edit_t *edits,
                         size_t edit_count) {
    if (!e || (!edits && edit_count > 0)) return -1;
    if (edit_count == 0) return 0;

    /* group by path: iterate distinct paths in first-seen order */
    bool *done = (bool *)calloc(edit_count, sizeof(bool));
    if (!done) return -1;
    for (size_t i = 0; i < edit_count; ++i) {
        if (done[i]) continue;
        const char *path = edits[i].path;
        buffer_t *b = editor_find(e, path);
        if (!b) { free(done); return -1; } /* missing buffer (FileNotFound) */

        /* collect indices for this path */
        size_t *grp = (size_t *)malloc(edit_count * sizeof(size_t));
        if (!grp) { free(done); return -1; }
        size_t g = 0;
        for (size_t j = i; j < edit_count; ++j)
            if (!done[j] && cab_ord_eq(edits[j].path, path)) { grp[g++] = j; done[j] = true; }

        /* order by RangeStart desc (stable insertion) */
        for (size_t a = 1; a < g; ++a) {
            size_t key = grp[a];
            int ks = edits[key].range_start;
            size_t bidx = a;
            while (bidx > 0 && edits[grp[bidx - 1]].range_start < ks) {
                grp[bidx] = grp[bidx - 1]; bidx--;
            }
            grp[bidx] = key;
        }

        char *cur = cab_strdup(b->text);
        if (!cur) { free(grp); free(done); return -1; }
        int len = (int)strlen(cur);
        bool ok = true;
        for (size_t a = 0; a < g; ++a) {
            const ca_file_edit_t *ed = &edits[grp[a]];
            if (ed->range_start < 0 || ed->range_end > len ||
                ed->range_end < ed->range_start) { ok = false; break; }
            char *nx = splice(cur, ed->range_start, ed->range_end, ed->replacement);
            if (!nx) { ok = false; break; }
            free(cur);
            cur = nx;
            len = (int)strlen(cur);
        }
        free(grp);
        if (!ok) { free(cur); free(done); return -1; }
        free(b->text);
        b->text = cur;
    }
    free(done);
    return 0;
}

const char *ca_dt_null_code_editor_backend_id(void) { return "null"; }

/* ── TokenContextInlineSuggester ────────────────────────────────────────── */

const char *ca_inline_suggester_backend_id(void) { return "token-context"; }
const char *ca_dt_null_inline_suggester_backend_id(void) { return "null"; }

static bool is_ident_char(char c) {
    return isalnum((unsigned char)c) || c == '_';
}

bool ca_inline_suggest(const ca_code_editor_t *editor, const char *path, int line,
                       int column, const char *context_before,
                       ca_inline_suggestion_t *out) {
    (void)line; (void)column;
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(path) || !context_before) return false;

    /* partial token at cursor = trailing identifier run of contextBefore */
    size_t cblen = strlen(context_before);
    size_t i = cblen;
    while (i > 0 && is_ident_char(context_before[i - 1])) i--;
    const char *partial = context_before + i;
    size_t plen = cblen - i;
    if (plen < 2) return false;

    /* file text: editor buffer for path, else contextBefore */
    char *owned = editor ? ca_code_editor_read(editor, path) : NULL;
    const char *file_text = owned ? owned : context_before;

    /* scan identifiers; track best candidate starting with partial and longer */
    char  *best = NULL;
    int    best_freq = 0;
    size_t best_len = 0;
    /* frequency: because we need highest frequency then shortest, do two passes:
     * first collect distinct candidates + counts. */
    typedef struct { char *tok; int freq; } cand_t;
    cand_t *cands = NULL; size_t cn = 0, cc = 0;
    bool oom = false;

    const char *p = file_text;
    while (*p) {
        if (!is_ident_char(*p)) { p++; continue; }
        const char *s = p;
        while (*p && is_ident_char(*p)) p++;
        size_t tlen = (size_t)(p - s);
        if (tlen <= plen) continue;
        if (strncmp(s, partial, plen) != 0) continue;
        /* find in cands */
        bool found = false;
        for (size_t k = 0; k < cn; ++k) {
            if (strlen(cands[k].tok) == tlen && strncmp(cands[k].tok, s, tlen) == 0) {
                cands[k].freq++; found = true; break;
            }
        }
        if (!found) {
            if (cn == cc) {
                size_t ncap = cc ? cc * 2 : 8;
                cand_t *nc = (cand_t *)realloc(cands, ncap * sizeof(cand_t));
                if (!nc) { oom = true; break; }
                cands = nc; cc = ncap;
            }
            char *tok = (char *)malloc(tlen + 1);
            if (!tok) { oom = true; break; }
            memcpy(tok, s, tlen); tok[tlen] = '\0';
            cands[cn].tok = tok; cands[cn].freq = 1; cn++;
        }
    }

    if (!oom) {
        for (size_t k = 0; k < cn; ++k) {
            size_t tl = strlen(cands[k].tok);
            /* highest freq, tie-break shortest (ThenBy Length) */
            if (cands[k].freq > best_freq ||
                (cands[k].freq == best_freq && best && tl < best_len)) {
                best = cands[k].tok;
                best_freq = cands[k].freq;
                best_len = tl;
            }
        }
    }

    bool result = false;
    if (!oom && best) {
        /* completion = suffix after partial */
        const char *completion = best + plen;
        double conf = (double)best_freq / 10.0;
        if (conf > 1.0) conf = 1.0;
        out->text = cab_strdup_empty(completion);
        if (out->text) {
            out->confidence = (float)conf;
            result = true;
        }
    }

    for (size_t k = 0; k < cn; ++k) free(cands[k].tok);
    free(cands);
    free(owned);
    return result;
}

/* ── InMemoryAgentShell ─────────────────────────────────────────────────── */

struct ca_agent_shell {
    ca_agent_turn_t *history;
    size_t           count, cap;
    long             seq;
};

ca_agent_shell_t *ca_agent_shell_create(void) {
    return (ca_agent_shell_t *)calloc(1, sizeof(ca_agent_shell_t));
}
void ca_agent_shell_destroy(ca_agent_shell_t *s) {
    if (!s) return;
    ca_agent_turn_free_array(s->history, s->count);
    free(s);
}
const char *ca_agent_shell_backend_id(const ca_agent_shell_t *s) {
    (void)s; return "in-memory";
}

/* Trim leading/trailing ASCII whitespace into a fresh string. */
static char *trim_dup2(const char *s) {
    while (*s == ' ' || *s == '\t' || *s == '\n' || *s == '\r') s++;
    size_t n = strlen(s);
    while (n > 0 && (s[n - 1] == ' ' || s[n - 1] == '\t' ||
                     s[n - 1] == '\n' || s[n - 1] == '\r')) n--;
    char *out = (char *)malloc(n + 1);
    if (!out) return NULL;
    memcpy(out, s, n);
    out[n] = '\0';
    return out;
}
/* portable case-insensitive prefix compare (n chars). */
static int strncasecmp_portable(const char *a, const char *b, size_t n) {
    for (size_t i = 0; i < n; ++i) {
        int ca = tolower((unsigned char)a[i]);
        int cb = tolower((unsigned char)b[i]);
        if (ca != cb) return ca - cb;
        if (ca == 0) return 0;
    }
    return 0;
}

static char *built_in_response(const char *prompt) {
    char *trimmed = trim_dup2(prompt);
    if (!trimmed) return NULL;
    char *resp = NULL;
    size_t tl = strlen(trimmed);
    if (tl >= 5 && strncasecmp_portable(trimmed, "read ", 5) == 0) {
        size_t need = strlen("Reading ") + (tl - 5) + strlen(" ...") + 1;
        resp = (char *)malloc(need);
        if (resp) snprintf(resp, need, "Reading %s ...", trimmed + 5);
    } else if (tl >= 6 && strncasecmp_portable(trimmed, "write ", 6) == 0) {
        size_t need = strlen("Writing ") + (tl - 6) + strlen(" ...") + 1;
        resp = (char *)malloc(need);
        if (resp) snprintf(resp, need, "Writing %s ...", trimmed + 6);
    } else if (strchr(trimmed, '?')) {
        resp = cab_strdup_empty(
            "Acknowledged the question; need more context to give a useful answer.");
    } else {
        size_t need = strlen("Acknowledged: ") + tl + 2;
        resp = (char *)malloc(need);
        if (resp) snprintf(resp, need, "Acknowledged: %s.", trimmed);
    }
    free(trimmed);
    return resp;
}

bool ca_agent_shell_run_turn(ca_agent_shell_t *s, const char *user_prompt,
                             ca_agent_turn_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!s || !user_prompt || !out) return false;

    char *response = built_in_response(user_prompt);
    if (!response) return false;

    long id = ++s->seq;
    ca_agent_turn_t turn;
    memset(&turn, 0, sizeof(turn));
    char idbuf[32];
    snprintf(idbuf, sizeof(idbuf), "turn-%ld", id);
    turn.turn_id = cab_strdup_empty(idbuf);
    turn.user_prompt = cab_strdup_empty(user_prompt);
    turn.response = response; /* transfer ownership */
    if (!turn.turn_id || !turn.user_prompt) { ca_agent_turn_free(&turn); return false; }

    /* store a copy in history, return another copy */
    if (s->count == s->cap) {
        size_t nc = s->cap ? s->cap * 2 : 8;
        void *n = realloc(s->history, nc * sizeof(ca_agent_turn_t));
        if (!n) { ca_agent_turn_free(&turn); return false; }
        s->history = (ca_agent_turn_t *)n;
        s->cap = nc;
    }
    if (!turn_copy(&s->history[s->count], &turn)) { ca_agent_turn_free(&turn); return false; }
    s->count++;
    if (!turn_copy(out, &turn)) { ca_agent_turn_free(&turn); return false; }
    ca_agent_turn_free(&turn);
    return true;
}

ca_agent_turn_t *ca_agent_shell_history(const ca_agent_shell_t *s, int limit,
                                        size_t *out_count) {
    if (!out_count) return NULL;
    if (!s || limit <= 0) { *out_count = (size_t)-1; return NULL; }
    if (s->count == 0) { *out_count = 0; return NULL; }
    size_t take = (size_t)limit < s->count ? (size_t)limit : s->count;
    size_t start = s->count - take; /* newest `take` in chronological order */
    ca_agent_turn_t *out = (ca_agent_turn_t *)calloc(take, sizeof(*out));
    if (!out) { *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < take; ++i) {
        if (!turn_copy(&out[i], &s->history[start + i])) {
            ca_agent_turn_free_array(out, i);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    *out_count = take;
    return out;
}

const char *ca_dt_null_agent_shell_backend_id(void) { return "null"; }

/* ── word-boundary (\bX\b) matching shared by planner + refactor ────────── */

/* Push a FileEdit for every \bname\b occurrence in `text` (replacement = newN).
 * Mirrors Regex(\bX\b).Matches -> FileEdit(f, m.Index, m.Index+len, new). */
static bool push_word_renames(edit_vec_t *ev, const char *path, const char *text,
                              const char *oldn, const char *newn) {
    size_t olen = strlen(oldn);
    if (olen == 0) return true;
    size_t tlen = strlen(text);
    for (size_t i = 0; i + olen <= tlen; ++i) {
        if (strncmp(text + i, oldn, olen) != 0) continue;
        bool left_boundary  = (i == 0) || !is_ident_char(text[i - 1]);
        bool right_boundary = (i + olen == tlen) || !is_ident_char(text[i + olen]);
        /* \b requires a word char on the matched side; oldn edges are word chars
         * only if they are identifier chars — mirror \b semantics against the
         * neighbours. */
        bool old_left_word  = is_ident_char(oldn[0]);
        bool old_right_word = is_ident_char(oldn[olen - 1]);
        if (old_left_word && !left_boundary) continue;
        if (old_right_word && !right_boundary) continue;
        if (!edit_vec_push(ev, path, (int)i, (int)(i + olen), newn)) return false;
        i += olen - 1;
    }
    return true;
}

/* ── PatternMatchPatchPlanner ───────────────────────────────────────────── */

const char *ca_patch_planner_backend_id(void) { return "pattern-match"; }
const char *ca_dt_null_patch_planner_backend_id(void) { return "null"; }

/* Case-insensitive token compare of a prefix word; returns pointer just past it
 * (skipping following spaces) or NULL if it does not match. */
static const char *match_kw(const char *s, const char *kw) {
    size_t kl = strlen(kw);
    if (strncasecmp_portable(s, kw, kl) != 0) return NULL;
    s += kl;
    return s;
}

static bool steps_single(char ***steps, size_t *count, const char *step) {
    char **v = (char **)calloc(1, sizeof(char *));
    if (!v) return false;
    v[0] = cab_strdup_empty(step);
    if (!v[0]) { free(v); return false; }
    *steps = v; *count = 1;
    return true;
}

bool ca_patch_plan(const ca_code_editor_t *editor, const char *goal,
                   ca_patch_plan_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!out || cab_is_ws(goal)) return false;

    out->goal = cab_strdup_empty(goal);
    if (!out->goal) return false;

    /* Trim goal for parsing. */
    char *g = trim_dup2(goal);
    if (!g) { ca_patch_plan_free(out); return false; }

    edit_vec_t ev = {0};
    char stepbuf[512];
    bool ok = true;
    bool handled = false;

    /* "rename X to Y [in F]" */
    const char *p = match_kw(g, "rename ");
    if (p) {
        while (*p == ' ') p++;
        const char *xs = p; while (*p && *p != ' ') p++;
        char oldn[128]; size_t xn = (size_t)(p - xs); if (xn >= sizeof(oldn)) xn = sizeof(oldn) - 1;
        memcpy(oldn, xs, xn); oldn[xn] = '\0';
        /* p points at the space before "to"; advance past spaces then match "to " */
        while (*p == ' ') p++;
        const char *tp = match_kw(p, "to ");
        if (tp && xn > 0) {
            while (*tp == ' ') tp++;
            const char *ys = tp; while (*tp && *tp != ' ') tp++;
            char newn[128]; size_t yn = (size_t)(tp - ys); if (yn >= sizeof(newn)) yn = sizeof(newn) - 1;
            memcpy(newn, ys, yn); newn[yn] = '\0';
            /* optional "in F" */
            const char *scope = NULL;
            while (*tp == ' ') tp++;
            const char *ip = match_kw(tp, "in ");
            if (ip) { while (*ip == ' ') ip++; scope = ip; }

            if (editor) {
                if (scope && *scope) {
                    char *stext = ca_code_editor_read(editor, scope);
                    if (stext) { ok = push_word_renames(&ev, scope, stext, oldn, newn); free(stext); }
                } else {
                    /* all buffers (Directory.GetCurrentDirectory scope) */
                    for (size_t i = 0; ok && i < editor->count; ++i)
                        ok = push_word_renames(&ev, editor->items[i].path,
                                               editor->items[i].text, oldn, newn);
                }
            }
            snprintf(stepbuf, sizeof(stepbuf),
                     "Rename '%s' -> '%s' across %zu location(s)", oldn, newn, ev.n);
            ok = ok && steps_single(&out->steps, &out->step_count, stepbuf);
            handled = true;
        }
    }

    /* "remove line N from F" */
    if (!handled) {
        const char *rp = match_kw(g, "remove ");
        if (rp) {
            while (*rp == ' ') rp++;
            const char *lp = match_kw(rp, "line ");
            if (lp) {
                while (*lp == ' ') lp++;
                char *endp = NULL;
                long lineno = strtol(lp, &endp, 10);
                if (endp != lp) {
                    const char *fp = endp; while (*fp == ' ') fp++;
                    const char *fromp = match_kw(fp, "from ");
                    if (fromp && lineno >= 1) {
                        while (*fromp == ' ') fromp++;
                        char *path = trim_dup2(fromp);
                        char *text = editor ? ca_code_editor_read(editor, path) : NULL;
                        if (path && text) {
                            /* find start offset of line `lineno`; range to end of that line */
                            int offset = 0; long current = 1; size_t tlen = strlen(text);
                            for (size_t i = 0; i < tlen; ++i) {
                                if (current == lineno) {
                                    offset = (int)i;
                                    const char *nl = strchr(text + i, '\n');
                                    int rend = nl ? (int)(nl - text) + 1 : (int)tlen;
                                    ok = edit_vec_push(&ev, path, offset, rend, "");
                                    break;
                                }
                                if (text[i] == '\n') current++;
                            }
                        }
                        free(text);
                        snprintf(stepbuf, sizeof(stepbuf), "Remove line %ld from %s",
                                 lineno, path ? path : "");
                        ok = ok && steps_single(&out->steps, &out->step_count, stepbuf);
                        free(path);
                        handled = true;
                    }
                }
            }
        }
    }

    /* "append TEXT to F" */
    if (!handled) {
        const char *ap = match_kw(g, "append ");
        if (ap) {
            /* AppendRx: ^append (.+?) to (.+)$ — non-greedy up to " to ". */
            while (*ap == ' ') ap++;
            /* find the last " to " occurrence's first? C# non-greedy takes first. */
            const char *to = strstr(ap, " to ");
            if (to) {
                size_t textlen = (size_t)(to - ap);
                char *text = (char *)malloc(textlen + 1);
                const char *pathp = to + 4;
                char *appendtext = NULL, *path = NULL;
                if (text) {
                    memcpy(text, ap, textlen); text[textlen] = '\0';
                    appendtext = trim_dup2(text);
                    free(text);
                }
                path = trim_dup2(pathp);
                if (appendtext) {
                    /* trim surrounding quotes (.Trim('"')) */
                    size_t al = strlen(appendtext);
                    if (al >= 2 && appendtext[0] == '"' && appendtext[al - 1] == '"') {
                        memmove(appendtext, appendtext + 1, al - 2);
                        appendtext[al - 2] = '\0';
                    }
                }
                if (appendtext && path) {
                    char *buf = editor ? ca_code_editor_read(editor, path) : NULL;
                    int len = buf ? (int)strlen(buf) : 0;
                    ok = edit_vec_push(&ev, path, len, len, appendtext);
                    free(buf);
                    snprintf(stepbuf, sizeof(stepbuf), "Append to %s", path);
                    ok = ok && steps_single(&out->steps, &out->step_count, stepbuf);
                    handled = true;
                }
                free(appendtext); free(path);
            }
        }
    }

    if (!handled) {
        ok = steps_single(&out->steps, &out->step_count, "no recognised intent");
    }

    free(g);
    if (!ok) { ca_file_edit_free_array(ev.v, ev.n); ca_patch_plan_free(out); return false; }
    out->proposed_edits = ev.v;
    out->edit_count = ev.n;
    return true;
}

int ca_patch_plan_apply(ca_code_editor_t *editor, const ca_patch_plan_t *plan) {
    if (!editor || !plan) return -1;
    return ca_code_editor_apply(editor, plan->proposed_edits, plan->edit_count);
}

/* ── RegexRefactorTool ──────────────────────────────────────────────────── */

const char *ca_refactor_tool_backend_id(void) { return "regex"; }
const char *ca_dt_null_refactor_tool_backend_id(void) { return "null"; }

ca_file_edit_t *ca_refactor_propose(const ca_code_editor_t *editor,
                                    const char *description,
                                    const char *const *target_paths,
                                    size_t target_count, size_t *out_count) {
    if (!out_count) return NULL;
    if (!description || (!target_paths && target_count > 0)) { *out_count = (size_t)-1; return NULL; }

    char *desc = trim_dup2(description);
    if (!desc) { *out_count = (size_t)-1; return NULL; }

    edit_vec_t ev = {0};
    bool ok = true;

    const char *rp = match_kw(desc, "rename ");
    const char *ep = match_kw(desc, "extract ");
    if (rp) {
        while (*rp == ' ') rp++;
        const char *xs = rp; while (*rp && *rp != ' ') rp++;
        char oldn[128]; size_t xn = (size_t)(rp - xs); if (xn >= sizeof(oldn)) xn = sizeof(oldn) - 1;
        memcpy(oldn, xs, xn); oldn[xn] = '\0';
        while (*rp == ' ') rp++;
        const char *tp = match_kw(rp, "to ");
        if (tp && xn > 0) {
            while (*tp == ' ') tp++;
            const char *ys = tp; while (*tp && *tp != ' ') tp++;
            char newn[128]; size_t yn = (size_t)(tp - ys); if (yn >= sizeof(newn)) yn = sizeof(newn) - 1;
            memcpy(newn, ys, yn); newn[yn] = '\0';
            for (size_t i = 0; ok && i < target_count && editor; ++i) {
                char *text = ca_code_editor_read(editor, target_paths[i]);
                if (!text) continue; /* File.Exists check */
                ok = push_word_renames(&ev, target_paths[i], text, oldn, newn);
                free(text);
            }
        }
    } else if (ep) {
        /* extract constant from "LIT" as NAME */
        const char *cp = match_kw(ep, "constant ");
        if (cp) {
            const char *fp = strstr(cp, "from ");
            if (fp) {
                fp += 5;
                while (*fp == ' ') fp++;
                if (*fp == '"') {
                    fp++;
                    const char *lit_s = fp;
                    while (*fp && *fp != '"') fp++;
                    if (*fp == '"') {
                        size_t litlen = (size_t)(fp - lit_s);
                        char *lit = (char *)malloc(litlen + 1);
                        char *name = NULL;
                        if (lit) { memcpy(lit, lit_s, litlen); lit[litlen] = '\0'; }
                        fp++;
                        const char *asp = strstr(fp, "as ");
                        if (asp) {
                            asp += 3; while (*asp == ' ') asp++;
                            const char *ns = asp; while (*asp && *asp != ' ') asp++;
                            size_t nn = (size_t)(asp - ns);
                            name = (char *)malloc(nn + 1);
                            if (name) { memcpy(name, ns, nn); name[nn] = '\0'; }
                        }
                        if (lit && name) {
                            /* quoted = "\"" + literal + "\"" */
                            size_t qlen = litlen + 2;
                            char *quoted = (char *)malloc(qlen + 1);
                            if (quoted) {
                                quoted[0] = '"'; memcpy(quoted + 1, lit, litlen);
                                quoted[qlen - 1] = '"'; quoted[qlen] = '\0';
                            }
                            for (size_t i = 0; ok && quoted && i < target_count && editor; ++i) {
                                char *text = ca_code_editor_read(editor, target_paths[i]);
                                if (!text) continue;
                                const char *first = strstr(text, quoted);
                                if (!first) { free(text); continue; }
                                const char *classk = strstr(text, "class ");
                                if (!classk) { free(text); continue; }
                                const char *brace = strchr(classk, '{');
                                if (!brace) { free(text); continue; }
                                int binsert = (int)(brace - text) + 1;
                                /* insertion = "\n    private const string NAME = \"LIT\";\n" */
                                size_t inslen = strlen("\n    private const string ") +
                                                strlen(name) + strlen(" = ") + qlen +
                                                strlen(";\n") + 1;
                                char *insertion = (char *)malloc(inslen);
                                if (!insertion) { free(text); ok = false; break; }
                                snprintf(insertion, inslen,
                                         "\n    private const string %s = %s;\n",
                                         name, quoted);
                                ok = edit_vec_push(&ev, target_paths[i], binsert, binsert, insertion);
                                free(insertion);
                                /* replace every literal occurrence */
                                for (const char *idx = strstr(text, quoted); ok && idx;
                                     idx = strstr(idx + 1, quoted)) {
                                    int pos = (int)(idx - text);
                                    ok = edit_vec_push(&ev, target_paths[i], pos,
                                                       pos + (int)qlen, name);
                                }
                                free(text);
                            }
                            free(quoted);
                        }
                        free(lit); free(name);
                    }
                }
            }
        }
    }

    free(desc);
    if (!ok) { ca_file_edit_free_array(ev.v, ev.n); *out_count = (size_t)-1; return NULL; }
    if (ev.n == 0) { free(ev.v); *out_count = 0; return NULL; }
    *out_count = ev.n;
    return ev.v;
}
