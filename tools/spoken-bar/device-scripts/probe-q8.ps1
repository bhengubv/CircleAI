# Asks the shipped fork's own CPU backend how it rounds Q8_0 activations: ggml_get_type_traits_cpu(Q8_0)
# -> from_float, called on values the reference quantiser and the AVX branch round differently.
# Light: loads two DLLs into this process and quantises two blocks of 32.
param([string]$Native = 'C:\Dev\Solutions\com.bhengubv\CircleAI\.claude\worktrees\agent-a84e0f486ab40e2b1\src\CircleAI\Inference\runtimes\win-x64\native')
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Q8Probe
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void FromFloat(float[] x, byte[] y, long k);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate IntPtr Traits(int type);

    public static string Run(string native, float amax, float value)
    {
        NativeLibrary.Load(System.IO.Path.Combine(native, "ggml-base.dll"));
        var cpu = NativeLibrary.Load(System.IO.Path.Combine(native, "ggml-cpu.dll"));
        var traits = Marshal.GetDelegateForFunctionPointer<Traits>(NativeLibrary.GetExport(cpu, "ggml_get_type_traits_cpu"));
        var table = traits(8); // GGML_TYPE_Q8_0
        var fromFloat = Marshal.GetDelegateForFunctionPointer<FromFloat>(Marshal.ReadIntPtr(table));
        var x = new float[32];
        x[0] = amax; x[1] = value; x[2] = 2.5f * amax / 127f; x[3] = 0.5f * amax / 127f;
        var y = new byte[34];
        fromFloat(x, y, 32);
        return $"amax {amax:R} value {value:R}: q[0] {(sbyte)y[2]} q[1] {(sbyte)y[3]} q[2] {(sbyte)y[4]} q[3] {(sbyte)y[5]}";
    }
}
'@
[Q8Probe]::Run($Native, 1.4285147190093994, 1.1304388046264648)
[Q8Probe]::Run($Native, 5.481141567230225, 2.7405707836151123)
[Q8Probe]::Run($Native, 127, 2.5)
