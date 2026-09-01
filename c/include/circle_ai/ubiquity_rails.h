#ifndef CIRCLE_AI_UBIQUITY_RAILS_H
#define CIRCLE_AI_UBIQUITY_RAILS_H

/*
 * ubiquity_rails.h - CircleAI.Distribution.Ubiquity, the rails that HOLD STATE.
 *
 * ubiquity.h next door is the constant half: the rails whose whole content is a
 * decision somebody has to defend, compiled in so that changing one is a commit
 * with a name on it. This file is the other half - the rails that remember
 * something between calls: an onboarding session part-way through, a queue of
 * operations waiting for a network, the windows during which the assistant has
 * agreed to stay quiet.
 *
 * IN C AN INTERFACE IS A STRUCT OF FUNCTION POINTERS AND THE I PREFIX GOES.
 * There is one implementation per rail and it is named for the thing, not for
 * how it stores it, so IOemPreloadCatalog and DefaultOemPreloadCatalog are both
 * ca_ubiquity_oem_preload_catalog_*. A host that wants a real integration
 * supplies its own struct; the default is the seam's proof that the shape works.
 *
 * MONEY IS IN MINOR UNITS AS INTEGERS. The C# uses decimal, which C has not got,
 * and float money is how a total stops matching the sum of its parts. Cents in,
 * cents out, and the formatter is the only place a decimal point appears.
 *
 * TIMES ARE int64_t UNIX SECONDS. Not time_t: time_t is 32-bit on some targets
 * this ships to, and a rail that stores an expiry is exactly the code that
 * breaks in 2038.
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

/* -- distribution --------------------------------------------------------- */

typedef struct {
    char *store_name;
    char *package_path;
    char *version;
    /* Parallel arrays rather than a map: the metadata is a handful of entries
     * written once and read whole, and a hash table for five strings costs more
     * to get right than it saves. */
    char **metadata_keys;
    char **metadata_values;
    size_t metadata_count;
} ca_app_store_package_t;

void ca_app_store_package_free(ca_app_store_package_t *package);

typedef struct ca_app_store_submitter ca_app_store_submitter_t;

ca_app_store_submitter_t *ca_ubiquity_app_store_submitter_new(void);
void ca_ubiquity_app_store_submitter_free(ca_app_store_submitter_t *submitter);

/* False for an unknown store rather than an error: submitting to a store that
 * does not exist is a configuration mistake, not a failure of this call, and the
 * caller wants to see which one was rejected rather than a stack trace. */
bool ca_ubiquity_app_store_submit(ca_app_store_submitter_t *submitter,
                                  const ca_app_store_package_t *package);

size_t ca_ubiquity_app_store_submitted_count(const ca_app_store_submitter_t *submitter);

typedef struct {
    char *channel;
    char *from_version;
    char *to_version;
    uint8_t *payload;
    size_t payload_len;
    uint8_t *signature;
    size_t signature_len;
} ca_delta_update_t;

void ca_delta_update_free(ca_delta_update_t *update);

typedef struct ca_signed_delta_updater ca_signed_delta_updater_t;

/* `verify` is the host's signature check. NULL means NOTHING IS VERIFIED and
 * every update is refused - not accepted. An updater that applies unsigned
 * deltas because no verifier was wired is a remote code execution hole with a
 * default value. */
ca_signed_delta_updater_t *ca_ubiquity_signed_delta_updater_new(
    bool (*verify)(void *state, const ca_delta_update_t *update), void *state);

void ca_ubiquity_signed_delta_updater_free(ca_signed_delta_updater_t *updater);

/* Refuses an update whose from_version does not match what the channel is
 * actually on: applying a delta to the wrong base produces a binary that is
 * neither version and passes no check afterwards. */
bool ca_ubiquity_signed_delta_apply(ca_signed_delta_updater_t *updater,
                                    const ca_delta_update_t *update);

const char *ca_ubiquity_signed_delta_current_version(
    const ca_signed_delta_updater_t *updater, const char *channel);

size_t ca_ubiquity_oem_preload_catalog_count(void);
const char *ca_ubiquity_oem_preload_catalog_at(size_t index);

size_t ca_ubiquity_carrier_preload_catalog_count(void);
const char *ca_ubiquity_carrier_preload_catalog_at(size_t index);

/* -- peers ---------------------------------------------------------------- */

