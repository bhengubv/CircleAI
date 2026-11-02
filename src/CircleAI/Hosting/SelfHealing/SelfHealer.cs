// SelfHealer.cs
//
// The self-heal LOOP: the one place that turns a caught failure into an action.
//
//   record it (never lost)  ->  if autonomy allows AND the brain is ready, diagnose
//   ->  a quick-fix the person allows and we have a safe remedy for  ->  run it
//   ->  everything else                                              ->  hand to a human
//
// Two rules keep it from ever making things worse:
//   1. It NEVER throws. Every path — a thrown fix, a dead brain, a broken log — ends
//      in a recorded outcome, not an exception bubbling back into the code that failed.
//   2. It NEVER loop-heals. A failure whose signature keeps coming back inside a short
//      window is escalated instead of re-fixed, so a fix that does not actually fix
//      cannot spin. Only safe, reversible remedies (ISafeFix) ever run automatically.
//
// It is brain-agnostic and desktop-testable: the analyst, the log, the remedies and
// the policy are all injected, so a test drives the whole loop with fakes.
//
// RULES FIRST (S6). A failure that came through the log hub carries its HealthEvent, and a
// fault Wolverine knows is decided by HealingRules - a ladder of remedies, tried in order,
// with what worked on this device before tried first - with no brain involved. A failure of
// the brain itself is never put to the brain: rules alone decide it, and one no rule covers is
// recorded and reported rather than guessed at. Only a fault no rule covers, outside the
// brain, reaches the analyst, and the analyst is handed the list of remedies that exist and
// may choose only from it. A remedy already run where the failure happened (a tool call
// retried while the request was still in hand) is recorded with its outcome, not run again.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Hosting.SelfHealing;

/// <summary>Turns a caught failure into a recorded outcome — healed, or handed to a human.</summary>
public interface ISelfHealer
{
    /// <summary>Analyse and (if allowed) heal one failure. Never throws; returns the record written.</summary>
    Task<HealingRecord> HealAsync(FailureContext failure, CancellationToken ct = default);

    /// <summary>Fire-and-forget convenience for exception hooks. Swallows everything.</summary>
    void Heal(Exception exception, string? source = null);

    /// <summary>Raised after each healing, so a dashboard can re-read.</summary>
    event Action? HealingChanged;
}

/// <summary>The default self-heal loop.</summary>
public sealed class SelfHealer : ISelfHealer
{
    private readonly IFailureAnalyst _analyst;
    private readonly IHealingLog _log;
    private readonly IReadOnlyList<ISafeFix> _fixes;
    private readonly ISelfHealPolicy _policy;
    private readonly TimeProvider _clock;
    private readonly IReadOnlyList<HealingRule> _rules;
    private readonly Func<string, string?>? _remembered;

    // The anti-loop breaker: how many times one failure signature may be seen in a
    // window before it is escalated instead of re-fixed.
    private static readonly TimeSpan Window = TimeSpan.FromMinutes(10);
    private const int MaxAttemptsPerWindow = 3;
    private readonly ConcurrentDictionary<string, (int Count, DateTimeOffset First)> _seen = new();

    /// <param name="analyst">The self-healing sense (categorise → recommend).</param>
    /// <param name="log">Where every failure and its outcome is recorded.</param>
    /// <param name="fixes">The safe, reversible remedies available to run.</param>
    /// <param name="policy">Autonomy + brain-readiness decisions.</param>
    /// <param name="clock">Injected for testable time; defaults to the system clock.</param>
    public SelfHealer(
        IFailureAnalyst analyst,
        IHealingLog log,
        IEnumerable<ISafeFix> fixes,
        ISelfHealPolicy policy,
        TimeProvider? clock = null)
        : this(analyst, log, fixes, policy, clock, rules: null, remembered: null)
    {
    }

