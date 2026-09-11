// MemoryMappingOptionTests.cs
//
// The flag that was off with no way on.
//
// QwenTextGenerator.MmapIsAllowed read CIRCLEAI_MNN_MMAP and nothing else. An
// Android app cannot set an environment variable for itself, so on the single
// platform this product ships to, the setting was not "off by default" - it was
// off permanently, and neither branch could be exercised on the device where the
// crash it guards against was found.
//
// It is off for a good reason: use_mmap and kvcache_mmap were the proven cause
// of an MNN SIGSEGV - v7 and v8 died, v9 survived twice with them disabled - and
// a process that dies mid-answer is worse than a slow first token. But "off with
// a way on" and "off with no way on" are different things, and only the first is
// a decision.

using System;
using CircleAI.Inference;
using Xunit;

namespace CircleAI.Tests;

[Collection("mmap")]
public class MemoryMappingOptionTests : IDisposable
{
    private readonly bool _was = QwenTextGenerator.AllowMemoryMapping;
    private readonly string? _wasEnv = Environment.GetEnvironmentVariable(Variable);

    private const string Variable = "CIRCLEAI_MNN_MMAP";

    public void Dispose()
    {
        QwenTextGenerator.AllowMemoryMapping = _was;
        Environment.SetEnvironmentVariable(Variable, _wasEnv);
    }

    [Fact]
    public void It_is_off_unless_somebody_turns_it_on()
    {
        Environment.SetEnvironmentVariable(Variable, null);
        QwenTextGenerator.AllowMemoryMapping = false;

        Assert.False(QwenTextGenerator.MmapIsAllowed);
    }

    [Fact]
    public void A_host_can_turn_it_on_in_code_which_is_the_only_way_on_a_phone()
    {
        // THE POINT OF THE CHANGE. There is no other door on Android.
        Environment.SetEnvironmentVariable(Variable, null);
        QwenTextGenerator.AllowMemoryMapping = true;

        Assert.True(QwenTextGenerator.MmapIsAllowed);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("0", false)]
    public void The_environment_still_decides_where_it_is_set(string value, bool expected)
    {
        // Kept because a desktop or CI run has no other way in, and that is
        // where an A/B of the crash actually gets done.
        Environment.SetEnvironmentVariable(Variable, value);
        QwenTextGenerator.AllowMemoryMapping = !expected;   // deliberately opposed

        Assert.Equal(expected, QwenTextGenerator.MmapIsAllowed);
    }

    [Fact]
    public void The_environment_can_turn_it_OFF_over_a_host_that_turned_it_on()
    {
        // The direction that matters in an emergency: somebody whose phone is
        // crashing needs a way to disable it that does not require a new build,
        // and an override that could only ever turn things ON would not give
        // them one.
        Environment.SetEnvironmentVariable(Variable, "0");
        QwenTextGenerator.AllowMemoryMapping = true;

        Assert.False(QwenTextGenerator.MmapIsAllowed);
    }

    [Fact]
    public void Anything_else_in_the_variable_is_ignored_rather_than_guessed_at()
    {
        // "true", "yes", "" and a typo all mean the same thing here: nobody has
        // expressed an opinion through this door, so the host's setting stands.
        foreach (var noise in new[] { "true", "yes", "", "  ", "maybe" })
        {
            Environment.SetEnvironmentVariable(Variable, noise);

            QwenTextGenerator.AllowMemoryMapping = true;
            Assert.True(QwenTextGenerator.MmapIsAllowed);

            QwenTextGenerator.AllowMemoryMapping = false;
            Assert.False(QwenTextGenerator.MmapIsAllowed);
        }
    }

    [Fact]
    public void The_variable_is_read_every_time_not_cached_on_first_touch()
    {
        // A static cached on first touch makes the answer depend on which test
        // ran first, which is the quietest kind of flake.
        Environment.SetEnvironmentVariable(Variable, "1");
        Assert.True(QwenTextGenerator.MmapIsAllowed);

        Environment.SetEnvironmentVariable(Variable, "0");
        Assert.False(QwenTextGenerator.MmapIsAllowed);
    }
}

/// <summary>Runs alone: the flag and the environment variable are process-wide.</summary>
[CollectionDefinition("mmap", DisableParallelization = true)]
public sealed class MmapCollection { }
