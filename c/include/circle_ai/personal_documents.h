#ifndef CIRCLE_AI_PERSONAL_DOCUMENTS_H
#define CIRCLE_AI_PERSONAL_DOCUMENTS_H

/*
 * personal_documents.h - CircleAI.Personal, CircleAI.Documents,
 * CircleAI.Domain and CircleAI.Markets (C11).
 *
 * Somebody's calendar, contacts and mail; the documents they need to produce;
 * the personal model that learns their way of doing things; and the market data
 * they might look at.
 *
 * THE PERSONAL ADAPTERS ARE THE MOST SENSITIVE SEAM IN THE CODEBASE. Contacts,
 * calendar and mail are, between them, most of a life. Every adapter here is
 * NULL by default, every read passes a consent token naming its scope, and
 * nothing is cached beyond the call. A default that read the address book
 * because a host forgot a line of configuration is the failure this shape
 * exists to make impossible.
 *
 * NOTHING HERE PLACES AN ORDER. The market half reads and routes; the router is
 * a seam a host fills, and no implementation in this codebase executes a trade
 * or moves money. That is deliberate and permanent.
 *
 * Conventions: ca_ prefix, _t types, opaque handles, strdup-owning fields with
 * matching *_free, errors via NULL / false. Pure C11 + libc.
 */

#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif

/* -- consent -------------------------------------------------------------- */

/* What a token permits. Flags, so one token can cover a coherent task without
 * becoming a token that covers everything. */
typedef enum {
    CA_CONSENT_SCOPE_NONE = 0,
    CA_CONSENT_SCOPE_CONTACTS_READ = 1 << 0,
    CA_CONSENT_SCOPE_CALENDAR_READ = 1 << 1,
    CA_CONSENT_SCOPE_CALENDAR_WRITE = 1 << 2,
    CA_CONSENT_SCOPE_EMAIL_READ = 1 << 3,
    /* Sending is separate from drafting, and always will be. An assistant that
     * can draft is useful; one that can send is one bad inference away from
     * mailing somebody's employer. */
    CA_CONSENT_SCOPE_EMAIL_SEND = 1 << 4,
    CA_CONSENT_SCOPE_LOCATION_READ = 1 << 5
} ca_consent_scope_t;

const char *ca_consent_scope_name(ca_consent_scope_t scope);

typedef struct {
    char *token_id;
    ca_consent_scope_t scopes;
    char *granted_by;
    int64_t granted_at_unix;
    int64_t expires_at_unix;
    /* What it was granted FOR, in the person's own terms. Carried so that a
     * consent review reads "to find a time to meet Thandi" rather than a
     * bitmask. */
    char *purpose;
} ca_user_consent_token_t;

void ca_user_consent_token_free(ca_user_consent_token_t *token);

/* Returns NULL for a blank granter, an empty scope set, or a non-positive
 * lifetime. Same rule as the antibody gate: an unbounded or unattributed grant
 * is not a stricter permission, it is one nobody can review or revoke. */
ca_user_consent_token_t *ca_user_consent_token_grant(ca_consent_scope_t scopes,
                                                     const char *granted_by,
                                                     const char *purpose,
                                                     int64_t duration_seconds,
                                                     int64_t now_unix);

bool ca_user_consent_token_covers(const ca_user_consent_token_t *token,
                                  ca_consent_scope_t required, int64_t now_unix);

typedef struct ca_consent_guard ca_consent_guard_t;

/*
 * Checks a token before an adapter is touched, and records that it did.
 *
 * The record is the point. A permission system nobody can audit is
 * indistinguishable from no permission system - the code looks careful either
 * way, and only a log can tell you which reads actually happened.
 */
ca_consent_guard_t *ca_consent_guard_new(void);
void ca_consent_guard_free(ca_consent_guard_t *guard);

bool ca_consent_guard_check(ca_consent_guard_t *guard,
                            const ca_user_consent_token_t *token,
                            ca_consent_scope_t required, int64_t now_unix,
                            char **out_reason);

size_t ca_consent_guard_denied_count(const ca_consent_guard_t *guard);
size_t ca_consent_guard_allowed_count(const ca_consent_guard_t *guard);

/* -- contacts ------------------------------------------------------------- */

typedef struct {
    char *contact_id;
    char *display_name;
    char **emails;
    size_t email_count;
    char **phones;
    size_t phone_count;
} ca_contact_t;

void ca_contact_free(ca_contact_t *contact);

