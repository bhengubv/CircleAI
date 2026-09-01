/*
 * ubiquity.c - see ubiquity.h.
 *
 * Constant tables. Everything returned is static storage borrowed by the
 * caller: nothing here allocates, so nothing here can fail, and a rail can be
 * read on any thread at any time without a lock.
 */

#include "circle_ai/ubiquity.h"

#include <stddef.h>

const char *ca_ubiquity_pwa_fallback(void) {
    return "https://app.circle.ai";
}

static const char *const ca_ubiquity_sideload_channel_items[] = { "APK", "IPA", "MSIX" };
size_t ca_ubiquity_sideload_channel_count(void) {
    return sizeof ca_ubiquity_sideload_channel_items / sizeof ca_ubiquity_sideload_channel_items[0];
}

const char *ca_ubiquity_sideload_channel_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_sideload_channel_count() ? ca_ubiquity_sideload_channel_items[index] : NULL;
}

static const char *const ca_ubiquity_linux_repo_fanout_items[] = { "apt", "yum", "pacman", "brew", "flatpak", "snap" };
size_t ca_ubiquity_linux_repo_fanout_count(void) {
    return sizeof ca_ubiquity_linux_repo_fanout_items / sizeof ca_ubiquity_linux_repo_fanout_items[0];
}

const char *ca_ubiquity_linux_repo_fanout_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_linux_repo_fanout_count() ? ca_ubiquity_linux_repo_fanout_items[index] : NULL;
}

bool ca_ubiquity_kaios_support(void) {
    return true;
}

int ca_ubiquity_low_ram_phone_support_floor_mb(void) {
    return 1024;
}

bool ca_ubiquity_low_cpu_optimization_enabled(void) {
    return true;
}

static const char *const ca_ubiquity_email_connector_registry_providers_items[] = { "Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP" };
size_t ca_ubiquity_email_connector_registry_providers_count(void) {
    return sizeof ca_ubiquity_email_connector_registry_providers_items / sizeof ca_ubiquity_email_connector_registry_providers_items[0];
}

const char *ca_ubiquity_email_connector_registry_providers_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_email_connector_registry_providers_count() ? ca_ubiquity_email_connector_registry_providers_items[index] : NULL;
}

static const char *const ca_ubiquity_calendar_connector_registry_providers_items[] = { "Google", "Outlook", "Apple", "Yahoo", "CalDAV" };
size_t ca_ubiquity_calendar_connector_registry_providers_count(void) {
    return sizeof ca_ubiquity_calendar_connector_registry_providers_items / sizeof ca_ubiquity_calendar_connector_registry_providers_items[0];
}

const char *ca_ubiquity_calendar_connector_registry_providers_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_calendar_connector_registry_providers_count() ? ca_ubiquity_calendar_connector_registry_providers_items[index] : NULL;
}

static const char *const ca_ubiquity_crm_connector_registry_providers_items[] = { "HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix" };
size_t ca_ubiquity_crm_connector_registry_providers_count(void) {
    return sizeof ca_ubiquity_crm_connector_registry_providers_items / sizeof ca_ubiquity_crm_connector_registry_providers_items[0];
}

const char *ca_ubiquity_crm_connector_registry_providers_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_crm_connector_registry_providers_count() ? ca_ubiquity_crm_connector_registry_providers_items[index] : NULL;
}

static const char *const ca_ubiquity_accounting_connector_registry_providers_items[] = { "Xero", "Sage", "QuickBooks", "Wave", "Manager.io" };
size_t ca_ubiquity_accounting_connector_registry_providers_count(void) {
    return sizeof ca_ubiquity_accounting_connector_registry_providers_items / sizeof ca_ubiquity_accounting_connector_registry_providers_items[0];
}

const char *ca_ubiquity_accounting_connector_registry_providers_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_accounting_connector_registry_providers_count() ? ca_ubiquity_accounting_connector_registry_providers_items[index] : NULL;
}

static const char *const ca_ubiquity_banking_connector_registry_providers_items[] = { "open-banking-ZA", "open-banking-NG", "open-banking-KE" };
size_t ca_ubiquity_banking_connector_registry_providers_count(void) {
    return sizeof ca_ubiquity_banking_connector_registry_providers_items / sizeof ca_ubiquity_banking_connector_registry_providers_items[0];
}

const char *ca_ubiquity_banking_connector_registry_providers_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_banking_connector_registry_providers_count() ? ca_ubiquity_banking_connector_registry_providers_items[index] : NULL;
}

