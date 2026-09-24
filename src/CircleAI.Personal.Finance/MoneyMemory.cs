#nullable enable

// MoneyMemory.cs
//
// How the money module gets to KNOW somebody, rather than meeting them again
// every session.
//
// THE POINT. A budget app that asks "what is your rent?" every time is not
// helping; it is interrogating. The facts that make money advice useful are
// stable for months — what you earn, when you are paid, what you must pay,
// what you owe and at what rate. Those belong in core memory: the tier the
// assistant does not forget. Then "can I afford this?" can be answered without
// asking five questions first, because the answer is already known.
//
// WHY CORE AND NOT A PRIVATE TABLE. The facts are already in SqliteMoneyStore,
// and that is where the AUTHORITATIVE numbers live. Core memory is not a second
// copy of the ledger — it is the handful of sentences the assistant needs in
// its head to talk like it knows you, and which surface in recall alongside
// everything else it knows. Keeping them in the module's own table would mean
// the assistant is fluent about your rent in the money screen and blank about
// it everywhere else. That split is exactly the "built but unreachable" defect
// this repo keeps producing.
//
// WHAT IS DELIBERATELY NOT REMEMBERED. Individual transactions. Card numbers.
// Account numbers. Balances that move daily. Core memory is for what is TRUE
// FOR MONTHS, and every extra sentence dilutes the tier. A person's spending
// on a given Tuesday is in the ledger where it belongs.
//
// REINFORCE, DO NOT DUPLICATE. Re-learning the same fact bumps the existing
// memory instead of adding a second sentence saying the same thing; a changed
// value replaces the old sentence. Without this, core fills with "rent is
// R4,200" / "rent is R4,500" / "rent is R4,500" and the assistant starts
// contradicting itself.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CircleAI.Memory.Consolidation;

namespace CircleAI.Personal.Finance;

/// <summary>
/// Promotes the durable facts of somebody's money life into core memory, and
/// reads them back for grounding. Owns no storage of its own.
/// </summary>
public sealed class MoneyMemory
{
    /// <summary>Topic label every memory written here carries.</summary>
    public const string Topic = "money";

    private readonly ICoreMemoryStore _core;

    public MoneyMemory(ICoreMemoryStore core)
        => _core = core ?? throw new ArgumentNullException(nameof(core));

