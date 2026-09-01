#ifndef CIRCLE_AI_DOMAIN_BOARDS_H
#define CIRCLE_AI_DOMAIN_BOARDS_H

/*
 * domain_boards.h - the vertical domains (C11).
 *
 * Forty-odd modules - family, healthcare, logistics, faith, pets, retail - that
 * share exactly one shape, because they are the same idea applied to different
 * subject matter:
 *
 *   a few RECORDS       what this domain is about
 *   a BOARD             where those records live and what can be asked of them
 *   a COMPANION ADAPTER the assistant, wrapped, so it knows the domain
 *
 * WHY ONE HEADER AND NOT FORTY. In C# each is a project because the namespace
 * carries the domain. C has no namespaces, the shape is identical, and forty
 * headers of eight lines each would hide the fact that it IS one shape - which
 * is the most useful thing about it. A domain added later inherits the seam for
 * free; a domain that needs to break it is a signal worth seeing.
 *
 * THE ADAPTER DECORATES, IT DOES NOT REPLACE. Every one wraps an existing
 * companion session, prefixes the domain's system-prompt snippet, and forwards.
 * It adds no capability the session did not already have - so a domain cannot
 * quietly acquire the ability to send mail or spend money by being a domain.
 *
 * MONEY IS MINOR UNITS, TIMES ARE int64_t UNIX SECONDS, throughout - the same
 * rules as everywhere else in this port, and for the same reasons.
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

/* -- the shared seam ------------------------------------------------------ */

/*
 * A companion session, wrapped for one domain.
 *
 * `inner` is borrowed and is never owned: the session outlives any one domain
 * adapter, and a domain that freed it would take the assistant down with it.
 * `system_prompt_snippet` is prefixed to every message the adapter forwards.
 */
typedef struct ca_domain_companion_adapter ca_domain_companion_adapter_t;

ca_domain_companion_adapter_t *ca_domain_companion_adapter_new(
    void *inner_session, const char *domain_id,
    const char *system_prompt_snippet);

void ca_domain_companion_adapter_free(ca_domain_companion_adapter_t *adapter);

const char *ca_domain_companion_adapter_domain_id(
    const ca_domain_companion_adapter_t *adapter);

/* Forwards with the snippet prefixed. Caller frees. */
char *ca_domain_companion_adapter_send(ca_domain_companion_adapter_t *adapter,
                                       const char *message);

/* Forwards to the agent loop rather than a single turn - what the domain
 * helpers below are built on. Caller frees. */
char *ca_domain_companion_adapter_agent(ca_domain_companion_adapter_t *adapter,
                                        const char *message);

/* -- family --------------------------------------------------------------- */

typedef struct {
    char *member_id;
    char *name;
    char *role;
    /* Unix seconds. A DATE, not a moment: stored as midnight UTC on the day,
     * because a birthday that shifts with a time zone is a birthday that is
     * wrong for somebody. */
    int64_t date_of_birth_unix;
} ca_family_member_t;

void ca_family_member_free(ca_family_member_t *member);

typedef struct {
    char *event_id;
    char *title;
    int64_t at_unix;
    char **member_ids;
    size_t member_count;
} ca_family_event_t;

void ca_family_event_free(ca_family_event_t *event);

typedef struct {
    char *expense_id;
    char *paid_by_id;
    int64_t amount_minor;
    char *currency;
    char *category;
    int64_t at_unix;
} ca_shared_expense_t;

void ca_shared_expense_free(ca_shared_expense_t *expense);

typedef struct ca_family_board ca_family_board_t;

ca_family_board_t *ca_family_board_new(void);
void ca_family_board_free(ca_family_board_t *board);

bool ca_family_board_add(ca_family_board_t *board, const ca_family_member_t *member);
const ca_family_member_t *ca_family_board_get_member(const ca_family_board_t *board,
                                                     const char *member_id);
ca_family_member_t *ca_family_board_members(const ca_family_board_t *board,
                                            size_t *out_count);
bool ca_family_board_schedule(ca_family_board_t *board, const ca_family_event_t *event);
ca_family_event_t *ca_family_board_events_for_member(const ca_family_board_t *board,
                                                     const char *member_id,
                                                     size_t *out_count);
