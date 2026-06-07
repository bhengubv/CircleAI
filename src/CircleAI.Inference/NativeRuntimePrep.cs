// NativeRuntimePrep.cs
//
// Glue between CircleAI.Runtime's NativeRuntimeFetcher (downloads MNN into a
// deeply nested cache dir) and NativeLibraryResolver (loads from
// runtimes/{rid}/native/). The fetcher and resolver used to disagree on path —
// mnnbridge.dll lived next to the assembly but MNN.dll lived in the cache, and
// Windows transitive-dep loading couldn't find MNN.dll from mnnbridge.dll's
// directory. This class makes them agree:
//
//   1. FlattenFetchedMnnCore  → copies the fetched MNN core library
//      (MNN.dll / libMNN.so / libMNN.dylib) into
//      {AppContext.BaseDirectory}/runtimes/{rid}/native/ next to
//      mnnbridge.dll. Best-effort + idempotent: skips when source==dest, when
//      dest is read-only, or when the file is already current.
//
//   2. PreloadMnnCore         → loads the (now-flattened) MNN core into the
//      process so Windows can satisfy mnnbridge.dll's transitive dependency
//      on MNN.dll from the already-loaded handle. Belt-and-suspenders for
//      hosts where step 1 cannot write to AppContext.BaseDirectory (Docker
//      squash, App Store sandbox).
//
//   3. AssertCanLoadMnnBridge → startup self-check. Calls
//      NativeLibrary.Load("mnnbridge") and on failure throws an
//      InvalidOperationException naming every path that was searched + the
//      expected layout. Fails fast with an actionable error instead of
//      letting the first P/Invoke explode with the cryptic 0x8007007E
//      "specified module could not be found".

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace CircleAI.Inference;

/// <summary>
/// Bridges NativeRuntimeFetcher's nested cache layout to the flat
/// runtimes/{rid}/native/ layout NativeLibraryResolver expects, and runs a
/// load self-check so missing-DLL failures surface with an actionable error
/// instead of <c>0x8007007E</c>.
/// </summary>
public static class NativeRuntimePrep
{
    /// <summary>
    /// Result of <see cref="PrepareForLoad"/>: where everything ended up.
    /// Surfaced through <c>/v1/diagnostics</c> so DLL-not-found failures are
    /// debuggable from the wire without log diving.
    /// </summary>
    public sealed record NativeRuntimePaths(
        string Rid,
        string ExpectedNativeDir,
        string MnnBridgePath,
        bool   MnnBridgeLoaded,
        string MnnCoreFetchedPath,
        string MnnCoreFlattenedPath,
        bool   MnnCorePreloaded,
        string? FlattenError,
        string? PreloadError);

