#ifndef CIRCLE_AI_CAREER_H
#define CIRCLE_AI_CAREER_H

/*
 * career.h - CircleAI.Career (C11).
 *
 * The profile, the interview that fills it in, tailoring it to one job advert,
 * and rendering the result.
 *
 * WHY A SCHEMA AND NOT A BLOB. The point of the profile is that it is queryable
 * and reusable: the same facts answer "draft me a CV for this security job"
 * today and "which of my jobs match this one" next month. A blob can be
 * rendered and cannot be reasoned about - and a blob is exactly what people
 * already have, a CV.doc they edit and re-save until nobody knows which one
 * they sent.
 *
 * ORGANISATION IS OPTIONAL AND `formal` IS A FLAG. Piece work, a family
 * business and a season on a farm are all work history. A schema that only
 * accepts salaried employment quietly tells most of the country it has never
 * worked, and that is the exact population this is for.
 *
 * EVIDENCE, NOT ASSERTION. A skill carries the id of the job where it was used,
 * so a CV can cite it instead of claiming a level nobody can check.
 *
 * ON-DEVICE, AND THERE IS NO SYNC. Employment history and contact details are
 * the personal information most able to do harm if it travelled. Nothing in
 * this module opens a socket.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, deep-copy getters, errors via NULL / SIZE_MAX. Pure C11 +
 * libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* ── identity ─────────────────────────────────────────────────────────────── */

/* Every field is optional except the name: a person part-way through the
 * interview has a real profile, not an invalid one. */
typedef struct {
    char *full_name;
    char *headline;
    char *phone;
    char *email;
    char *location;
    char *summary;
} ca_profile_identity_t;

void ca_profile_identity_free(ca_profile_identity_t *identity);

/* ── history ──────────────────────────────────────────────────────────────── */

typedef struct {
    int64_t id;
    char *role;
    /* NULL for piece work and self-employment, which is most of it. */
    char *organisation;
    bool formal;
    /* Free text, not dates. "2019", "winter 2020" and "about three years ago"
     * are all real answers, and a date picker that refuses them loses the job. */
    char *start_text;
    char *end_text;
    char **achievements;
    size_t achievement_count;
} ca_profile_history_t;

void ca_profile_history_free(ca_profile_history_t *history);

/* ── skills, education, certifications, languages ─────────────────────────── */

typedef struct {
    int64_t id;
    char *name;
    /* Negative means unstated. Zero is a real answer - somebody starting out. */
    double years;
    /* The history row this was used in, or 0 for none. This is what turns a
     * claim into something a reader can check. */
    int64_t evidence_history_id;
} ca_profile_skill_t;

void ca_profile_skill_free(ca_profile_skill_t *skill);

typedef struct {
    int64_t id;
    char *qualification;
    char *institution;
    char *year;
    /* An incomplete qualification is still worth listing, and hiding it is how
     * a schema decides somebody's three years did not happen. */
    bool completed;
} ca_profile_education_t;

void ca_profile_education_free(ca_profile_education_t *education);

typedef struct {
    int64_t id;
    char *name;
    char *issuer;
    char *year;
    char *expires;
} ca_profile_certification_t;

void ca_profile_certification_free(ca_profile_certification_t *certification);

typedef struct {
    int64_t id;
    char *name;
    /* Free text: "home language", "conversational", "read only" are all more
     * useful than a five-point scale nobody agrees on. */
    char *level;
} ca_profile_language_t;

void ca_profile_language_free(ca_profile_language_t *language);

/* ── the whole profile ────────────────────────────────────────────────────── */

typedef struct {
    ca_profile_identity_t identity;

    ca_profile_history_t *history;
    size_t history_count;

    ca_profile_skill_t *skills;
    size_t skill_count;

    ca_profile_education_t *education;
    size_t education_count;

    ca_profile_certification_t *certifications;
    size_t certification_count;

    ca_profile_language_t *languages;
    size_t language_count;
} ca_career_profile_t;

ca_career_profile_t *ca_career_profile_new(void);
void ca_career_profile_free(ca_career_profile_t *profile);

/* Each returns the new row's id, or 0 on failure. The profile takes a deep
 * copy: a caller may free what it passed. */
int64_t ca_career_profile_add_history(ca_career_profile_t *profile,
                                      const ca_profile_history_t *history);
int64_t ca_career_profile_add_skill(ca_career_profile_t *profile,
                                    const ca_profile_skill_t *skill);
int64_t ca_career_profile_add_education(ca_career_profile_t *profile,
                                        const ca_profile_education_t *education);
int64_t ca_career_profile_add_certification(ca_career_profile_t *profile,
                                            const ca_profile_certification_t *certification);
int64_t ca_career_profile_add_language(ca_career_profile_t *profile,
                                       const ca_profile_language_t *language);

/* ── the interview ────────────────────────────────────────────────────────── */

/* Which part of the profile a question is filling in. */
typedef enum {
    CA_PROFILE_FIELD_IDENTITY = 0,
    CA_PROFILE_FIELD_HISTORY,
    CA_PROFILE_FIELD_SKILL,
    CA_PROFILE_FIELD_EDUCATION,
    CA_PROFILE_FIELD_CERTIFICATION,
    CA_PROFILE_FIELD_LANGUAGE
} ca_profile_field_t;

