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
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                 && RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            // Do NOT send a 32-bit process after the x64 redistributable. That advice
            // was here until 2026-10-24 and it cannot work: the architecture is the
            // cause, not a missing CRT. Alibaba publishes no 32-bit Windows MNN bundle
            // in any release (checked 3.3.0 through 3.6.1 - every Windows asset is
            // windows_x64), so there is nothing to install, download or copy.
            sb.AppendLine("   This is a 32-bit (x86) Windows process, and there is no 32-bit");
            sb.AppendLine("   MNN build to load — not in this tree, and not upstream.");
            sb.AppendLine();
            sb.AppendLine("   • Alibaba ships no windows_x86 bundle in any MNN release, so the");
            sb.AppendLine("     runtime fetcher has no bundle registered for (Windows, X86) and");
            sb.AppendLine("     will not acquire one. This is not a missing redistributable and");
            sb.AppendLine("     not a packaging omission.");
            sb.AppendLine("   • Run the x64 service instead. That is the supported Windows host");
            sb.AppendLine("     and it carries runtimes/win-x64/native/{mnnbridge.dll, MNN.dll}.");
            sb.AppendLine("   • To make x86 work at all, MNN must be built from source for Win32");
            sb.AppendLine("     and mnnbridge rebuilt against it — see native/mnn-bridge/BUILD.md.");
            sb.AppendLine();
            sb.AppendLine("   Everything in the service that is not inference works on x86.");

            if (File.Exists(Path.Combine(
                    AppContext.BaseDirectory, "runtimes", "win-x64", "native", "MNN.dll")))
            {
                sb.AppendLine();
                sb.AppendLine("   NOTE: there IS an MNN.dll under runtimes/win-x64/native/ beside");
                sb.AppendLine("   this process. It is 64-bit and cannot be loaded here. Its presence");
                sb.AppendLine("   means nothing about x86 support.");
            }
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            sb.AppendLine("   • Install the Visual C++ 2015-2022 Redistributable for this");
            sb.AppendLine($"     process's architecture ({RuntimeInformation.ProcessArchitecture}) — MNN.dll");
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