    /// <summary>
    /// One-shot prep: flatten the fetched MNN core into the resolver-visible
    /// dir, preload it, then self-check mnnbridge. Throws on self-check
    /// failure with an actionable error.
    /// </summary>
    /// <param name="mnnCoreFetchedPath">Absolute path to the fetched MNN core
    /// (Windows: MNN.dll, Linux: libMNN.so, macOS: libMNN.dylib or
    /// MNN.framework binary). Typically <c>NativeRuntimeInstall.MnnCorePath</c>
    /// from <c>CircleAI.Runtime</c>.</param>
    /// <param name="extractedRoot">Cache directory the runtime was extracted
    /// into. Used only for diagnostics.</param>
    /// <param name="log">Optional logger.</param>
    public static NativeRuntimePaths PrepareForLoad(
        string  mnnCoreFetchedPath,
        string  extractedRoot,
        ILogger? log = null)
    {
        if (string.IsNullOrWhiteSpace(mnnCoreFetchedPath))
            throw new ArgumentException("Fetched MNN core path is empty.", nameof(mnnCoreFetchedPath));
        if (string.IsNullOrWhiteSpace(extractedRoot))
            throw new ArgumentException("Runtime extract root is empty.", nameof(extractedRoot));

        var rid = RuntimeInformation.RuntimeIdentifier ?? "unknown-rid";
        var expectedNativeDir = Path.Combine(
            AppContext.BaseDirectory, "runtimes", rid, "native");
        var mnnCoreFileName = Path.GetFileName(mnnCoreFetchedPath);
        var mnnBridgeFileName = MnnBridgeFileNameForCurrentPlatform();
        var mnnCoreFlattened = Path.Combine(expectedNativeDir, mnnCoreFileName);
        var mnnBridgePath    = Path.Combine(expectedNativeDir, mnnBridgeFileName);

        // 1. Flatten — copy fetched MNN core next to mnnbridge.dll.
        string? flattenError = null;
        try
        {
            FlattenFetchedMnnCore(mnnCoreFetchedPath, expectedNativeDir);
        }
        catch (Exception ex)
        {
            flattenError = ex.Message;
            log?.LogWarning(ex,
                "NativeRuntimePrep: failed to flatten MNN core into '{Dest}'. " +
                "Falling back to preload-by-absolute-path.", expectedNativeDir);
        }

        // 2. Preload — load MNN core into the process so the OS can satisfy
        //    mnnbridge's transitive dep from the in-memory handle.
        //    Prefer the flattened copy; fall back to the fetched cache path.
        string? preloadError = null;
        bool preloaded = false;
        var preloadSource = File.Exists(mnnCoreFlattened)
            ? mnnCoreFlattened
            : mnnCoreFetchedPath;
        try
        {
            if (File.Exists(preloadSource))
            {
                NativeLibrary.Load(preloadSource);
                preloaded = true;
                log?.LogInformation(
                    "NativeRuntimePrep: preloaded MNN core from '{Path}'.", preloadSource);
            }
            else
            {
                preloadError = $"MNN core not found at '{preloadSource}' for preload.";
            }
        }
        catch (Exception ex)
        {
            preloadError = ex.Message;
            log?.LogWarning(ex,
                "NativeRuntimePrep: NativeLibrary.Load failed for MNN core at '{Path}'.",
                preloadSource);
        }

        // 3. Register resolver so mnnbridge lookups walk our search paths.
        var nestedDir = Path.GetDirectoryName(mnnCoreFetchedPath);
        if (!string.IsNullOrWhiteSpace(nestedDir))
            NativeLibraryResolver.OverrideDirectory = nestedDir;
        NativeLibraryResolver.EnsureRegistered();

        // 4. Self-check — load mnnbridge. Prefer an absolute-path load
        //    when the DLL is at the expected location (this lets Windows
        //    resolve mnnbridge's transitive MNN.dll dep from the same
        //    directory). Fall back to a resolver-driven load by name so
        //    hosts with a non-standard layout still get a chance.
        bool bridgeLoaded;
        Exception? lastLoadError = null;
        try
        {
            if (File.Exists(mnnBridgePath))
            {
                var handle = NativeLibrary.Load(mnnBridgePath);
                bridgeLoaded = handle != nint.Zero;
            }
            else
            {
                // No mnnbridge at the expected path — let the registered
                // resolver search assembly-relative paths.
                var handle = NativeLibrary.Load(
                    "mnnbridge",
                    typeof(NativeRuntimePrep).Assembly,
                    searchPath: null);
                bridgeLoaded = handle != nint.Zero;
            }
        }
        catch (Exception ex)
        {
            lastLoadError = ex;
            bridgeLoaded = false;
        }

        if (!bridgeLoaded)
        {
            var ex = lastLoadError
                ?? new DllNotFoundException(
                    "NativeLibrary.Load(\"mnnbridge\") returned a null handle without an explicit exception.");
            throw new InvalidOperationException(BuildLoadFailureMessage(
                ex, rid, expectedNativeDir, mnnBridgePath,
                mnnCoreFetchedPath, extractedRoot, flattenError, preloadError), ex);
        }

        log?.LogInformation(
            "NativeRuntimePrep ready: rid={Rid} bridge={Bridge} coreFlat={CoreFlat} corePreloaded={Preloaded}.",
            rid, mnnBridgePath, mnnCoreFlattened, preloaded);

        return new NativeRuntimePaths(
            Rid:                  rid,
            ExpectedNativeDir:    expectedNativeDir,
            MnnBridgePath:        mnnBridgePath,
            MnnBridgeLoaded:      bridgeLoaded,
            MnnCoreFetchedPath:   mnnCoreFetchedPath,
            MnnCoreFlattenedPath: mnnCoreFlattened,
            MnnCorePreloaded:     preloaded,
            FlattenError:         flattenError,
            PreloadError:         preloadError);
    }

