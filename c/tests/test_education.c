/*
 * test_education.c — CircleAI.Education (C11 port) verification against
 * EducationPrimitives.cs.
 */

#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <assert.h>
#include <math.h>
#include "circle_ai/circle_ai.h"

static ca_edu_course_t mk_course(const char *id, const char *name) {
    ca_edu_course_t c; memset(&c, 0, sizeof(c));
    c.course_id = (char *)id; c.name = (char *)name;
    c.subject = (char *)"Math"; c.grade_band = (char *)"7-9";
    return c;
}
static ca_edu_lesson_t mk_lesson(const char *id, const char *cid,
                                 const char *title, int order) {
    ca_edu_lesson_t l; memset(&l, 0, sizeof(l));
    l.lesson_id = (char *)id; l.course_id = (char *)cid; l.title = (char *)title;
    l.duration_ticks = 6000000000LL; l.order_index = order;   /* 10 min */
    return l;
}
static ca_edu_student_t mk_student(const char *id, const char *name,
                                   const char *cid, double pct) {
    ca_edu_student_t s; memset(&s, 0, sizeof(s));
    s.student_id = (char *)id; s.name = (char *)name; s.course_id = (char *)cid;
    s.progress_pct = pct;
    return s;
}

static void test_courses_lessons(void) {
    ca_edu_board_t *b = ca_edu_board_create();
    assert(b);

    ca_edu_course_t c = mk_course("c1", "Algebra");
    assert(ca_edu_board_add_course(b, &c) == 0);
    ca_edu_course_t got;
    assert(ca_edu_board_get_course(b, "c1", &got));
    assert(strcmp(got.name, "Algebra") == 0 && strcmp(got.subject, "Math") == 0);
    ca_edu_course_free(&got);
    assert(!ca_edu_board_get_course(b, "nope", &got));

    /* lessons ordered by OrderIndex ascending regardless of insertion order. */
    ca_edu_lesson_t l3 = mk_lesson("l3", "c1", "Third", 3);
    ca_edu_lesson_t l1 = mk_lesson("l1", "c1", "First", 1);
    ca_edu_lesson_t l2 = mk_lesson("l2", "c1", "Second", 2);
    ca_edu_lesson_t lx = mk_lesson("lx", "c2", "Other", 1);
    assert(ca_edu_board_add_lesson(b, &l3) == 0);
    assert(ca_edu_board_add_lesson(b, &l1) == 0);
    assert(ca_edu_board_add_lesson(b, &l2) == 0);
    assert(ca_edu_board_add_lesson(b, &lx) == 0);

    size_t n = 0;
    ca_edu_lesson_t *arr = ca_edu_board_lessons_for(b, "c1", &n);
    assert(n == 3);
    assert(strcmp(arr[0].lesson_id, "l1") == 0);
    assert(strcmp(arr[1].lesson_id, "l2") == 0);
    assert(strcmp(arr[2].lesson_id, "l3") == 0);
    ca_edu_lesson_free_array(arr, n);

    arr = ca_edu_board_lessons_for(b, "zzz", &n);
    assert(n == 0 && arr == NULL);

    ca_edu_board_destroy(b);
    printf("  courses_lessons: ok\n");
}

static void test_students(void) {
    ca_edu_board_t *b = ca_edu_board_create();

    /* UpdateProgress on unknown -> 1. */
    assert(ca_edu_board_update_progress(b, "sX", 50.0) == 1);
    /* AvgProgressFor with no students -> 0.0. */
    assert(ca_edu_board_avg_progress_for(b, "c1") == 0.0);

    ca_edu_student_t s1 = mk_student("s1", "Al", "c1", 20.0);
    ca_edu_student_t s2 = mk_student("s2", "Bo", "c1", 40.0);
    ca_edu_student_t s3 = mk_student("s3", "Cy", "c2", 100.0);
    assert(ca_edu_board_enrol(b, &s1) == 0);
    assert(ca_edu_board_enrol(b, &s2) == 0);
    assert(ca_edu_board_enrol(b, &s3) == 0);

    size_t n = 0;
    ca_edu_student_t *arr = ca_edu_board_students_for(b, "c1", &n);
    assert(n == 2);
    ca_edu_student_free_array(arr, n);

    /* avg over c1 = (20+40)/2 = 30. */
    assert(fabs(ca_edu_board_avg_progress_for(b, "c1") - 30.0) < 1e-9);
    /* avg over c2 = 100. */
    assert(fabs(ca_edu_board_avg_progress_for(b, "c2") - 100.0) < 1e-9);

    /* UpdateProgress mutates. */
    assert(ca_edu_board_update_progress(b, "s1", 80.0) == 0);
    assert(fabs(ca_edu_board_avg_progress_for(b, "c1") - 60.0) < 1e-9);

    ca_edu_board_destroy(b);
    printf("  students: ok\n");
}

int main(void) {
    test_courses_lessons();
    test_students();
    printf("test_education: all assertions passed\n");
    return 0;
}