bool ca_family_board_record(ca_family_board_t *board, const ca_shared_expense_t *expense);
int64_t ca_family_board_total_paid_by(const ca_family_board_t *board,
                                      const char *member_id, int64_t since_unix);
int64_t ca_family_board_spend_by_category(const ca_family_board_t *board,
                                          const char *category, int64_t since_unix);

ca_domain_companion_adapter_t *ca_family_companion_adapter_new(void *inner_session);

/* -- healthcare ----------------------------------------------------------- */

typedef struct {
    char *appointment_id;
    char *patient_id;
    char *practitioner;
    char *reason;
    int64_t at_unix;
    char *location;
} ca_health_appointment_t;

void ca_health_appointment_free(ca_health_appointment_t *appointment);

typedef struct ca_healthcare_board ca_healthcare_board_t;

ca_healthcare_board_t *ca_healthcare_board_new(void);
void ca_healthcare_board_free(ca_healthcare_board_t *board);

bool ca_healthcare_board_schedule(ca_healthcare_board_t *board,
                                  const ca_health_appointment_t *appointment);

ca_health_appointment_t *ca_healthcare_board_upcoming(const ca_healthcare_board_t *board,
                                                      int64_t from_unix,
                                                      size_t *out_count);

/* Nothing here diagnoses, and no adapter in this file is permitted to. The
 * domain snippet says so to the model as well, because a health question asked
 * of an assistant is exactly where a confident wrong answer does harm. */
ca_domain_companion_adapter_t *ca_healthcare_companion_adapter_new(void *inner_session);

/* -- elderly care --------------------------------------------------------- */

typedef struct {
    char *reminder_id;
    char *person_id;
    char *medication;
    char *dose;
    int64_t at_unix;
    /* Whether a person confirmed. A reminder that was SHOWN and one that was
     * ACTED ON are different facts, and a care board that conflates them
     * reports somebody as having taken medication they did not. */
    bool acknowledged;
} ca_med_reminder_t;

void ca_med_reminder_free(ca_med_reminder_t *reminder);

typedef struct ca_elderly_care_board ca_elderly_care_board_t;

ca_elderly_care_board_t *ca_elderly_care_board_new(void);
void ca_elderly_care_board_free(ca_elderly_care_board_t *board);

bool ca_elderly_care_board_add_reminder(ca_elderly_care_board_t *board,
                                        const ca_med_reminder_t *reminder);

bool ca_elderly_care_board_acknowledge(ca_elderly_care_board_t *board,
                                       const char *reminder_id, int64_t at_unix);

ca_med_reminder_t *ca_elderly_care_board_missed(const ca_elderly_care_board_t *board,
                                                int64_t as_of_unix, size_t *out_count);

ca_domain_companion_adapter_t *ca_elderly_companion_adapter_new(void *inner_session);

/* -- personal health, mental health, finance ------------------------------ */

typedef struct {
    char *reading_id;
    char *kind;        /* "heart_rate", "bp_systolic", "spo2" */
    double value;
    char *unit;
    int64_t at_unix;
} ca_vital_reading_t;

void ca_vital_reading_free(ca_vital_reading_t *reading);

typedef struct ca_personal_health_board ca_personal_health_board_t;

ca_personal_health_board_t *ca_personal_health_board_new(void);
void ca_personal_health_board_free(ca_personal_health_board_t *board);

bool ca_personal_health_board_record(ca_personal_health_board_t *board,
                                     const ca_vital_reading_t *reading);

ca_vital_reading_t *ca_personal_health_board_series(
    const ca_personal_health_board_t *board, const char *kind,
    int64_t since_unix, size_t *out_count);

ca_domain_companion_adapter_t *ca_personal_health_companion_adapter_new(
    void *inner_session);

typedef struct {
    char *strategy_id;
    char *name;
    char *description;
    /* What the person said helped, in their words. Never generated: a coping
     * strategy invented by a model and presented as the person's own is the
     * worst failure available in this domain. */
    char *source;
    int helpfulness;   /* 1..5, 0 = not rated */
} ca_coping_strategy_t;

