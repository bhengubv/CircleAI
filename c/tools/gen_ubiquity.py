"""Generates c/include/circle_ai/ubiquity.h and c/src/ubiquity.c.

The C# side is itself a table — seventy-odd "rails", each a small interface with
a default whose whole content is a constant. Writing that table out by hand
seventy times is how a value gets mistyped and nobody notices, so the table is
the source here and the C is generated from it, exactly as the C# is generated
from the same list of decisions.

In C an interface becomes free functions and the `I` prefix goes: there is one
implementation and it is named for the thing, not for how it stores it. So
`IOemPreloadCatalog` and `DefaultOemPreloadCatalog` are both `ca_oem_preload_*`.
"""
import io, os

# name, kind, payload, doc
#   list   -> a constant list of strings
#   text   -> a constant string
#   flag   -> a constant bool
#   real   -> a constant double
#   int    -> a constant int
RAILS = [
    # ── distribution ────────────────────────────────────────────────────────
    ("pwa_fallback", "text", "https://app.circle.ai",
     "Where a device with no installable package goes instead."),
    ("sideload_channel", "list", ["APK", "IPA", "MSIX"],
     "Formats somebody can be handed directly, without a store."),
    ("linux_repo_fanout", "list", ["apt", "yum", "pacman", "brew", "flatpak", "snap"],
     "Package managers the Linux build fans out to."),

    # ── hardware reach ──────────────────────────────────────────────────────
    ("kaios_support", "flag", True,
     "KaiOS is compiled for. A feature phone is still a phone."),
    ("low_ram_phone_support_floor_mb", "int", 1024,
     "The RAM floor the product is expected to work at. Below this is not a\n"
     "  target device; at this it must still work."),
    ("low_cpu_optimization_enabled", "flag", True,
     "Optimising for a slow CPU is on by default, not a tuning option: the\n"
     "  devices this is for are the slow ones."),

    # ── connectors ──────────────────────────────────────────────────────────
    ("email_connector_registry_providers", "list",
     ["Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP"],
     "Mail providers with a connector. IMAP is last and is the one that means\n"
     "  somebody with an unlisted provider is not shut out."),
    ("calendar_connector_registry_providers", "list", ["Google", "Outlook", "Apple", "Yahoo", "CalDAV"],
     "Calendar providers. CalDAV plays the same role IMAP does above."),
    ("crm_connector_registry_providers", "list", ["HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix"],
     "CRMs with a connector."),
    ("accounting_connector_registry_providers", "list",
     ["Xero", "Sage", "QuickBooks", "Wave", "Manager.io"],
     "Accounting packages with a connector."),
    ("banking_connector_registry_providers", "list",
     ["open-banking-ZA", "open-banking-NG", "open-banking-KE"],
     "Open-banking rails, by jurisdiction. Named by standard rather than by\n"
     "  bank, because the standard is what a connector actually speaks."),

    # ── regulators ──────────────────────────────────────────────────────────
    ("sarb_sandbox_status_approved", "flag", False,
     "SARB sandbox status. FALSE, and it stays false until it is not — a\n"
     "  regulatory claim defaulting to true is the one lie that ends a company."),
    ("icasa_approval_status_approved", "flag", False,
     "ICASA status. False for the same reason."),
    ("global_regulator_engagement_jurisdictions", "list", ["ZA", "NG", "KE", "US", "CA", "UK", "EU"],
     "Jurisdictions with active regulator engagement."),
    ("tax_invoice_registry_schemes", "list", ["VAT", "GST", "Sales Tax", "DST"],
     "Invoice tax schemes that can be issued under."),
    ("lawful_intercept_compliance_posture", "text",
     "Money decryptable to law, comms permanently blind",
     "The posture, in one line, because it is the sentence a regulator asks for.\n"
     "  Money is auditable; conversations are not, and cannot be made so later."),

    # ── failure modes ───────────────────────────────────────────────────────
    ("brain_unreachable_mode_local_takeover", "flag", True,
     "When the remote brain cannot be reached the local one takes over. A\n"
     "  device that stops working when a server does is not on-device."),
    ("no_internet_cache_target_hit_rate", "real", 0.80,
     "The share of requests that must be answerable with no internet at all."),
    ("storage_full_degradation_policy_order", "text",
     "cache > old-snapshots > chat-history > nothing",
     "What is given up first when the disk fills. 'nothing' is last and is\n"
     "  literal: the assistant never deletes what somebody said to it."),
    ("public_disaster_mode_state", "text", "normal",
     "Current disaster posture."),

    # ── cost ────────────────────────────────────────────────────────────────
    ("sustainable_per_user_cost_math_revenue_cents", "int", 1900,
     "Cents, not a float. Money in fractional binary is how a total stops\n"
     "  matching the sum of its parts."),
    ("sustainable_per_user_cost_math_marginal_cents", "int", 380,
     "The marginal cost that revenue has to clear."),
    ("per_call_cost_ceiling_cents", "int", 40,
     "A single call may not cost more than this."),
    ("free_tier_cost_capping_cap_cents", "int", 20,
     "What the free tier is allowed to cost, per user, per month."),
    ("local_first_routing_preferred", "flag", True,
     "On-device first, always, unless something says otherwise."),

    # ── network effects ─────────────────────────────────────────────────────
    ("referral_programme_reward_cents", "int", 1900,
     "The referral reward, in the local currency's minor unit."),
    ("referral_programme_currency", "text", "ZAR", "The currency that reward is in."),
    ("family_ai_sharing_max_members", "int", 6, "How many people share one family plan."),
    ("cross_provider_federation_enabled", "flag", True,
     "Federating with other providers is on. A network that only talks to\n"
     "  itself is a walled garden with extra steps."),
    ("group_network_effects_types", "list", ["Stokvel", "Church", "Community"],
     "The group shapes people actually organise into here. Stokvel first,\n"
     "  because it is the one a foreign product always leaves out."),
    ("user_growth_flywheel_mechanic", "text", "user invites friend; both get a month free",
     "The growth mechanic, stated plainly so it can be argued with."),

    # ── duty of care ────────────────────────────────────────────────────────
    ("third_party_harm_liability_framework", "text",
     "Operator-of-record indemnity backed by insurance pool",
     "Who answers when a third party is harmed."),
    ("child_protection_mode_coppa", "flag", True, "COPPA."),
    ("child_protection_mode_gdpr_k", "flag", True, "GDPR-K."),
    ("religious_accommodation_modes", "list", ["prayer times", "Shabbat mode", "Eid silence"],
     "Accommodations that change when the assistant speaks at all."),
    ("indigenous_data_sovereignty_standard", "text", "CARE Principles",
     "The standard indigenous data is held to. CARE, not just FAIR: FAIR makes\n"
     "  data usable, CARE asks whose it is."),
]


