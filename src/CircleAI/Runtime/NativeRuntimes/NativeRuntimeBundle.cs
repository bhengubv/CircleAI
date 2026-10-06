// NativeRuntimeBundle.cs
//
// Describes a single fetchable Alibaba MNN runtime archive: where to fetch
// it, how to verify it, and which file name to recursively locate inside
// the extracted tree.
//
// 1.2.0 reality check: Alibaba ships ONE archive per (OS, arch) carrying
// multiple backend libraries inside. The MNN binary is deeply nested
// (e.g. lib/x64/Release/Dynamic/MD/MNN.dll on Windows, or
// Dynamic/MNN.framework/Versions/A/MNN on macOS). The fetcher RECURSIVELY
// finds the binary by name — callers can't rely on a flat layout.
//
// "mnnbridge" is the CircleAI shim, NOT shipped by Alibaba. It lives
// in the CircleAI NuGet package's runtimes/ folder and is resolved by
// CircleAI.Inference.NativeLibraryResolver via its assembly-relative
// fallback paths. The bundle and install records below deliberately
// expose only MNN — bridge resolution is a separate concern.

using CircleAI.Runtime.Backends;
using CircleAI.Runtime.Capabilities;

namespace CircleAI.Runtime.NativeRuntimes;

/// <summary>
/// A single fetchable MNN runtime bundle for one (OS, arch, backend) tuple.
/// </summary>
/// <param name="MnnVersion">MNN release version (e.g. <c>"3.5.0"</c>).</param>
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
/// <param name="MnnCoreLibraryName">
/// File name of the MNN core library to locate inside the extracted tree
/// (e.g. <c>"MNN.dll"</c>, <c>"libMNN.so"</c>, <c>"libMNN.dylib"</c>, or
/// the framework binary name <c>"MNN"</c> on macOS). The fetcher searches
/// recursively from the extract root and applies per-platform preferences
/// (Windows: prefer Dynamic/MD over Dynamic/MT over Static; macOS: prefer
/// the framework binary over a flat dylib).
/// </param>
public sealed record NativeRuntimeBundle(
    string MnnVersion,
    OperatingSystemKind Os,
    ArchitectureKind Arch,
    BackendKind Backend,
    Uri PrimaryUri,
    Uri? FallbackUri,
    string? ArchiveSha256Hex,
    string MnnCoreLibraryName);

/// <summary>
/// Result of a successful <see cref="INativeRuntimeFetcher.EnsureRuntimeAsync"/> call —
/// describes where the runtime now lives on disk and where MNN was found.
/// </summary>
/// <param name="Bundle">The bundle that was fetched (or matched in cache).</param>
/// <param name="ExtractedRoot">Absolute directory the archive was extracted into.</param>
/// <param name="MnnCorePath">
/// Absolute path to the MNN core library at its real nested location.
/// Callers that need to configure the P/Invoke search path should pass
/// <see cref="System.IO.Path.GetDirectoryName(string)"/> of this value to
/// <c>CircleAI.Inference.NativeLibraryResolver.OverrideDirectory</c>.
/// </param>
public sealed record NativeRuntimeInstall(
    NativeRuntimeBundle Bundle,
    string ExtractedRoot,
    string MnnCorePath);