typedef struct {
    char *peer_id;
    char *endpoint;
    char **available_hashes;
    size_t hash_count;
} ca_peer_t;

void ca_peer_free(ca_peer_t *peer);

typedef struct ca_peer_advertiser {
    void *state;
    const char *(*backend_id)(void *state);
    /* Returns a heap array of *out_count. Zero peers is the normal answer on a
     * device with no network, not an error. */
    ca_peer_t *(*discover)(void *state, size_t *out_count);
    void (*free_fn)(void *state);
} ca_peer_advertiser_t;

void ca_peer_advertiser_free(ca_peer_advertiser_t *advertiser);

/* Backend id "null", discovers nobody. The default so that a host which has not
 * wired a transport gets an empty list rather than a null dereference. */
ca_peer_advertiser_t *ca_null_peer_advertiser_new(void);

/* -- onboarding ----------------------------------------------------------- */

typedef struct {
    char *session_id;
    char *phone_number;
    bool biometric_enrolled;
    /* How long the person waited to get to something usable. Recorded because
     * it is the number the onboarding rail exists to keep small. */
    int64_t time_to_active_ms;
} ca_onboarding_session_t;

void ca_onboarding_session_free(ca_onboarding_session_t *session);

typedef struct ca_phone_pin_biometric_onboarding ca_phone_pin_biometric_onboarding_t;

ca_phone_pin_biometric_onboarding_t *ca_ubiquity_phone_pin_biometric_onboarding_new(void);
void ca_ubiquity_phone_pin_biometric_onboarding_free(
    ca_phone_pin_biometric_onboarding_t *onboarding);

ca_onboarding_session_t *ca_ubiquity_phone_pin_biometric_start(
    ca_phone_pin_biometric_onboarding_t *onboarding, const char *phone_number);

bool ca_ubiquity_phone_pin_biometric_complete(
    ca_phone_pin_biometric_onboarding_t *onboarding, const char *session_id,
    const char *pin, bool biometric_ok);

/* THE PIN IS NEVER STORED. What is kept is a salted hash, and this compares
 * against that - so a memory dump of a half-onboarded device does not hand over
 * everybody's PIN. The comparison is constant-time for the same reason it is
 * everywhere else: a timing difference on a four-digit secret is a four-digit
 * search. */
bool ca_ubiquity_phone_pin_biometric_verify_pin(
    const ca_phone_pin_biometric_onboarding_t *onboarding,
    const char *phone_number, const char *pin);

/* The first screen, which is not a screen: nothing to fill in, nothing to read.
 * Returns borrowed static storage. */
const char *ca_ubiquity_no_manual_first_run_show(void);

/* Setup driven entirely by voice in the language somebody actually thinks in.
 * False for a language with no voice assets rather than falling back to English:
 * an English setup flow for somebody who does not read English is the failure
 * this rail exists to prevent, and doing it silently hides that it happened. */
bool ca_ubiquity_voice_led_setup_run(const char *mother_tongue);

typedef struct {
    char *name;
} ca_personality_choice_t;

void ca_personality_choice_free(ca_personality_choice_t *choice);

typedef struct ca_ai_personality_wizard ca_ai_personality_wizard_t;

ca_ai_personality_wizard_t *ca_ubiquity_ai_personality_wizard_new(void);
void ca_ubiquity_ai_personality_wizard_free(ca_ai_personality_wizard_t *wizard);

size_t ca_ubiquity_ai_personality_preset_count(const ca_ai_personality_wizard_t *wizard);
const char *ca_ubiquity_ai_personality_preset_at(const ca_ai_personality_wizard_t *wizard,
                                                 size_t index);

/* Rejects a personality that is not a preset. The wizard is a closed list on
 * purpose - an arbitrary string here becomes a prompt fragment later. */
bool ca_ubiquity_ai_personality_select(ca_ai_personality_wizard_t *wizard,
                                       const char *session_id, const char *name);

const char *ca_ubiquity_ai_personality_selected(const ca_ai_personality_wizard_t *wizard,
                                                const char *session_id);

typedef struct ca_personal_data_import ca_personal_data_import_t;

ca_personal_data_import_t *ca_ubiquity_personal_import_new(void);
void ca_ubiquity_personal_import_free(ca_personal_data_import_t *import);

bool ca_ubiquity_personal_import_run(ca_personal_data_import_t *import,
                                     const char *session_id, const char *source);