bool ca_ubiquity_sarb_sandbox_status_approved(void) {
    return false;
}

bool ca_ubiquity_icasa_approval_status_approved(void) {
    return false;
}

static const char *const ca_ubiquity_global_regulator_engagement_jurisdictions_items[] = { "ZA", "NG", "KE", "US", "CA", "UK", "EU" };
size_t ca_ubiquity_global_regulator_engagement_jurisdictions_count(void) {
    return sizeof ca_ubiquity_global_regulator_engagement_jurisdictions_items / sizeof ca_ubiquity_global_regulator_engagement_jurisdictions_items[0];
}

const char *ca_ubiquity_global_regulator_engagement_jurisdictions_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_global_regulator_engagement_jurisdictions_count() ? ca_ubiquity_global_regulator_engagement_jurisdictions_items[index] : NULL;
}

static const char *const ca_ubiquity_tax_invoice_registry_schemes_items[] = { "VAT", "GST", "Sales Tax", "DST" };
size_t ca_ubiquity_tax_invoice_registry_schemes_count(void) {
    return sizeof ca_ubiquity_tax_invoice_registry_schemes_items / sizeof ca_ubiquity_tax_invoice_registry_schemes_items[0];
}

const char *ca_ubiquity_tax_invoice_registry_schemes_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_tax_invoice_registry_schemes_count() ? ca_ubiquity_tax_invoice_registry_schemes_items[index] : NULL;
}

const char *ca_ubiquity_lawful_intercept_compliance_posture(void) {
    return "Money decryptable to law, comms permanently blind";
}

bool ca_ubiquity_brain_unreachable_mode_local_takeover(void) {
    return true;
}

double ca_ubiquity_no_internet_cache_target_hit_rate(void) {
    return 0.8;
}

const char *ca_ubiquity_storage_full_degradation_policy_order(void) {
    return "cache > old-snapshots > chat-history > nothing";
}

const char *ca_ubiquity_public_disaster_mode_state(void) {
    return "normal";
}

int ca_ubiquity_sustainable_per_user_cost_math_revenue_cents(void) {
    return 1900;
}

int ca_ubiquity_sustainable_per_user_cost_math_marginal_cents(void) {
    return 380;
}

int ca_ubiquity_per_call_cost_ceiling_cents(void) {
    return 40;
}

int ca_ubiquity_free_tier_cost_capping_cap_cents(void) {
    return 20;
}

bool ca_ubiquity_local_first_routing_preferred(void) {
    return true;
}

int ca_ubiquity_referral_programme_reward_cents(void) {
    return 1900;
}

const char *ca_ubiquity_referral_programme_currency(void) {
    return "ZAR";
}

int ca_ubiquity_family_ai_sharing_max_members(void) {
    return 6;
}

bool ca_ubiquity_cross_provider_federation_enabled(void) {
    return true;
}

static const char *const ca_ubiquity_group_network_effects_types_items[] = { "Stokvel", "Church", "Community" };
size_t ca_ubiquity_group_network_effects_types_count(void) {
    return sizeof ca_ubiquity_group_network_effects_types_items / sizeof ca_ubiquity_group_network_effects_types_items[0];
}

const char *ca_ubiquity_group_network_effects_types_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_group_network_effects_types_count() ? ca_ubiquity_group_network_effects_types_items[index] : NULL;
}

const char *ca_ubiquity_user_growth_flywheel_mechanic(void) {
    return "user invites friend; both get a month free";
}

const char *ca_ubiquity_third_party_harm_liability_framework(void) {
    return "Operator-of-record indemnity backed by insurance pool";
}

bool ca_ubiquity_child_protection_mode_coppa(void) {
    return true;
}

bool ca_ubiquity_child_protection_mode_gdpr_k(void) {
    return true;
}

static const char *const ca_ubiquity_religious_accommodation_modes_items[] = { "prayer times", "Shabbat mode", "Eid silence" };
size_t ca_ubiquity_religious_accommodation_modes_count(void) {
    return sizeof ca_ubiquity_religious_accommodation_modes_items / sizeof ca_ubiquity_religious_accommodation_modes_items[0];
}

const char *ca_ubiquity_religious_accommodation_modes_at(size_t index) {
    /* Out of range is NULL, not a crash: a caller walking a list it did
     * not size is a bug worth surviving. */
    return index < ca_ubiquity_religious_accommodation_modes_count() ? ca_ubiquity_religious_accommodation_modes_items[index] : NULL;
}

const char *ca_ubiquity_indigenous_data_sovereignty_standard(void) {
    return "CARE Principles";
}

