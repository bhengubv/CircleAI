// DeviceHealing.cs
//
// The head's IHealingView — the browser-safe face the Wolverine dashboard reads and
// drives, over the product-side loop (ISelfHealer) and log (IHealingLog). Same shape
// as DeviceBrain/DeviceMemory: the screen talks to the Assistant contract, this maps
// it to the product. It relays the loop's HealingChanged as its own Changed so the
// dashboard re-reads the instant something heals.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Assistant;
using CircleAI.Hosting.SelfHealing;

namespace CircleAI.Assistant.Device;

/// <summary>On-device <see cref="IHealingView"/> over the self-heal loop and its log.</summary>
public sealed class DeviceHealing : IHealingView, IDisposable
{
    private readonly IHealingLog _log;
    private readonly ISelfHealer _healer;
    private readonly ISettings _settings;
    private readonly TimeProvider _clock;

    public DeviceHealing(IHealingLog log, ISelfHealer healer, ISettings settings, TimeProvider? clock = null)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _healer = healer ?? throw new ArgumentNullException(nameof(healer));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _clock = clock ?? TimeProvider.System;
        _healer.HealingChanged += Raise;
    }

    /// <inheritdoc />
    public event Action? Changed;

    private void Raise() => Changed?.Invoke();

    /// <inheritdoc />
    public async Task<HealthSummary> HealthAsync(CancellationToken ct = default)
    {
        var recent = await _log.RecentAsync(200, ct).ConfigureAwait(false);
        var since = _clock.GetUtcNow().AddHours(-24);

        int failures = 0, healed = 0, needs = 0;
        DateTimeOffset? lastHealed = null;
        foreach (var r in recent)   // newest first
        {
            if (r.At >= since) failures++;
            if (r.Outcome == HealingOutcome.Healed)
            {
                healed++;
                lastHealed ??= r.At;
            }
            if (r.NeedsHuman && !r.Handled) needs++;
        }

        return new HealthSummary(failures, healed, needs, lastHealed, await AutonomyAsync(ct).ConfigureAwait(false));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealingItem>> HealedAsync(CancellationToken ct = default)
        => Map(await _log.RecentAsync(100, ct).ConfigureAwait(false),
               r => r.Outcome == HealingOutcome.Healed,
               detail: r => r.ActionTaken);

    /// <inheritdoc />
    public async Task<IReadOnlyList<HealingItem>> NeedsYouAsync(CancellationToken ct = default)
        => Map(await _log.RecentAsync(100, ct).ConfigureAwait(false),
               r => r.NeedsHuman && !r.Handled,
               detail: r => r.Summary);

    /// <inheritdoc />
    public async Task<AutonomyLevel> AutonomyAsync(CancellationToken ct = default)
        => AutonomyLevels.Parse(await _settings
            .ServiceSettingAsync("selfheal", "autonomy", AutonomyLevels.AutoFixSafe, ct)
            .ConfigureAwait(false));

    /// <inheritdoc />
    public async Task SetAutonomyAsync(AutonomyLevel level, CancellationToken ct = default)
    {
        await _settings.SetServiceSettingAsync("selfheal", "autonomy", AutonomyLevels.ToWire(level), ct)
            .ConfigureAwait(false);
        Raise();
    }

    /// <inheritdoc />
    public async Task MarkHandledAsync(string id, CancellationToken ct = default)
    {
        await _log.MarkHandledAsync(id, ct).ConfigureAwait(false);
        Raise();
    }

    /// <inheritdoc />
    public void Dispose() => _healer.HealingChanged -= Raise;

    private static IReadOnlyList<HealingItem> Map(
        IReadOnlyList<HealingRecord> records,
        Func<HealingRecord, bool> where,
        Func<HealingRecord, string> detail)
    {
        var list = new List<HealingItem>();
        foreach (var r in records)
        {
            if (!where(r)) continue;
            var title = string.IsNullOrWhiteSpace(r.Source) ? r.Message : $"{r.Source}: {r.Message}";
            list.Add(new HealingItem(r.Id, r.At, title, detail(r), r.RecommendedAction, Pretty(r.Outcome), r.NeedsHuman));
        }
        return list;
    }

    private static string Pretty(HealingOutcome outcome) => outcome switch
    {
        HealingOutcome.Healed => "Healed",
        HealingOutcome.Escalated => "Needs you",
        HealingOutcome.Failed => "Fix failed",
        HealingOutcome.AwaitingBrain => "Awaiting brain",
        _ => "Recorded",
    };
}
