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

---

# Machine-readable record

`kotlin/tools/parity_kt.py` reads the blocks below, so this document IS the
measure's configuration. A type listed here stops being counted as missing;
adding a line is therefore a claim on the record, not a way to make a number go
up quietly.

**What is NOT excluded here, and is in the Swift port.** Kotlin has annotations,
so `CircleAIVerificationStatusAttribute` is portable. Kotlin has sealed classes
with real subclass names, so a C# nested-record hierarchy keeps its type names
rather than collapsing to enum cases. Kotlin has JDBC, so `Memory.Sql` is
portable. Those three categories are exclusions in Swift and work here.

## Android and MAUI platform bindings

These ARE the platform head. The Kotlin port is a pure JVM library with no
Android SDK dependency, and a class that pretends to bind an Android service it
cannot reach is worse than an honest absence. The seams they fill —
`IAudioCapture`, `IDeviceContext`, the memory-probe hook — are all ported, so an
Android host supplies its own.

```excluded
Device.AndroidDeviceMemory           reads ActivityManager.MemoryInfo
Device.AndroidMemoryPressure         ComponentCallbacks2.onTrimMemory
Device.CircleNeuronBinder            Android Binder/AIDL
Device.CircleNeuronConnection        Android ServiceConnection
Device.CircleNeuronService           Android bound service
Device.ServiceState                  Android bound-service state
Device.DeviceMemoryProbe             dispatches to the two Android probes
Device.IResidentListener             foreground-service microphone contract
Maui.AlwaysOnService                 .NET MAUI binding
Maui.CircleAlwaysOnAndroidService    Android foreground service
Maui.HealthBoardBridge               .NET MAUI binding
Maui.LocationBridge                  .NET MAUI binding
Maui.MauiAudioCapture                .NET MAUI binding
Maui.MauiCameraCapture               .NET MAUI binding
Maui.MauiDeviceContext               .NET MAUI binding
Maui.MauiInferenceService            .NET MAUI binding
Maui.MauiPushSender                  .NET MAUI binding
```

## Native runtimes

Everything that needs onnxruntime, whisper.cpp, espeak-ng, Open JTalk or MNN
linked in. Where a DECISION was worth keeping it is ported and only the binding
stays behind — `WakeWordFactory` chooses the engine without onnxruntime,
`KaldiFbank` is the whole DSP front end in pure Kotlin.

```excluded
Voice.OnnxSessionFactory             needs onnxruntime
Voice.OnnxTtsEngine                  needs onnxruntime
Voice.OnnxSpeakerIdentity            needs onnxruntime
Voice.OnnxSpeechEmotionDetector      needs onnxruntime
Voice.KokoroTtsEngine                needs onnxruntime
Voice.PocketTtsEngine                needs onnxruntime
Voice.ToucanOnnxTtsEngine            needs onnxruntime
Voice.ZipformerKwsSpotter            needs onnxruntime
Voice.ZipformerWakeWordDetector      needs onnxruntime
Voice.WhisperTranscriber             needs whisper.cpp
Voice.WhisperNetTranscriber          needs whisper.cpp
Voice.NativeEspeakPhonemizer         espeak-ng is GPL: out of process only
Voice.OpenJTalkPhonemizer            needs Open JTalk and its 103 MB dictionary
Companion.OnnxSpeakerIdentityAdapter needs onnxruntime
Companion.OnnxSpeechEmotionSensor    needs onnxruntime
Inference.QwenTextGenerator          needs the MNN runtime
Inference.KimiVlGenerator            needs the MNN runtime
Inference.MnnNativeDiagnostics       needs the MNN runtime
Inference.MnnRuntimeConfig           needs the MNN runtime
Inference.LoRAAdapterManager         needs the MNN runtime
Inference.MmapWeightLoader           needs the MNN runtime
Inference.SpeculativeDecodingPipeline  needs the MNN runtime
Inference.NativeLibraryResolver      resolves .so/.dll by platform ABI
Inference.NativeRuntimePrep          unpacks native runtimes per platform ABI
Inference.Server.MnnInferenceBridgeFactory  needs the MNN runtime
Runtime.NativeRuntimeFetcher         fetches and extracts native runtime archives
Embeddings.Local.TurboVecEmbeddingIndex  P/Invoke over the turbovec Rust crate
```

`NativeEspeakPhonemizer` has a second reason: espeak-ng is GPL. The subprocess
phonemizer IS ported, because a pipe is a boundary the licence respects and
linking would make this package GPL too.

