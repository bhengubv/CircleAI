"""Generates python/src/circle_ai/distribution/ubiquity_rails.py.

A SEPARATE file from ubiquity.py, which already holds the rails that carry
state — the app-store submitter, the signed delta updater, the abuse-safe
mode with its FNV-1a safety phrase. This one is the constant half.

The C# side is itself a table — seventy-odd "rails", each a small interface with
a default whose whole content is a constant, plus a dozen that hold state.
Writing that out by hand seventy times is how a value gets mistyped and nobody
notices, so the table is the source here and the Python is generated from it,
exactly as the C port does for the same module.

WHY THESE ARE CONSTANTS AND NOT CONFIGURATION. Every one of them is a DECISION
somebody made and has to be able to defend. A regulator approval that could be
flipped by a config file is not an approval; a cost ceiling a deployment can
raise is not a ceiling.

Money is in MINOR UNITS as integers throughout. Money in fractional binary is
how a total stops matching the sum of its parts.
"""
import io, os

# (interface name, kind, payload, doc)
#   list -> a constant list of strings, exposed as a property
#   text -> a constant string
#   flag -> a constant bool
#   real -> a constant float
#   int  -> a constant int
#   pair -> two named values: [(prop, kind, value), ...]
RAILS = [
    # ── distribution ────────────────────────────────────────────────────────
    ("IOemPreloadCatalog", "list", ("partners",
     ["Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei"]),
     "OEMs with a preload agreement. Mid-tier and below, because that is what\n    people here actually buy."),
    ("ICarrierPreloadCatalog", "list", ("carriers",
     ["MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel"]),
     "Carriers with a preload agreement."),
    ("IPwaFallback", "text", ("pwa_url", "https://app.circle.ai"),
     "Where a device with no installable package goes instead."),
    ("ISideloadChannel", "list", ("formats", ["APK", "IPA", "MSIX"]),
     "Formats somebody can be handed directly, without a store."),
    ("ILinuxRepoFanout", "list", ("repos",
     ["apt", "yum", "pacman", "brew", "flatpak", "snap"]),
     "Package managers the Linux build fans out to."),

    # ── trust ───────────────────────────────────────────────────────────────
    ("IThirdPartySecurityAuditPublisher", "text",
     ("report_url", "https://trust.circle.ai/audit"),
     "Where the third-party audit is published. A claim nobody can check is a\n    claim, not evidence."),
    ("IComplianceCertifications", "list", ("certifications",
     ["SOC 2 Type II", "ISO 27001", "ISO 27701"]),
     "Certifications held."),
    ("IBugBountyChannel", "pair",
     [("platform", "text", "HackerOne"), ("submission_url", "text", "https://h1.com/circleai")],
     "Where to report a vulnerability, and on what platform."),
    ("IPrivacyRegulationCompliance", "list", ("laws", ["GDPR", "POPIA", "CCPA", "LGPD"]),
     "Privacy laws this is built to comply with. POPIA is second because it is\n    the one that governs most of the people using it."),
    ("IVerifiablePrivacyProof", "pair",
     [("build_is_reproducible", "flag", True),
      ("source_url", "text", "https://github.com/bhengubv/CircleAI")],
     "A reproducible build and a source URL: the two things that let somebody\n    CHECK the privacy claim instead of believing it."),

    # ── pricing ─────────────────────────────────────────────────────────────
    ("IPluginMarketplaceRevenueShare", "pair",
     [("author_share", "real", 0.70), ("verified_safe_share", "real", 0.50)],
     "What a plugin author keeps. Verified-safe plugins take a smaller share\n    because verification costs something and somebody has to pay for it."),
    ("ICarrierRevenueShare", "real", ("carrier_share", 0.25),
     "What a carrier takes on a preload."),

    # ── localisation ────────────────────────────────────────────────────────
    ("ISaServiceConnectors", "pair",
     [("banks", "list", ["Capitec", "FNB", "Standard", "Absa", "Nedbank"]),
      ("wallets", "list", ["PayFast", "SnapScan"])],
     "South African banks and wallets with a connector."),
    ("ICrossBorderCorridors", "list", ("corridors", ["SADC", "ECOWAS", "EAC"]),
     "Cross-border corridors money can move along."),

    # ── connectors ──────────────────────────────────────────────────────────
    ("IEmailConnectorRegistry", "list", ("providers",
     ["Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP"]),
     "Mail providers with a connector. IMAP is LAST and is the one that means\n    somebody with an unlisted provider is not shut out."),
    ("ICalendarConnectorRegistry", "list", ("providers",
     ["Google", "Outlook", "Apple", "Yahoo", "CalDAV"]),
     "Calendar providers. CalDAV plays the same role IMAP does above."),
    ("ICrmConnectorRegistry", "list", ("providers",
     ["HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix"]),
     "CRMs with a connector."),
    ("IAccountingConnectorRegistry", "list", ("providers",
     ["Xero", "Sage", "QuickBooks", "Wave", "Manager.io"]),
     "Accounting packages with a connector."),
    ("IBankingConnectorRegistry", "list", ("providers",
     ["open-banking-ZA", "open-banking-NG", "open-banking-KE"]),
     "Open-banking rails, by jurisdiction. Named by STANDARD rather than by\n    bank, because the standard is what a connector actually speaks."),

    # ── hardware ────────────────────────────────────────────────────────────
    ("IKaiOsSupport", "flag", ("is_compiled", True),
     "KaiOS is compiled for. A feature phone is still a phone."),
    ("ILowRamPhoneSupport", "int", ("floor_mb", 512),
     "The RAM floor. Below this is not a target device; AT this it must still\n    work."),
    ("ILowCpuOptimization", "int", ("floor_mhz", 600),
     "The CPU floor. Optimising for a slow CPU is on by default, not a tuning\n    option: the devices this is for are the slow ones."),

    # ── regulators ──────────────────────────────────────────────────────────
    ("ISarbSandboxStatus", "flag", ("approved", False),
     "SARB sandbox status. FALSE, and it stays false until it is not — a\n    regulatory claim defaulting to true is the one lie that ends a company."),
    ("IIcasaApprovalStatus", "flag", ("approved", False),
     "ICASA status. False for the same reason."),
    ("IGlobalRegulatorEngagement", "list", ("active_jurisdictions",
     ["ZA", "NG", "KE", "US", "CA", "UK", "EU"]),
     "Jurisdictions with active regulator engagement."),
    ("ITaxInvoiceRegistry", "list", ("schemes", ["VAT", "GST", "Sales Tax", "DST"]),
     "Invoice tax schemes that can be issued under."),
    ("ILawfulInterceptCompliance", "text",
     ("posture", "Money decryptable to law, comms permanently blind"),
     "The posture, in one line, because it is the sentence a regulator asks\n    for. Money is auditable; conversations are not, and cannot be made so\n    later."),

    # ── failure modes ───────────────────────────────────────────────────────
    ("IBrainUnreachableMode", "flag", ("local_takeover_enabled", True),
     "When the remote brain cannot be reached the local one takes over. A\n    device that stops working when a server does is not on-device."),
    ("INoInternetCacheTarget", "real", ("hit_rate_target", 0.80),
     "The share of requests that must be answerable with no internet at all."),
    ("IStorageFullDegradationPolicy", "text",
     ("degrade_order", "cache > old-snapshots > chat-history > nothing"),
     "What is given up first when the disk fills. 'nothing' is last and is\n    LITERAL: the assistant never deletes what somebody said to it."),
    ("IPublicDisasterMode", "text", ("current_state", "normal"),
     "Current disaster posture."),

    # ── cost ────────────────────────────────────────────────────────────────
    ("ISustainablePerUserCostMath", "pair",
     [("monthly_revenue_per_user_minor", "int", 1900),
      ("monthly_marginal_cost_per_user_minor", "int", 380)],
     "Minor units, not floats. Money in fractional binary is how a total stops\n    matching the sum of its parts."),
    ("IPerCallCostCeiling", "int", ("ceiling_micro_usd", 400000),
     "A single call may not cost more than this."),
    ("IFreeTierCostCapping", "int", ("monthly_cap_micro_usd", 200000),
     "What the free tier is allowed to cost, per user, per month."),
    ("ILocalFirstRouting", "flag", ("preferred", True),
     "On-device first, always, unless something says otherwise."),

    # ── network effects ─────────────────────────────────────────────────────
    ("IReferralProgramme", "pair",
     [("reward_minor", "int", 1900), ("currency", "text", "ZAR")],
     "The referral reward, in the local currency's minor unit."),
    ("IFamilyAiSharing", "int", ("max_members", 6),
     "How many people share one family plan."),
    ("ICrossProviderFederation", "flag", ("enabled", True),
     "Federating with other providers is on. A network that only talks to\n    itself is a walled garden with extra steps."),
    ("IGroupNetworkEffects", "list", ("group_types", ["Stokvel", "Church", "Community"]),
     "The group shapes people actually organise into here. Stokvel FIRST,\n    because it is the one a foreign product always leaves out."),
    ("IUserGrowthFlywheel", "text", ("mechanic", "user invites friend; both get a month free"),
     "The growth mechanic, stated plainly so it can be argued with."),

    # ── duty of care ────────────────────────────────────────────────────────
    ("IThirdPartyHarmLiability", "text",
     ("framework", "Operator-of-record indemnity backed by insurance pool"),
     "Who answers when a third party is harmed."),
    ("IChildProtectionMode", "pair",
     [("coppa_compliant", "flag", True), ("gdpr_k_compliant", "flag", True)],
     "COPPA and GDPR-K."),
    ("IReligiousAccommodation", "list", ("supported_modes",
     ["prayer times", "Shabbat mode", "Eid silence"]),
     "Accommodations that change when the assistant SPEAKS AT ALL."),
    ("IIndigenousDataSovereignty", "text", ("standard", "CARE Principles"),
     "The standard indigenous data is held to. CARE, not just FAIR: FAIR makes\n    data usable, CARE asks whose it is."),
]

