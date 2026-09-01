# Swift parity: what is deliberately not a one-to-one type

The Swift port is measured by matching C# public type names against Swift
declarations. That measure is useful and it is not the truth: Swift has one
namespace where C# has many, no attributes, no exception classes, no DI
container, and enum cases where C# has nested record types. Every difference
below is a decision, not an omission.

**The rule this file exists to serve:** a port is done when every C# public type
is either ported or written down here with a reason. No self-chosen percentage
thresholds.

`swift/tools/parity_swift.py` reads the machine-readable blocks in this file, so
this document IS the measure's configuration. A type listed here stops being
counted as missing; adding a line here is therefore a claim on the record, not a
way to make a number go up quietly.

---

## 1. Deliberate renames — ported, under a different name

Swift has ONE namespace. Where two C# namespaces both define `Account`, one of
them has to give, and a bare `Account` in a package this size is a name nobody
should be able to claim.

```renames
Banking.Account                      = BankAccount
Personal.Finance.Account             = FinanceAccount
Hosting.CloudFallback.ProviderIds    = CloudProviderIds
Vision.Cloud.GeneratorIds            = VisionGeneratorIds
```

`CloudProviderIds` and `VisionGeneratorIds` are prefixed for the same reason and
one more: they hold *different* ids for the *same* vendor (`openai` versus
`openai-images`), and two types both called `ProviderIds` in one namespace is how
an image request quietly resolves to a chat model.

---

## 2. Swift constructs that have no type name

### 2a. Enum cases where C# has nested records

C# models a closed set as an abstract record with sealed subtypes. Swift models
the same set as an enum with associated values. The subtypes are *cases*, which
have no type name for a measure to find — but the set is complete and the
exhaustiveness is stronger, because a Swift `switch` will not compile if a case
is missed.

```excluded
Cast.Url                             enum case CastMediaSource.url
Cast.File                            enum case CastMediaSource.file
Cast.Bytes                           enum case CastMediaSource.bytes
Media.RawImageSource                 enum case ImageSource.raw
Media.EncodedImageSource             enum case ImageSource.encoded
Realtime.SpeechStartedEvent          enum case RealtimeEvent.speechStarted
Realtime.SpeechEndedEvent            enum case RealtimeEvent.speechEnded
Realtime.TranscriptDeltaEvent        enum case RealtimeEvent.transcriptDelta
Realtime.TranscriptFinalEvent        enum case RealtimeEvent.transcriptFinal
Realtime.ToolCallEvent               enum case RealtimeEvent.toolCall
Realtime.TurnCompleteEvent           enum case RealtimeEvent.turnComplete
Realtime.SessionErrorEvent           enum case RealtimeEvent.sessionError
Workflows.TaskUpdatedEvent           enum case RealtimePacaEvent.taskUpdated
Workflows.QueryInvalidationEvent     enum case RealtimePacaEvent.queryInvalidation
Workflows.DocCursorMoveEvent         enum case RealtimePacaEvent.docCursorMove
Workflows.AgentActivityEvent         enum case RealtimePacaEvent.agentActivity
Workflows.ConversationStepEvent      enum case RealtimePacaEvent.conversationStep
```

### 2b. The telephony speech-lifecycle events

C# dispatches these by walking the CLR class hierarchy with `DynamicInvoke`.
Swift has no equivalent and should not grow one, so the union is a single
`SpeechLifecycleEvent` struct carrying a `SpeechEventSelector`. Subscribing to
"all events" and subscribing to one kind are both still expressible; what is
gone is reflective dispatch, which is a good thing to lose on a phone.

```excluded
Telephony.CallerSpeechStartedEvent   SpeechLifecycleEvent + selector
Telephony.CallerSpeechEndedEvent     SpeechLifecycleEvent + selector
Telephony.TranscriptInterimEvent     SpeechLifecycleEvent + selector
Telephony.TranscriptFinalEvent_v2    SpeechLifecycleEvent + selector
Telephony.AgentThinkingEvent         SpeechLifecycleEvent + selector
Telephony.AgentSpeakingStartedEvent  SpeechLifecycleEvent + selector
Telephony.AgentSpeakingFinishedEvent SpeechLifecycleEvent + selector
Telephony.SpeechErrorEvent           SpeechLifecycleEvent + selector
```

### 2c. Static classes holding extension methods

A C# static class exists only to hang extension methods on. A Swift extension
has no type name at all — the methods land directly on the type they extend,
which is what the C# was emulating in the first place.

```excluded
Memory.AffectStateVadExtensions      extension on AffectState
Hosting.ToolCatalogExtensions        extension on the tool catalogue
Companion.CompanionRecallExtensions  extension on the companion recall seam
```

### 2d. Attributes

Swift has no attributes. `CircleAIVerificationStatusAttribute` is the
`CircleAIVerificationStatus` protocol: a type states how far it has been proven
as a static property. Same information, and it is still discoverable — a caller
asks the type rather than its metadata.

