// ServiceHealing.cs
//
// The Wolverine dashboard, in an app that does not run the self-heal loop.
//
// THE LOOP NEEDS A BRAIN AND THE MODEL CACHES, and this app has neither. Diagnosing
// a failure is a model call (IFailureAnalyst); the one safe fix wired so far frees
// the regenerable model cache. Both live in CircleAI now, so the loop lives there —
// keeping it here would mean referencing CircleAI.Hosting, and behind it the whole
// inference engine, for an app whose job is to render.
//
// SO THE SCREEN SAYS WHERE IT RUNS instead of showing an empty dashboard. An empty
// "healed: 0, needs you: 0" is the worst available answer: it reads as "nothing has
// gone wrong" when the truth is "nothing here is watching". Same shape as
// ServiceResidentAssistant — a head that cannot do a thing says so and says where it
// can be done.
//
// CLOSING THIS PROPERLY IS A WIRE CHANGE. LinkVerb covers recall, remember, skills
// and capabilities; there is no healing verb, so this app cannot read the service's
// log even to display it. That is a protocol addition with its own scope, not
// something to fake with a plausible-looking list.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Client;

namespace CircleAI.Samples.App.Services;

/// <inheritdoc />
public sealed class ServiceHealing(LinkedBrain brain) : IHealingView
{
    /// <inheritdoc />
    /// <remarks>Declared to satisfy the interface, never raised: nothing heals here.</remarks>
    public event Action? Changed { add { } remove { } }

    /// <inheritdoc />
    /// <remarks>
    /// ZEROES WITH A REASON BESIDE THEM. The counts are honest — this process has
    /// healed nothing and has nothing waiting — and the screen's own copy is what
    /// says the watching happens in CircleAI. Autonomy reads Off because this app's
    /// dial is off, not because the service's is.
    /// </remarks>
    public Task<HealthSummary> HealthAsync(CancellationToken ct = default)
        => Task.FromResult(new HealthSummary(0, 0, 0, null, AutonomyLevel.Off));

    /// <inheritdoc />
    public Task<IReadOnlyList<HealingItem>> HealedAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HealingItem>>([]);

    /// <inheritdoc />
    public Task<IReadOnlyList<HealingItem>> NeedsYouAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<HealingItem>>([Elsewhere]);

    /// <inheritdoc />
    public Task<AutonomyLevel> AutonomyAsync(CancellationToken ct = default)
        => Task.FromResult(AutonomyLevel.Off);

    /// <inheritdoc />
    /// <remarks>Ignored: the dial that matters belongs to CircleAI, and the link has no verb for it.</remarks>
    public Task SetAutonomyAsync(AutonomyLevel level, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc />
    public Task MarkHandledAsync(string id, CancellationToken ct = default) => Task.CompletedTask;

    /// <inheritdoc />
    /// <remarks>
    /// Nothing to exercise. A self-check that reported "passed" without running a loop
    /// would be the exact lie this file exists to avoid.
    /// </remarks>
    public Task RunSelfCheckAsync(CancellationToken ct = default) => Task.CompletedTask;

    /// <summary>The one row: where the self-healing actually runs.</summary>
    private HealingItem Elsewhere => brain.ServiceInstalled
        ? new HealingItem(
            "self-heal-elsewhere",
            DateTimeOffset.Now,
            "Self-healing runs in CircleAI",
            "This app renders; CircleAI holds the models and watches for failures. "
          + "Open CircleAI to see what it has fixed and what is waiting for you.",
            "Open CircleAI",
            "Recorded",
            NeedsHuman: false)
        : new HealingItem(
            "self-heal-no-service",
            DateTimeOffset.Now,
            "CircleAI is not installed",
            "This app asks CircleAI to think, to listen and to watch itself. "
          + "Install it to use any of them.",
            "Install CircleAI",
            "Escalated",
            NeedsHuman: true);
}
