# C parity exclusions

The C port is measured by matching C# public type names against identifiers the
headers declare — types AND functions, because a C# class of static helpers
legitimately becomes a set of free functions here.

**The rule this file exists to serve:** a port is done when every C# public type
is either ported or written down here with a reason. No self-chosen percentage
thresholds.

`c/tools/parity_c.py` reads the machine-readable blocks below, so this document
IS the measure's configuration. A type listed here stops being counted as
missing; adding a line is therefore a claim on the record, not a way to make a
number go up quietly.

A `Module.*` line excludes a whole module, which is what a platform head or an
ASP.NET project actually is.

---

## What C does differently, and why it is not an omission

C has no classes, no exceptions, no generics and no garbage collector. Four
consequences show up throughout:

- **A static class becomes free functions.** `ca_commerce_board_add_line` covers
  what a C# `CommerceBoard` method did. The measure matches on word sets for
  exactly this reason.
- **An interface becomes a struct of function pointers**, and the `I` prefix goes
  — there is one implementation and it is named for the thing, not for how it
  stores it. So `IClientBook` and `InMemoryClientBook` are both `ca_client_book`.
- **An exception becomes an error code.** Every `...Exception` is a value in an
  enum, so the word "Exception" never appears in a symbol.
- **A `...EventArgs` becomes the argument list of a callback.** There is no type
  to find, because the fields are the parameters.

---

## Platform heads and managed runtimes

C cannot be the platform head for a .NET or Android surface, and it cannot host
a managed web framework. These are whole modules rather than scattered types.

```excluded
Maui.*                               .NET MAUI bindings
Device.*                             Android Binder/AIDL and framework callbacks
Inference.Server.*                   ASP.NET Core minimal APIs and host builder
Hosting.Mcp.*                        ASP.NET Core minimal APIs
Web.*                                Blazor / ASP.NET surface
Desktop.*                            binds a Windows desktop shell
WindowsAutomation.*                  UI Automation is a Windows COM API
Memory.Sql.*                         ADO.NET; the on-device SQLite path is ported
```

## Native runtimes

Everything needing onnxruntime, whisper.cpp, espeak-ng, Open JTalk, MNN or the
turbovec Rust crate linked in. Where a DECISION was worth keeping it is ported
and only the binding stays behind — the wake-word engine choice, the confirmer
tier and the Kaldi filterbank are all pure C.

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
Runtime.NativeRuntimeFetcher         fetches and extracts native runtime archives
Embeddings.Local.TurboVecEmbeddingIndex  P/Invoke over the turbovec Rust crate
Charts.PdfSharpChartRenderer         needs PDFsharp
Documents.PdfSharpDocumentEngine     needs PDFsharp
Presentations.PdfSharpDeckEngine     needs PDFsharp
```

## The DI container

Registration code for `Microsoft.Extensions.DependencyInjection`. C construction
is explicit: a caller builds what it needs and passes it in. The IDS those files
define ARE ported, because a typo in a registration key is a provider that is
configured, present and never selected.

```excluded
AetherNet.ServiceCollectionExtensions                        DI registration
CodeAgent.ServiceCollectionExtensions                        DI registration
Hosting.ServiceCollectionExtensions                          DI registration
Hosting.NeuronServiceCollectionExtensions                    DI registration
Hosting.CloudFallback.CloudFallbackServiceCollectionExtensions  DI registration
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
Companion.ServiceCollectionExtensions                        DI registration
```

## Managed-language constructs

```excluded
Core.CircleAIComponentBase           Blazor ComponentBase
Core.CircleAIVerificationStatusAttribute  C has no attributes or reflection
Memory.AffectStateVadExtensions      C# extension-method holder
Hosting.ToolCatalogExtensions        C# extension-method holder
Companion.CompanionRecallExtensions  C# extension-method holder
Plugins.PluginLoader                 loads .NET assemblies by reflection
Plugins.PluginLoadResult             the assembly loader's result shape
```

---

## Still owed

Everything not listed above and not yet ported is real remaining work, and the
measure counts it. Run:

```bash
python3 c/tools/parity_c.py --full
```