void ca_coping_strategy_free(ca_coping_strategy_t *strategy);

typedef struct ca_mental_health_board ca_mental_health_board_t;

ca_mental_health_board_t *ca_mental_health_board_new(void);
void ca_mental_health_board_free(ca_mental_health_board_t *board);

bool ca_mental_health_board_add(ca_mental_health_board_t *board,
                                const ca_coping_strategy_t *strategy);

ca_coping_strategy_t *ca_mental_health_board_most_helpful(
    const ca_mental_health_board_t *board, size_t max, size_t *out_count);

/* The snippet for this one carries a crisis instruction: some things are not an
 * assistant's to handle, and the adapter has to say which. */
ca_domain_companion_adapter_t *ca_personal_mental_companion_adapter_new(
    void *inner_session);

typedef struct {
    char *transaction_id;
    int64_t amount_minor;
    char *currency;
    char *category;
    char *description;
    int64_t at_unix;
} ca_finance_transaction_t;

void ca_finance_transaction_free(ca_finance_transaction_t *transaction);

typedef struct {
    char *category;
    int64_t limit_minor;
    char *currency;
    char *period;      /* "month", "week" */
} ca_budget_line_t;

void ca_budget_line_free(ca_budget_line_t *line);

typedef struct ca_personal_finance_board ca_personal_finance_board_t;

ca_personal_finance_board_t *ca_personal_finance_board_new(void);
void ca_personal_finance_board_free(ca_personal_finance_board_t *board);

bool ca_personal_finance_board_record(ca_personal_finance_board_t *board,
                                      const ca_finance_transaction_t *transaction);

bool ca_personal_finance_board_set_budget(ca_personal_finance_board_t *board,
                                          const ca_budget_line_t *line);

int64_t ca_personal_finance_board_spent(const ca_personal_finance_board_t *board,
                                        const char *category, int64_t since_unix);

/* Explains and totals. Does not move money - the same rule the markets router
 * follows, and for the same reason. */
ca_domain_companion_adapter_t *ca_personal_finance_companion_adapter_new(
    void *inner_session);

/* -- business, commerce, accounting --------------------------------------- */

typedef struct {
    char *unit_id;
    char *name;
    char *parent_unit_id;   /* NULL at the top */
} ca_business_unit_t;

void ca_business_unit_free(ca_business_unit_t *unit);

typedef struct {
    char *kpi_id;
    char *name;
    double value;
    char *unit;
    int64_t at_unix;
} ca_kpi_sample_t;

void ca_kpi_sample_free(ca_kpi_sample_t *sample);

typedef struct {
    char *target_id;
    int year;
    int quarter;        /* 1..4 */
    char *kpi_id;
    double target_value;
} ca_quarter_target_t;

void ca_quarter_target_free(ca_quarter_target_t *target);

typedef struct ca_business_board ca_business_board_t;

ca_business_board_t *ca_business_board_new(void);
void ca_business_board_free(ca_business_board_t *board);

bool ca_business_board_add_unit(ca_business_board_t *board,
                                const ca_business_unit_t *unit);
bool ca_business_board_record_kpi(ca_business_board_t *board,
                                  const ca_kpi_sample_t *sample);
bool ca_business_board_set_target(ca_business_board_t *board,
                                  const ca_quarter_target_t *target);

/* Progress against a target as a fraction, or negative when there is no target
 * or no sample. Zero means no progress and must stay distinguishable from
 * "nothing to compare against". */
double ca_business_board_progress(const ca_business_board_t *board,
                                  const char *kpi_id, int year, int quarter);

ca_domain_companion_adapter_t *ca_business_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_commerce_companion_adapter_new(void *inner_session);

typedef struct {
    char *entry_id;
    char *account;
    /* Positive debits, negative credits, in minor units. One signed field
     * rather than two columns: two columns permit an entry that is both, and
     * every reconciliation bug starts there. */
    int64_t amount_minor;
    char *currency;
    char *memo;
    int64_t at_unix;
} ca_accounting_entry_t;

