# CircleAI.Security.Antibodies

Defensive-by-purpose threat-**awareness** for CircleAI. These are the "antibodies":
the OSINT / threat-intel / malware-awareness capabilities — but admitted into the
product **only** in a form that warns and shields the user, never one that attacks.

Read the boundary first: [`docs/SECURITY_AUTHORIZED_USE.md`](../../docs/SECURITY_AUTHORIZED_USE.md).
This library is that document made true at runtime.

## What it does

Three questions, each about a threat to **the user**:

| Capability | Question | Framing |
|---|---|---|
| `FileReputationAwareness` | Is a file the user is about to open known-bad? | warn before they open it |
| `NetworkIndicatorAwareness` | Is a URL / IP / domain they're about to trust known-bad? | warn before they connect |
| `BreachExposureAwareness` | Has the user's **own** identity turned up in a breach? | tell them to rotate it |

Every capability returns a `ThreatAwarenessResult` — a verdict plus guidance the user
can act on. There is no action-taking on this surface. It warns; it never quarantines,
reports, or touches a third party.

## The two rules that shape the code

1. **Deny by default.** Nothing runs without a granted decision from an
   `IAuthorizedUseGate`. The shipped default, `NullAuthorizedUseGate`, denies
   everything. The whole subsystem is inert until a host wires a gate that can grant.
2. **Only under a defined threat.** Every call requires a `DefensiveThreatContext` —
   a named, recorded reason. No "just in case" scans.

`IDefensiveAntibodySystem` is the only run path, and it asks the gate before it touches
any capability. That is what structurally guarantees "every capability behind the gate".

## Offline, de-Googled, low-end friendly

- **No network.** The only data source is a local `ILocalIndicatorCorpus`. No live
  feeds, no remote APIs, no Google resolvers.
- **Nothing bundled loose.** The default `EmptyIndicatorCorpus` holds no indicators at
  all. A host supplies a local (ideally signed) dataset the device carries.
- **Privacy-preserving.** Files are assessed by SHA-256 hash, not contents. Identity
  values are hashed before any lookup and never persisted.
- **Dependency-free.** Pure BCL — no NuGet or project references. Nothing to restore on
  a phone; the smallest possible supply-chain surface for a security component.

## Using it

Deny-by-default (the shipped posture — refuses everything):

```csharp
IDefensiveAntibodySystem antibodies = DefensiveAntibodySystem.CreateDenyByDefault();

var threat = DefensiveThreatContext.Raise(
    reason: "User tapped an unexpected APK link in a message.",
    severity: ThreatSeverity.Elevated,
    raisedBy: "user-action");

var result = await antibodies.AssessFileAsync(
    FileArtifact.FromContent("update.apk", fileBytes), threat);

// result.WasAuthorized == false  → nothing was checked; the gate denied it.
```

Opting a capability in — explicit, time-boxed consent + a local corpus:

```csharp
var consents = new InMemoryAuthorizedUseConsentStore();
consents.Record(AuthorizedUseConsent.Grant(
    AntibodyCapability.FileReputationAwareness,
    grantedBy: "device-owner",
    scope: "suspicious APK from message",
    duration: TimeSpan.FromMinutes(10)));

var gate = new ExplicitConsentAuthorizedUseGate(consents);

var corpus = new InMemoryIndicatorCorpus();
corpus.Add(IndicatorKind.FileHashSha256, knownBadSha256Hex,
    ThreatAwarenessVerdict.KnownBad,
    note: "Known Android banking trojan.",
    protectiveGuidance: "Delete it and do not grant it any permissions.",
    source: "device-local malware set");

IDefensiveAntibodySystem antibodies = DefensiveAntibodySystem.Create(gate, corpus);

var result = await antibodies.AssessFileAsync(
    FileArtifact.FromContent("update.apk", fileBytes), threat);
// Now the check runs — and only for the 10-minute consent window, for this one capability.
```

## Layout

- `Gate/` — the authorized-use boundary: `IAuthorizedUseGate`, `NullAuthorizedUseGate`
  (deny-by-default), `ExplicitConsentAuthorizedUseGate`, consent types, and the
  `DefensiveThreatContext`.
- `Awareness/` — the defensive assessors, the local corpus abstraction, and the
  result / subject / verdict types. Depends only on the BCL — it knows nothing about
  the gate.
- `DefensiveAntibodySystem` — the gated facade tying the two together.

## Reference, not lineage

The *shape* of the threat knowledge was studied from `malwoverview`, `findme`,
`deepdarkCTI`, `ghost-osint-crm`, `hacktricks`, and `neko-master`. Their offensive and
third-party-facing behaviours were **not** carried across — only the defensive,
user-protecting core was reframed and admitted, behind this gate.