typedef struct {
    ca_profile_field_t field;
    /* Asked out loud. Written as somebody would actually say it, because this
     * is read by a voice and "Enter your employment history" is not a question
     * anybody answers. */
    char *prompt;
    /* An answer may be skipped. A person who does not want to give an email
     * address must be able to finish the profile without one. */
    bool required;
} ca_interview_question_t;

void ca_interview_question_free(ca_interview_question_t *question);

typedef struct ca_career_interview ca_career_interview_t;

ca_career_interview_t *ca_career_interview_new(void);
void ca_career_interview_free(ca_career_interview_t *interview);

/* The next question, or NULL when there is nothing left to ask. Caller frees. */
ca_interview_question_t *ca_career_interview_next(ca_career_interview_t *interview);

/* Records an answer and advances. An empty answer to an optional question is a
 * SKIP, not a failure - the difference is what lets somebody finish. */
bool ca_career_interview_answer(ca_career_interview_t *interview, const char *answer);

/* 0.0 to 1.0. Shown to a person, so it moves with every answer rather than
 * jumping at the end of a section. */
double ca_career_interview_progress(const ca_career_interview_t *interview);

/* ── job specs ────────────────────────────────────────────────────────────── */

typedef struct {
    int64_t id;
    char *title;
    char *employer;
    char *text;
    /* "typed", "pasted", "photographed", "dictated". Kept because a spec read
     * off a photograph is likelier to be garbled, and a later mismatch is
     * easier to explain when the source is known. */
    char *source;
    int64_t added_unix;
} ca_job_spec_t;

void ca_job_spec_free(ca_job_spec_t *spec);

/* ── tailoring ────────────────────────────────────────────────────────────── */

/* One decision about one fact: include it, and WHY.
 *
 * The reason is not decoration. The person is the one signing the application,
 * so they have to be able to see why a job was left out and put it back. A
 * ranked list with no reasons is something they can only accept or reject
 * wholesale. */
typedef struct {
    int64_t fact_id;
    char *text;
    bool include;
    char *reason;
    double score;
} ca_tailoring_choice_t;

void ca_tailoring_choice_free(ca_tailoring_choice_t *choice);

/* Chooses which facts go into an application, by word overlap with the advert.
 *
 * A fact with NO overlap is excluded rather than padded in: an application that
 * lists everything is the CV the person already had. Returns a heap array of
 * `*out_count` choices, newest-scoring first, or NULL. Caller frees each choice
 * and then the array. */
ca_tailoring_choice_t *ca_profile_tailoring_choose(const ca_career_profile_t *profile,
                                                   const ca_job_spec_t *spec,
                                                   size_t max_facts,
                                                   size_t *out_count);

/* ── rendering ────────────────────────────────────────────────────────────── */

/* Renders the chosen facts as plain text. Caller frees.
 *
 * TEXT, not PDF: a PDF writer is a host concern, and the layout decisions -
 * contact at the top, omit empty sections - are the part worth carrying. An
 * employer who cannot find a phone number in three seconds does not scroll. */
char *ca_profile_to_cv(const ca_career_profile_t *profile,
                       const ca_tailoring_choice_t *choices,
                       size_t choice_count);

/* ── the store ────────────────────────────────────────────────────────────── */

/* The document AND what went into it.
 *
 * `selected_facts` is why a second application can start from the first instead
 * of from scratch - a rendered blob alone cannot be re-tailored. It also makes
 * the record honest: for any application there is a row saying which facts were
 * claimed, to whom, and when. */
typedef struct {
    int64_t id;
    /* 0 when the document was not made for a specific advert. */
    int64_t spec_id;
    uint8_t *pdf;
    size_t pdf_len;
    int64_t *selected_facts;
    size_t selected_fact_count;
    int64_t approved_unix;
} ca_approved_document_t;

void ca_approved_document_free(ca_approved_document_t *document);

typedef struct ca_career_store ca_career_store_t;

/* NULL when the database cannot be opened or the schema cannot be created. */
ca_career_store_t *ca_career_store_open(const char *database_path);
void ca_career_store_close(ca_career_store_t *store);

bool ca_career_store_save_identity(ca_career_store_t *store,
                                   const ca_profile_identity_t *identity);

/* The whole profile. Caller frees with ca_career_profile_free. */
ca_career_profile_t *ca_career_store_load(ca_career_store_t *store);

int64_t ca_career_store_add_spec(ca_career_store_t *store, const ca_job_spec_t *spec);

/* Newest first: the spec somebody is working on is the one they just added. */
ca_job_spec_t *ca_career_store_specs(ca_career_store_t *store, size_t *out_count);

int64_t ca_career_store_approve(ca_career_store_t *store,
                                const ca_approved_document_t *document);

/* Every approval, newest first. Nothing is ever deleted - the record of what
 * was claimed, to whom, and when is the point. Pass spec_id 0 for all. */
ca_approved_document_t *ca_career_store_approvals(ca_career_store_t *store,
                                                  int64_t spec_id,
                                                  size_t *out_count);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_CAREER_H */