void ca_accounting_entry_free(ca_accounting_entry_t *entry);

typedef struct ca_accounting_board ca_accounting_board_t;

ca_accounting_board_t *ca_accounting_board_new(void);
void ca_accounting_board_free(ca_accounting_board_t *board);

bool ca_accounting_board_post(ca_accounting_board_t *board,
                              const ca_accounting_entry_t *entry);

/* Sum over an account. Returns false rather than a wrong total when currencies
 * are mixed. */
bool ca_accounting_board_balance(const ca_accounting_board_t *board,
                                 const char *account, int64_t *out_minor);

ca_domain_companion_adapter_t *ca_commerce_accounting_companion_adapter_new(
    void *inner_session);

typedef struct {
    char *payment_id;
    int64_t amount_minor;
    char *currency;
    char *status;
    int64_t at_unix;
} ca_finance_payment_t;

void ca_finance_payment_free(ca_finance_payment_t *payment);

ca_domain_companion_adapter_t *ca_commerce_finance_companion_adapter_new(
    void *inner_session);

ca_domain_companion_adapter_t *ca_commerce_integration_xero_companion_adapter_new(
    void *inner_session);

/* -- PayFast -------------------------------------------------------------- */

typedef struct {
    char *merchant_id;
    char *merchant_key;
    /* Sandbox by default. A payment integration that defaults to live is one
     * bad configuration away from taking real money in a test. */
    bool sandbox;
    char *return_url;
    char *cancel_url;
    char *notify_url;
} ca_pay_fast_config_t;

void ca_pay_fast_config_free(ca_pay_fast_config_t *config);

typedef struct {
    char *payment_id;
    char *payment_status;
    int64_t amount_gross_minor;
    int64_t amount_fee_minor;
    int64_t amount_net_minor;
    char *merchant_payment_id;
    char *signature;
} ca_pay_fast_itn_payload_t;

void ca_pay_fast_itn_payload_free(ca_pay_fast_itn_payload_t *payload);

/*
 * Verifies an instant transaction notification.
 *
 * THREE CHECKS, ALL REQUIRED: the signature matches, the source address is
 * PayFast's, and the amount matches what was actually ordered. An ITN handler
 * that checks only the signature will happily mark an order paid for one rand,
 * because the signature is over whatever the payload says.
 */
bool ca_pay_fast_verify_itn(const ca_pay_fast_config_t *config,
                            const ca_pay_fast_itn_payload_t *payload,
                            const char *source_address,
                            int64_t expected_amount_minor,
                            char **out_reason);

typedef struct ca_pay_fast_board ca_pay_fast_board_t;

ca_pay_fast_board_t *ca_pay_fast_board_new(const ca_pay_fast_config_t *config);
void ca_pay_fast_board_free(ca_pay_fast_board_t *board);

bool ca_pay_fast_board_accept(ca_pay_fast_board_t *board,
                              const ca_pay_fast_itn_payload_t *payload);

ca_domain_companion_adapter_t *ca_commerce_integration_pay_fast_companion_adapter_new(
    void *inner_session);

/* -- education, HR, parenting, kids --------------------------------------- */

typedef struct {
    char *student_id;
    char *name;
    char *year_group;
    char **subjects;
    size_t subject_count;
} ca_student_record_t;

void ca_student_record_free(ca_student_record_t *record);

typedef struct ca_education_board ca_education_board_t;

ca_education_board_t *ca_education_board_new(void);
void ca_education_board_free(ca_education_board_t *board);

bool ca_education_board_enrol(ca_education_board_t *board,
                              const ca_student_record_t *record);

const ca_student_record_t *ca_education_board_get(const ca_education_board_t *board,
                                                  const char *student_id);

ca_domain_companion_adapter_t *ca_education_companion_adapter_new(void *inner_session);

typedef struct {
    char *request_id;
    char *employee_id;
    char *leave_type;
    int64_t from_unix;
    int64_t to_unix;
    char *status;
} ca_leave_request_t;

void ca_leave_request_free(ca_leave_request_t *request);

