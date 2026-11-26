// CoreAffinity.cs
//
// Holds a thread to the device's big cores. The engine already balances a product by stealing, so a
// product only waits for its slowest worker - and the slowest is a worker the scheduler parked on a
// little core after it slept across a streamed layer's gap. Pinning the workers to the big-core SET
// keeps stealing free to move work among the fast cores while never landing on an A53.
//
// THE OFF-SWITCH IS IsWindows/IsMacOS, NOT IsAndroid. The CoreCLR worker on the phone does not report
// as Android (CircleAI is a plain net10.0 library), so guarding on IsAndroid left the pin off there.
// AND THE PROPERTY NAME IS A RAW byte*, not a marshalled string: LPUTF8Str on a string threw
// MarshalDirectiveException on the worker's CoreCLR marshaller (measured 2026-11-19). Both P/Invokes are
// now fully blittable. KernelLimits uses the same sched_setaffinity to measure one core.

using System;
using System.Runtime.InteropServices;
using CircleAI.Core.Logging;
using CircleAI.Inference.Managed.Kernels;

namespace CircleAI.Inference.Managed.Runtime;

internal static unsafe class CoreAffinity
{
    [DllImport("libc", EntryPoint = "sched_setaffinity")]
    private static extern int SetAffinity(int pid, nint size, ulong* mask);

    [DllImport("libc", EntryPoint = "__system_property_get")]
    private static extern int SystemPropertyGet(byte* name, byte* value);

    private static bool _propLogged;
    private static bool _pinLogged;

    private static bool OffDevice => OperatingSystem.IsWindows() || OperatingSystem.IsMacOS();

    // Whether a run asked for big-core pinning through debug.circleai.pinbig ("1" = on). adb sets it without a
    // rebuild (adb shell setprop debug.circleai.pinbig 1), so a brain built after it pins and one after "0" does not.
    internal static bool PinRequestedByProperty()
    {
        if (OffDevice) return false;
        try
        {
            ReadOnlySpan<byte> property = "debug.circleai.pinbig"u8;   // 21 bytes, ASCII
            var name = stackalloc byte[property.Length + 1];
            property.CopyTo(new Span<byte>(name, property.Length));
            name[property.Length] = 0;                                 // null terminator for the C string
            var value = stackalloc byte[92];                           // PROP_VALUE_MAX
            var length = SystemPropertyGet(name, value);
            var on = length >= 1 && value[0] == (byte)'1';
            if (!_propLogged) { _propLogged = true; CircleLog.Info("Engine", $"pinbig property: len {length}, on={on}"); }
            return on;
        }
        catch (Exception ex)
        {
            if (!_propLogged) { _propLogged = true; CircleLog.Warn("Engine", $"pinbig property unreadable: {ex.GetType().Name}"); }
            return false;
        }
    }

    // The big cores as a bit mask, read once: the cores with the highest top clock (KernelLimits).
    private static readonly ulong BigCoreMask = BuildMask();

    private static ulong BuildMask()
    {
        try
        {
            ulong mask = 0;
            foreach (var core in KernelLimits.BigCores())
                if (core is >= 0 and < 64) mask |= 1UL << core;
            return mask;
        }
        catch
        {
            return 0;
        }
    }

    // Holds the calling thread to the big cores. A no-op off-device, with no big-core mask, or on any failure -
    // the thread keeps running wherever the scheduler placed it.
    internal static void PinCurrentThreadToBigCores()
    {
        if (OffDevice || BigCoreMask == 0) return;
        try
        {
            var mask = BigCoreMask;
            var result = SetAffinity(0, sizeof(ulong), &mask);
            if (!_pinLogged) { _pinLogged = true; CircleLog.Info("Engine", $"big-core pin: mask 0x{BigCoreMask:x}, sched_setaffinity={result}"); }
        }
        catch (Exception ex)
        {
            if (!_pinLogged) { _pinLogged = true; CircleLog.Warn("Engine", $"big-core pin failed: {ex.GetType().Name}"); }
        }
    }
}
