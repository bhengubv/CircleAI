// MemoryNow.cs
//
// HOW MUCH RAM THE PHONE CAN GIVE RIGHT NOW: /proc/meminfo's MemAvailable - what Linux could hand a process without
// swapping, page cache it can drop included. The recipe reads it at the start of a turn to decide how many of a model's
// layers stay warm and how many stream from flash (LayerStream). Null where there is no /proc (the desktop legs read it
// from the runtime instead).

namespace CircleAI.Inference.Managed.Runtime;

/// <summary>The RAM available now.</summary>
public static class MemoryNow
{
    /// <summary>Bytes the system could give without swapping; null when it does not say.</summary>
    public static long? AvailableBytes()
    {
        if (OperatingSystem.IsLinux() || OperatingSystem.IsAndroid())
        {
            try
            {
                foreach (var line in File.ReadLines("/proc/meminfo"))
                {
                    if (!line.StartsWith("MemAvailable:", StringComparison.Ordinal)) continue;
                    var digits = line.AsSpan("MemAvailable:".Length).Trim();
                    var end = digits.IndexOf(' ');
                    if (end > 0) digits = digits[..end];
                    return long.TryParse(digits, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var kb)
                        ? kb * 1024
                        : null;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return null;
            }
            return null;
        }
        var info = GC.GetGCMemoryInfo();
        return info.TotalAvailableMemoryBytes > 0 ? info.TotalAvailableMemoryBytes - info.MemoryLoadBytes : null;
    }
}