HEADER = '''"""The constant ubiquity rails — the decisions that turn the substrate into
something reachable where people actually are.

GENERATED by python/tools/gen_rails.py. Do not edit by hand; edit the table.

ubiquity.py alongside this holds the rails that HOLD STATE: the app-store
submitter, the signed delta updater, the abuse-safe mode and its safety phrase.
This file is the other half — the ones whose whole content is a decision.

Which stores and package managers it ships through, which connectors exist,
what the regulators have and have not approved, what happens when the network
or the disk runs out, what a user costs, and what the product will not do to a
child or to somebody in a dangerous house.

WHY THESE ARE CONSTANTS AND NOT CONFIGURATION. Every one of them is a DECISION
somebody made and has to be able to defend. A regulator approval that could be
flipped by a config file is not an approval; a cost ceiling that a deployment
can raise is not a ceiling. They live in the code so that changing one is a
commit with a name on it.

Money is in MINOR UNITS as integers throughout.
"""

from __future__ import annotations

from abc import ABC, abstractmethod

'''


def emit():
    out = [HEADER]
    exported = []

    for name, kind, payload, doc in RAILS:
        default = "Default" + name[1:]
        exported.extend([name, default])

        if kind == "pair":
            props = [(p, k, v) for p, k, v in payload]
        else:
            props = [(payload[0], kind, payload[1])]

        out.append('class %s(ABC):\n    """%s"""\n\n' % (name, doc))
        for prop, _, _ in props:
            out.append("    @property\n    @abstractmethod\n    def %s(self): ...\n\n" % prop)

        out.append('class %s(%s):\n    """The decision as it stands."""\n\n' % (default, name))
        for prop, k, v in props:
            out.append("    @property\n    def %s(self):\n        return %s\n\n" % (prop, repr(v)))

    out.append("__all__ = [\n")
    for n in sorted(exported):
        out.append('    "%s",\n' % n)
    out.append("]\n")

    path = "python/src/circle_ai/distribution"
    os.makedirs(path, exist_ok=True)
    io.open(os.path.join(path, "ubiquity_rails.py"), "w", encoding="utf-8", newline="\n").write("".join(out))
    print("wrote ubiquity_rails.py — %d rails, %d names" % (len(RAILS), len(exported)))


emit()
