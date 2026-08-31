# Swift parity: what is deliberately NOT ported

Every other C# module is ported or being ported. These are excluded on
purpose, and the reason is recorded here so nobody has to re-derive it -
or worse, fake a port to make a number go up.

| Module | Types | Why it is excluded |
|---|---|---|
| `CircleAI.Maui` | 9 | MAUI heads: `MauiAudioCapture`, `MauiCameraCapture`, `CircleAlwaysOnAndroidService`, the location and health bridges. These ARE the platform layer. Swift has its own (AVFoundation, CoreLocation, HealthKit), and a line-by-line port would be a MAUI shim that never runs. |
| `CircleAI.Device` | 8 | Android bindings: `AndroidDeviceMemory`, `AndroidMemoryPressure`, `CircleNeuronBinder`/`Connection`/`Service`. Java interop over an Android Service. The Swift equivalent is a different mechanism, not a translation. |
| `CircleAI.Memory.Sql` | 2 | `AdoAtomStore` and `SqlDialect` sit on ADO.NET. There is no ADO.NET for Swift; a Swift build would talk to Postgres through a different driver entirely. |
| `CircleAI.AetherNet` | 7 of 12 | Adapters over the external `AetherNet.Core`/`Transport`/`Security`/`Messaging` NuGet packages, v2.\*. Those packages do not exist for Swift, so the adapters have nothing to adapt. The parts that are OURS - `MeshCapabilityAdvertisement`, the registry, the broadcaster - are ported. |

## What "ported" means everywhere else

Where a module has real I/O, the deterministic half is ported IN FULL and
tested, and the I/O sits behind a protocol with an honest null
implementation. That is the same shape the C# uses, so this is a faithful
port rather than a reduced one:

- `Cast` - SSDP framing, device XML, SOAP, DIDL, clock formats all ported;
  sockets and HTTP behind protocols.
- `Hosting.CloudFallback` - request shaping, SSE framing, per-vendor delta
  extraction all ported; the HTTP call behind `ICloudChatTransport`.
- `Mesh` - peer selection and the wire envelopes ported; the transport pump
  is a host concern.
- `Media` - the whole raster pipeline, including real PNG and APNG bytes.
  Deflate is STORED blocks, because Swift has no cross-platform zlib; the
  output is valid, just larger. Stated, not hidden.
- `CodeAgent` - the loop, the parser and the path guard ported; the brain,
  the editor and the command runner are seams.
