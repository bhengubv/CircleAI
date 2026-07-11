/*
 * education.c — CircleAI.Education (C11 port of EducationPrimitives.cs).
 *
 * InMemoryEducationBoard over three linear stores (courses / lessons / students),
 * each id-keyed with dictionary-set replace semantics. Pure C11 + libc.
 */

#include "circle_ai/education.h"
#include "board_common.h"

/* ── record deep-copy / free ────────────────────────────────────────────── */

void ca_edu_course_free(ca_edu_course_t *c) {
    if (!c) return;
    free(c->course_id);
    free(c->name);
    free(c->subject);
    free(c->grade_band);
    c->course_id = c->name = c->subject = c->grade_band = NULL;
}

static bool course_copy(ca_edu_course_t *dst, const ca_edu_course_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->course_id  = cab_strdup_empty(src->course_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->subject    = cab_strdup_empty(src->subject);
    dst->grade_band = cab_strdup_empty(src->grade_band);
    if (!dst->course_id || !dst->name || !dst->subject || !dst->grade_band) {
        ca_edu_course_free(dst);
        return false;
    }
    return true;
}

void ca_edu_lesson_free(ca_edu_lesson_t *l) {
    if (!l) return;
    free(l->lesson_id);
    free(l->course_id);
    free(l->title);
    l->lesson_id = l->course_id = l->title = NULL;
}
void ca_edu_lesson_free_array(ca_edu_lesson_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_edu_lesson_free(&arr[i]);
    free(arr);
}

static bool lesson_copy(ca_edu_lesson_t *dst, const ca_edu_lesson_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->lesson_id = cab_strdup_empty(src->lesson_id);
    dst->course_id = cab_strdup_empty(src->course_id);
    dst->title     = cab_strdup_empty(src->title);
    dst->duration_ticks = src->duration_ticks;
    dst->order_index    = src->order_index;
    if (!dst->lesson_id || !dst->course_id || !dst->title) {
        ca_edu_lesson_free(dst);
        return false;
    }
    return true;
}

void ca_edu_student_free(ca_edu_student_t *s) {
    if (!s) return;
    free(s->student_id);
    free(s->name);
    free(s->course_id);
    s->student_id = s->name = s->course_id = NULL;
}
void ca_edu_student_free_array(ca_edu_student_t *arr, size_t count) {
    if (!arr) return;
    for (size_t i = 0; i < count; ++i) ca_edu_student_free(&arr[i]);
    free(arr);
}

static bool student_copy(ca_edu_student_t *dst, const ca_edu_student_t *src) {
    memset(dst, 0, sizeof(*dst));
    dst->student_id = cab_strdup_empty(src->student_id);
    dst->name       = cab_strdup_empty(src->name);
    dst->course_id  = cab_strdup_empty(src->course_id);
    dst->progress_pct = src->progress_pct;
    if (!dst->student_id || !dst->name || !dst->course_id) {
        ca_edu_student_free(dst);
        return false;
    }
    return true;
}

/* ── board ──────────────────────────────────────────────────────────────── */

struct ca_edu_board {
    ca_edu_course_t  *courses;
    size_t            course_count, course_cap;
    ca_edu_lesson_t  *lessons;
    size_t            lesson_count, lesson_cap;
    ca_edu_student_t *students;
    size_t            student_count, student_cap;
};

ca_edu_board_t *ca_edu_board_create(void) {
    return (ca_edu_board_t *)calloc(1, sizeof(ca_edu_board_t));
}
void ca_edu_board_destroy(ca_edu_board_t *b) {
    if (!b) return;
    for (size_t i = 0; i < b->course_count; ++i)  ca_edu_course_free(&b->courses[i]);
    for (size_t i = 0; i < b->lesson_count; ++i)  ca_edu_lesson_free(&b->lessons[i]);
    for (size_t i = 0; i < b->student_count; ++i) ca_edu_student_free(&b->students[i]);
    free(b->courses);
    free(b->lessons);
    free(b->students);
    free(b);
}

int ca_edu_board_add_course(ca_edu_board_t *b, const ca_edu_course_t *c) {
    if (!b || !c) return -1;
    for (size_t i = 0; i < b->course_count; ++i) {
        if (cab_ord_eq(b->courses[i].course_id, c->course_id)) {
            ca_edu_course_t copy;
            if (!course_copy(&copy, c)) return -1;
            ca_edu_course_free(&b->courses[i]);
            b->courses[i] = copy;
            return 0;
        }
    }
    ca_edu_course_t copy;
    if (!course_copy(&copy, c)) return -1;
    if (b->course_count == b->course_cap) {
        size_t nc = b->course_cap ? b->course_cap * 2 : 4;
        void *n = realloc(b->courses, nc * sizeof(*b->courses));
        if (!n) { ca_edu_course_free(&copy); return -1; }
        b->courses = (ca_edu_course_t *)n;
        b->course_cap = nc;
    }
    b->courses[b->course_count++] = copy;
    return 0;
}

bool ca_edu_board_get_course(const ca_edu_board_t *b, const char *id,
                             ca_edu_course_t *out) {
    if (out) memset(out, 0, sizeof(*out));
    if (!b || !id || !out) return false;
    for (size_t i = 0; i < b->course_count; ++i)
        if (cab_ord_eq(b->courses[i].course_id, id))
            return course_copy(out, &b->courses[i]);
    return false;
}

int ca_edu_board_add_lesson(ca_edu_board_t *b, const ca_edu_lesson_t *l) {
    if (!b || !l) return -1;
    for (size_t i = 0; i < b->lesson_count; ++i) {
        if (cab_ord_eq(b->lessons[i].lesson_id, l->lesson_id)) {
            ca_edu_lesson_t copy;
            if (!lesson_copy(&copy, l)) return -1;
            ca_edu_lesson_free(&b->lessons[i]);
            b->lessons[i] = copy;
            return 0;
        }
    }
    ca_edu_lesson_t copy;
    if (!lesson_copy(&copy, l)) return -1;
    if (b->lesson_count == b->lesson_cap) {
        size_t nc = b->lesson_cap ? b->lesson_cap * 2 : 4;
        void *n = realloc(b->lessons, nc * sizeof(*b->lessons));
        if (!n) { ca_edu_lesson_free(&copy); return -1; }
        b->lessons = (ca_edu_lesson_t *)n;
        b->lesson_cap = nc;
    }
    b->lessons[b->lesson_count++] = copy;
    return 0;
}

/* Stable ascending sort of collected indices by order_index. */
static void lesson_sort_asc(const ca_edu_board_t *b, size_t *idx, size_t n) {
    for (size_t i = 1; i < n; ++i) {
        size_t key = idx[i];
        int ko = b->lessons[key].order_index;
        size_t j = i;
        while (j > 0 && b->lessons[idx[j - 1]].order_index > ko) {
            idx[j] = idx[j - 1];
            j--;
        }
        idx[j] = key;
    }
}

ca_edu_lesson_t *ca_edu_board_lessons_for(const ca_edu_board_t *b,
                                          const char *course_id,
                                          size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !course_id) { *out_count = (size_t)-1; return NULL; }
    if (b->lesson_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->lesson_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->lesson_count; ++i)
        if (cab_ord_eq(b->lessons[i].course_id, course_id)) idx[n++] = i;
    lesson_sort_asc(b, idx, n);

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_edu_lesson_t *out = (ca_edu_lesson_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!lesson_copy(&out[i], &b->lessons[idx[i]])) {
            ca_edu_lesson_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

int ca_edu_board_enrol(ca_edu_board_t *b, const ca_edu_student_t *r) {
    if (!b || !r) return -1;
    for (size_t i = 0; i < b->student_count; ++i) {
        if (cab_ord_eq(b->students[i].student_id, r->student_id)) {
            ca_edu_student_t copy;
            if (!student_copy(&copy, r)) return -1;
            ca_edu_student_free(&b->students[i]);
            b->students[i] = copy;
            return 0;
        }
    }
    ca_edu_student_t copy;
    if (!student_copy(&copy, r)) return -1;
    if (b->student_count == b->student_cap) {
        size_t nc = b->student_cap ? b->student_cap * 2 : 4;
        void *n = realloc(b->students, nc * sizeof(*b->students));
        if (!n) { ca_edu_student_free(&copy); return -1; }
        b->students = (ca_edu_student_t *)n;
        b->student_cap = nc;
    }
    b->students[b->student_count++] = copy;
    return 0;
}

int ca_edu_board_update_progress(ca_edu_board_t *b, const char *student_id,
                                 double pct) {
    if (!b || !student_id) return -1;
    for (size_t i = 0; i < b->student_count; ++i) {
        if (cab_ord_eq(b->students[i].student_id, student_id)) {
            b->students[i].progress_pct = pct;
            return 0;
        }
    }
    return 1;   /* InvalidOperationException: unknown student */
}

ca_edu_student_t *ca_edu_board_students_for(const ca_edu_board_t *b,
                                            const char *course_id,
                                            size_t *out_count) {
    if (!out_count) return NULL;
    if (!b || !course_id) { *out_count = (size_t)-1; return NULL; }
    if (b->student_count == 0) { *out_count = 0; return NULL; }

    size_t *idx = (size_t *)malloc(b->student_count * sizeof(size_t));
    if (!idx) { *out_count = (size_t)-1; return NULL; }
    size_t n = 0;
    for (size_t i = 0; i < b->student_count; ++i)
        if (cab_ord_eq(b->students[i].course_id, course_id)) idx[n++] = i;

    if (n == 0) { free(idx); *out_count = 0; return NULL; }
    ca_edu_student_t *out = (ca_edu_student_t *)calloc(n, sizeof(*out));
    if (!out) { free(idx); *out_count = (size_t)-1; return NULL; }
    for (size_t i = 0; i < n; ++i) {
        if (!student_copy(&out[i], &b->students[idx[i]])) {
            ca_edu_student_free_array(out, i);
            free(idx);
            *out_count = (size_t)-1;
            return NULL;
        }
    }
    free(idx);
    *out_count = n;
    return out;
}

double ca_edu_board_avg_progress_for(const ca_edu_board_t *b,
                                     const char *course_id) {
    if (!b || !course_id) return 0.0;
    double sum = 0.0;
    size_t n = 0;
    for (size_t i = 0; i < b->student_count; ++i) {
        if (cab_ord_eq(b->students[i].course_id, course_id)) {
            sum += b->students[i].progress_pct;
            n++;
        }
    }
    return n == 0 ? 0.0 : sum / (double)n;
}
