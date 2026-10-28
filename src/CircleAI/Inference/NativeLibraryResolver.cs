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

    /// <summary>Best-effort preload of the engine cores sitting next to a bridge.</summary>
    /// <remarks>
    /// THIS EXISTED FOR MNN AND llama.cpp WAS NEVER ADDED TO IT, which is the whole
    /// reason a Windows service carrying a complete llama.cpp reported "engines MNN".
    /// Measured on this box on 2026-10-27 against the shipped single-file EXE: the
    /// extract directory held llamabridge.dll, llama.dll and ggml{,-base,-cpu}.dll at
    /// runtimes/win-x64/native/, every one of them the right architecture, and
    /// LlamaGenerator.IsAvailable was still false - so the catalogue marked every GGUF
    /// incompatible and the service called a half-downloaded Bonsai "dead weight ...
    /// this build cannot run it". The natives were there. Nothing could open them.
    /// <para>
    /// WHY A PRELOAD IS NEEDED AT ALL, which the original comment had right: Windows
    /// resolves a DLL's own imports against the PROCESS search path, not against the
    /// folder the DLL was loaded from, so a bridge loaded by absolute path out of
    /// runtimes/ cannot find its siblings. MNN.dll has no such imports and loads alone;
    /// llamabridge.dll imports llama.dll, which imports the three ggml libraries. One
    /// self-contained engine hid the gap.
    /// </para>
    /// <para>
    /// ORDER IS LOAD-BEARING: ggml-base and ggml-cpu before ggml, ggml before llama,
    /// llama before the bridge that imports it. Each is best-effort and a miss is
    /// silent, because this runs for every bridge on every platform and a core that is
    /// not shipped here is not an error - the TryLoad of the bridge itself is what
    /// decides.
    /// </para>
    /// </remarks>
    private static void PreloadCoreBeside(string nativeDir)
    {
        foreach (var core in CoreFileNames())
        {
            var path = Path.Combine(nativeDir, core);
            if (!File.Exists(path)) continue;
            try { NativeLibrary.TryLoad(path, out _); } catch { /* best-effort */ }
        }
    }

    /// <summary>Every engine core a bridge in this folder might import, dependants last.</summary>
    private static IEnumerable<string> CoreFileNames()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            yield return "ggml-base.dll";
            yield return "ggml-cpu.dll";
            yield return "ggml.dll";
            yield return "llama.dll";
            yield return "MNN.dll";
            yield break;
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            yield return "libggml-base.dylib";
            yield return "libggml-cpu.dylib";
            yield return "libggml.dylib";
            yield return "libllama.dylib";
            yield return "libMNN.dylib";
            yield break;
        }

        yield return "libggml-base.so";
        yield return "libggml-cpu.so";
        yield return "libggml.so";
        yield return "libllama.so";
        yield return "libMNN.so";
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