size_t ca_ubiquity_personal_import_count(const ca_personal_data_import_t *import,
                                         const char *session_id);

typedef struct {
    char *member_id;
    char *display_name;
    char *role;
} ca_household_member_t;

void ca_household_member_free(ca_household_member_t *member);

typedef struct ca_family_onboarding ca_family_onboarding_t;

ca_family_onboarding_t *ca_ubiquity_family_onboarding_new(void);
void ca_ubiquity_family_onboarding_free(ca_family_onboarding_t *onboarding);

/* Refuses duplicate member ids and an empty household. A household of nobody is
 * a shape the rest of the family features cannot handle, and finding that out
 * three screens later is worse than refusing here. */
bool ca_ubiquity_family_onboarding_create_household(ca_family_onboarding_t *onboarding,
                                                    const char *owner_id,
                                                    const ca_household_member_t *members,
                                                    size_t member_count);

size_t ca_ubiquity_family_onboarding_member_count(const ca_family_onboarding_t *onboarding,
                                                  const char *owner_id);

/* -- trust ---------------------------------------------------------------- */

const char *ca_ubiquity_third_party_security_audit_publisher_report_url(void);

size_t ca_ubiquity_compliance_certifications_count(void);
const char *ca_ubiquity_compliance_certifications_at(size_t index);

const char *ca_ubiquity_bug_bounty_channel_platform(void);
const char *ca_ubiquity_bug_bounty_channel_submission_url(void);

size_t ca_ubiquity_privacy_regulation_compliance_count(void);
const char *ca_ubiquity_privacy_regulation_compliance_at(size_t index);

/* Reproducible build plus a source URL: the two things that let somebody check
 * the privacy claim instead of believing it. */
bool ca_ubiquity_verifiable_privacy_proof_build_is_reproducible(void);
const char *ca_ubiquity_verifiable_privacy_proof_source_url(void);

typedef struct {
    char *call_id;
    char **actions_taken;
    size_t action_count;
    /* Every destination the call sent data to. EMPTY IS THE INTERESTING CASE and
     * it must be distinguishable from "not recorded": a receipt that cannot tell
     * "nothing left the device" from "we did not look" is worth nothing. */
    char **data_egress;
    size_t egress_count;
    int64_t cost_micro_usd;
} ca_transparency_receipt_t;

void ca_transparency_receipt_free(ca_transparency_receipt_t *receipt);

typedef struct ca_per_call_transparency ca_per_call_transparency_t;

ca_per_call_transparency_t *ca_ubiquity_per_call_transparency_new(void);
void ca_ubiquity_per_call_transparency_free(ca_per_call_transparency_t *transparency);

void ca_ubiquity_per_call_transparency_note(ca_per_call_transparency_t *transparency,
                                            const char *call_id, const char *action,
                                            const char *egress_destination,
                                            int64_t cost_micro_usd);

/* NULL for a call nobody recorded, which is not the same as a call that did
 * nothing. Caller frees. */
ca_transparency_receipt_t *ca_ubiquity_per_call_transparency_receipt_for(
    const ca_per_call_transparency_t *transparency, const char *call_id);

/* -- pricing -------------------------------------------------------------- */

typedef struct {
    char *name;
    /* Minor units. R19.00 is 1900. */
    int64_t monthly_price_minor;
    char *currency;
    char **features;
    size_t feature_count;
} ca_pricing_tier_t;

void ca_pricing_tier_free(ca_pricing_tier_t *tier);

size_t ca_ubiquity_pricing_matrix_count(void);
/* Borrowed. The matrix is static: a price a deployment could change is not a
 * price, it is a negotiation. */
const ca_pricing_tier_t *ca_ubiquity_pricing_matrix_at(size_t index);
const ca_pricing_tier_t *ca_ubiquity_pricing_matrix_find(const char *name);

double ca_ubiquity_plugin_marketplace_revenue_share_author(void);
double ca_ubiquity_plugin_marketplace_revenue_share_verified_safe(void);
double ca_ubiquity_carrier_revenue_share(void);

/* -- localisation --------------------------------------------------------- */

/* Formats minor units with the ISO code. Caller frees.
 *
 * Takes minor units and does the division HERE, in one place, so that the number
 * on a screen and the number in a ledger cannot disagree. */