typedef struct {
    char *review_id;
    char *employee_id;
    char *period;
    char *summary;
    int rating;        /* 1..5, 0 = unrated */
    int64_t at_unix;
} ca_performance_review_t;

void ca_performance_review_free(ca_performance_review_t *review);

/* HR's snippet forbids the adapter from drafting a decision about a named
 * person's employment. It can summarise a policy; it cannot recommend who to
 * let go. */
ca_domain_companion_adapter_t *ca_hr_companion_adapter_new(void *inner_session);

typedef struct ca_parenting_board ca_parenting_board_t;

ca_parenting_board_t *ca_parenting_board_new(void);
void ca_parenting_board_free(ca_parenting_board_t *board);

ca_domain_companion_adapter_t *ca_parenting_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_kids_companion_adapter_new(void *inner_session);

/* -- logistics, real estate, retail, construction ------------------------- */

typedef struct ca_logistics_board ca_logistics_board_t;

ca_logistics_board_t *ca_logistics_board_new(void);
void ca_logistics_board_free(ca_logistics_board_t *board);

ca_domain_companion_adapter_t *ca_logistics_companion_adapter_new(void *inner_session);

typedef struct ca_real_estate_board ca_real_estate_board_t;

ca_real_estate_board_t *ca_real_estate_board_new(void);
void ca_real_estate_board_free(ca_real_estate_board_t *board);

ca_domain_companion_adapter_t *ca_real_estate_companion_adapter_new(void *inner_session);

typedef struct {
    char *sku;
    char *location;
    int on_hand;
    int reserved;
    int64_t at_unix;
} ca_stock_level_t;

void ca_stock_level_free(ca_stock_level_t *level);

/* Available is on-hand minus reserved and can go NEGATIVE - an oversell that
 * has already happened. Clamping it to zero hides the one number somebody
 * needs to see. */
int ca_stock_level_available(const ca_stock_level_t *level);

ca_domain_companion_adapter_t *ca_retail_companion_adapter_new(void *inner_session);

typedef struct {
    char *entry_id;
    char *project_id;
    char *category;
    int64_t amount_minor;
    char *currency;
    int64_t at_unix;
} ca_cost_entry_t;

void ca_cost_entry_free(ca_cost_entry_t *entry);

ca_domain_companion_adapter_t *ca_construction_companion_adapter_new(void *inner_session);

/* -- home, energy, IoT ---------------------------------------------------- */

typedef struct {
    char *task_id;
    char *description;
    int64_t due_unix;
    char *recurrence;
    bool completed;
} ca_maintenance_task_t;

void ca_maintenance_task_free(ca_maintenance_task_t *task);

ca_domain_companion_adapter_t *ca_home_companion_adapter_new(void *inner_session);

typedef struct {
    char *meter_id;
    double reading;
    char *unit;
    int64_t at_unix;
} ca_meter_reading_t;

void ca_meter_reading_free(ca_meter_reading_t *reading);

/* Consumption between two readings. Handles a meter that has ROLLED OVER,
 * which a plain subtraction reports as a large negative and which then shows
 * up as a credit on somebody's usage. */
double ca_meter_reading_consumption(const ca_meter_reading_t *earlier,
                                    const ca_meter_reading_t *later,
                                    double rollover_at);

typedef struct {
    char *device_id;
    char *name;
    char *kind;
    char *room;
    bool online;
    int64_t last_seen_unix;
} ca_io_t_device_t;

void ca_io_t_device_free(ca_io_t_device_t *device);

typedef struct {
    char *device_id;
    char *metric;
    double value;
    char *unit;
    int64_t at_unix;
} ca_io_t_telemetry_t;

void ca_io_t_telemetry_free(ca_io_t_telemetry_t *telemetry);

typedef struct {
    char *device_id;
    char *action;
    char **arg_keys;
    char **arg_values;
    size_t arg_count;
} ca_io_t_command_t;

void ca_io_t_command_free(ca_io_t_command_t *command);

typedef struct ca_io_t_board ca_io_t_board_t;

ca_io_t_board_t *ca_io_t_board_new(void);
void ca_io_t_board_free(ca_io_t_board_t *board);

