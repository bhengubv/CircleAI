# Circle AI — Native Runtime Setup

Circle AI runs **Alibaba MNN** as its inference engine on every platform.
Two binaries are required at runtime:

| File | Purpose |
|---|---|
| `mnnbridge` (`.dll` / `.so` / `.dylib` / `.a`) | Circle AI's C ABI shim around MNN-LLM's C++ API |
| `MNN` core (`MNN.dll` / `libMNN.so` / `libMNN.dylib` / `libMNN.a`) | Alibaba's MNN runtime |

> **You do not normally need to do anything from this page.**
> `CircleAI.Inference` 3.0.1 ships the prebuilt natives for **8 RIDs**:
> `win-x64`, `linux-x64`, `linux-arm64`, `osx-arm64`, `osx-x64`,
> `android-arm`, `android-arm64`, `ios-arm64` under
> `runtimes/{rid}/native/` inside the NuGet package. `dotnet build`
> lays them next to your binary automatically and `NativeRuntimePrep`
> (in `CircleAI.Inference`) preloads them on first P/Invoke. If load
> works, this page is just history.
>
> Separately, `CircleAI.Embeddings.Local` 3.0.1 ships the **TurboVec**
> SIMD vector backend's natives for **7 RIDs**: `win-x64`, `linux-x64`,
> `osx-arm64`, `osx-x64`, `android-arm64`, `android-x64`, `ios-arm64`.
> Same auto-resolution path — same diagnostics surface — but the
> source tree is `native/turbovec/` rather than `native/mnn-bridge/`.
>
> Read on only if you need to:
>
> - Build the natives yourself (new RID, security-audited build, custom
>   MNN backend),
> - Diagnose a load failure (`/v1/diagnostics` reports the resolved
>   paths — start there),
> - Or set up a non-standard layout (e.g. air-gapped server, custom MAUI
>   native-lib path).

---

## Resolution path (what `NativeRuntimePrep` actually does)

On first inference, in this order:

1. Look in `AppContext.BaseDirectory/runtimes/{rid}/native/` (the
   standard NuGet layout — populated by `dotnet build` or
   `dotnet publish`).
2. If absent, look in `NativeRuntimeFetcher`'s cache at
   `%LOCALAPPDATA%/CircleAI/runtime/` (a one-time download from
   ModelScope-mirrored or GitHub-mirrored prebuilt archives).
3. If absent, look at `AIOptions.NativeLibDir` if the host wired one
   (e.g. `Android.App.Application.Context.ApplicationInfo.NativeLibraryDir`
   on Android).
4. If still absent, throw an actionable error naming every path tried.

`/v1/diagnostics` (when running `CircleAI.Inference.Server`) emits the
resolved `mnnbridge_path` / `mnn_core_*` block. That's the fastest way
to know which path won.

---

## Prebuilt native layout (per RID)

When `CircleAI.Inference` is installed via NuGet, this is what lands
under your build output:

```
runtimes/
├── android-arm/native/      libmnnbridge.so + libMNN.so + libllm.so + libMNN_Express.so + libc++_shared.so + …
├── android-arm64/native/    libmnnbridge.so + libMNN.so + libllm.so + libMNN_Express.so + libc++_shared.so + …
├── ios-arm64/native/        libmnnbridge.a  + libMNN.a   (static — linked at app build time via NativeReference)
├── linux-arm64/native/      libmnnbridge.so + libMNN.so + libllm.so + libMNN_Express.so
├── linux-x64/native/        libmnnbridge.so + libMNN.so
├── osx-arm64/native/        libmnnbridge.dylib + libMNN.dylib   (codesigned: Apple Distribution: The Other Bhengu (Pty) Ltd)
├── osx-x64/native/          libmnnbridge.dylib + libMNN.dylib   (codesigned)
└── win-x64/native/          mnnbridge.dll      + MNN.dll
```

