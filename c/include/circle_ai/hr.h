#ifndef CIRCLE_AI_HR_H
#define CIRCLE_AI_HR_H

/*
 * hr.h — CircleAI.HR (C11 port of HRPrimitives.cs).
 *
 *   Records : Employee(EmployeeId, Name, Role, DateTime HiredOn,
 *                       decimal Salary, Currency);
 *             LeaveRequest(RequestId, EmployeeId, Kind, DateTime From,
 *                          DateTime To, Status);
 *             PerformanceReview(ReviewId, EmployeeId, DateTime ReviewedOn,
 *                               int RatingOutOf5, Notes).
 *   Board   : IHRBoard -> InMemoryHRBoard
 *               Hire (EmployeeId keyed set), GetEmployee(id) -> employee?,
 *               Employees ordered by Name asc, Request (RequestId keyed set),
 *               DecideLeave(requestId, decision) (throws on unknown => rc 1),
 *               PendingLeaves() where Status == "Pending" (OrdinalIgnoreCase)
 *               in insertion order, Review (appends), AvgRatingFor(employeeId)
 *               mean of RatingOutOf5 over that employee, 0.0 when none
 *               (DefaultIfEmpty(0).Average()).
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / count SIZE_MAX. decimal
 * Salary as ca_hr_decimal_t (int64 scaled 1e6). DateTime fields as int64 Unix ms
 * UTC. Linear arrays, no pthreads. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef int64_t ca_hr_decimal_t;
#define CA_HR_DECIMAL_SCALE 1000000LL

/* Employee(EmployeeId, Name, Role, DateTime HiredOn, decimal Salary, Currency). */
typedef struct {
    char           *employee_id; /* owned, non-null */
    char           *name;        /* owned, non-null */
    char           *role;        /* owned, non-null */
    int64_t         hired_on_ms; /* DateTime as Unix ms UTC */
    ca_hr_decimal_t salary;
    char           *currency;    /* owned, non-null */
} ca_hr_employee_t;

void ca_hr_employee_free(ca_hr_employee_t *e);
void ca_hr_employee_free_array(ca_hr_employee_t *arr, size_t count);

/* LeaveRequest(RequestId, EmployeeId, Kind, DateTime From, DateTime To, Status). */
typedef struct {
    char   *request_id;  /* owned, non-null */
    char   *employee_id; /* owned, non-null */
    char   *kind;        /* owned, non-null */
    int64_t from_ms;     /* DateTime as Unix ms UTC */
    int64_t to_ms;       /* DateTime as Unix ms UTC */
    char   *status;      /* owned, non-null */
} ca_hr_leave_t;

void ca_hr_leave_free(ca_hr_leave_t *r);
void ca_hr_leave_free_array(ca_hr_leave_t *arr, size_t count);

/* PerformanceReview(ReviewId, EmployeeId, DateTime ReviewedOn, int RatingOutOf5,
 * Notes). */
typedef struct {
    char   *review_id;    /* owned, non-null */
    char   *employee_id;  /* owned, non-null */
    int64_t reviewed_on_ms;
    int     rating_out_of_5;
    char   *notes;        /* owned, non-null */
} ca_hr_review_t;

void ca_hr_review_free(ca_hr_review_t *r);

typedef struct ca_hr_board ca_hr_board_t;

ca_hr_board_t *ca_hr_board_create(void); /* NULL on OOM */
void ca_hr_board_destroy(ca_hr_board_t *b);

/* Hire(e) — EmployeeId keys the store (replace). 0 / -1 on bad args/OOM. */
int ca_hr_board_hire(ca_hr_board_t *b, const ca_hr_employee_t *e);

/* GetEmployee(id) -> fresh owned copy into *out, true; false on miss/bad args. */
bool ca_hr_board_get_employee(const ca_hr_board_t *b, const char *id,
                              ca_hr_employee_t *out);

/* Employees -> fresh owned array (*out_count) ordered by Name asc. NULL + 0 when
 * empty; NULL + SIZE_MAX on error. */
ca_hr_employee_t *ca_hr_board_employees(const ca_hr_board_t *b,
                                        size_t *out_count);

/* Request(r) — RequestId keys the store. 0 / -1. */
int ca_hr_board_request(ca_hr_board_t *b, const ca_hr_leave_t *r);

/* DecideLeave(requestId, decision) — replaces Status. 0 on success, -1 on bad
 * args/OOM, 1 when unknown (InvalidOperationException). */
int ca_hr_board_decide_leave(ca_hr_board_t *b, const char *request_id,
                             const char *decision);

/* PendingLeaves() -> fresh owned array (*out_count): Status == "Pending"
 * (OrdinalIgnoreCase) in insertion order. NULL + 0 when empty; NULL + SIZE_MAX
 * on error. */
ca_hr_leave_t *ca_hr_board_pending_leaves(const ca_hr_board_t *b,
                                          size_t *out_count);

/* Review(r) — appends. 0 / -1. */
int ca_hr_board_review(ca_hr_board_t *b, const ca_hr_review_t *r);

/* AvgRatingFor(employeeId) -> mean RatingOutOf5 over that employee, 0.0 when
 * none (DefaultIfEmpty(0).Average()). */
double ca_hr_board_avg_rating_for(const ca_hr_board_t *b,
                                  const char *employee_id);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_HR_H */