def c_name(rail):
    return "ca_ubiquity_" + rail


HEADER = '''#ifndef CIRCLE_AI_UBIQUITY_H
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

'''

FOOTER = '''
#ifdef __cplusplus
}
#endif

#endif /* CIRCLE_AI_UBIQUITY_H */
'''

SOURCE_HEAD = '''/*
 * ubiquity.c - see ubiquity.h.
 *
 * Constant tables. Everything returned is static storage borrowed by the
 * caller: nothing here allocates, so nothing here can fail, and a rail can be
 * read on any thread at any time without a lock.
 */

#include "circle_ai/ubiquity.h"

#include <stddef.h>

'''


def emit():
    h = [HEADER]
    c = [SOURCE_HEAD]

    for rail, kind, payload, doc in RAILS:
        name = c_name(rail)
        h.append("/* %s */\n" % doc.replace("\n", "\n * "))

        if kind == "list":
            h.append("size_t %s_count(void);\n" % name)
            h.append("const char *%s_at(size_t index);\n\n" % name)

            items = ", ".join('"%s"' % s for s in payload)
            c.append("static const char *const %s_items[] = { %s };\n" % (name, items))
            c.append("size_t %s_count(void) {\n"
                     "    return sizeof %s_items / sizeof %s_items[0];\n}\n\n"
                     % (name, name, name))
            c.append("const char *%s_at(size_t index) {\n"
                     "    /* Out of range is NULL, not a crash: a caller walking a list it did\n"
                     "     * not size is a bug worth surviving. */\n"
                     "    return index < %s_count() ? %s_items[index] : NULL;\n}\n\n"
                     % (name, name, name))

        elif kind == "text":
            h.append("const char *%s(void);\n\n" % name)
            c.append('const char *%s(void) {\n    return "%s";\n}\n\n' % (name, payload))

        elif kind == "flag":
            h.append("bool %s(void);\n\n" % name)
            c.append("bool %s(void) {\n    return %s;\n}\n\n"
                     % (name, "true" if payload else "false"))

        elif kind == "int":
            h.append("int %s(void);\n\n" % name)
            c.append("int %s(void) {\n    return %d;\n}\n\n" % (name, payload))

        elif kind == "real":
            h.append("double %s(void);\n\n" % name)
            c.append("double %s(void) {\n    return %r;\n}\n\n" % (name, payload))

    h.append(FOOTER)

    os.makedirs("c/include/circle_ai", exist_ok=True)
    os.makedirs("c/src", exist_ok=True)
    io.open("c/include/circle_ai/ubiquity.h", "w", encoding="utf-8", newline="\n").write("".join(h))
    io.open("c/src/ubiquity.c", "w", encoding="utf-8", newline="\n").write("".join(c))
    print("wrote ubiquity.h and ubiquity.c — %d rails" % len(RAILS))


emit()
