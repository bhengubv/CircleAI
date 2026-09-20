// SelfHealingCatalogObserver.cs
//
// The bridge that makes the model catalogue's failures VISIBLE in Wolverine. The
// catalogue (Core/Inference) sits below the self-heal loop and cannot call up into
// it, so it raises neutral IModelCatalogObserver events; this bridge — which lives
// where both the catalogue contract and the loop are in scope — turns the ones a
// person should see into FailureContexts fed to ISelfHealer. From there they take
// the normal path: recorded (never lost), diagnosed by the resident brain, and
// escalated onto Wolverine's "waiting" list when they need a human.
//
// This is the "verbose logging is not thin" requirement applied to the catalogue:
// a tampered / unsigned feed and a device that can run nothing are exactly the
// failures a user needs to see the app notice — and now does.
//
//   • a refused feed          → a security escalation ("a feed was refused")
//   • no compatible model     → the "outdated model" symptom, made legible
//   • an applied feed / a good re-assessment → NOT failures; left for a plain
//     ILogger, so the failure log Wolverine reads stays a failure log.

using System;
using CircleAI.Core;
using CircleAI.Core.Models;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>
/// Routes catalogue failures into the self-heal loop so Wolverine shows them.
/// Wire it as the <see cref="IModelCatalogObserver"/> the catalogue subsystem
/// reports to.
/// </summary>
public sealed class SelfHealingCatalogObserver : IModelCatalogObserver
{
    private readonly ISelfHealer _healer;

    public SelfHealingCatalogObserver(ISelfHealer healer)
        => _healer = healer ?? throw new ArgumentNullException(nameof(healer));

    /// <inheritdoc />
    public void OnFeedRejected(CatalogFeedRejection reason, string source, string detail)
    {
        var message = reason switch
        {
            CatalogFeedRejection.Untrusted =>
                "A model-catalogue update from an untrusted source was refused.",
            _ =>
                "A malformed model-catalogue update was refused.",
        };

        // Fire-and-forget: HealAsync never throws, and the caller (the feed writer)
        // must not block on a brain diagnosis while applying a feed.
        _ = _healer.HealAsync(new FailureContext(
            Message:    message,
            Source:     "model-catalogue",
            Details:    $"{reason} from '{source}': {detail}"));
    }

    /// <inheritdoc />
    public void OnNoCompatibleModel(ModelModality modality, int catalogued, DeviceProbe probe)
    {
        _ = _healer.HealAsync(new FailureContext(
            Message: $"This device can't run any catalogued {modality} model right now.",
            Source:  "model-catalogue",
            Details: $"{catalogued} model(s) catalogued; usable RAM ~{probe.UsableRamGb:0.0} GB, " +
                     $"free storage ~{probe.StorageFreeGb:0.0} GB. " +
                     "A smaller model or a catalogue refresh is needed."));
    }

    /// <inheritdoc />
    public void OnFeedApplied(int upserted, string source)
    {
        // Not a failure — belongs in a plain ILogger, not the failure log Wolverine
        // reads. No-op here so the healing log stays a record of things that went
        // wrong, not a firehose.
    }

    /// <inheritdoc />
    public void OnCatalogueAssessed(int compatible, int total, DeviceProbe probe)
    {
        // As above: a successful re-assessment is information, not a failure.
    }
}