char *ca_ubiquity_currency_formatter_format(int64_t amount_minor,
                                            const char *iso_currency_code);

/* E.164 in, presentation out. Caller frees.
 *
 * The default returns the input unchanged, and that is deliberate: a wrong
 * national format is worse than none, because it looks authoritative. A host
 * with a real library replaces this rail. */
char *ca_ubiquity_phone_number_formatter_format(const char *e164,
                                                const char *country_iso_alpha2);

/* Whether names in this language are handled properly - not merely stored.
 *
 * The list is the languages whose naming conventions are actually understood:
 * click letters, diacritics, the fact that a "surname" is not always the last
 * word. Claiming a language here that is only tolerated is how somebody's name
 * comes back mangled on their own device. */
bool ca_ubiquity_cultural_name_recogniser_recognises(const char *iso_language);

/* Borrowed static storage. Falls back to "Hello" rather than to nothing. */
const char *ca_ubiquity_cultural_greetings_for(const char *iso_language);

size_t ca_ubiquity_sa_service_connectors_bank_count(void);
const char *ca_ubiquity_sa_service_connectors_bank_at(size_t index);
size_t ca_ubiquity_sa_service_connectors_wallet_count(void);
const char *ca_ubiquity_sa_service_connectors_wallet_at(size_t index);

size_t ca_ubiquity_cross_border_corridors_count(void);
const char *ca_ubiquity_cross_border_corridors_at(size_t index);

/* TRUE by default and for every language.
 *
 * The default is the whole point: knowledge belonging to a community is not the
 * assistant's to repeat because a model happened to ingest it. Elder review is
 * the gate, and a rail that defaulted to "no review needed" would make the
 * exception the rule. */
bool ca_ubiquity_indigenous_knowledge_protocols_requires_elder_review(
    const char *iso_language);

/* -- hardware ------------------------------------------------------------- */

bool ca_ubiquity_low_ram_phone_supports_mb(int ram_mb);
bool ca_ubiquity_low_cpu_supports_clock_mhz(int clock_mhz);
bool ca_ubiquity_kai_os_support_is_compiled(void);

typedef struct ca_offline_queued_operation ca_offline_queued_operation_t;

ca_offline_queued_operation_t *ca_ubiquity_offline_queue_new(void);
void ca_ubiquity_offline_queue_free(ca_offline_queued_operation_t *queue);

bool ca_ubiquity_offline_queue_enqueue(ca_offline_queued_operation_t *queue,
                                       const char *operation_json);

/* FIFO. Ordering is not an implementation detail here - operations queued
 * offline are things like "send this", and replaying them out of order is how a
 * reply arrives before the message it answers. Caller frees the result. */
char *ca_ubiquity_offline_queue_dequeue(ca_offline_queued_operation_t *queue);

size_t ca_ubiquity_offline_queue_pending_count(const ca_offline_queued_operation_t *queue);

/* -- fallbacks: answering somebody with no data --------------------------- */

typedef struct ca_sms_fallback ca_sms_fallback_t;

/* `deliver` is the host's SMS gateway; NULL records without sending, which is
 * what a test wants and what a device with no SIM does. */
ca_sms_fallback_t *ca_ubiquity_sms_fallback_new(
    void (*deliver)(void *state, const char *phone, const char *body), void *state);

void ca_ubiquity_sms_fallback_free(ca_sms_fallback_t *fallback);

bool ca_ubiquity_sms_fallback_answer(ca_sms_fallback_t *fallback,
                                     const char *phone_number, const char *question);

size_t ca_ubiquity_sms_fallback_sent_count(const ca_sms_fallback_t *fallback);

typedef struct ca_ussd_fallback ca_ussd_fallback_t;

ca_ussd_fallback_t *ca_ubiquity_ussd_fallback_new(void);
void ca_ubiquity_ussd_fallback_free(ca_ussd_fallback_t *fallback);

/*
 * One USSD turn: session id and keypress in, the next menu out.
 *
 * A REAL STATE MACHINE, not a fixed string. USSD has no back button and no
 * scrollback - the menu on the screen is the entire interface - so an
 * unrecognised keypress REDISPLAYS the current menu rather than resetting to the
 * root. Resetting would drop somebody three levels deep back to the start for a
 * mistyped digit, on the one interface where they cannot see what happened.
 *
 * Caller frees.
 */
