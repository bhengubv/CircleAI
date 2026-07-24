# CircleAI.Security.Defense

The **defensive immune system** for Circle AI: an always-on, on-device network/threat
monitor that shields the user by default. This is a pre-launch baseline — a user should
never be in the wild unprotected.

It is **defensive by purpose, end to end**: it watches only the device's own network
metadata (dialed IPs, DNS lookups, TLS SNI — never payloads), matches it against a
bundled known-bad indicator list, flags anomalous connection patterns, and escalates.
It performs no scanning, probing, or offensive action.

## What it does

- **Known-bad matching** — every outbound/inbound observation is checked against an
  offline blocklist of malicious IPv4 addresses, IPv4 CIDR ranges, IPv6 addresses, and
  domains (exact **and** parent-domain suffix).
- **Anomaly flags** — bounded sliding-window heuristics catch what a blocklist cannot:
  outbound fan-out (scan/sweep) and connection floods.
- **C2 beaconing escalation** — repeated contact with the *same* known-bad indicator
  escalates a `High` hit to a `Critical` command-and-control signal.
- **Always-on / autonomic** — started once at boot by the host, not user-launched.

## How it complements the rest of the SDK

This library is a **sibling** of `CircleAI.Security` and does not modify it.

| Concern | Owner |
|---|---|
| Peer trust scoring on the mesh (route around bad nodes) | `CircleAI.Security` (`PeerSecurityEvent`, `NodeTrustRegistry`) |
| Local runtime anomalies (memory / control-flow / biometric) | `CircleAI.Security` (`AnomalySignal`, `ISecurityWatchdog`) |
| **Network wire threats (known-bad endpoints, egress anomalies)** | **this library (`ThreatSignal`, `IThreatMonitor`)** |

The optional `WatchdogThreatSink` **forwards** a network `ThreatSignal` into the existing
`ISecurityWatchdog` as an `AnomalySignal`, so one response policy (key rotation, mesh
isolation, state rollback) covers both surfaces.

## Pairs with Panik/Nope SOS

Implement `ISosEscalation` in the SOS app and register a `SosThreatSink`. Only
`Critical` signals (default `SosSeverityFloor`) escalate, so the SOS channel stays quiet
until the device is genuinely compromised.

## Wiring (no DI container required)

```csharp
// feed  : your INetworkObservationFeed (Android VpnService metadata, AetherNet events…)
// sinks : where confirmed threats go
var sink = new CompositeThreatSink(
    new WatchdogThreatSink(securityWatchdog),   // reuse the existing immune system
    new SosThreatSink(panikNopeSos));           // life-safety escalation

DefenseModule defense = await DefenseModule.CreateAsync(feed, sink, loggerFactory: lf);
await defense.StartAsync();   // call once at boot — autonomic thereafter
```

## Bundled blocklist

`Data/defense-blocklist.txt` is embedded and loaded fully offline. It is refreshable at
runtime via `IIndicatorSource.RefreshFromAsync(...)` (e.g. a feed delivered over the
AetherNet mesh) but never *requires* the network.

The committed seed ships only **safe, public-domain placeholders** (RFC 5737/3849
documentation IP ranges, RFC 2606/6761 reserved domains) so no live third-party
malicious address is carried in the repo. It models these free production feeds — all
under licences that permit bundling and redistribution:

| Feed | Content | Licence |
|---|---|---|
| abuse.ch URLhaus | malicious URLs / hosts | CC0 1.0 (Public Domain) |
| abuse.ch Feodo Tracker | botnet C2 IPv4 | CC0 1.0 (Public Domain) |
| abuse.ch ThreatFox | IP / domain / hash IOCs | CC0 1.0 (Public Domain) |
| StevenBlack/hosts | ad + malware hosts | MIT |

## Design constraints

- **.NET 10**, no third-party NuGet dependencies (BCL only; logging via the
  `Microsoft.Extensions.Logging.Abstractions` already in `CircleAI.Core`).
- **100% offline**, **de-Googled** (no GMS / Play Services / platform cloud APIs).
- **Low-end Android**: `Evaluate()` is synchronous and allocation-light; all tracking
  state is bounded by `DefenseOptions.MaxTrackedConnections`.

## Verification status

- `BlocklistIndicatorSource`, `BlocklistThreatMonitor` — `WireProven` (deterministic,
  single-process; in-process signal stream).
- `AlwaysOnDefenseSentinel` — `Reference` (loop verified against a synthetic feed; real
  operation needs a host-provided `INetworkObservationFeed`).