typedef struct ca_contacts_adapter {
    void *state;
    /* Every read takes a token. Not a constructor argument: a token supplied
     * once at construction is a permission that outlives the task it was for. */
    ca_contact_t *(*search)(void *state, const ca_user_consent_token_t *token,
                            const char *query, size_t *out_count);
    void (*free_fn)(void *state);
} ca_contacts_adapter_t;

void ca_contacts_adapter_free(ca_contacts_adapter_t *adapter);

/* Finds nobody. THE DEFAULT. */
ca_contacts_adapter_t *ca_null_contacts_adapter_new(void);

/* -- calendar ------------------------------------------------------------- */

typedef struct {
    char *event_id;
    char *title;
    int64_t starts_unix;
    int64_t ends_unix;
    char *location;
    /* The IANA zone the event was CREATED in. Not a UTC offset: an offset is
     * wrong twice a year, and a recurring 09:00 meeting that drifts an hour
     * every March is the classic version of this bug. */
    char *time_zone_id;
    bool all_day;
} ca_calendar_event_t;

void ca_calendar_event_free(ca_calendar_event_t *event);

typedef struct ca_calendar_adapter {
    void *state;
    ca_calendar_event_t *(*between)(void *state, const ca_user_consent_token_t *token,
                                    int64_t from_unix, int64_t to_unix,
                                    size_t *out_count);
    /* Separate scope from reading, and a separate method, so that "look at my
     * calendar" cannot become "put something in it". */
    bool (*create)(void *state, const ca_user_consent_token_t *token,
                   const ca_calendar_event_t *event);
    void (*free_fn)(void *state);
} ca_calendar_adapter_t;

void ca_calendar_adapter_free(ca_calendar_adapter_t *adapter);
ca_calendar_adapter_t *ca_null_calendar_adapter_new(void);

/* -- email ---------------------------------------------------------------- */

typedef struct {
    char *message_id;
    char *from_address;
    char **to_addresses;
    size_t to_count;
    char *subject;
    char *body;
    int64_t at_unix;
} ca_email_message_t;

void ca_email_message_free(ca_email_message_t *message);

typedef struct ca_email_adapter {
    void *state;
    ca_email_message_t *(*recent)(void *state, const ca_user_consent_token_t *token,
                                  size_t max, size_t *out_count);
    /* Produces a draft and returns its id. Drafting is not sending, and this
     * adapter deliberately has no send at all - a message leaving somebody's
     * account is an action a person takes, in their own mail client, having
     * read it. */
    char *(*draft)(void *state, const ca_user_consent_token_t *token,
                   const ca_email_message_t *message);
    void (*free_fn)(void *state);
} ca_email_adapter_t;

void ca_email_adapter_free(ca_email_adapter_t *adapter);
ca_email_adapter_t *ca_null_email_adapter_new(void);

typedef struct ca_personal_companion_adapter ca_personal_companion_adapter_t;

/* Wires the three into the companion, behind one guard. One guard rather than
 * three so that a consent review is a single list, and so no adapter can be
 * added later that quietly skips the check. */
ca_personal_companion_adapter_t *ca_personal_companion_adapter_new(
    ca_consent_guard_t *guard, ca_contacts_adapter_t *contacts,
    ca_calendar_adapter_t *calendar, ca_email_adapter_t *email);

void ca_personal_companion_adapter_free(ca_personal_companion_adapter_t *adapter);

/* -- documents ------------------------------------------------------------ */

typedef enum {
    CA_DOCUMENT_FORMAT_MARKDOWN = 0,
    CA_DOCUMENT_FORMAT_HTML,
    CA_DOCUMENT_FORMAT_PDF,
    CA_DOCUMENT_FORMAT_DOCX,
    CA_DOCUMENT_FORMAT_PLAIN_TEXT
} ca_document_format_t;

const char *ca_document_format_name(ca_document_format_t format);
const char *ca_document_format_extension(ca_document_format_t format);

typedef struct {
    char *title;
    ca_document_format_t format;
    char *language;
    char *template_id;
    char *payload_json;
} ca_document_request_t;

void ca_document_request_free(ca_document_request_t *request);

typedef struct {
    char *full_name;
    char *email;
    char *phone_e164;
    char *location;
    char **links;
    size_t link_count;
} ca_cv_contact_t;

void ca_cv_contact_free(ca_cv_contact_t *contact);

