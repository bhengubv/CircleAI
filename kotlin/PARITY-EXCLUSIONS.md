# Kotlin parity exclusions

163 of 166 modules are at or above 60% type coverage; overall 2438 of 2640
public types (92.3%). This file names what is **deliberately absent** and why,
so that a gap in the parity report can be read as a decision rather than as
unfinished work.

The rule applied throughout: **port what the C# decides, leave what the C#
merely calls.** Where a C# type is a thin wrapper over a platform API, the
Kotlin port carries the contract and the logic and leaves the wrapper to the
host — because a Kotlin class that pretends to bind an Android service it
cannot reach is worse than an honest absence.

---

## CircleAI.Device — 0 of 8

| Type | Why not |
|---|---|
| `AndroidDeviceMemory` | Reads `ActivityManager.MemoryInfo` through the Android framework. |
| `AndroidMemoryPressure` | `ComponentCallbacks2.onTrimMemory` — an Android lifecycle callback. |
| `CircleNeuronBinder`, `CircleNeuronConnection`, `CircleNeuronService`, `ServiceState` | Android **Binder/AIDL**. There is no cross-platform Binder, and the whole point of these types is the IPC boundary. |
| `DeviceMemoryProbe` | Dispatches to the two Android probes above. |
| `IResidentListener` | The contract for a foreground service that keeps the microphone resident. |

**What the Kotlin port does instead:** the memory-pressure *policy* — what to
unload and in what order — is already ported and tested; only the Android hooks
that feed it are absent. A JVM host implements `IResidentListener` and the
probe seam itself.

Worth stating plainly, because it has bitten before: on Android the memory
probe must read *physical* RAM, not the GC heap limit. A head that forgets to
set the platform probe silently reports the JVM heap and every decision built
on it is wrong.

## CircleAI.Maui — 0 of 9

Every type here is a .NET MAUI binding: `MauiAudioCapture`, `MauiCameraCapture`,
`MauiDeviceContext`, `MauiInferenceService`, `MauiPushSender`, `LocationBridge`,
`HealthBoardBridge`, `AlwaysOnService`, `CircleAlwaysOnAndroidService`.

MAUI is a .NET UI framework. There is no Kotlin equivalent to port *to* — the
Kotlin counterpart of this module is an Android app, and it implements the same
seams (`IAudioCapture`, `ICameraCapture`, `IPushSender`, …) which **are** ported
here. The contracts crossed; the framework bindings did not, and could not.

## CircleAI.AetherNet — 4 of 12

The four ported are the ones with logic in them. The eight absent are adapters
onto the **AetherNet SDK**, which lives in a separate repository
(`bhengubv/aether-protocol`) and is not a dependency of this package:

`AetherNetCompanionStateChannel`, `AetherNetContextAdapter`,
`AetherNetDirectiveSink`, `AetherNetInboundDirectiveBridge`,
`AetherNetTelemetryAdapter`, `CircleAiAetherNetAiProvider`,
`IMeshCapabilityBroadcaster`, `ServiceCollectionExtensions`.

Each is a translation between a CircleAI contract and an AetherNet type. Porting
the translation without the types it translates would produce classes that
reference nothing and cannot be tested. When the Kotlin AetherNet client exists,
these are the first thing to write against it.

## Dependency-injection registration — 3 types

`ServiceCollectionExtensions` (×2), `MemoryServiceCollectionExtensions`.

These register types with `Microsoft.Extensions.DependencyInjection`. Kotlin has
no single equivalent container, and hard-wiring one — Koin, Dagger, Hilt — into
a library would force that choice on every consumer. Every type they register is
ported and constructible directly; wiring is the host's to do.

---

## What is **not** excluded

Two things that could reasonably have been excluded and were not, because they
turned out to be portable on the JVM:

- **`CircleAI.Memory.Sql`** — `SqlDialect` and `AdoAtomStore` are ported over
  `java.sql.Connection`, with **no driver dependency in the library**, exactly as
  the C# takes a `DbConnection` and references none. `sqlite-jdbc` is added at
  *test* scope only, so the suite runs real SQL against a real engine.

- **`CircleAI.Cast`'s I/O half** — `TcpMediaHost` is a real HTTP server with
  Range support, tested against a real socket with a real HTTP client rather
  than a fake in front of one.
