# turbovec-bridge

C ABI wrapper over the [`turbovec`](https://github.com/RyanCodrai/turbovec)
Rust crate. Consumed by `CircleAI.Embeddings.Local` via P/Invoke to back
the HNSW / quantised-search path.

## Layout

```
native/turbovec-bridge/
├── Cargo.toml                  # crate-type = ["cdylib"]
├── src/lib.rs                  # extern "C" wrappers
├── include/turbovecbridge.h    # authoritative C ABI
├── vendor/turbovec/            # vendored upstream core crate (MIT)
├── patches/                    # any local turbovec patches we apply
└── BUILD.md                    # this file
```

`vendor/turbovec/` is a vendored snapshot of
`github.com/bhengubv/turbovec` (which is itself a fork of
RyanCodrai/turbovec, MIT). Refresh by re-downloading the tarball, copying
the `turbovec/` subcrate, and re-running `cargo build`.

## Build (Windows / x64)

```powershell
cd native/turbovec-bridge
cargo build --release
copy target/release/turbovecbridge.dll ../../src/CircleAI.Embeddings.Local/runtimes/win-x64/native/
```

The resulting `.dll` is loaded by the .NET runtime via `LoadLibrary`
(see `TurboVecInterop.cs`).

## Cross-build

Same Mac/Linux build server pattern as `mnn-bridge` (L1–L4 / M1 task
history). The Rust toolchain on the build boxes already supports the
matrix:

| RID            | Toolchain target                |
|----------------|---------------------------------|
| win-x64        | x86_64-pc-windows-msvc          |
| linux-x64      | x86_64-unknown-linux-gnu        |
| linux-arm64    | aarch64-unknown-linux-gnu       |
| osx-x64        | x86_64-apple-darwin             |
| osx-arm64      | aarch64-apple-darwin            |
| android-arm64  | aarch64-linux-android           |
| android-x64    | x86_64-linux-android            |
| ios-arm64      | aarch64-apple-ios               |

Per RID:
```sh
cargo build --release --target=<triple>
# Copy target/<triple>/release/{turbovecbridge.dll,libturbovecbridge.so,libturbovecbridge.dylib}
# into ../../src/CircleAI.Embeddings.Local/runtimes/<RID>/native/
```

## ABI versioning

Every breaking change to `lib.rs` bumps `tvb_abi_version()` (currently
`1`). Managed callers can check via `TurboVecInterop.AbiVersion`. If a
host loads an older `.dll`, the .NET side throws on init rather than
crashing on a mismatched signature.

## License

The bridge crate is MIT; the vendored `turbovec` core is MIT. Both ship
under `LICENSE` files. No upstream MNN-style "non-commercial" surprises.