## ASP.NET Core

The inference server is minimal-API endpoint registration, auth handlers and a
host builder. A JVM server would be a different program rather than a port. The
DTOs, options and the SSE framing are data and ARE ported.

```excluded
Inference.Server.Program                   ASP.NET entry point
Inference.Server.InferenceServerBuilder    ASP.NET host builder
Inference.Server.AdminEndpoints            ASP.NET minimal API
Inference.Server.ChatCompletionsEndpoint   ASP.NET minimal API
Inference.Server.CompanionEndpoint         ASP.NET minimal API
Inference.Server.DiagnosticsEndpoint       ASP.NET minimal API
Inference.Server.EmbeddingsEndpoint        ASP.NET minimal API
Inference.Server.ApiKeyAuthSchemeOptions   ASP.NET auth handler options
Hosting.Mcp.McpEndpoints                   ASP.NET minimal API
Hosting.HttpLoopbackEndpoint               ASP.NET minimal API
Core.CircleAIComponentBase                 Blazor ComponentBase
```

## The DI container

Registration code for `Microsoft.Extensions.DependencyInjection`. There is no
such container here and adding one would be a large opinion imposed on every
host; Kotlin construction is explicit. The IDS those files define ARE ported,
because a typo in a registration key is a provider that is configured, present
and never selected.

```excluded
AetherNet.ServiceCollectionExtensions                        DI registration
CodeAgent.ServiceCollectionExtensions                        DI registration
Hosting.ServiceCollectionExtensions                          DI registration
Hosting.NeuronServiceCollectionExtensions                    DI registration
Hosting.CloudFallback.CloudFallbackServiceCollectionExtensions  DI registration
Hosting.Mcp.McpServiceCollectionExtensions                   DI registration
Hosting.Multiplayer.MultiplayerServiceCollectionExtensions   DI registration
Memory.ServiceCollectionExtensions                           DI registration
Memory.MemoryServiceCollectionExtensions                     DI registration
Mesh.ServiceCollectionExtensions                             DI registration
Plugins.PluginsServiceCollectionExtensions                   DI registration
Realtime.RealtimeServiceCollectionExtensions                 DI registration
Realtime.Cloud.RealtimeCloudServiceCollectionExtensions      DI registration
Runtime.CircleAIRuntimeServiceCollectionExtensions           DI registration
Security.AetherNet.ServiceCollectionExtensions               DI registration
Speech.Cloud.SpeechCloudServiceCollectionExtensions          DI registration
Telephony.TelephonyServiceCollectionExtensions               DI registration
Telephony.Plivo.PlivoServiceCollectionExtensions             DI registration
Telephony.Telnyx.TelnyxServiceCollectionExtensions           DI registration
Telephony.Twilio.TwilioServiceCollectionExtensions           DI registration
Vision.Cloud.VisionCloudServiceCollectionExtensions          DI registration
Web.ServiceCollectionExtensions                              DI registration
Companion.ServiceCollectionExtensions                        DI registration
```

## Extension-method holders

A C# static class that exists only to hang extension methods on. Kotlin
extension functions live at file scope and have no holder type — which is what
the C# was emulating.

```excluded
Memory.AffectStateVadExtensions      extension functions on AffectState
Hosting.ToolCatalogExtensions        extension functions on the tool catalogue
Companion.CompanionRecallExtensions  extension functions on the recall seam
```

## .NET assembly loading

`PluginLoader` loads `.dll` files into collectible `AssemblyLoadContext`s and
finds the plugin by reflection. The JVM has classloaders, but loading a .NET
assembly is not a thing it can do, and the C# type is that loader.
`PluginLoadResult` is its return shape. The lifecycle around it IS ported.

```excluded
Plugins.PluginLoader                 loads .NET assemblies
Plugins.PluginLoadResult             the assembly loader's result shape
```

## PDFsharp

A PDF writer is a third-party managed library. The engines behind these seams
are host-supplied; the seams are ported.

```excluded
Charts.PdfSharpChartRenderer         needs PDFsharp
Documents.PdfSharpDocumentEngine     needs PDFsharp
Presentations.PdfSharpDeckEngine     needs PDFsharp
```

## Windows-only automation

```excluded
WindowsAutomation.UiElementHelpers   UI Automation is a Windows COM API
Desktop.DesktopCompanionAdapter      binds a Windows desktop shell
```
