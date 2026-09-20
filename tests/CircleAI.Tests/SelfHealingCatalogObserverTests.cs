// SelfHealingCatalogObserverTests.cs
//
// The bridge from catalogue events to the self-heal loop Wolverine reads. The
// failures a person should see — a refused feed, a device that can run nothing —
// go through the healer (recorded, diagnosed, escalated). The successes do not
// touch the failure log, so it stays a record of what went wrong.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Core;
using CircleAI.Core.Models;
using CircleAI.Hosting.SelfHealing;
using Xunit;

namespace CircleAI.Tests;

public class SelfHealingCatalogObserverTests
{
    sealed class FakeHealer : ISelfHealer
    {
        public readonly List<FailureContext> Healed = new();

        public Task<HealingRecord> HealAsync(FailureContext failure, CancellationToken ct = default)
        {
            Healed.Add(failure);   // record synchronously, before the fire-and-forget task is observed
            return Task.FromResult(new HealingRecord(
                Guid.NewGuid().ToString("N"), DateTimeOffset.UtcNow, "sig",
                failure.Source ?? "", failure.Message, "defer", "cat", failure.Message,
                null, "recorded", HealingOutcome.Escalated, NeedsHuman: true, Handled: false));
        }

        public void Heal(Exception exception, string? source = null) { }
        public event Action? HealingChanged { add { } remove { } }
    }

    static DeviceProbe Probe()
        => new(3_000_000_000, 5_000_000_000, GpuKind.None, 4, ThermalClass.Passive, Connectivity.Online)
        { RamTotalBytes = 3_000_000_000 };

    [Fact]
    public void A_rejected_update_is_escalated_through_the_healer()
    {
        var healer = new FakeHealer();
        var obs = new SelfHealingCatalogObserver(healer);

        obs.OnFeedRejected(CatalogFeedRejection.Untrusted, "mesh", "peer not in trusted set");

        Assert.Single(healer.Healed);
        Assert.Equal("model-catalogue", healer.Healed[0].Source);
        Assert.Contains("untrusted", healer.Healed[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void No_compatible_model_is_escalated_with_the_device_summary()
    {
        var healer = new FakeHealer();
        var obs = new SelfHealingCatalogObserver(healer);

        obs.OnNoCompatibleModel(ModelModality.Chat, catalogued: 5, Probe());

        Assert.Single(healer.Healed);
        Assert.Contains("run any catalogued", healer.Healed[0].Message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("smaller model", healer.Healed[0].Details!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Informational_events_do_not_touch_the_failure_log()
    {
        var healer = new FakeHealer();
        var obs = new SelfHealingCatalogObserver(healer);

        obs.OnFeedApplied(7, "github-feed");
        obs.OnCatalogueAssessed(compatible: 3, total: 9, Probe());

        Assert.Empty(healer.Healed);   // a successful feed is not a failure
    }
}
