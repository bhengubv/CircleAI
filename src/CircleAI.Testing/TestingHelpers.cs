// TestingHelpers.cs
//
// (3.3.0) Top-up: deterministic ID + clock helpers commonly needed by
// tests that need stable values across runs.

using System;

namespace CircleAI.Testing;

public static class DeterministicIds
{
    public static string FromSeed(string seed, string prefix = "test")
    {
        if (string.IsNullOrWhiteSpace(seed)) throw new ArgumentException("seed required");
        unchecked
        {
            uint h = 2166136261u;
            foreach (var c in seed) { h ^= c; h *= 16777619u; }
            return $"{prefix}-{h:x8}";
        }
    }
}

public sealed class FrozenClock
{
    public DateTimeOffset Now { get; private set; }
    public FrozenClock(DateTimeOffset start) => Now = start;
    public void Advance(TimeSpan by) => Now += by;
    public void SetTo(DateTimeOffset to) => Now = to;
}
