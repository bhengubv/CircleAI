// NativeLibraryResolver.cs
//
// Registers a custom DllImportResolver that searches for MNN native binaries
// under a `runtimes/{RID}/native/` directory relative to the assembly location.
// This is the standard NuGet native library layout.
//
// Required libraries (ship all alongside CircleAI.Inference.dll):
//   Windows x64  : runtimes/win-x64/native/mnnbridge.dll  + MNN.dll
//   Linux x64    : runtimes/linux-x64/native/libmnnbridge.so  + libMNN.so
//   macOS arm64  : runtimes/osx-arm64/native/libmnnbridge.dylib + libMNN.dylib
//   Android arm64: runtimes/android-arm64/native/libmnnbridge.so + libMNN.so [+ libMNN_CL.so GPU]
//   iOS arm64    : statically linked into app bundle
//
// Build instructions: CircleAI/native/mnn-bridge/BUILD.md
// MNN releases (pre-built): https://github.com/alibaba/MNN/releases

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace CircleAI.Inference;

public static class NativeLibraryResolver
{
    private static bool _registered;
    private static readonly object _lock = new();

    /// <summary>
    /// Optional override directory injected by the host (e.g. Android
    /// <c>nativeLibraryDir</c>, or a custom dev-machine path).
    /// Set this before calling <see cref="EnsureRegistered"/>.
    /// </summary>
    public static string? OverrideDirectory { get; set; }

    /// <summary>
    /// Register the resolver. Safe to call multiple times; registration
    /// only happens once per process.
    /// </summary>
    public static void EnsureRegistered()
    {
        if (_registered) return;
        lock (_lock)
        {
            if (_registered) return;
            NativeLibrary.SetDllImportResolver(
                Assembly.GetExecutingAssembly(), Resolve);
            _registered = true;
        }
    }

    private static nint Resolve(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        var nativeFileName = GetNativeFileName(libraryName);
        if (nativeFileName is null) return nint.Zero;

        // 1. Host-injected override directory (Android nativeLibraryDir, custom path).
        if (!string.IsNullOrWhiteSpace(OverrideDirectory))
        {
            var overrideCandidate = Path.Combine(OverrideDirectory, nativeFileName);
            if (File.Exists(overrideCandidate) &&
                NativeLibrary.TryLoad(overrideCandidate, out var overrideHandle))
                return overrideHandle;
        }

        // 2. Standard runtimes/{RID}/native/ layout (NuGet / desktop deployment).
        //    RuntimeInformation.RuntimeIdentifier can be MORE specific than the
        //    folder that shipped (e.g. "win10-x64" vs "win-x64"), so try the
        //    specific RID first, then the portable RID.
        var assemblyDir = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        foreach (var rid in CandidateRids())
        {
            var nativeDir = Path.Combine(assemblyDir, "runtimes", rid, "native");
            var candidate = Path.Combine(nativeDir, nativeFileName);
            if (!File.Exists(candidate)) continue;

            // Preload the MNN core from the same directory so mnnbridge's transitive
            // dependency on it resolves even where the OS would not search the loaded
            // DLL's own directory.
            PreloadCoreBeside(nativeDir);

            if (NativeLibrary.TryLoad(candidate, out var handle))
                return handle;
        }

        // 3. Same directory as the assembly (flat deployment).
        var assemblyFlat = Path.GetDirectoryName(assembly.Location) ?? AppContext.BaseDirectory;
        var flat = Path.Combine(assemblyFlat, nativeFileName);
        if (File.Exists(flat) &&
            NativeLibrary.TryLoad(flat, out var flatHandle))
            return flatHandle;

        // 4. AppContext.BaseDirectory (Windows Service / self-contained publish).
        var baseDir = Path.Combine(AppContext.BaseDirectory, nativeFileName);
        if (File.Exists(baseDir) &&
            NativeLibrary.TryLoad(baseDir, out var baseDirHandle))
            return baseDirHandle;

        return nint.Zero; // Fall back to default OS resolution.
    }

    /// <summary>
    /// Native-runtime RIDs to try, most specific first: the process RID
    /// (may be "win10-x64") then the portable RID ("win-x64") that ships in the
    /// <c>runtimes/</c> folder.
    /// </summary>
    private static IEnumerable<string> CandidateRids()
    {
        var specific = RuntimeInformation.RuntimeIdentifier;
        if (!string.IsNullOrWhiteSpace(specific)) yield return specific;

        var os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
               : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
               : "linux";
        var arch = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64   => "x64",
            Architecture.Arm64 => "arm64",
            Architecture.X86   => "x86",
            Architecture.Arm   => "arm",
            _                  => "x64",
        };
        var portable = $"{os}-{arch}";
        if (!string.Equals(portable, specific, StringComparison.OrdinalIgnoreCase))
            yield return portable;
    }

    /// <summary>Best-effort preload of the MNN core sitting next to mnnbridge.</summary>
    private static void PreloadCoreBeside(string nativeDir)
    {
        var core = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "MNN.dll"
                 : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "libMNN.dylib"
                 : "libMNN.so";
        var path = Path.Combine(nativeDir, core);
        if (File.Exists(path))
        {
            try { NativeLibrary.TryLoad(path, out _); } catch { /* best-effort */ }
        }
    }

    private static string? GetNativeFileName(string libraryName)
    {
        // Normalise: strip leading "lib" and any extension.
        var name = Path.GetFileNameWithoutExtension(libraryName)
            .TrimStart('l', 'i', 'b'); // strips "lib" prefix so libmnnbridge → mnnbridge, libMNN → MNN

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            return $"{name}.dll";

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            return $"lib{name}.dylib";

        // Linux + Android
        return $"lib{name}.so";
    }
}
