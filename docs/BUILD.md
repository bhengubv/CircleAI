# CircleAI — Build guide

## 1. Toolchain

- .NET 9 SDK (latest patch). .NET 10 SDK works too — the host SDK is
  forward-compatible with net9.0 target framework projects.
- Git
- (Optional) Docker if you want to build the container image

Verify:

```bash
dotnet --version           # 9.x or 10.x
git --version
```

## 2. Restore + build

```bash
git clone https://github.com/bhengubv/CircleAI.git
cd CircleAI
dotnet restore CircleAI.sln
dotnet build CircleAI.sln -c Release
```

The solution is large (**132 csprojs** under `src/` — 42 on the 3.0.1
contract line, 6 mid-line foundation packages, 84 on the 1.2.0
companion + adapter line — plus ~30 test projects under `tests/` and 9
language-port directories under `python/`, `typescript/`, `go/`,
`kotlin/`, `swift/`, `rust/`, `c/`, `android/`, `harmonyos/`). Expect a
1–2 minute restore the first time, then ~30 seconds for incremental
builds.

## 3. Run the test suites

The full suite covers ~30 test projects across runtime, server,
hosting, security, personality, knowledge, federation, agents,
simulation, wearable / biosignals, memory, and each of the new 3.0
pillar contract surfaces (Vision, Speech, Spatial, Banking, …).

```bash
# Everything at once — recommended
dotnet test CircleAI.sln -c Release

# Or run one project at a time
dotnet test tests/CircleAI.Runtime.Tests             -c Release
dotnet test tests/CircleAI.Inference.Server.Tests    -c Release
dotnet test tests/CircleAI.Hosting.InferenceBridge.Tests -c Release
# …etc.

# Pass --verbosity normal for the per-project test counts.
```

If a test project fails to restore due to a sibling project's missing
TFM, run `dotnet restore` against that project alone first — the solution
mixes `net9.0` and `net10.0` targets.

## 4. Building the hosted server

```bash
dotnet publish src/CircleAI.Inference.Server/CircleAI.Inference.Server.csproj \
    -c Release \
    -o publish/server
```

Run:

```bash
cd publish/server
dotnet CircleAI.Inference.Server.dll
# -> Now listening on: http://localhost:5000
```

For a self-contained single-file binary (useful for the Windows-service
install path):

```bash
dotnet publish src/CircleAI.Inference.Server/CircleAI.Inference.Server.csproj \
    -c Release \
    -r win-x64 \
    --self-contained true \
    -p:PublishSingleFile=true \
    -o publish/win-x64
```

## 5. Building the Docker image

```bash
docker build -f src/CircleAI.Inference.Server/Dockerfile -t circleai/inference-server:latest .
```

The build is multi-stage: .NET 9 SDK image for build, ASP.NET 9 runtime
image for the artefact. The MNN native runtime is NOT bundled — the
server fetches it on first launch, so the image is the same for x86_64
and arm64 hosts.

## 6. Building one of the language ports

Each port has its own toolchain.

```bash
# Rust
cd rust && cargo build --release

# Go
cd go && go build ./...

# Python
cd python && pip install -e . && pytest

# TypeScript
cd typescript && npm ci && npm run build && npm test

# Kotlin (JVM)
cd kotlin && ./gradlew build

# Swift
cd swift && swift build && swift test

# C
cd c && cmake -S . -B build && cmake --build build

# Android (Kotlin)
cd android && ./gradlew assembleRelease

# HarmonyOS (ArkTS)
cd harmonyos && hvigorw build
```

Each `tests/` directory ships against the same fixture vectors under
`fixtures/` so cross-language `AffectState`, language registry, and
companion contract behaviour stay byte-identical.

## 7. Refreshing the native-runtime registry

When Alibaba ships a new MNN release:

1. Download the bundle once:
   ```bash
   curl -L -o mnn.zip "https://github.com/alibaba/MNN/releases/download/<version>/<file>"
   ```
2. Compute its SHA-256:
   ```bash
   shasum -a 256 mnn.zip            # macOS / Linux
   certutil -hashfile mnn.zip SHA256 # Windows
   ```
3. Add the entry to
   `src/CircleAI.Runtime/NativeRuntimes/embedded_native_registry.json`
   with the URL on `modelscope.cn` (primary) and the GitHub URL above
   as `fallback_url`.
4. Rebuild + run `tests/CircleAI.Runtime.Tests`.

The registry tolerates missing SHAs (it just trusts the served bytes
when none is pinned) — but DO pin every bundle that ships in a release.

## 8. CI

CI is currently disabled — see `~/.claude/projects/.../memory/no-ci-until-redesigned.md`.
All commits to master-bound branches must include `[skip ci]` in the
message. Tests are run locally before push; the helper scripts under
`AppInfo/Deployment/` handle the publish + transfer steps.

## 9. Common build errors

- **`NETSDK1004: Assets file not found`** — `dotnet restore` hasn't been
  run for that project. Run `dotnet restore <project.csproj>`.
- **`error NU1201: Project X is not compatible with net9.0`** — the
  project targets `net10.0` only; restore it standalone or upgrade your
  SDK. CircleAI.Aether is the most common case.
- **`CS0117 ModelTier`** — you're on a pre-1.2 cache. After the Phase 2
  rename the Runtime enum is `CapabilityTier`. Clean `obj/` and rebuild.
