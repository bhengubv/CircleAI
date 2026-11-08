// PageFaults.cs
//
// HOW OFTEN THE PROCESS WENT LOOKING FOR A PAGE IT DID NOT HAVE, from /proc/self/stat: a minor fault
// found the page in RAM (the OS's cache) and only mapped it; a major fault had to read it from flash.
// Counted around each word, it says whether what a word reads stays warm or comes from flash every
// time - the proof a streaming engine owes, beside its products and its head. Whole-process counts, so
// the ears and the voice are in them too. Null where there is no /proc.

namespace CircleAI.Inference.Managed.Runtime;

/// <summary>The process's page faults so far.</summary>
internal static class PageFaults
{
    private static readonly bool Readable = OperatingSystem.IsLinux() || OperatingSystem.IsAndroid();

    /// <summary>Minor and major faults since the process began; null where they cannot be read.</summary>
    public static (long Minor, long Major)? Now()
    {
        if (!Readable) return null;
        try
        {
            Span<byte> stat = stackalloc byte[1024];
            using var handle = File.OpenHandle("/proc/self/stat", FileMode.Open, FileAccess.Read);
            var length = RandomAccess.Read(handle, stat, 0);
            return Parse(stat[..length]);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// minflt and majflt out of a /proc/[pid]/stat line: fields 10 and 12. The command name, field 2,
    /// can hold spaces and brackets, so the fields are counted from after its last ')'.
    /// </summary>
    internal static (long Minor, long Major)? Parse(ReadOnlySpan<byte> stat)
    {
        var close = stat.LastIndexOf((byte)')');
        if (close < 0) return null;
        var rest = stat[(close + 1)..];
        long minor = -1, major = -1;
        var field = 2; // the command name was field 2
        var i = 0;
        while (i < rest.Length && field < 12)
        {
            while (i < rest.Length && rest[i] == (byte)' ') i++;
            if (i >= rest.Length) break;
            field++;
            long value = 0;
            var numeric = true;
            var start = i;
            for (; i < rest.Length && rest[i] != (byte)' '; i++)
            {
                var b = rest[i];
                if (b is >= (byte)'0' and <= (byte)'9') value = value * 10 + (b - '0');
                else numeric = false;
            }
            if (i == start) break;
            if (field == 10 && numeric) minor = value;
            if (field == 12 && numeric) major = value;
        }
        return minor >= 0 && major >= 0 ? (minor, major) : null;
    }
}