typedef struct {
    char *employer;
    char *title;
    int64_t from_unix;
    /* Negative means CURRENT. Not "today": writing today's date makes a CV that
     * silently ages, and a document regenerated next year would claim the job
     * ended then. */
    int64_t to_unix;
    char **bullets;
    size_t bullet_count;
} ca_cv_experience_t;

void ca_cv_experience_free(ca_cv_experience_t *experience);

typedef struct {
    char *institution;
    char *qualification;
    int64_t completed_unix;
    char *note;
} ca_cv_education_t;

void ca_cv_education_free(ca_cv_education_t *education);

typedef struct {
    char *name;
    char *issuer;
    int64_t issued_unix;
    int64_t expires_unix;   /* negative = does not expire */
    char *credential_id;
} ca_cv_certification_t;

void ca_cv_certification_free(ca_cv_certification_t *certification);

typedef struct {
    ca_cv_contact_t *contact;
    char *summary;
    ca_cv_experience_t *experience;
    size_t experience_count;
    ca_cv_education_t *education;
    size_t education_count;
    ca_cv_certification_t *certifications;
    size_t certification_count;
    char **skills;
    size_t skill_count;
} ca_cv_document_t;

void ca_cv_document_free(ca_cv_document_t *document);

typedef struct {
    char *to_name;
    char *to_organisation;
    char *role;
    char *body;
    ca_cv_contact_t *from_contact;
    int64_t dated_unix;
} ca_cover_letter_t;

void ca_cover_letter_free(ca_cover_letter_t *letter);

typedef struct {
    char **column_headings;
    size_t column_count;
    char **cells;      /* row-major */
    size_t row_count;
    char *caption;
} ca_report_table_t;

void ca_report_table_free(ca_report_table_t *table);

typedef struct {
    char *heading;
    char *body;
    ca_report_table_t *tables;
    size_t table_count;
    int level;
} ca_report_section_t;

void ca_report_section_free(ca_report_section_t *section);

typedef struct {
    char *title;
    char *subtitle;
    char *author;
    int64_t dated_unix;
    ca_report_section_t *sections;
    size_t section_count;
} ca_report_document_t;

void ca_report_document_free(ca_report_document_t *document);

typedef struct ca_document_engine {
    void *state;
    /* Renders into bytes. Caller frees. NULL when the format is unsupported -
     * a real answer on a device with no PDF engine, and better than a blank
     * document that reads as a delivery failure. */
    uint8_t *(*render)(void *state, const ca_document_request_t *request,
                       size_t *out_len);
    bool (*supports)(void *state, ca_document_format_t format);
    void (*free_fn)(void *state);
} ca_document_engine_t;

void ca_document_engine_free(ca_document_engine_t *engine);

/* -- the personal model --------------------------------------------------- */

typedef struct {
    char *adapter_id;
    char *base_model_id;
    int rank;
    int64_t trained_at_unix;
    int64_t bytes;
    /* How many examples it was fitted on. Reported because a LoRA trained on
     * eleven examples and one trained on four thousand are different things,
     * and only one of them should be trusted to change how an assistant
     * writes. */
    int example_count;
} ca_lora_adapter_state_t;

void ca_lora_adapter_state_free(ca_lora_adapter_state_t *state);

typedef struct {
    char *adapter_id;
    double final_loss;
    int epochs;
    int64_t duration_ms;
    char *note;
} ca_lora_training_summary_t;

void ca_lora_training_summary_free(ca_lora_training_summary_t *summary);

typedef struct ca_personal_lora {
    void *state;
    const ca_lora_adapter_state_t *(*current)(void *state);
    /* Training happens ON DEVICE or not at all. The examples are somebody's own
     * writing, and shipping them somewhere to fit an adapter is the one thing a
     * personal model must never do. */
    ca_lora_training_summary_t *(*train)(void *state, const char **examples,
                                         size_t count);
    void (*free_fn)(void *state);
} ca_personal_lora_t;

void ca_personal_lora_free(ca_personal_lora_t *lora);

ca_personal_lora_t *ca_personal_lora_new(void);
ca_personal_lora_t *ca_null_personal_lora_new(void);

/* -- the memory palace ---------------------------------------------------- */