    /// <param name="analyst">The self-healing sense (categorise → recommend).</param>
    /// <param name="log">Where every failure and its outcome is recorded.</param>
    /// <param name="fixes">The safe, reversible remedies available to run.</param>
    /// <param name="policy">Autonomy + brain-readiness decisions.</param>
    /// <param name="clock">Injected for testable time; defaults to the system clock.</param>
    /// <param name="rules">The rules asked first; null is <see cref="HealingRules.Standard"/>.</param>
    /// <param name="remembered">
    /// What worked on this device before, by fingerprint (SelfModel.RemedyThatWorked) - tried
    /// first when a rule's ladder holds it. Null remembers nothing.
    /// </param>
    public SelfHealer(
        IFailureAnalyst analyst,
        IHealingLog log,
        IEnumerable<ISafeFix> fixes,
        ISelfHealPolicy policy,
        TimeProvider? clock,
        IReadOnlyList<HealingRule>? rules,
        Func<string, string?>? remembered = null)
    {
        _analyst = analyst ?? throw new ArgumentNullException(nameof(analyst));
        _log = log ?? throw new ArgumentNullException(nameof(log));
        _fixes = (fixes ?? throw new ArgumentNullException(nameof(fixes))).ToList();
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _clock = clock ?? TimeProvider.System;
        _rules = rules ?? HealingRules.Standard;
        _remembered = remembered;
    }

    /// <inheritdoc />
    public event Action? HealingChanged;

    /// <summary>
    /// A safe fix was applied - the failure, and the remedy's name. Raised after the record is
    /// written, so whoever checks whether it worked (DeviceWolverine) can watch for the answer.
    /// </summary>
    public event Action<FailureContext, string>? Applied;

