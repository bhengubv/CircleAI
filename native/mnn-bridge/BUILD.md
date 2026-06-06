# mnnbridge — build instructions

mnnbridge is CircleAI's C ABI shim around Alibaba MNN's C++ `MNN::Transformer::Llm`
class. It exports the flat `mnn_llm_*` functions that `CircleAI.Inference.MnnInterop`
P/Invokes into.

The Alibaba MNN bundles ship `MNN.dll` / `libMNN.so` / `libMNN.dylib` only —
they do NOT ship `mnnbridge`. You build mnnbridge yourself (from this
directory) and bundle it alongside `MNN` in the CircleAI.Inference NuGet
package's `runtimes/{RID}/native/` folder.

---

## Layout

```
native/mnn-bridge/
├── BUILD.md                 ← this file
├── CMakeLists.txt           ← portable build
├── build-windows.ps1        ← turnkey Windows x64 build
├── include/
│   └── mnnbridge.h          ← public C ABI
├── src/
│   └── mnnbridge.cpp        ← C++ implementation over MNN-LLM
└── test/
    └── smoke.cpp            ← optional smoke test (load + report)
```

---

## Windows x64 (the only platform built on the CI host today)

Turnkey path — handles download, extract, build, copy:

```powershell
cd C:\Dev\Solutions\com.bhengubv\CircleAI\native\mnn-bridge
.\build-windows.ps1
```

The script:

1. Downloads `mnn_3.5.0_windows_x64_cpu_opencl.zip` from
   `github.com/alibaba/MNN/releases/3.5.0` to `%TEMP%\mnnbridge-build-cache\`
   (skipped if already cached).
2. Verifies SHA-256 (`e37dbed6a5a6c26122239468d7fc8569d003c7f4a12c8a8024a33660fb13e4b7`).
3. Extracts the bundle.
4. Runs CMake configure + build with the **MultiThreadedDLL** CRT
   (matches .NET's runtime CRT linkage).
5. Copies `mnnbridge.dll` AND `MNN.dll` to
   `..\..\src\CircleAI.Inference\runtimes\win-x64\native\`.

Add `-WithSmokeTest` to also build the `mnnbridge_smoke.exe` binary that
loads a config.json and reports version / vocab / context — useful for
verifying the build before packaging.

Prerequisites:
- Visual Studio 2022 with the C++ Desktop workload, OR Visual Studio Build
  Tools 2022.
- CMake 3.16+ on PATH.

---

## Linux x64

```bash
cd native/mnn-bridge

# Download + extract MNN 3.5.0 Linux bundle
mkdir -p /tmp/mnnbridge-build-cache && cd /tmp/mnnbridge-build-cache
curl -sLO https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_linux_x64_cpu_opencl.zip
unzip -q mnn_3.5.0_linux_x64_cpu_opencl.zip

# Build
cd /path/to/CircleAI/native/mnn-bridge
cmake -S . -B build \
  -DMNN_ROOT=/tmp/mnnbridge-build-cache/mnn_3.5.0_linux_x64_cpu_opencl \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build -j

# Copy to NuGet payload
mkdir -p ../../src/CircleAI.Inference/runtimes/linux-x64/native
cp build/libmnnbridge.so          ../../src/CircleAI.Inference/runtimes/linux-x64/native/
cp /tmp/mnnbridge-build-cache/mnn_3.5.0_linux_x64_cpu_opencl/lib/x64/libMNN.so \
    ../../src/CircleAI.Inference/runtimes/linux-x64/native/
```

---

## macOS arm64 (Apple Silicon)

```bash
cd native/mnn-bridge

# Download + extract MNN 3.5.0 macOS bundle (universal x64+arm82)
mkdir -p /tmp/mnnbridge-build-cache && cd /tmp/mnnbridge-build-cache
curl -sLO https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip
unzip -q mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal.zip

# Build for arm64
cd /path/to/CircleAI/native/mnn-bridge
cmake -S . -B build \
  -DMNN_ROOT=/tmp/mnnbridge-build-cache/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build -j

# Copy to NuGet payload
mkdir -p ../../src/CircleAI.Inference/runtimes/osx-arm64/native
cp build/libmnnbridge.dylib       ../../src/CircleAI.Inference/runtimes/osx-arm64/native/
# MNN ships as a framework on macOS — copy the framework binary directly.
cp /tmp/mnnbridge-build-cache/mnn_3.5.0_macos_x64_arm82_cpu_opencl_metal/Dynamic/MNN.framework/Versions/A/MNN \
    ../../src/CircleAI.Inference/runtimes/osx-arm64/native/libMNN.dylib

# Sign + notarise for distribution (required for end-user installs):
codesign --sign "Developer ID Application: <your team>" --options runtime \
    ../../src/CircleAI.Inference/runtimes/osx-arm64/native/libmnnbridge.dylib