typedef struct ca_mem_palace_store {
    void *state;
    bool (*place)(void *state, const char *locus, const char *content);
    /* Borrowed; NULL when nothing is at that locus. */
    const char *(*recall)(void *state, const char *locus);
    char **(*walk)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_mem_palace_store_t;

void ca_mem_palace_store_free(ca_mem_palace_store_t *store);

/* Ordered, and the order is the point: a memory palace works because the walk
 * is the same every time. A store that returned loci in hash order would be a
 * dictionary with a decorative name. */
ca_mem_palace_store_t *ca_mem_palace_store_new(void);
ca_mem_palace_store_t *ca_null_mem_palace_store_new(void);

/* -- domain pipelines ----------------------------------------------------- */

typedef struct ca_multi_pass_financial_agent ca_multi_pass_financial_agent_t;

/*
 * Reads financial documents in several passes - extract, reconcile, explain.
 *
 * Multi-pass because a single pass over a bank statement gets the arithmetic
 * right and the CATEGORIES wrong, and the categories are what somebody acts on.
 *
 * It explains and it totals. It does not move money, and no seam here leads to
 * anything that does.
 */
ca_multi_pass_financial_agent_t *ca_multi_pass_financial_agent_new(void *generator);
void ca_multi_pass_financial_agent_free(ca_multi_pass_financial_agent_t *agent);

char *ca_multi_pass_financial_agent_analyse(ca_multi_pass_financial_agent_t *agent,
                                            const char *document_text);

typedef struct ca_template_job_search_pipeline ca_template_job_search_pipeline_t;

ca_template_job_search_pipeline_t *ca_template_job_search_pipeline_new(void);
void ca_template_job_search_pipeline_free(ca_template_job_search_pipeline_t *pipeline);

/* Tailors a CV and cover letter to one posting. Templated rather than
 * generated wholesale: a fabricated line on somebody's CV is a lie with their
 * name on it, so the facts come from the CV and only the emphasis moves. */
bool ca_template_job_search_pipeline_tailor(ca_template_job_search_pipeline_t *pipeline,
                                            const ca_cv_document_t *cv,
                                            const char *posting,
                                            ca_cover_letter_t *out_letter);

typedef struct ca_template_presentation_generator ca_template_presentation_generator_t;

ca_template_presentation_generator_t *ca_template_presentation_generator_new(void);
void ca_template_presentation_generator_free(
    ca_template_presentation_generator_t *generator);

ca_report_document_t *ca_template_presentation_generator_build(
    ca_template_presentation_generator_t *generator, const char *brief);

/* -- markets -------------------------------------------------------------- */

typedef struct {
    char *symbol;
    char *name;
    char *exchange;
    char *currency;
    char *asset_class;
} ca_instrument_t;

void ca_instrument_free(ca_instrument_t *instrument);

typedef struct ca_instrument_catalog {
    void *state;
    const ca_instrument_t *(*get)(void *state, const char *symbol);
    ca_instrument_t *(*search)(void *state, const char *query, size_t *out_count);
    void (*free_fn)(void *state);
} ca_instrument_catalog_t;

void ca_instrument_catalog_free(ca_instrument_catalog_t *catalog);

ca_instrument_catalog_t *ca_instrument_catalog_new(void);
ca_instrument_catalog_t *ca_null_instrument_catalog_new(void);

typedef struct {
    char *symbol;
    /* Minor units of the instrument's currency, same rule as everywhere else. */
    int64_t price_minor;
    int64_t at_unix;
    /* When the feed last actually updated. A stale quote rendered as a live one
     * is the market equivalent of a wrong answer stated confidently. */
    int64_t as_of_unix;
} ca_market_quote_t;

void ca_market_quote_free(ca_market_quote_t *quote);

typedef struct ca_market_data_feed {
    void *state;
    ca_market_quote_t *(*quote)(void *state, const char *symbol);
    void (*free_fn)(void *state);
} ca_market_data_feed_t;

void ca_market_data_feed_free(ca_market_data_feed_t *feed);

ca_market_data_feed_t *ca_market_data_feed_new(void);
ca_market_data_feed_t *ca_null_market_data_feed_new(void);

typedef struct ca_order_router {
    void *state;
    /* A SEAM AND NOTHING MORE. No implementation in this codebase executes an
     * order, and the null one is not a placeholder for one that will. Placing a
     * trade is a person's action, taken in their own broker, having read it. */
    bool (*submit)(void *state, const char *symbol, int64_t quantity,
                   const char *side, char **out_reason);
    void (*free_fn)(void *state);
} ca_order_router_t;

void ca_order_router_free(ca_order_router_t *router);

/* Refuses every order with a reason saying so. The only implementation here. */
ca_order_router_t *ca_null_order_router_new(void);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_PERSONAL_DOCUMENTS_H */