    /// <summary>The names of the remedies this loop can run - the only list the analyst may choose from.</summary>
    public IReadOnlyList<string> RemedyNames =>
        _fixes.Select(f => f.Category).Where(c => !string.IsNullOrWhiteSpace(c)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <inheritdoc />
    public void Heal(Exception exception, string? source = null)
    {
        if (exception is null) return;
        var context = FromException(exception, source);
        _ = HealQuietlyAsync(context);
    }

    private async Task HealQuietlyAsync(FailureContext failure)
    {
        try { await HealAsync(failure, CancellationToken.None).ConfigureAwait(false); }
        catch { /* a fire-and-forget heal must never surface its own failure */ }
    }

    /// <inheritdoc />
    public async Task<HealingRecord> HealAsync(FailureContext failure, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var now = _clock.GetUtcNow();
        var health = failure.Event;

        // THE FINGERPRINT WHEN THERE IS ONE (S6): the same key the device remembers remedies
        // under and WolverineAPI de-duplicates on, so one failure is one thing everywhere.
        var signature = health?.Fingerprint is { Length: > 0 } fingerprint ? fingerprint : Signature(failure);

        HealingVerdict? verdict = null;
        HealingOutcome outcome;
        string actionTaken;
        bool needsHuman;
        string? applied = null;
        HealingRule? rule;

        if (IsRecurring(signature, now))
        {
            // Seen too often, too fast — a fix that is not fixing. Hand it on rather
            // than spin. This is the "never make it worse" guard.
            outcome = HealingOutcome.Escalated;
            actionTaken = "escalated — this keeps recurring";
            needsHuman = true;
        }
        else if (!await Safe(_policy.ShouldAnalyseAsync(ct)).ConfigureAwait(false))
        {
            // Autonomy is Off: record and stop.
            outcome = HealingOutcome.Recorded;
            actionTaken = "recorded — self-healing is off";
            needsHuman = false;
        }
        else if (health is { Remedy: { Length: > 0 } done, Worked: { } worked })
        {
            // REMEDIED IN PLACE, where the work was still in hand (S6): recorded with its
            // outcome, never run a second time from here.
            verdict = new HealingVerdict(HealingKind.QuickFix, done,
                worked ? "Remedied where it happened." : "The remedy where it happened did not take.",
                RecommendedAction: Remedies.Describe(done), Confidence: 1.0);
            outcome = worked ? HealingOutcome.Healed : HealingOutcome.Failed;
            actionTaken = worked ? $"remedied in place — {done}" : $"remedied in place — {done} — it did not take; handed on";
            needsHuman = !worked;
        }
        else if ((rule = HealingRules.For(health, _rules)) is not null)
        {
            // RULES FIRST: a fault Wolverine knows needs no brain to decide it.
            verdict = new HealingVerdict(HealingKind.QuickFix, rule.Ladder[0], $"Known fault: {rule.Name}.",
                RecommendedAction: Remedies.Describe(rule.Ladder[0]), Confidence: 1.0);

            if (!await Safe(_policy.MayAutoFixAsync(ct)).ConfigureAwait(false))
            {
                outcome = HealingOutcome.Escalated;
                actionTaken = $"suggested {rule.Ladder[0]} — waiting for you (suggest-only)";
                needsHuman = true;
            }
            else
            {
                var tried = 0;
                foreach (var remedy in Ordered(rule.Ladder, signature))
                {
                    if (Exact(remedy) is not { } fix) continue;
                    tried++;
                    if (await TryFix(fix, failure, ct).ConfigureAwait(false)) { applied = remedy; break; }
                }

                if (applied is not null)
                {
                    verdict = verdict with { Category = applied, RecommendedAction = Remedies.Describe(applied) };
                    outcome = HealingOutcome.Healed;
                    actionTaken = $"auto-fixed — {applied}";
                    needsHuman = false;
                }
                else if (tried == 0)
                {
                    outcome = HealingOutcome.Escalated;
                    actionTaken = "known fault, but no remedy for it is wired here — handed on";
                    needsHuman = true;
                }
                else
                {
                    outcome = HealingOutcome.Failed;
                    actionTaken = "every remedy for this is already at its limit — handed on";
                    needsHuman = true;
                }
            }
        }
        else if (HealingRules.IsTheBrainsOwn(health))
        {
            // THE BRAIN IS NOT ASKED ABOUT ITS OWN FAILURE: rules alone decide, and no rule
            // covers this one - the phone stopping the service, a death nothing explains.
            // Recorded and reported upstream, never guessed at; and not put to the person, who
            // can do nothing about it. If it keeps happening, the recurrence guard above says so.
            outcome = HealingOutcome.Recorded;
            actionTaken = "the brain itself failed and no rule covers it — recorded and reported, not guessed at";
            needsHuman = false;
        }
        else if (!await Safe(_policy.BrainReadyAsync(ct)).ConfigureAwait(false))
        {
            // The brain is cold. Never call the analyst cold (it would block on a
            // model load). Hand it on instead.
            outcome = HealingOutcome.AwaitingBrain;
            actionTaken = "escalated — the brain was not ready to diagnose";
            needsHuman = true;
        }
        else
        {
            // AN UNKNOWN FAULT: the brain may choose a remedy, but only from the list that exists.
            verdict = await SafeAnalyse(failure.Remedies is null ? failure with { Remedies = RemedyNames } : failure, ct)
                .ConfigureAwait(false);
            var mayAutoFix = await Safe(_policy.MayAutoFixAsync(ct)).ConfigureAwait(false);
            var fix = verdict.Kind == HealingKind.QuickFix ? MatchFix(verdict) : null;

            if (verdict.Kind == HealingKind.QuickFix && mayAutoFix && fix is not null)
            {
                if (await TryFix(fix, failure, ct).ConfigureAwait(false))
                {
                    applied = fix.Category;
                    outcome = HealingOutcome.Healed;
                    actionTaken = $"auto-fixed — {fix.Category}";
                    needsHuman = false;
                }
                else
                {
                    outcome = HealingOutcome.Failed;
                    actionTaken = $"the {fix.Category} fix did not take — handed on";
                    needsHuman = true;
                }
            }
            else
            {
                outcome = HealingOutcome.Escalated;
                actionTaken = verdict.Kind switch
                {
                    HealingKind.QuickFix when !mayAutoFix => "suggested a fix — waiting for you (suggest-only)",
                    HealingKind.QuickFix => "suggested a fix — no safe remedy is wired for it",
                    HealingKind.Patch => "needs a code change — handed to you",
                    _ => "handed to you",
                };
                needsHuman = true;
            }
        }

        var record = new HealingRecord(
            Id: Guid.NewGuid().ToString("N"),
            At: now,
            Signature: signature,
            Source: string.IsNullOrWhiteSpace(failure.Source) ? "app" : failure.Source!,
            Message: failure.Message,
            Kind: verdict is null ? "unclassified" : KindText(verdict.Kind),
            Category: verdict?.Category ?? "unclassified",
            Summary: verdict?.Summary ?? "Recorded; not analysed.",
            RecommendedAction: verdict?.RecommendedAction,
            ActionTaken: actionTaken,
            Outcome: outcome,
            NeedsHuman: needsHuman,
            Handled: false);

        // Recording must not throw the loop — a lost entry is bad, a thrown one is worse.
        try { await _log.AppendAsync(record, ct).ConfigureAwait(false); }
        catch { /* swallow: the outcome is decided; a failed write must not re-raise */ }

        try { HealingChanged?.Invoke(); }
        catch { /* a subscriber must not take the loop down */ }

        if (applied is not null)
        {
            try { Applied?.Invoke(failure, applied); }
            catch { /* the fix is applied; a watcher that throws must not undo the record */ }
        }

        return record;
    }

    // ── rules ──────────────────────────────────────────────────────────────────

    // The ladder in the order to try it: what worked on this device before for this very
    // failure first, then the rule's own order.
    private IEnumerable<string> Ordered(IReadOnlyList<string> ladder, string signature)
    {
        string? remembered = null;
        try { remembered = _remembered?.Invoke(signature); }
        catch { /* a memory that cannot be read is the shipped order */ }

        if (remembered is not null && ladder.Contains(remembered, StringComparer.OrdinalIgnoreCase))
            yield return ladder.First(r => string.Equals(r, remembered, StringComparison.OrdinalIgnoreCase));
        foreach (var remedy in ladder)
            if (!string.Equals(remedy, remembered, StringComparison.OrdinalIgnoreCase)) yield return remedy;
    }

    // A rule names its remedy exactly; there is no fuzziness in what a rule may run.
    private ISafeFix? Exact(string remedy) =>
        _fixes.FirstOrDefault(f => string.Equals(f.Category, remedy, StringComparison.OrdinalIgnoreCase));

    // ── anti-loop breaker ──────────────────────────────────────────────────────

    private bool IsRecurring(string signature, DateTimeOffset now)
    {
        var entry = _seen.AddOrUpdate(
            signature,
            _ => (1, now),
            (_, prev) => now - prev.First > Window ? (1, now) : (prev.Count + 1, prev.First));
        return entry.Count > MaxAttemptsPerWindow;
    }

    // ── matching a remedy to a verdict ─────────────────────────────────────────

    private ISafeFix? MatchFix(HealingVerdict verdict)
    {
        var category = verdict.Category?.Trim();
        if (string.IsNullOrEmpty(category)) return null;
        return _fixes.FirstOrDefault(f => CategoryMatches(f.Category, category));
    }

    // A fix handles a category when either name contains the other, case-insensitively —
    // so a "cache" fix matches "cache", "cache-miss", or "stale-cache".
    private static bool CategoryMatches(string fixCategory, string verdictCategory)
    {
        if (string.IsNullOrWhiteSpace(fixCategory)) return false;
        return verdictCategory.Contains(fixCategory, StringComparison.OrdinalIgnoreCase)
            || fixCategory.Contains(verdictCategory, StringComparison.OrdinalIgnoreCase);
    }

    // ── no-throw wrappers ──────────────────────────────────────────────────────

    private async Task<HealingVerdict> SafeAnalyse(FailureContext failure, CancellationToken ct)
    {
        try { return await _analyst.AnalyseAsync(failure, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return HealingVerdict.NeedsAHuman($"The analyst failed: {ex.GetType().Name}."); }
    }

    private static async Task<bool> TryFix(ISafeFix fix, FailureContext failure, CancellationToken ct)
    {
        try { return await fix.TryFixAsync(failure, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }   // a fix that throws did not fix — escalate.
    }

    // A policy call that throws is treated as "no" — the cautious side.
    private static async Task<bool> Safe(Task<bool> policyCall)
    {
        try { return await policyCall.ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    // ── helpers ────────────────────────────────────────────────────────────────

    private static string Signature(FailureContext f)
    {
        var source = (f.Source ?? "").Trim();
        var firstLine = (f.Message ?? "").Split('\n', 2)[0].Trim();
        return $"{source}|{firstLine}".ToLowerInvariant();
    }

    private static FailureContext FromException(Exception ex, string? source)
    {
        var root = ex.GetBaseException();
        return new FailureContext(
            Message: root.Message,
            StackTrace: ex.StackTrace,
            Source: source ?? ex.TargetSite?.DeclaringType?.Name,
            Details: root.GetType().FullName);
    }

    private static string KindText(HealingKind kind) => kind switch
    {
        HealingKind.QuickFix => "quick-fix",
        HealingKind.Patch => "patch",
        _ => "defer",
    };
}