bool ca_io_t_board_register(ca_io_t_board_t *board, const ca_io_t_device_t *device);
bool ca_io_t_board_record(ca_io_t_board_t *board, const ca_io_t_telemetry_t *telemetry);

/*
 * Sends a command to a registered device.
 *
 * REGISTERED ONLY, and never to a device discovered on the network without
 * somebody adding it. This is the seam that turns text from a model into
 * something physical happening in a room, and the set of things it can touch
 * must be a list a person wrote.
 */
bool ca_io_t_board_command(ca_io_t_board_t *board, const ca_io_t_command_t *command);

typedef struct ca_io_t_companion_pipeline ca_io_t_companion_pipeline_t;

ca_io_t_companion_pipeline_t *ca_io_t_companion_pipeline_new(ca_io_t_board_t *board,
                                                             void *inner_session);

void ca_io_t_companion_pipeline_free(ca_io_t_companion_pipeline_t *pipeline);

/* -- faith, community, civic ---------------------------------------------- */

typedef struct {
    char *book;
    int chapter;
    int verse_start;
    int verse_end;    /* equal to start for a single verse */
    char *translation;
} ca_scripture_reference_t;

void ca_scripture_reference_free(ca_scripture_reference_t *reference);

/* Caller frees. Renders "John 3:16" or "John 3:16-18". */
char *ca_scripture_reference_format(const ca_scripture_reference_t *reference);

typedef struct {
    char *request_id;
    char *text;
    char *requested_by;
    int64_t at_unix;
    /* Private by default. A prayer request is somebody's confidence, and a
     * default that shared it would be a breach dressed as a feature. */
    bool is_private;
} ca_prayer_request_t;

void ca_prayer_request_free(ca_prayer_request_t *request);

ca_domain_companion_adapter_t *ca_faith_companion_adapter_new(void *inner_session);

typedef struct {
    char *opportunity_id;
    char *title;
    char *organisation;
    char *location;
    int64_t at_unix;
    char *contact;
} ca_volunteer_opportunity_t;

void ca_volunteer_opportunity_free(ca_volunteer_opportunity_t *opportunity);

ca_domain_companion_adapter_t *ca_community_companion_adapter_new(void *inner_session);

typedef struct {
    char *representative_id;
    char *name;
    char *office;
    char *constituency;
    char *contact;
    char *party;
} ca_representative_t;

void ca_representative_free(ca_representative_t *representative);

/* The civic snippet is explicitly non-partisan: the adapter reports who holds
 * an office and how to reach them, and does not advise how to vote. */
ca_domain_companion_adapter_t *ca_civic_companion_adapter_new(void *inner_session);

/* -- fitness, sports, food, pets, travel ---------------------------------- */

typedef struct {
    char *set_id;
    char *exercise;
    int repetitions;
    double weight_kg;
    int64_t at_unix;
} ca_exercise_set_t;

void ca_exercise_set_free(ca_exercise_set_t *set);

ca_domain_companion_adapter_t *ca_fitness_companion_adapter_new(void *inner_session);

typedef struct {
    char *session_id;
    char *sport;
    int64_t at_unix;
    int64_t duration_seconds;
    char *notes;
} ca_training_session_t;

void ca_training_session_free(ca_training_session_t *session);

ca_domain_companion_adapter_t *ca_sports_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_food_companion_adapter_new(void *inner_session);

typedef struct {
    char *appointment_id;
    char *pet_id;
    char *practice;
    char *reason;
    int64_t at_unix;
} ca_vet_appointment_t;

void ca_vet_appointment_free(ca_vet_appointment_t *appointment);

typedef struct {
    char *pet_id;
    double weight_kg;
    int64_t at_unix;
} ca_weight_sample_t;

void ca_weight_sample_free(ca_weight_sample_t *sample);

ca_domain_companion_adapter_t *ca_pets_companion_adapter_new(void *inner_session);

typedef struct {
    char *stay_id;
    char *hotel_name;
    char *city;
    int64_t check_in_unix;
    int64_t check_out_unix;
    char *confirmation;
} ca_hotel_stay_t;

