// NativeRuntimeBundle.cs
//
// Describes a single pre-built MNN runtime archive: where to fetch it,
// where its contents land on disk after extraction, and the per-OS native
// library names callers should P/Invoke. Registry-of-record is
// NativeRuntimes/embedded_native_registry.json.

using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.NativeRuntimes;

/// <summary>
/// A single fetchable MNN runtime bundle for one (OS, arch, backend) tuple.
/// </summary>
/// <param name="MnnVersion">MNN release version (e.g. <c>"3.0.0"</c>).</param>
/// <param name="Os">Target OS family.</param>
/// <param name="Arch">Target CPU architecture.</param>
/// <param name="Backend">Execution backend the bundle implements.</param>
/// <param name="PrimaryUri">Primary download URI (ModelScope when available).</param>
/// <param name="FallbackUri">
/// Fallback URI (Alibaba GitHub release mirror). <c>null</c> means there is
/// no fallback registered.
/// </param>
/// <param name="ArchiveSha256Hex">
/// SHA-256 of the archive in hex. <c>null</c> when the bundle has not yet
/// been pinned — the fetcher will trust the served bytes. Bundles that
/// ship in production releases MUST set this.
/// </param>
/// <param name="MnnBridgeLibraryName">
/// File name of the mnnbridge wrapper library inside the extracted archive
/// (e.g. <c>"mnnbridge.dll"</c>, <c>"libmnnbridge.so"</c>).
/// </param>
/// <param name="MnnCoreLibraryName">
/// File name of the MNN core library inside the extracted archive
/// (e.g. <c>"MNN.dll"</c>, <c>"libMNN.so"</c>, <c>"libMNN.dylib"</c>).
/// </param>
public sealed record NativeRuntimeBundle(
    string MnnVersion,
    OperatingSystemKind Os,
    ArchitectureKind Arch,
    BackendKind Backend,
    Uri PrimaryUri,
    Uri? FallbackUri,
    string? ArchiveSha256Hex,
    string MnnBridgeLibraryName,
    string MnnCoreLibraryName);

/// <summary>
/// Result of a successful <see cref="INativeRuntimeFetcher.EnsureRuntimeAsync"/> call —
/// describes where the runtime now lives on disk and which libraries to load.
/// </summary>
/// <param name="Bundle">The bundle that was fetched (or matched in cache).</param>
/// <param name="ExtractedRoot">Absolute directory the archive was extracted into.</param>
/// <param name="MnnBridgePath">Absolute path to the mnnbridge shim.</param>
/// <param name="MnnCorePath">Absolute path to MNN core.</param>
public sealed record NativeRuntimeInstall(
    NativeRuntimeBundle Bundle,
    string ExtractedRoot,
    string MnnBridgePath,
    string MnnCorePath);