    /// <summary>
    /// Copies <paramref name="fetchedMnnCorePath"/> to
    /// <paramref name="destDir"/>/<c>{filename}</c>. Idempotent — if the
    /// destination is the same file (by full path) or already matches in
    /// length + last-write-time, no copy happens. Best-effort: throws on
    /// real I/O failure so the caller can decide whether to fall back.
    /// </summary>
    public static void FlattenFetchedMnnCore(string fetchedMnnCorePath, string destDir)
    {
        if (string.IsNullOrWhiteSpace(fetchedMnnCorePath))
            throw new ArgumentException("Fetched MNN core path is empty.", nameof(fetchedMnnCorePath));
        if (!File.Exists(fetchedMnnCorePath))
            throw new FileNotFoundException(
                $"Fetched MNN core does not exist at '{fetchedMnnCorePath}'.", fetchedMnnCorePath);

        Directory.CreateDirectory(destDir);
        var destFile = Path.Combine(destDir, Path.GetFileName(fetchedMnnCorePath));

        // No-op when source == dest (rare: someone configured the fetcher to point at
        // the runtimes/{rid}/native/ dir directly).
        if (string.Equals(
                Path.GetFullPath(fetchedMnnCorePath),
                Path.GetFullPath(destFile),
                StringComparison.OrdinalIgnoreCase))
            return;

        // Skip if dest already matches source on length AND last-write-time —
        // covers the "second server launch reuses the same fetched runtime" case
        // without an extra copy.
        if (File.Exists(destFile))
        {
            var srcInfo = new FileInfo(fetchedMnnCorePath);
            var dstInfo = new FileInfo(destFile);
            if (srcInfo.Length == dstInfo.Length &&
                srcInfo.LastWriteTimeUtc == dstInfo.LastWriteTimeUtc)
                return;
        }

        // Copy via temp + move so a crash mid-copy doesn't leave a half-written DLL.
        var tempFile = destFile + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            File.Copy(fetchedMnnCorePath, tempFile, overwrite: true);
            File.Move(tempFile, destFile, overwrite: true);
        }
        catch
        {
            if (File.Exists(tempFile)) try { File.Delete(tempFile); } catch { /* best-effort */ }
            throw;
        }
    }

    /// <summary>
    /// Per-platform filename for the CircleAI shim.
    /// </summary>
    public static string MnnBridgeFileNameForCurrentPlatform()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "mnnbridge.dll";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return "libmnnbridge.dylib";
        return "libmnnbridge.so";
    }

    private static string BuildLoadFailureMessage(
        Exception innerEx,
        string    rid,
        string    expectedNativeDir,
        string    expectedMnnBridgePath,
        string    mnnCoreFetchedPath,
        string    extractedRoot,
        string?   flattenError,
        string?   preloadError)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("CircleAI MNN native runtime failed to load.");
        sb.AppendLine();
        sb.Append("  RID                : ").AppendLine(rid);
        sb.Append("  Expected mnnbridge : ").Append(expectedMnnBridgePath)
          .AppendLine(File.Exists(expectedMnnBridgePath) ? "  (exists)" : "  (MISSING)");
        var expectedCorePath = Path.Combine(expectedNativeDir, Path.GetFileName(mnnCoreFetchedPath));
        sb.Append("  Expected MNN core  : ").Append(expectedCorePath)
          .AppendLine(File.Exists(expectedCorePath) ? "  (exists)" : "  (MISSING)");
        sb.Append("  Fetched MNN core   : ").Append(mnnCoreFetchedPath)
          .AppendLine(File.Exists(mnnCoreFetchedPath) ? "  (exists)" : "  (MISSING)");
        sb.Append("  Fetched cache root : ").AppendLine(extractedRoot);
        if (flattenError is not null)
            sb.Append("  Flatten error      : ").AppendLine(flattenError);
        if (preloadError is not null)
            sb.Append("  Preload error      : ").AppendLine(preloadError);
        sb.AppendLine();
        sb.AppendLine("Fix:");
        sb.AppendLine($"  • Ensure CircleAI.Inference 1.3.1+ is referenced (ships {expectedMnnBridgePath}).");
        sb.AppendLine($"  • On Windows, install Visual C++ 2015-2022 Redistributable (x64) for the MD-CRT MNN.dll.");
        sb.AppendLine($"  • Or copy the fetched MNN core into '{expectedNativeDir}' manually.");
        sb.AppendLine();
        sb.Append("Inner: ").Append(innerEx.GetType().Name).Append(" — ").Append(innerEx.Message);
        return sb.ToString();
    }
}
