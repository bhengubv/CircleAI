// ModuleMemoryTests.cs
//
// Memory as a service every module consumes - including the ones that must not
// keep anything.
//
// THE POINT THAT IS EASY TO GET BACKWARDS: a live interpreter must never retain
// what passes through it, and a safety gate must never remember that something
// was allowed. It is tempting to conclude those features should have no memory.
// They must - because "never keep this" is itself a thing that has to be
// remembered, and a module with no continuity cannot hold its own prohibition.
//
// So every test here is some version of one question: does a module that may
// keep nothing still know what it is not allowed to do?

using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace CircleAI.Tests;

public class ModuleMemoryTests : IDisposable
{
    private readonly string _dir;
    private readonly MemoryService _memory;

    public ModuleMemoryTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "circleai-module-" + Guid.NewGuid().ToString("N"));
        _memory = new MemoryService(_dir, "test-device");
    }

    public void Dispose()
    {
        _memory.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private IModuleMemory Module(string name, MemoryRetention retention = MemoryRetention.Everything) =>
        new ModuleMemory(_memory, name, retention);

    private static MemoryAtom Rule(string text) => new()
    {
        Kind = AtomKind.Ruling, Text = text, Subject = "policy",
    };

    private static MemoryAtom WhatHappened(string text) => new()
    {
        Kind = AtomKind.Decision, Text = text, Subject = "session",
        Outcome = DecisionOutcome.Resolved,
    };

    // ==================================================================
    // A module that may keep nothing still keeps its rules
    // ==================================================================

    [Fact]
    public async Task An_interpreter_remembers_that_it_must_not_remember()
    {
        // ⭐ THE WHOLE POINT. Take the memory away from an interpreter and it
        // cannot hold the one thing it most needs to know.
        var interpret = Module("interpret", MemoryRetention.RulesOnly);

        var kept = await interpret.RememberAsync(
            Rule("Never keep what passes through an interpreted conversation"));

        Assert.True(kept);

        var back = await interpret.RecallAsync(new Situation("interpret", "policy"));
        Assert.Contains(back.Atoms, a => a.Text.StartsWith("Never keep", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_interpreter_does_not_keep_what_passed_through_it()
    {
        // Those are two other people's words. Keeping them is surveillance.
        var interpret = Module("interpret", MemoryRetention.RulesOnly);

        var kept = await interpret.RememberAsync(
            WhatHappened("She said she would meet him at six"));

        Assert.False(kept);
        Assert.Equal(0, await _memory.CountAsync());
    }

    [Fact]
    public async Task A_module_that_may_keep_only_rules_reads_nothing_into_them()
    {
        // Extraction reads whatever it is given, and what passes through an
        // interpreter is precisely what must never be read.
        var interpret = Module("interpret", MemoryRetention.RulesOnly);

        var report = await interpret.HeardAsync(
            "Never restart a device without asking. The adb push did not work.");

        Assert.Equal(0, report.Considered);
        Assert.Empty(report.Recorded);
        Assert.Equal(0, await _memory.CountAsync());
    }

    [Fact]
    public async Task A_gate_never_remembers_that_something_was_allowed()
    {
        // Being talked past once would otherwise buy you past it forever.
        var gate = Module("safety", MemoryRetention.RulesOnly);

        Assert.False(await gate.RememberAsync(WhatHappened("That request was fine last time")));
        Assert.True(await gate.RememberAsync(Rule("Always re-evaluate; never trust a previous verdict")));

        var back = await gate.RecallAsync(new Situation("safety", "policy"));
        Assert.Single(back.Atoms);
        Assert.Contains("re-evaluate", back.Atoms[0].Text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Reading_is_never_restricted_by_retention()
    {
        // A safety gate that could not read the owner's standing rules would be
        // worse at its job, not safer at it.
        await _memory.RememberAsync(new MemoryAtom
        {
            Kind = AtomKind.Ruling,
            Text = "Never restart a device or toggle its radios without asking",
            Subject = "device:state",
        });

        var gate = Module("safety", MemoryRetention.RulesOnly);
        var back = await gate.RecallAsync(new Situation("device", "state"));

        Assert.NotEmpty(back.Atoms);
    }

    // ==================================================================
    // Whose atom is this
    // ==================================================================

    [Fact]
    public async Task What_a_module_recorded_can_be_found_as_that_modules()
    {
        var career = Module("career");
        await career.RememberAsync(WhatHappened("They want work that is not driving"));

        var mine = await career.RecallAsync(new Situation("career"));

        Assert.Contains(mine.Atoms, a => a.Subject!.StartsWith("career", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_modules_subject_still_rolls_up_to_the_module()
    {
        // "career:goals" has to be findable both as itself and as everything
        // career remembers, or a module's memory is a pile.
        var career = Module("career");
        await career.RememberAsync(new MemoryAtom
        {
            Kind = AtomKind.Fact, Text = "They drive a taxi at weekends", Subject = "goals",
        });

        Assert.NotEmpty((await career.RecallAsync(new Situation("career", "goals"))).Atoms);
        Assert.NotEmpty((await career.RecallAsync(new Situation("career"))).Atoms);
    }

    [Fact]
    public async Task A_module_does_not_stamp_itself_twice()
    {
        var career = Module("career");
        await career.RememberAsync(new MemoryAtom
        {
            Kind = AtomKind.Fact, Text = "Something", Subject = "career:goals",
        });

        var all = await _memory.AllAsync();
        Assert.Equal("career:goals", all[0].Subject);
    }

    [Fact]
    public async Task Two_modules_recording_the_same_words_stay_apart()
    {
        // One device, one memory, and still an answer to "who said that".
        await Module("career").RememberAsync(WhatHappened("The same sentence"));
        await Module("banking").RememberAsync(WhatHappened("The same sentence"));

        var all = await _memory.AllAsync();
        Assert.Equal(2, all.Count);
        Assert.Contains(all, a => a.Subject == "career:session");
        Assert.Contains(all, a => a.Subject == "banking:session");
    }

    [Fact]
    public void A_module_has_to_say_what_it_is()
    {
        Assert.Throws<ArgumentException>(() => new ModuleMemory(_memory, "  "));
    }

    // ==================================================================
    // One memory per device
    // ==================================================================

    [Fact]
    public void Registering_it_twice_does_not_give_a_device_two_memories()
    {
        // A second store would be a second set of facts about one person,
        // disagreeing quietly.
        var services = new ServiceCollection()
            .AddCircleMemory(Path.Combine(_dir, "one"), "test-device")
            .AddCircleMemory(Path.Combine(_dir, "two"), "test-device");

        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<IMemoryService>(),
            provider.GetRequiredService<IMemoryService>());
    }

    [Fact]
    public void Every_module_resolves_to_its_own_view_of_the_one_memory()
    {
        var services = new ServiceCollection()
            .AddCircleMemory(Path.Combine(_dir, "shared"), "test-device")
            .AddModuleMemory("career")
            .AddModuleMemory("interpret", MemoryRetention.RulesOnly);

        using var provider = services.BuildServiceProvider();

        var career = provider.GetRequiredKeyedService<IModuleMemory>("career");
        var interpret = provider.GetRequiredKeyedService<IModuleMemory>("interpret");

        Assert.Equal("career", career.Module);
        Assert.Equal("interpret", interpret.Module);
        Assert.Equal(MemoryRetention.Everything, career.Retention);
        Assert.Equal(MemoryRetention.RulesOnly, interpret.Retention);
    }

    [Fact]
    public void What_a_module_may_keep_is_declared_in_code_not_read_from_the_memory()
    {
        // THE GUARANTEE HAS TO SURVIVE THE MEMORY BEING WIPED. A prohibition
        // that lives only in the store fails open on a fresh device, on a
        // restored one, and on any device where somebody edited the file - and
        // a rule that can be forgotten is not a rule.
        var services = new ServiceCollection()
            .AddCircleMemory(Path.Combine(_dir, "empty"), "test-device")
            .AddModuleMemory("interpret", MemoryRetention.RulesOnly);

        using var provider = services.BuildServiceProvider();
        var interpret = provider.GetRequiredKeyedService<IModuleMemory>("interpret");

        // Nothing has ever been written here, so there is no rule to read.
        Assert.Equal(MemoryRetention.RulesOnly, interpret.Retention);
    }
}
