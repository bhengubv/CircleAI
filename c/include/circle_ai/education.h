#ifndef CIRCLE_AI_EDUCATION_H
#define CIRCLE_AI_EDUCATION_H

/*
 * education.h — CircleAI.Education (C11 port of EducationPrimitives.cs).
 *
 *   Records : Course(CourseId, Name, Subject, GradeBand);
 *             Lesson(LessonId, CourseId, Title, TimeSpan Duration, OrderIndex);
 *             StudentRecord(StudentId, Name, CourseId, ProgressPct).
 *   Board   : IEducationBoard -> InMemoryEducationBoard.
 *             AddCourse(c) (CourseId keyed set), GetCourse(id) -> course?,
 *             AddLesson(l) (LessonId keyed set), LessonsFor(courseId) ordered by
 *             OrderIndex ascending, Enrol(r) (StudentId keyed set),
 *             UpdateProgress(studentId, pct) (throws on unknown student),
 *             StudentsFor(courseId) (insertion order), AvgProgressFor(courseId)
 *             (0.0 when none).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. Duration
 * is TimeSpan ticks (100ns). Linear arrays, no pthreads.
 *
 * Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Course(CourseId, Name, Subject, GradeBand). */
typedef struct {
    char *course_id;   /* owned, non-null */
    char *name;        /* owned, non-null */
    char *subject;     /* owned, non-null */
    char *grade_band;  /* owned, non-null */
} ca_edu_course_t;

void ca_edu_course_free(ca_edu_course_t *c);

/* Lesson(LessonId, CourseId, Title, TimeSpan Duration, int OrderIndex). */
typedef struct {
    char   *lesson_id;      /* owned, non-null */
    char   *course_id;      /* owned, non-null */
    char   *title;          /* owned, non-null */
    int64_t duration_ticks; /* TimeSpan ticks */
    int     order_index;
} ca_edu_lesson_t;

void ca_edu_lesson_free(ca_edu_lesson_t *l);
void ca_edu_lesson_free_array(ca_edu_lesson_t *arr, size_t count);

/* StudentRecord(StudentId, Name, CourseId, double ProgressPct). */
typedef struct {
    char  *student_id;  /* owned, non-null */
    char  *name;        /* owned, non-null */
    char  *course_id;   /* owned, non-null */
    double progress_pct;
} ca_edu_student_t;

void ca_edu_student_free(ca_edu_student_t *s);
void ca_edu_student_free_array(ca_edu_student_t *arr, size_t count);

typedef struct ca_edu_board ca_edu_board_t;

/* InMemoryEducationBoard(). NULL on OOM. */
ca_edu_board_t *ca_edu_board_create(void);
void ca_edu_board_destroy(ca_edu_board_t *b);

/* AddCourse(c) — deep-copies; CourseId keyed set. 0 / -1 on bad args/OOM. */
int ca_edu_board_add_course(ca_edu_board_t *b, const ca_edu_course_t *c);
/* GetCourse(id) -> fresh owned copy into *out, true; false on miss. */
bool ca_edu_board_get_course(const ca_edu_board_t *b, const char *id,
                             ca_edu_course_t *out);

/* AddLesson(l) — deep-copies; LessonId keyed set. 0 / -1. */
int ca_edu_board_add_lesson(ca_edu_board_t *b, const ca_edu_lesson_t *l);
/* LessonsFor(courseId) -> fresh owned array (*out_count) ordered by OrderIndex
 * ascending. NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_edu_lesson_t *ca_edu_board_lessons_for(const ca_edu_board_t *b,
                                          const char *course_id,
                                          size_t *out_count);

/* Enrol(r) — deep-copies; StudentId keyed set. 0 / -1. */
int ca_edu_board_enrol(ca_edu_board_t *b, const ca_edu_student_t *r);
/* UpdateProgress(studentId, pct). 0 on success, -1 on bad args, 1 when the
 * student is unknown (InvalidOperationException). */
int ca_edu_board_update_progress(ca_edu_board_t *b, const char *student_id,
                                 double pct);
/* StudentsFor(courseId) -> fresh owned array (*out_count) in insertion order.
 * NULL + 0 when empty; NULL + SIZE_MAX on error. */
ca_edu_student_t *ca_edu_board_students_for(const ca_edu_board_t *b,
                                            const char *course_id,
                                            size_t *out_count);
/* AvgProgressFor(courseId) -> mean ProgressPct of the course's students, 0.0
 * when none. */
double ca_edu_board_avg_progress_for(const ca_edu_board_t *b,
                                     const char *course_id);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_EDUCATION_H */