Sizes (approx, stripped): mnnbridge ≈ 40–60 KB; MNN core ≈ 3–15 MB
depending on arch + backends enabled.

---

## Building the natives yourself

Sources: [`native/mnn-bridge/`](native/mnn-bridge/) — CMake project,
single-file `src/mnnbridge.cpp`. See
[`native/mnn-bridge/BUILD.md`](native/mnn-bridge/BUILD.md) for the
turnkey scripts per platform.

| RID | MNN bundle to fetch | Toolchain |
|---|---|---|
| `win-x64` | `mnn_3.5.0_windows_x64_cpu_opencl.zip` | MSVC 2022 + CMake |
| `linux-x64` | `mnn_3.5.0_linux_x64_cpu_opencl.zip` | gcc 13+ + CMake (headers cloned from `alibaba/MNN` tag `3.5.0`) |
| `linux-arm64` | (no prebuilt) — build MNN 3.5.0 from source via `aarch64-linux-gnu-g++` cross toolchain | gcc cross |
| `osx-arm64` / `osx-x64` | `mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip` | Xcode + CMake (`-DCMAKE_OSX_ARCHITECTURES=arm64` or `x86_64`) |
| `android-arm64` / `android-arm` | `mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip` | Android NDK r26d+, `-DANDROID_ABI=arm64-v8a` / `armeabi-v7a` |
| `ios-arm64` | `mnn_3.5.0_ios_armv82_cpu_metal_coreml.zip` | Xcode + CMake (`-DCMAKE_SYSTEM_NAME=iOS -DBUILD_SHARED_LIBS=OFF`) |

Universal pattern:

```bash
cd native/mnn-bridge
cmake -S . -B build \
    -DMNN_ROOT=/path/to/extracted/mnn_bundle \
    -DCMAKE_BUILD_TYPE=Release
cmake --build build -j
```

Then drop the output (`build/libmnnbridge.{so,dylib}` /
`build/Release/mnnbridge.dll`) + the matching `libMNN.*` from the
bundle into `runtimes/{rid}/native/` and re-pack.

For Linux ARM64, MNN itself has to be cross-built — Alibaba doesn't ship
an aarch64-linux prebuilt. See `BUILD.md` for the cmake toolchain file.

---

## macOS codesigning

The shipped macOS dylibs are codesigned with
`Apple Distribution: The Other Bhengu (Pty) Ltd (78QHBHRR7Q)`.

That's enough for App Store / TestFlight distribution and for headless /
server / dev use. For direct end-user `.dmg` distribution the consuming
app must additionally notarise. The SDK doesn't do this — it's a
distribution-time concern, not a build-time one.

---

## Model bundles

Native runtime is one thing; model **weights** are another.

The first time `AIService.StartAsync()` runs, `ModelDownloadService`
fetches the selected model bundle from ModelScope (catalog discovery in
`IModelSelector.BestFit`), verifies per-file SHA-256, and caches under
`AIOptions.ModelStorageDir` (defaults to
`{AppContext.BaseDirectory}/models`).

Bundle size depends on the model the selector picked — see
[ARCHITECTURE.md](ARCHITECTURE.md) for how that decision is made.

---

## Diagnostics

If chat completion fails with `Unable to load DLL 'mnnbridge'`:

1. `POST /v1/diagnostics` — read the `native_runtime` block; it lists
   the expected mnnbridge path, the fetched MNN core path, and any
   flatten / preload error.
2. If `expected_mnnbridge: (MISSING)` — your build didn't propagate
   `runtimes/{rid}/native/`. Check the `CircleAI.Inference` NuGet
   reference is `3.0.1` or any release in the 1.3.1 → 3.0.1 line that
   carries the native runtime layout.
3. If `expected_mnnbridge: (exists)` but bridge_loaded is false — most
   likely a missing OS dep. On Windows, install the
   "Visual C++ 2015-2022 Redistributable (x64)." On Linux, check
   `ldd` against `libmnnbridge.so`.
