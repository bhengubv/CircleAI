// Circle26ContractTests.cs
//
// (2.6.0) Contract tests for Observer + Safety + ModelAlignment.

using System;
using System.Threading.Tasks;
using CircleAI.ModelAlignment;
using CircleAI.Observer;
using CircleAI.Guardrails;
using Xunit;

namespace CircleAI.Tests;

public sealed class Circle26ContractTests
{
    // ── Observer ─────────────────────────────────────────────────────

    [Fact]
    public async Task NullSensor_StartStopSafe()
    {
        var s = new NullSensor();
        await s.StartAsync();
        await s.StopAsync();
        await s.DisposeAsync();
        Assert.Equal("null", s.BackendId);
    }

    [Fact]
    public void InMemoryObservationToolbox_RegisterListAndGet()
    {
        var t = new InMemoryObservationToolbox();
        var tool = new ObservationTool("t1", "desc", new[] { "tag" },
            (args, ct) => ValueTask.FromResult("ok"));
        t.RegisterTool(tool);

        Assert.True(t.TryGet("t1", out var got));
        Assert.Same(tool, got);
        Assert.Single(t.ListTools());
    }

    [Fact]
    public async Task NullObservationLoop_StartStopSafe()
    {
        var l = new NullObservationLoop();
        await l.StartAsync(TimeSpan.FromMilliseconds(100));
        await l.StopAsync();
        await l.DisposeAsync();
    }

    // ── Safety ───────────────────────────────────────────────────────

    [Fact]
    public async Task NullContentFilter_RefusesByDefault()
    {
        var v = await NullContentFilter.Instance.ClassifyAsync("hi");
        Assert.Equal(SafetyVerdict.Refuse, v.Verdict);
    }

    [Fact]
    public async Task NullRefusalPolicy_AlwaysRefuses()
        => Assert.True(await NullRefusalPolicy.Instance.ShouldRefuseAsync(Array.Empty<SafetyFinding>()));

    [Fact]
    public async Task NullPromptInjectionDetector_RefusesByDefault()
    {
        var v = await NullPromptInjectionDetector.Instance.InspectAsync("hi", "rag");
        Assert.Equal(SafetyVerdict.Refuse, v.Verdict);
    }

    [Fact]
    public async Task NullSafetyAuditLog_IsNoop()
    {
        await NullSafetyAuditLog.Instance.LogAsync(new SafetyAuditEntry(
            DateTimeOffset.UtcNow, "u", "act", SafetyVerdict.Refuse, "reason"));
        Assert.Empty(await NullSafetyAuditLog.Instance.ReadAsync("u"));
    }

    // ── ModelAlignment ───────────────────────────────────────────────

    [Fact]
    public async Task NullAlignmentToolkit_FailsToApply()
    {
        var r = await NullAlignmentToolkit.Instance.ApplyAsync(
            "m1",
            new AlignmentProfile("p1", "x", Array.Empty<string>(), DateTimeOffset.UtcNow, true));
        Assert.False(r.Success);
        Assert.NotNull(r.FailureReason);
    }

    [Fact]
    public async Task NullAlignmentAuditor_AlwaysOkSinceNothingApplied()
        => await NullAlignmentAuditor.Instance.AssertOkToPublishAsync("m1");

    [Fact]
    public async Task NullAlignmentToolkit_ListReturnsEmpty()
        => Assert.Empty(await NullAlignmentToolkit.Instance.ListAppliedAsync("m1"));
}
