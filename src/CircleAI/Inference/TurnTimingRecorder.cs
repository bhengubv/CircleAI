// TurnTimingRecorder.cs
//
// WHERE ONE ANSWER'S TIME WENT, MEASURED WHERE IT IS SPENT.
//
// The only breakdown there was came from the native bridge's logcat line, which no
// managed code can read, and from character counts. "31 s, 29 of them prompt
// processing" on AngelDust could not be split into waiting for the model, waiting in
// line, building the prompt, reading it and writing the answer - so nobody could say
// which of those to fix. Each stage writes its own number here, on the options object
// that travels with the call, and the request coordinator turns it into the TurnTiming
// that goes on the ask's record and across the link.

using System.Diagnostics;
using System.Threading;

namespace CircleAI.Inference;

/// <summary>Collects per-stage timings for one generation call. Thread-safe; -1 means not measured.</summary>
public sealed class TurnTimingRecorder
{
    private long _enrichMs = -1;
    private long _prefillMs = -1;
    private long _decodeMs = -1;
    private int _promptTokens = -1;
    private int _outputTokens = -1;
    private string? _kvPath;
    private string? _finish;

    /// <summary>Milliseconds spent composing the prompt (skills, memory, history).</summary>
    public long EnrichMs { get => Interlocked.Read(ref _enrichMs); set => Interlocked.Exchange(ref _enrichMs, value); }

    /// <summary>Milliseconds from handing the prompt to the runtime until the first piece of answer.</summary>
    public long PrefillMs { get => Interlocked.Read(ref _prefillMs); set => Interlocked.Exchange(ref _prefillMs, value); }

    /// <summary>Milliseconds from the first piece of answer to the last.</summary>
    public long DecodeMs { get => Interlocked.Read(ref _decodeMs); set => Interlocked.Exchange(ref _decodeMs, value); }

    /// <summary>The prompt's length in tokens, as the runtime counted or estimated it.</summary>
    public int PromptTokens { get => Volatile.Read(ref _promptTokens); set => Volatile.Write(ref _promptTokens, value); }

    /// <summary>The answer's length in tokens.</summary>
    public int OutputTokens { get => Volatile.Read(ref _outputTokens); set => Volatile.Write(ref _outputTokens, value); }

    /// <summary>How the prompt cache was used: reset, prefix-hit, continued, none.</summary>
    public string? KvPath { get => Volatile.Read(ref _kvPath); set => Volatile.Write(ref _kvPath, value); }

    /// <summary>Why generation stopped: eos, length, cancel, error.</summary>
    public string? Finish { get => Volatile.Read(ref _finish); set => Volatile.Write(ref _finish, value); }

    /// <summary>A stopwatch reading, for stages that time themselves.</summary>
    public static long Now() => Stopwatch.GetTimestamp();

    /// <summary>Milliseconds since a <see cref="Now"/> reading.</summary>
    public static long Since(long start) => (long)Stopwatch.GetElapsedTime(start).TotalMilliseconds;
}