    /// <summary>
    /// Writes a fact, or updates the one already held for the same
    /// <paramref name="lead"/>.
    /// </summary>
    /// <param name="lead">
    /// The stable opening of the sentence ("Rent is ", "Payday is "). This is
    /// the identity of the fact — it is how a later value finds and replaces an
    /// earlier one. It must not contain the value.
    /// </param>
    /// <param name="statement">The full sentence, third person, as core stores it.</param>
    /// <param name="kind">Why it is in core — asserted by the person, or inferred.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>True when something was written or changed; false when it was already known.</returns>
    public async Task<bool> RememberAsync(
        string lead, string statement, CoreMemoryKind kind = CoreMemoryKind.UserAsserted,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lead);
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);

        var existing = (await _core.ListAllAsync(ct).ConfigureAwait(false))
            .FirstOrDefault(m => string.Equals(m.Topic, Topic, StringComparison.OrdinalIgnoreCase)
                              && m.Statement.StartsWith(lead, StringComparison.OrdinalIgnoreCase));

        if (existing is not null)
        {
            // Unchanged: reinforce. This is what makes a long-standing fact
            // outrank a passing one when core is ranked for display.
            if (string.Equals(existing.Statement, statement, StringComparison.Ordinal))
            {
                await _core.ReinforceAsync(existing.Id, ct).ConfigureAwait(false);
                return false;
            }

            // Changed: the old sentence is now false and must not survive.
            await _core.RemoveAsync(existing.Id, ct).ConfigureAwait(false);
        }

        await _core.AddAsync(new CoreMemory
        {
            Statement = statement,
            Kind      = kind,
            Topic     = Topic,
        }, ct).ConfigureAwait(false);

        return true;
    }

    /// <summary>Everything core currently knows about this person's money.</summary>
    public async Task<IReadOnlyList<CoreMemory>> RecallAsync(CancellationToken ct = default)
        => (await _core.ListAllAsync(ct).ConfigureAwait(false))
            .Where(m => string.Equals(m.Topic, Topic, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(m => m.ReinforcementCount)
            .ToArray();

    /// <summary>
    /// Learns what is durable about how somebody is paid. The pay DAY matters as
    /// much as the amount — "can this wait until Friday" is unanswerable without
    /// it, and it is the question people actually ask.
    /// </summary>
    public async Task<int> LearnIncomeAsync(IEnumerable<IncomeSource> sources, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var list = sources.ToArray();
        if (list.Length == 0) return 0;

        var learned = 0;
        var monthly = MoneyPlan.MonthlyIncome(list);

        if (await RememberAsync("Monthly income is ",
                $"Monthly income is about {Money(monthly)}.", CoreMemoryKind.UserAsserted, ct)
                .ConfigureAwait(false)) learned++;

        foreach (var s in list.Where(s => s.DayOfMonth is >= 1 and <= 31))
        {
            if (await RememberAsync($"Payday for {s.Name} is ",
                    $"Payday for {s.Name} is the {Ordinal(s.DayOfMonth!.Value)} of the month.",
                    CoreMemoryKind.UserAsserted, ct).ConfigureAwait(false)) learned++;
        }

        return learned;
    }

    /// <summary>
    /// Learns the fixed costs that define what is actually free to spend.
    /// Only costs at or above <paramref name="floor"/> are promoted — core is a
    /// small tier and a R30 subscription is not a fact worth never forgetting.
    /// </summary>
    public async Task<int> LearnFixedCostsAsync(
        IEnumerable<BudgetLine> lines, decimal floor = 200m, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(lines);
        var learned = 0;
        foreach (var l in lines.Where(l => l.MonthlyLimit >= floor).OrderByDescending(l => l.MonthlyLimit))
        {
            if (await RememberAsync($"{l.Category} costs ",
                    $"{l.Category} costs about {Money(l.MonthlyLimit)} a month.",
                    CoreMemoryKind.UserAsserted, ct).ConfigureAwait(false)) learned++;
        }
        return learned;
    }

    /// <summary>
    /// Learns what is owed. The RATE is remembered deliberately: it is the fact
    /// that decides which debt to attack, and it is the one people least often
    /// have to hand.
    /// </summary>
    public async Task<int> LearnDebtsAsync(IEnumerable<Debt> debts, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(debts);
        var learned = 0;
        foreach (var d in debts)
        {
            if (await RememberAsync($"Owes on {d.Name}: ",
                    $"Owes on {d.Name}: {Money(d.Balance)} at {Rate(d.AnnualRatePct)}% a year, " +
                    $"minimum {Money(d.MinPayment)} a month.",
                    CoreMemoryKind.UserAsserted, ct).ConfigureAwait(false)) learned++;
        }
        return learned;
    }

    /// <summary>
    /// Learns a spending SHAPE from the ledger — not transactions, the pattern.
    /// Inferred rather than asserted, because nobody said it out loud; it was
    /// observed. Only the leading category, and only when it is genuinely
    /// dominant, so the assistant never claims a pattern out of noise.
    /// </summary>
    /// <param name="transactions">The ledger to read the shape from.</param>
    /// <param name="minShare">Percentage share below which no claim is made.</param>
    /// <param name="ct">Cancellation token.</param>
    public async Task<bool> LearnSpendingShapeAsync(
        IEnumerable<FinanceTransaction> transactions, decimal minShare = 25m,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var breakdown = MoneyPlan.WhereItWent(transactions);
        if (breakdown.Count == 0) return false;

        var top = breakdown[0];
        if (top.Share < minShare) return false;

        // "Most" has to mean clearly ahead of the runner-up. Three categories at
        // 33% each have no "most" — naming one would be inventing a pattern out
        // of an even spread, and an assistant that does that once is not trusted
        // about the times it is right.
        if (breakdown.Count > 1 && top.Spent < breakdown[1].Spent * 1.5m) return false;

        return await RememberAsync("Most spending goes on ",
            $"Most spending goes on {top.Category} — about {Rate(top.Share)}% of what goes out.",
            CoreMemoryKind.PatternInferred, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The one-paragraph brief the assistant can be given before it answers a
    /// money question, so it speaks from what is known instead of asking again.
    /// Empty when nothing is known yet — an empty brief is better than a
    /// confident one built from nothing.
    /// </summary>
    public async Task<string> BriefAsync(CancellationToken ct = default)
    {
        var known = await RecallAsync(ct).ConfigureAwait(false);
        return known.Count == 0
            ? string.Empty
            : string.Join(" ", known.Select(m => m.Statement));
    }

    // Statements go into core memory and are read back by the assistant, so they
    // must not change shape with the device's locale — on a comma-decimal phone
    // an interest rate would otherwise be stored as "24,5%" and never match the
    // "24.5%" written on the statement the person is holding.
    private static string Money(decimal v) => "R" + v.ToString("N2", CultureInfo.InvariantCulture);

    private static string Rate(decimal v) => v.ToString("0.##", CultureInfo.InvariantCulture);

    private static string Ordinal(int day) => day switch
    {
        1 or 21 or 31 => $"{day}st",
        2 or 22       => $"{day}nd",
        3 or 23       => $"{day}rd",
        _             => $"{day}th",
    };
}
