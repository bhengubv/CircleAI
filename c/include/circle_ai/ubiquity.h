#ifndef CIRCLE_AI_UBIQUITY_H
#define CIRCLE_AI_UBIQUITY_H

/*
 * ubiquity.h - CircleAI.Distribution.Ubiquity (C11).
 *
 * The "rails" that turn the substrate into something reachable where people
 * actually are: which stores and package managers it ships through, which
 * connectors exist, what the regulators have and have not approved, what
 * happens when the network or the disk runs out, what a user costs, and what
 * the product will not do to a child or to somebody in a dangerous house.
 *
 * WHY THESE ARE CONSTANTS AND NOT CONFIGURATION. Every one of them is a
 * DECISION somebody made and has to be able to defend. A regulator approval
 * that could be flipped by a config file is not an approval; a cost ceiling
 * that a deployment can raise is not a ceiling. They live in the binary so that
 * changing one is a commit with a name on it.
 *
 * In C an interface becomes free functions and the I prefix goes: there is one
 * implementation and it is named for the thing, not for how it stores it, so
 * IOemPreloadCatalog and DefaultOemPreloadCatalog are both ca_ubiquity_oem_*.
 *
 * Money is in MINOR UNITS as integers throughout. Money in fractional binary is
 * how a total stops matching the sum of its parts.
 *
 * Conventions: ca_ prefix, borrowed const char * returns (static storage, never
 * freed by the caller), counts as size_t, lists as (count, index) pairs.
 */

#include <stdbool.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

/* Where a device with no installable package goes instead. */
const char *ca_ubiquity_pwa_fallback(void);

/* Formats somebody can be handed directly, without a store. */
size_t ca_ubiquity_sideload_channel_count(void);
const char *ca_ubiquity_sideload_channel_at(size_t index);

/* Package managers the Linux build fans out to. */
size_t ca_ubiquity_linux_repo_fanout_count(void);
const char *ca_ubiquity_linux_repo_fanout_at(size_t index);

/* KaiOS is compiled for. A feature phone is still a phone. */
bool ca_ubiquity_kaios_support(void);

/* The RAM floor the product is expected to work at. Below this is not a
 *   target device; at this it must still work. */
int ca_ubiquity_low_ram_phone_support_floor_mb(void);

/* Optimising for a slow CPU is on by default, not a tuning option: the
 *   devices this is for are the slow ones. */
bool ca_ubiquity_low_cpu_optimization_enabled(void);

/* Mail providers with a connector. IMAP is last and is the one that means
 *   somebody with an unlisted provider is not shut out. */
size_t ca_ubiquity_email_connector_registry_providers_count(void);
const char *ca_ubiquity_email_connector_registry_providers_at(size_t index);

/* Calendar providers. CalDAV plays the same role IMAP does above. */
size_t ca_ubiquity_calendar_connector_registry_providers_count(void);
const char *ca_ubiquity_calendar_connector_registry_providers_at(size_t index);

/* CRMs with a connector. */
size_t ca_ubiquity_crm_connector_registry_providers_count(void);
const char *ca_ubiquity_crm_connector_registry_providers_at(size_t index);

/* Accounting packages with a connector. */
size_t ca_ubiquity_accounting_connector_registry_providers_count(void);
const char *ca_ubiquity_accounting_connector_registry_providers_at(size_t index);

/* Open-banking rails, by jurisdiction. Named by standard rather than by
 *   bank, because the standard is what a connector actually speaks. */
size_t ca_ubiquity_banking_connector_registry_providers_count(void);
const char *ca_ubiquity_banking_connector_registry_providers_at(size_t index);

/* SARB sandbox status. FALSE, and it stays false until it is not — a
 *   regulatory claim defaulting to true is the one lie that ends a company. */
bool ca_ubiquity_sarb_sandbox_status_approved(void);

/* ICASA status. False for the same reason. */
bool ca_ubiquity_icasa_approval_status_approved(void);

/* Jurisdictions with active regulator engagement. */
size_t ca_ubiquity_global_regulator_engagement_jurisdictions_count(void);
const char *ca_ubiquity_global_regulator_engagement_jurisdictions_at(size_t index);

/* Invoice tax schemes that can be issued under. */
size_t ca_ubiquity_tax_invoice_registry_schemes_count(void);
const char *ca_ubiquity_tax_invoice_registry_schemes_at(size_t index);

/* The posture, in one line, because it is the sentence a regulator asks for.
 *   Money is auditable; conversations are not, and cannot be made so later. */
const char *ca_ubiquity_lawful_intercept_compliance_posture(void);

/* When the remote brain cannot be reached the local one takes over. A
 *   device that stops working when a server does is not on-device. */
bool ca_ubiquity_brain_unreachable_mode_local_takeover(void);

/* The share of requests that must be answerable with no internet at all. */
double ca_ubiquity_no_internet_cache_target_hit_rate(void);

/* What is given up first when the disk fills. 'nothing' is last and is
 *   literal: the assistant never deletes what somebody said to it. */
const char *ca_ubiquity_storage_full_degradation_policy_order(void);

/* Current disaster posture. */
const char *ca_ubiquity_public_disaster_mode_state(void);

/* Cents, not a float. Money in fractional binary is how a total stops
 *   matching the sum of its parts. */
int ca_ubiquity_sustainable_per_user_cost_math_revenue_cents(void);

/* The marginal cost that revenue has to clear. */
int ca_ubiquity_sustainable_per_user_cost_math_marginal_cents(void);

/* A single call may not cost more than this. */
int ca_ubiquity_per_call_cost_ceiling_cents(void);

/* What the free tier is allowed to cost, per user, per month. */
int ca_ubiquity_free_tier_cost_capping_cap_cents(void);

/* On-device first, always, unless something says otherwise. */
bool ca_ubiquity_local_first_routing_preferred(void);

/* The referral reward, in the local currency's minor unit. */
int ca_ubiquity_referral_programme_reward_cents(void);

/* The currency that reward is in. */
const char *ca_ubiquity_referral_programme_currency(void);

/* How many people share one family plan. */
int ca_ubiquity_family_ai_sharing_max_members(void);

/* Federating with other providers is on. A network that only talks to
 *   itself is a walled garden with extra steps. */
bool ca_ubiquity_cross_provider_federation_enabled(void);

/* The group shapes people actually organise into here. Stokvel first,
 *   because it is the one a foreign product always leaves out. */
size_t ca_ubiquity_group_network_effects_types_count(void);
const char *ca_ubiquity_group_network_effects_types_at(size_t index);

/* The growth mechanic, stated plainly so it can be argued with. */
const char *ca_ubiquity_user_growth_flywheel_mechanic(void);

/* Who answers when a third party is harmed. */
const char *ca_ubiquity_third_party_harm_liability_framework(void);

/* COPPA. */
bool ca_ubiquity_child_protection_mode_coppa(void);

/* GDPR-K. */
bool ca_ubiquity_child_protection_mode_gdpr_k(void);

/* Accommodations that change when the assistant speaks at all. */
size_t ca_ubiquity_religious_accommodation_modes_count(void);
const char *ca_ubiquity_religious_accommodation_modes_at(size_t index);

/* The standard indigenous data is held to. CARE, not just FAIR: FAIR makes
 *   data usable, CARE asks whose it is. */
const char *ca_ubiquity_indigenous_data_sovereignty_standard(void);


#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_UBIQUITY_H */