char *ca_ubiquity_ussd_fallback_respond(ca_ussd_fallback_t *fallback,
                                        const char *ussd_session, const char *input);

/* -- services ------------------------------------------------------------- */

typedef struct ca_whats_app_integration ca_whats_app_integration_t;

ca_whats_app_integration_t *ca_ubiquity_whats_app_integration_new(
    void (*send)(void *state, const char *phone, const char *body), void *state);

void ca_ubiquity_whats_app_integration_free(ca_whats_app_integration_t *integration);

/* Validates E.164 before recording. The check is here rather than at the gateway
 * because an invalid number that reaches the outbox has already been counted as
 * sent, and reconciling that later is guesswork. */
bool ca_ubiquity_whats_app_send(ca_whats_app_integration_t *integration,
                                const char *phone_number, const char *message);

size_t ca_ubiquity_whats_app_outbox_count(const ca_whats_app_integration_t *integration);

typedef struct ca_telegram_integration ca_telegram_integration_t;

ca_telegram_integration_t *ca_ubiquity_telegram_integration_new(
    void (*send)(void *state, const char *chat_id, const char *body), void *state);

void ca_ubiquity_telegram_integration_free(ca_telegram_integration_t *integration);

bool ca_ubiquity_telegram_send(ca_telegram_integration_t *integration,
                               const char *chat_id, const char *message);

size_t ca_ubiquity_telegram_outbox_count(const ca_telegram_integration_t *integration);

/* -- recovery ------------------------------------------------------------- */

typedef struct ca_lost_device_flow ca_lost_device_flow_t;

ca_lost_device_flow_t *ca_ubiquity_lost_device_flow_new(void);
void ca_ubiquity_lost_device_flow_free(ca_lost_device_flow_t *flow);

bool ca_ubiquity_lost_device_remote_wipe(ca_lost_device_flow_t *flow, const char *device_id);
bool ca_ubiquity_lost_device_is_wiped(const ca_lost_device_flow_t *flow,
                                      const char *device_id);

typedef struct ca_inheritance_protocol ca_inheritance_protocol_t;

ca_inheritance_protocol_t *ca_ubiquity_inheritance_protocol_new(void);
void ca_ubiquity_inheritance_protocol_free(ca_inheritance_protocol_t *protocol);

/* Refuses owner == designee. Naming yourself your own heir is not a designation,
 * it is a way for the recovery flow to hand an account to whoever already has
 * it. */
bool ca_ubiquity_inheritance_designate(ca_inheritance_protocol_t *protocol,
                                       const char *owner_id, const char *designee_id);

const char *ca_ubiquity_inheritance_designee_for(const ca_inheritance_protocol_t *protocol,
                                                 const char *owner_id);

/*
 * Wipes and returns a certificate: SHA-256 over "wipe|owner|iso-time|nonce".
 *
 * The nonce is what makes the certificate evidence rather than decoration -
 * without it the hash is a function of the owner and the second, and anybody can
 * produce one for a wipe that never happened.
 *
 * Writes 32 bytes into `out_certificate`.
 */
bool ca_ubiquity_verifiable_wipe_and_certify(const char *owner_id,
                                             uint8_t out_certificate[32]);

/*
 * Everything held about somebody, as a portable bundle. Caller frees.
 *
 * Not a favour and not a retention feature: it is the thing that makes leaving
 * possible, and a product that cannot be left is not one somebody chose.
 */
char *ca_ubiquity_data_portability_export(const char *owner_id);

typedef struct ca_account_compromise_recovery ca_account_compromise_recovery_t;

ca_account_compromise_recovery_t *ca_ubiquity_account_compromise_recovery_new(void);
void ca_ubiquity_account_compromise_recovery_free(
    ca_account_compromise_recovery_t *recovery);

bool ca_ubiquity_account_compromise_begin(ca_account_compromise_recovery_t *recovery,
                                          const char *owner_id);
bool ca_ubiquity_account_compromise_in_recovery(
    const ca_account_compromise_recovery_t *recovery, const char *owner_id);
void ca_ubiquity_account_compromise_complete(ca_account_compromise_recovery_t *recovery,
                                             const char *owner_id);

/* -- failure modes -------------------------------------------------------- */

typedef struct ca_impaired_user_mode ca_impaired_user_mode_t;

ca_impaired_user_mode_t *ca_ubiquity_impaired_user_mode_new(void);
void ca_ubiquity_impaired_user_mode_free(ca_impaired_user_mode_t *mode);

