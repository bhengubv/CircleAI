# CircleAI Security — Authorized-Use Boundary

> **The one-line rule.** CircleAI's security is **defensive by purpose, end to end.**
> Every part of it exists to **shield the user**, never to attack. The threat-intel /
> OSINT / malware-awareness pieces — the **"antibodies"** — are produced *only* under a
> defined threat, run *only* behind an explicit **authorized-use gate**, and are
> **denied by default**. They are never bundled loose and never run on their own.

This document is the written boundary that ROADMAP Principle 8 and Phase 3 require
*before* any antibody enters the product. It is a **hard product rule**, not a
guideline. The rule is enforced in code by
[`src/CircleAI.Security.Antibodies`](../src/CircleAI.Security.Antibodies/) — this
document says *what* the boundary is; that library makes it *true at runtime*.

---

## Why this exists

The target user is on a hostile network, on a cheap phone, with no other protection.
The app itself has to be their shield. That makes security a **shipping gate**, not a
later feature: no user goes into the wild unprotected.

But "protection" cuts one way only. The moment a capability can *reach out and act on a
third party* — scan them, profile them, probe them — it stops being a shield and starts
being a weapon. CircleAI does not ship weapons. So the awareness capabilities that *look*
offensive in other toolkits (malware analysis, breach corpora, indicator lookups) are
admitted into CircleAI **only after being reframed as defence**, and **only behind this
boundary**.

---

## The boundary, plainly

### 1. Defensive by purpose, end to end
Every antibody answers exactly one shape of question, and always *about a threat to the
user*:

- *"Is **this file** the user is about to open known-bad?"* → warn before they open it.
- *"Is **this URL / IP / domain** the user is about to trust known-bad?"* → warn before they connect.
- *"Has **the user's own identity** (their email / username / phone) turned up in a breach corpus?"* → tell them to rotate it.

None of them scan, profile, enumerate, or probe another person or system. There is no
"look up *that* person," no active reconnaissance, no exploitation. The subject of every
antibody query is **the user's own safety**.

### 2. Produced only under a defined threat
An antibody never runs "just in case." Every invocation must carry a
**`DefensiveThreatContext`** — an explicit, recorded statement of *what threat* justifies
running it, *who* raised it, and *when*. No defined threat → no run. This is what
"never bundled loose" means in practice: the capability is inert until a real,
named threat calls for it.

### 3. Gated behind explicit authorized-use — **deny by default**
Between the caller and every antibody sits an **`IAuthorizedUseGate`**. The gate must be
*explicitly satisfied* before any antibody executes. The shipped default,
**`NullAuthorizedUseGate`, denies every request.** A capability becomes reachable only
when the host wires a gate that returns an explicit, unexpired, capability-scoped
authorization (see `ExplicitConsentAuthorizedUseGate`). Absence of configuration is
absence of permission. Silence is denial.

### 4. Never run by default, never bundled loose
- Antibodies are **off** in every build unless a threat + an authorization turn a single
  one on, for a single assessment.
- No live threat feeds. No network calls. No Google / GMS resolvers. The only data an
  antibody consults is a **local, read-only indicator corpus** the device already
  carries (empty by default — nothing ships loose). Fully offline, de-Googled,
  low-end-Android friendly.
- The user's identity is **hashed before lookup**; raw emails / usernames / phone numbers
  are never stored or transmitted by the breach-awareness path.

### 5. Warn and shield — never act
An antibody's output is **awareness**: a verdict plus *protective guidance the user can
act on* ("do not open this file," "rotate this password"). It never quarantines a third
party, never reports anyone, never takes an offensive action. It informs the person it is
protecting, and stops there. Autonomic *defensive reflexes* (the always-on monitor /
network defence of Phase 3) are a separate, purely-shielding subsystem; the antibodies
covered here are the deliberate, gated, on-demand awareness layer that sits above them.

---

## What is explicitly out of scope (and stays out)

The following are **not** built, not gated-and-hidden, simply **absent** — and this
document is the standing instruction that they remain absent:

- Active scanning, probing, or enumeration of any third-party host, account, or person.
- Exploitation, payload delivery, credential attacks, or any "offensive antibody."
- Profiling or dossier-building about anyone other than the user, about themselves.
- Live OSINT feeds, remote threat-intel APIs, or any call off the device.
- Any capability that acts *on* a third party rather than *informing* the user.

Source toolkits studied for *what threat-intel exists* (malwoverview, findme, deepdarkCTI,
ghost-osint-crm, hacktricks, neko-master) were used as a **reference for the shape of the
knowledge only**. Their offensive and third-party-facing behaviours were **not** carried
across. Only the defensive, user-protecting core was reframed and admitted.

---

## How the boundary is enforced in code

`CircleAI.Security.Antibodies` implements this document:

| Boundary rule | Enforcement |
|---|---|
| Deny by default | `NullAuthorizedUseGate` — the shipped default — denies every `AuthorizedUseRequest`. |
| Explicit authorized-use | `IAuthorizedUseGate` must return a granted `AuthorizationDecision` before any assessor runs; `ExplicitConsentAuthorizedUseGate` grants only on explicit, unexpired, capability-scoped consent. |
| Only under a defined threat | Every entry point requires a non-null `DefensiveThreatContext`; no context → no run. |
| Every capability behind the gate | `IDefensiveAntibodySystem` is the *only* run path, and it calls the gate before touching any assessor. A denied decision returns a `NotAuthorized` result **without invoking the capability.** |
| Offline / de-Googled / nothing loose | Assessors consult only an `ILocalIndicatorCorpus`; the default is `EmptyIndicatorCorpus` (no indicators bundled), no network, no Google APIs. |
| Warn, don't act | Every result is a `ThreatAwarenessResult` — a verdict plus `ProtectiveGuidance` — and nothing else. There is no action-taking API on this surface. |
| Protect the user's own identity | The breach path hashes identities (SHA-256) before lookup; raw identity values are never persisted. |

If a future change would let any antibody reach out and act on a third party, or run
without a satisfied gate and a defined threat, that change **violates this boundary** and
must not ship.

---

*Owner: CircleAI Security. This boundary is a shipping gate. Reviewed against ROADMAP
Principle 8 ("Built-in protection is a shipping gate — defensive by purpose, end to end")
and Phase 3 ("Immune system").*