void ca_hotel_stay_free(ca_hotel_stay_t *stay);

ca_domain_companion_adapter_t *ca_travel_companion_adapter_new(void *inner_session);

/* -- hospitality ---------------------------------------------------------- */

typedef struct {
    char *room_number;
    char *room_type;
    int max_occupancy;
    bool out_of_service;
} ca_hotel_room_t;

void ca_hotel_room_free(ca_hotel_room_t *room);

typedef struct {
    char *reservation_id;
    char *guest_name;
    char *room_number;
    int64_t check_in_unix;
    int64_t check_out_unix;
    int guests;
} ca_guest_reservation_t;

void ca_guest_reservation_free(ca_guest_reservation_t *reservation);

typedef struct {
    char *note_id;
    char *reservation_id;
    char *text;
    int64_t at_unix;
    char *author;
} ca_front_desk_note_t;

void ca_front_desk_note_free(ca_front_desk_note_t *note);

ca_domain_companion_adapter_t *ca_hospitality_companion_adapter_new(void *inner_session);

/* -- gaming, creative, media, legal, beauty, agriculture ------------------ */

typedef struct {
    char *title_id;
    char *name;
    char *platform;
    char *genre;
} ca_game_title_t;

void ca_game_title_free(ca_game_title_t *title);

typedef struct {
    char *achievement_id;
    char *title_id;
    char *name;
    int64_t unlocked_unix;
} ca_achievement_unlock_t;

void ca_achievement_unlock_free(ca_achievement_unlock_t *unlock);

ca_domain_companion_adapter_t *ca_gaming_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_creative_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_media_companion_adapter_new(void *inner_session);

/* The legal snippet states plainly that this is not legal advice, and the
 * adapter repeats it in the answer rather than only in the prompt - a
 * disclaimer the model was told about but did not say is a disclaimer nobody
 * received. */
ca_domain_companion_adapter_t *ca_legal_companion_adapter_new(void *inner_session);

ca_domain_companion_adapter_t *ca_beauty_companion_adapter_new(void *inner_session);
ca_domain_companion_adapter_t *ca_agriculture_companion_adapter_new(void *inner_session);

/* -- relationships, wearable, accessibility ------------------------------- */

typedef struct {
    char *contact_id;
    char *name;
    char *relationship;
    int64_t last_contacted_unix;
    /* Days. What "keeping in touch" means differs per person, so it is stated
     * per contact rather than assumed. */
    int desired_interval_days;
} ca_person_contact_t;

void ca_person_contact_free(ca_person_contact_t *contact);

/* Days overdue, or negative when not yet due. */
int ca_person_contact_overdue_days(const ca_person_contact_t *contact, int64_t now_unix);

ca_domain_companion_adapter_t *ca_relationships_companion_adapter_new(void *inner_session);

typedef struct {
    char *device_id;
    bool on_wrist;
    int battery_percent;      /* negative = unknown */
    /* A wearable's screen is small enough that the reply LENGTH has to change,
     * not just its layout. Carried so the adapter can shorten rather than
     * truncate. */
    int max_reply_chars;
    bool haptics_available;
} ca_wearable_context_t;

void ca_wearable_context_free(ca_wearable_context_t *context);

ca_domain_companion_adapter_t *ca_wearable_companion_adapter_new(void *inner_session);

typedef struct {
    /* What to change, not what is wrong with the person. "Needs larger text" is
     * actionable; a diagnosis is not ours to record. */
    char *hint;
    char *applies_to;
    int priority;
} ca_adaptation_hint_t;

void ca_adaptation_hint_free(ca_adaptation_hint_t *hint);

typedef struct {
    char *profile_id;
    ca_adaptation_hint_t *hints;
    size_t hint_count;
    bool prefers_reduced_motion;
    bool prefers_high_contrast;
    bool prefers_screen_reader;
    double text_scale;
} ca_user_accessibility_profile_t;

void ca_user_accessibility_profile_free(ca_user_accessibility_profile_t *profile);

ca_domain_companion_adapter_t *ca_accessibility_companion_adapter_new(void *inner_session);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_DOMAIN_BOARDS_H */
