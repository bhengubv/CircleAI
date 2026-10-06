// ArchHelpers.cs
//
// Cross-platform helpers for mapping RuntimeInformation values onto the
// ArchitectureKind / OperatingSystemKind enums used by HostProfile.

using System.Runtime.InteropServices;

namespace CircleAI.Runtime.Capabilities.Internal;

internal static class ArchHelpers
{
    public static ArchitectureKind FromRuntime(Architecture a) => a switch
    {
        Architecture.X86       => ArchitectureKind.X86,
        Architecture.X64       => ArchitectureKind.X64,
        Architecture.Arm       => ArchitectureKind.Arm,
        Architecture.Arm64     => ArchitectureKind.Arm64,
        Architecture.LoongArch64 => ArchitectureKind.Loong64,
        _ => ArchitectureKind.Unknown,
    };

    public static OperatingSystemKind ResolveOsKind()
    {
        // Order matters — RuntimeInformation reports Android as Linux too.
        if (OperatingSystem.IsAndroid())   return OperatingSystemKind.Android;
        if (OperatingSystem.IsIOS()
            || OperatingSystem.IsTvOS()
            || OperatingSystem.IsWatchOS()
            || OperatingSystem.IsMacCatalyst()) return OperatingSystemKind.IOS;
        if (OperatingSystem.IsMacOS())     return OperatingSystemKind.MacOS;
        if (OperatingSystem.IsWindows())   return OperatingSystemKind.Windows;
        if (OperatingSystem.IsLinux())     return OperatingSystemKind.Linux;
        // OpenHarmony presents itself as Linux to .NET — there is no built-in
        // OperatingSystem.IsHarmonyOS yet, so explicit detection lives in the
        // OpenHarmony port (CircleAI/harmonyos/) and is layered in by the host.
        return OperatingSystemKind.Unknown;
    }
}