bool ca_ubiquity_impaired_user_mode_engage(ca_impaired_user_mode_t *mode,
                                           const char *owner_id);
bool ca_ubiquity_impaired_user_mode_is_engaged(const ca_impaired_user_mode_t *mode,
                                               const char *owner_id);
void ca_ubiquity_impaired_user_mode_disengage(ca_impaired_user_mode_t *mode,
                                              const char *owner_id);

typedef struct ca_abusive_environment_mode ca_abusive_environment_mode_t;

ca_abusive_environment_mode_t *ca_ubiquity_abusive_environment_mode_new(void);
void ca_ubiquity_abusive_environment_mode_free(ca_abusive_environment_mode_t *mode);

bool ca_ubiquity_abusive_environment_engage(ca_abusive_environment_mode_t *mode,
                                            const char *owner_id);
bool ca_ubiquity_abusive_environment_is_engaged(const ca_abusive_environment_mode_t *mode,
                                                const char *owner_id);

/*
 * A phrase somebody can say out loud, in front of the person they are afraid of,
 * that engages abuse-safe mode without looking like anything.
 *
 * DETERMINISTIC PER OWNER, from an eight-word benign vocabulary, via FNV-1a-32
 * over UTF-8. FNV rather than the platform's string hash because .NET randomises
 * its hash per process: the phrase must survive a restart, and it must be
 * BYTE-IDENTICAL in every port, or somebody's phrase stops working the day their
 * device changes hands between an Android build and a desktop one.
 *
 * The vocabulary is deliberately dull - thunder, river, amber, field, rain,
 * stone, harbor, linen - so the sentence sounds like nothing at all.
 *
 * Borrowed; valid until the mode is freed.
 */
const char *ca_ubiquity_abusive_environment_safety_phrase(
    ca_abusive_environment_mode_t *mode, const char *owner_id);

/* Exposed because the phrase has to be reproducible OUTSIDE this rail - a test
 * in another port checks the same owner gives the same words. */
uint32_t ca_ubiquity_fnv1a32(const char *utf8);

/* -- cultural ------------------------------------------------------------- */

const char *ca_ubiquity_third_party_harm_liability_framework(void);

typedef struct ca_quiet_mode ca_quiet_mode_t;

ca_quiet_mode_t *ca_ubiquity_quiet_mode_new(void);
void ca_ubiquity_quiet_mode_free(ca_quiet_mode_t *mode);

/* A window during which the assistant does not speak first. Refuses a
 * non-positive duration: a zero-length quiet window reads as "quiet is on" to
 * anybody skimming the list, and is silently never true. */
bool ca_ubiquity_quiet_mode_engage(ca_quiet_mode_t *mode, const char *reason,
                                   int64_t duration_seconds);

bool ca_ubiquity_quiet_mode_is_quiet_at(const ca_quiet_mode_t *mode, int64_t moment_unix);

/* Windows that have not yet ended. Expired ones are filtered on read rather than
 * swept on a timer - there is no thread here, and a rail that needs one to stay
 * correct is a rail that is wrong whenever the thread is late. */
size_t ca_ubiquity_quiet_mode_active_window_count(const ca_quiet_mode_t *mode,
                                                  int64_t now_unix);

bool ca_ubiquity_child_protection_mode_coppa(void);
bool ca_ubiquity_child_protection_mode_gdpr_k(void);

size_t ca_ubiquity_religious_accommodation_count(void);
const char *ca_ubiquity_religious_accommodation_at(size_t index);

const char *ca_ubiquity_indigenous_data_sovereignty_standard(void);

typedef struct ca_public_transparency ca_public_transparency_t;

ca_public_transparency_t *ca_ubiquity_public_transparency_new(void);
void ca_ubiquity_public_transparency_free(ca_public_transparency_t *transparency);

/* Refuses anything that is not an absolute http/https URL. A relative link as
 * evidence resolves against whatever page renders it, which means the claim
 * points at a different document depending on where you read it. */
bool ca_ubiquity_public_transparency_link_evidence(ca_public_transparency_t *transparency,
                                                   const char *claim,
                                                   const char *evidence_url);

size_t ca_ubiquity_public_transparency_linked_count(
    const ca_public_transparency_t *transparency);

#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_UBIQUITY_RAILS_H */
