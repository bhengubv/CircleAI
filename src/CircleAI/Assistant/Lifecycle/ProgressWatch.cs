// ProgressWatch.cs
//
// THE WATCHDOG'S RULE: NOTHING WRITTEN, AND NOTHING WORKED ON EITHER (OPEN-GAPS E33).
//
// The supervisor ended the inference process after 90 s with no piece of an answer, on the rule
// that a turn that quiet is a hung native call. On the P30 on 2026-11-02 it was not: the C# 2B and
// its tower were reading a picture turn at about two tokens a second, the watchdog ended them twice,
// and Wolverine - told the turn had timed out - shortened every brain's context for seven hours. A
// working call spends the processor and a hung one spends none, so the inference process's own CPU
// time counts as progress here.
//
// AND A CEILING, because a native call that spins for ever spends the processor too. A job carries
// its own deadline; the coordinator cancels there, and a C# prefill stops at its next token. A job
// still running a minute past its deadline is a call that will not stop, and its process is ended
// however busy it is.
//
// PURE, so the rule is a unit test on both framework legs. The Android side samples the CPU time
// (the inference process reads its own /proc/self/stat) and does the ending.

using System.Globalization;

namespace CircleAI.Assistant.Lifecycle;

/// <summary>
/// Whether a turn in another process is still getting somewhere: a piece of its answer, or that
/// process spending the processor on it.
/// </summary>
public sealed class ProgressWatch
{
    /// <summary>The share of one core a process must spend between two samples for them to count as work.</summary>
    /// <remarks>
    /// A prefill keeps several cores busy. A process waiting on a hung call spends almost nothing - its
    /// binder threads answer a poll four times a second, well under one per cent of a core.
    /// </remarks>
    public const double BusyShare = 0.25;

    /// <summary>How far past its own deadline a job may still be running before its process is ended.</summary>
    /// <remarks>
    /// A cancelled C# prefill stops at its next token; the vision tower (25 s on the P30) and MNN's
    /// prefill, one native call (26 s for 911 tokens there), run to their end first. A minute is past both.
    /// </remarks>
    public static readonly TimeSpan OverrunLimit = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly TimeSpan _limit;
    private DateTimeOffset _lastWrite;
    private DateTimeOffset _lastWork = DateTimeOffset.MinValue;
    private (int Pid, long CpuMs, DateTimeOffset At)? _sample;
    private double? _cores;

    /// <summary>A watch over a turn that began at <paramref name="start"/>.</summary>
    /// <param name="start">When the turn began; the first silence is counted from here.</param>
    /// <param name="limit">How long a turn may write nothing and work on nothing.</param>
    public ProgressWatch(DateTimeOffset start, TimeSpan limit)
    {
        _lastWrite = start;
        _limit = limit;
    }

    /// <summary>The cores the process kept busy between its last two samples; null before there are two.</summary>
    public double? Cores { get { lock (_gate) return _cores; } }

    /// <summary>A piece of the answer arrived at <paramref name="at"/>.</summary>
    public void Wrote(DateTimeOffset at)
    {
        lock (_gate) if (at > _lastWrite) _lastWrite = at;
    }

    /// <summary>
    /// The process's CPU time, <paramref name="cpuMs"/>, as sampled at <paramref name="at"/>. True when
    /// it worked since the sample before - at least <see cref="BusyShare"/> of a core.
    /// </summary>
    /// <remarks>
    /// A SAMPLE ALREADY SEEN, or one from another process, is no evidence: a new process counts its
    /// time from nought, and its first sample only starts the count again.
    /// </remarks>
    public bool Worked(int pid, long cpuMs, DateTimeOffset at)
    {
        lock (_gate)
        {
            if (pid <= 0 || cpuMs < 0) return false;
            var before = _sample;
            if (before is { } seen && seen.Pid == pid && at <= seen.At) return false;
            _sample = (pid, cpuMs, at);
            if (before is not { } last || last.Pid != pid)
            {
                _cores = null;
                return false;
            }

            var wallMs = (at - last.At).TotalMilliseconds;
            _cores = (cpuMs - last.CpuMs) / wallMs;
            if (_cores < BusyShare) return false;
            if (at > _lastWork) _lastWork = at;
            return true;
        }
    }

    /// <summary>Whether the turn has written nothing and its process worked on nothing for longer than the limit.</summary>
    public bool Stalled(DateTimeOffset now)
    {
        lock (_gate) return now - (_lastWork > _lastWrite ? _lastWork : _lastWrite) > _limit;
    }

    /// <summary>Whether the turn has written nothing for longer than the limit - alive, if at all, by the work it was seen doing.</summary>
    public bool Silent(DateTimeOffset now)
    {
        lock (_gate) return now - _lastWrite > _limit;
    }

    /// <summary>Whether a job still running <paramref name="pastDeadline"/> after its deadline is a call that will not stop.</summary>
    public static bool Overran(TimeSpan pastDeadline) => pastDeadline > OverrunLimit;
}

/// <summary>A process's CPU time - user and system - as Linux and Android report it in /proc.</summary>
public static class ProcessCpu
{
    /// <summary>Clock ticks a second in /proc's times (USER_HZ), on every Linux and Android ABI.</summary>
    private const int TicksPerSecond = 100;

    /// <summary>The CPU time a /proc/[pid]/stat line reports, in milliseconds; null when it is not one.</summary>
    /// <remarks>
    /// COUNTED FROM THE LAST ')'. The second field is the command in parentheses, and a command may
    /// hold spaces and parentheses of its own. utime and stime are the 14th and 15th fields, so the
    /// 12th and 13th after it.
    /// </remarks>
    public static long? FromStat(string? stat)
    {
        if (string.IsNullOrWhiteSpace(stat)) return null;
        var close = stat.LastIndexOf(')');
        if (close < 0) return null;
        var fields = stat[(close + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields.Length < 13
            || !long.TryParse(fields[11], NumberStyles.None, CultureInfo.InvariantCulture, out var user)
            || !long.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var system))
            return null;
        return (user + system) * 1000 / TicksPerSecond;
    }

    /// <summary>This process's CPU time from /proc/self/stat, in milliseconds; -1 where there is none to read.</summary>
    public static long OfThisProcess()
    {
        try { return FromStat(File.ReadAllText("/proc/self/stat")) ?? -1; }
        catch { return -1; }
    }
}
