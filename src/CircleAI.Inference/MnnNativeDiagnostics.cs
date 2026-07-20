#nullable enable

// MnnNativeDiagnostics.cs
//
// Turns "System.DllNotFoundException: mnnbridge" into an error that tells you
// what to actually do about it.
//
// Why this exists
// ─────────────────────────────────────────────────────────────────────────
// NativeRuntimePrep already builds an excellent diagnostic — RID, every path
// searched, flatten/preload errors, concrete fixes. But it is only called from
// the SERVER path (MnnInferenceBridgeFactory / InferenceServerBuilder). The
// mobile and embedded path goes QwenTextGenerator -> MnnInterop -> P/Invoke,
// and got the raw CLR exception instead.
//
// Measured on a Huawei MAR-LX1M, 2026-07-20: the model downloaded perfectly
// (429 MB, SHA-verified), then StartAsync threw "DllNotFoundException:
// mnnbridge" with no indication that the cause was a BUILD-time packaging
// omission — native libs under runtimes/<rid>/native/ never reach an APK
// across a ProjectReference. Diagnosing that took an APK teardown. It should
// have taken reading the exception.

using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace CircleAI.Inference;

/// <summary>
/// Builds actionable errors for MNN native-load failures.
/// </summary>
public static class MnnNativeDiagnostics
{
    /// <summary>
    /// Expected shim filename for the running platform.
    /// </summary>
    public static string BridgeFileName
    {
        get
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "mnnbridge.dll";
            if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))     return "libmnnbridge.dylib";
            return "libmnnbridge.so";
        }
    }

    /// <summary>
    /// Wraps a native-load failure in an exception that names the platform, the
    /// paths searched, and the fix that actually applies to this host.
    /// </summary>
    public static InvalidOperationException Explain(Exception inner, string modelPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("CircleAI could not load the MNN native runtime (" + BridgeFileName + ").");
        sb.AppendLine();
        sb.AppendLine("The model itself is fine — this is a NATIVE LIBRARY problem, and on");
        sb.AppendLine("mobile it is almost always a build-time packaging omission.");
        sb.AppendLine();
        sb.Append("  RID              : ").AppendLine(RuntimeInformation.RuntimeIdentifier ?? "unknown");
        sb.Append("  Model            : ").AppendLine(modelPath);
        sb.Append("  Expected library : ").AppendLine(BridgeFileName);
        sb.Append("  Base directory   : ").AppendLine(AppContext.BaseDirectory);

        var overrideDir = NativeLibraryResolver.OverrideDirectory;
        sb.Append("  Resolver override: ")
          .AppendLine(string.IsNullOrWhiteSpace(overrideDir) ? "(none)" : overrideDir);

        sb.AppendLine();
        sb.AppendLine("Fix:");

        if (OperatingSystem.IsAndroid())
        {
            sb.AppendLine("  Android — the library must be INSIDE the APK, at lib/<abi>/.");
            sb.AppendLine("  CircleAI.Inference stores it under runtimes/<rid>/native/, which is the");
            sb.AppendLine("  NuGet PACKAGE convention and does NOT flow across a ProjectReference.");
            sb.AppendLine();
            sb.AppendLine("   • In-repo heads: the repo-root Directory.Build.targets bundles these");
            sb.AppendLine("     automatically. If you disabled it, set CircleAIBundleMnn=true.");
            sb.AppendLine("   • Out-of-repo heads: add explicitly —");
            sb.AppendLine("       <AndroidNativeLibrary");
            sb.AppendLine("           Include=\"...runtimes/android-arm64/native/*.so\">");
            sb.AppendLine("         <Abi>arm64-v8a</Abi>");
            sb.AppendLine("       </AndroidNativeLibrary>");
            sb.AppendLine("     Glob the whole folder — the runtime is 8 files, and a partial set");
            sb.AppendLine("     fails on the dependency chain.");
            sb.AppendLine("   • Verify with:  unzip -l your.apk | grep mnn");
            sb.AppendLine("   • Also set AIOptions.NativeLibDir to ApplicationInfo.NativeLibraryDir.");
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sb.AppendLine("   • Install the Visual C++ 2015-2022 Redistributable (x64) — MNN.dll");
            sb.AppendLine("     needs the MD-CRT and fails to load without it.");
            sb.AppendLine($"   • Ensure {BridgeFileName} and MNN.dll sit together in");
            sb.AppendLine($"     {Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier ?? "<rid>", "native")}");
        }
        else
        {
            sb.AppendLine($"   • Ensure {BridgeFileName} and libMNN.so sit together in");
            sb.AppendLine($"     {Path.Combine(AppContext.BaseDirectory, "runtimes", RuntimeInformation.RuntimeIdentifier ?? "<rid>", "native")}");
            sb.AppendLine("   • Or set CircleAIBundleMnn=true so the build copies them for you.");
        }

        sb.AppendLine();
        sb.Append("Inner: ").Append(inner.GetType().Name).Append(" — ").Append(inner.Message);

        return new InvalidOperationException(sb.ToString(), inner);
    }
}