```excluded
Core.CircleAIVerificationStatusAttribute  protocol CircleAIVerificationStatus
```

---

## 3. The DI container

Every `ServiceCollectionExtensions` is registration code for
`Microsoft.Extensions.DependencyInjection`. There is no such container in the
Swift package and adding one would be a large opinion imposed on every host.
Swift construction is explicit: a caller builds what it needs and passes it in.

The *ids* those files define are a different matter and ARE ported — see
`CloudProviderIds` and `VisionGeneratorIds` above — because a typo in a
registration key is a provider that is configured, present, and never selected.

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

---

## 4. Native dependencies

These need a library linked in. The Swift package has no dependencies and runs
on hosts where those libraries are not present, so the *decisions* around them
cross and the binding does not. Where a decision was worth keeping, it was
extracted first: `WakeWordFactory` chooses the engine and the confirmer tier
without onnxruntime; `ConfirmedKeywordSpotter` holds the two-stage policy behind
`IKeywordSpotter`; `KaldiFbank` is the whole DSP front end in pure Swift.

### 4a. onnxruntime, whisper.cpp, espeak-ng, Open JTalk

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
Voice.NativeEspeakPhonemizer         needs espeak-ng linked (GPL: out of process only)
Voice.OpenJTalkPhonemizer            needs Open JTalk and its 103 MB dictionary
Companion.OnnxSpeakerIdentityAdapter needs onnxruntime
Companion.OnnxSpeechEmotionSensor    needs onnxruntime
Inference.QwenTextGenerator          needs the MNN runtime
Inference.KimiVlGenerator            needs the MNN runtime
Inference.MnnNativeDiagnostics       needs the MNN runtime
Inference.MnnRuntimeConfig           needs the MNN runtime
Inference.LoRAAdapterManager         needs the MNN runtime
Inference.MmapWeightLoader           needs the MNN runtime
Inference.NativeLibraryResolver      resolves .so/.dll by platform ABI
Inference.NativeRuntimePrep          unpacks native runtimes per platform ABI
Inference.Server.MnnInferenceBridgeFactory  needs the MNN runtime
```

`NativeEspeakPhonemizer` has a second reason: espeak-ng is GPL. The subprocess
phonemizer IS ported (`EspeakPhonemizer`), because a pipe is a boundary the
licence respects and linking it would make this package GPL too.

### 4b. Android and MAUI platform heads

These ARE the platform head. A Swift package cannot implement an Android
`Service`, a MAUI `MauiAudioCapture` or an AIDL binder, and the seams they fill
(`IAudioCapture`, `IDeviceContext`, `PlatformMemory`) are all ported so a Swift
host can supply its own.

```excluded
Maui.AlwaysOnService                 MAUI platform head
Maui.CircleAlwaysOnAndroidService    Android foreground service
Maui.HealthBoardBridge               MAUI platform head
Maui.LocationBridge                  MAUI platform head
Maui.MauiAudioCapture                MAUI platform head
Maui.MauiCameraCapture               MAUI platform head
Maui.MauiDeviceContext               MAUI platform head
Maui.MauiInferenceService            MAUI platform head
Maui.MauiPushSender                  MAUI platform head
Device.AndroidDeviceMemory           Android platform head
Device.AndroidMemoryPressure         Android platform head
Device.DeviceMemoryProbe             Android platform head
Device.CircleNeuronService           Android bound service
Device.CircleNeuronBinder            Android AIDL binder
Device.CircleNeuronConnection        Android service connection
Device.ServiceState                  Android bound-service state
Device.IResidentListener             Android resident-listening seam
```

### 4c. PDFsharp

A PDF writer is a third-party managed library with no Swift equivalent, and
writing one is a different project. The engines behind these seams are host-
supplied; the seams themselves are ported.

```excluded
Charts.PdfSharpChartRenderer         needs PDFsharp
Documents.PdfSharpDocumentEngine     needs PDFsharp
Presentations.PdfSharpDeckEngine     needs PDFsharp
```

### 4d. ASP.NET Core

The inference server is minimal-API endpoint registration, authentication
handlers and a host builder. Swift has no ASP.NET, and a server for it would be
a different program rather than a port. The DTOs, options and the SSE writer are
data and ARE ported.

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
```

### 4e. Blazor

```excluded
Core.CircleAIComponentBase           Blazor ComponentBase
```

### 4f. ADO.NET

`Memory.Sql` is a `DbConnection`-based store for SQL Server and PostgreSQL. The
Swift package has no database driver and adding one would be a dependency; the
SQLite stores that ship on-device ARE ported.

```excluded
Memory.Sql.AdoAtomStore              needs ADO.NET
Memory.Sql.SqlDialect                needs ADO.NET
```

---

## 5. Still owed

Everything not listed above and not yet ported is real remaining work, and the
measure counts it. Run:

```bash
python3 swift/tools/parity_swift.py --full
```