```

For Intel Mac, change `-DCMAKE_OSX_ARCHITECTURES=x86_64` and
`runtimes/osx-x64/native/`.

---

## Android (per-ABI)

```bash
# Set up Android NDK
export ANDROID_NDK=/path/to/android-ndk-r26b
export PATH=$ANDROID_NDK/toolchains/llvm/prebuilt/linux-x86_64/bin:$PATH

# Download MNN Android bundle (carries all ABIs)
mkdir -p /tmp/mnnbridge-build-cache && cd /tmp/mnnbridge-build-cache
curl -sLO https://github.com/alibaba/MNN/releases/download/3.5.0/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip
unzip -q mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan.zip

# Build per ABI
for abi in arm64-v8a armeabi-v7a; do
    cd /path/to/CircleAI/native/mnn-bridge
    cmake -S . -B build-$abi \
      -DCMAKE_TOOLCHAIN_FILE=$ANDROID_NDK/build/cmake/android.toolchain.cmake \
      -DANDROID_ABI=$abi \
      -DANDROID_PLATFORM=android-26 \
      -DMNN_ROOT=/tmp/mnnbridge-build-cache/mnn_3.5.0_android_armv7_armv8_cpu_opencl_vulkan \
      -DCMAKE_BUILD_TYPE=Release
    cmake --build build-$abi -j
done

# Copy to NuGet payload (CircleAI.Maui consumes from these paths)
mkdir -p ../../src/CircleAI.Inference/runtimes/android-arm64/native
cp build-arm64-v8a/libmnnbridge.so ../../src/CircleAI.Inference/runtimes/android-arm64/native/
```

---

## iOS (static link required)

iOS doesn't permit dlopen'd dynamic libraries from the app sandbox, so
mnnbridge ships as a static `.a` and is link-time integrated by the
consuming MAUI app via `<NativeReference>` (handled by
`CircleAI.Inference.targets`).

```bash
# Build static lib for ios-arm64
cd /path/to/CircleAI/native/mnn-bridge
cmake -S . -B build-ios \
  -DCMAKE_SYSTEM_NAME=iOS \
  -DCMAKE_OSX_ARCHITECTURES=arm64 \
  -DCMAKE_OSX_DEPLOYMENT_TARGET=15.0 \
  -DBUILD_SHARED_LIBS=OFF \
  -DMNN_ROOT=/tmp/mnnbridge-build-cache/mnn_3.5.0_ios_armv82_cpu_metal_coreml \
  -DCMAKE_BUILD_TYPE=Release
cmake --build build-ios -j

mkdir -p ../../src/CircleAI.Inference/runtimes/ios-arm64/native
cp build-ios/libmnnbridge.a ../../src/CircleAI.Inference/runtimes/ios-arm64/native/
```

You'll also need the MNN static library (`libMNN.a`) from the iOS
bundle — copy it next to `libmnnbridge.a`.

---

## Verifying the build

Quick load-only check:

```powershell
# Windows
.\build-windows.ps1 -WithSmokeTest
build\Release\mnnbridge_smoke.exe   # prints version, exits 0
```

```bash
# Linux/macOS
cmake -S . -B build -DMNN_ROOT=... -DMNNBRIDGE_BUILD_TEST=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build
./build/mnnbridge_smoke
```

With a real MNN-LLM model on disk (e.g. `MNN/Qwen3-0.6B-MNN` downloaded
to `./qwen3-0.6b-mnn/`):

```bash
./build/mnnbridge_smoke ./qwen3-0.6b-mnn/config.json "Hello, who are you?"
```

You should see version → "Loaded." → context_size / vocab_size → a stream
of token IDs.

---

## How this integrates with the NuGet package

`CircleAI.Inference.targets` looks for binaries at the standard
`runtimes/{RID}/native/` layout. When you copy `mnnbridge.{dll,so,dylib}`
into that path and rerun `dotnet pack`, the targets file packs them under
the same `runtimes/{RID}/native/` path inside the `.nupkg`.

The result on the consumer side:

- They install `CircleAI.Inference` from nuget.org.
- NuGet places `runtimes/win-x64/native/mnnbridge.dll` + `MNN.dll` next
  to the consuming app's binary at restore time.
- `MnnInterop`'s P/Invoke finds `mnnbridge.dll` via the standard search
  path, which loads `MNN.dll` from the same directory by name. Inference
  works without any first-launch network hop.

For consumers who want runtime-fetched MNN (smaller NuGet, fetch-on-demand),
the existing `NativeRuntimeFetcher` path still works — `mnnbridge.dll`
comes from the NuGet, `MNN.dll` comes from the fetcher's cache. The
`NativeLibraryResolver`'s search paths handle the lookup.

---

## Versioning

`mnn_bridge_version()` returns a string like `1.0.0-mnn3.5.0`. The first
part is the bridge's own SemVer; the second tracks the MNN release this
build links against. When Alibaba ships MNN 3.6.0, update the version
string + `MnnSha256` in `build-windows.ps1` + the equivalent constants
in `embedded_native_registry.json` and re-run the per-platform builds.
