#nullable enable

// MoneyPlan.cs
//
// The arithmetic. Every number a person is told about their own money is
// computed HERE, in C#, and never by the language model.
//
// WHY THAT IS A HARD RULE. The on-device brain is a 0.6B. It does not reliably
// do arithmetic and it does not emit tool calls (see the ArithmeticIntent work
// and circleai-06b-wont-toolcall). A model that invents "you'll be debt free in
// 14 months" is not a smaller version of being right — for somebody deciding
// whether to take another loan it is worse than silence. The model's job is to
// EXPLAIN these numbers in the person's language. It never produces them.
//
// SOUTH AFRICAN CONTEXT. Debt here is usually several small accounts at very
// different rates (store card, personal loan, vehicle), and the question that
// actually matters is "which one do I kill first, and what does that save me".
// Avalanche answers it in rands; snowball answers it in morale. We compute both
// and let the person choose, because telling somebody the mathematically optimal
// order is useless if they abandon it in month three.
//
// ON MONEY TYPES: decimal throughout, never double. Interest is computed on the
// balance at the start of each month — simple, explainable, and matching how a
// person reads their own statement.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace CircleAI.Personal.Finance;

/// <summary>How often money arrives. Everything is normalised to a month.</summary>
public enum PayCycle
{
    /// <summary>Every week — 52 a year, not 4 a month.</summary>
    Weekly,
    /// <summary>Every two weeks — 26 a year.</summary>
    Fortnightly,
    /// <summary>Once a month.</summary>
    Monthly,
}

/// <summary>Money that arrives regularly.</summary>
/// <param name="Name">"Wages", "SASSA grant", "piece work".</param>
/// <param name="Amount">Per <paramref name="Cycle"/>, not per month.</param>
/// <param name="Cycle">How often it arrives; normalised by <see cref="MoneyPlan.ToMonthly"/>.</param>
/// <param name="DayOfMonth">Pay day where it is known — the 25th, the last Friday.</param>
/// <param name="Id">Store row id; 0 until persisted.</param>
public sealed record IncomeSource(
    string    Name,
    decimal   Amount,
    PayCycle  Cycle       = PayCycle.Monthly,
    int?      DayOfMonth  = null,
    long      Id          = 0);

/// <summary>
/// Something owed. <paramref name="AnnualRatePct"/> is the nominal annual rate
/// as written on the statement (e.g. 24.5 for 24.5%), not a fraction.
/// </summary>
public sealed record Debt(
    string   Name,
    decimal  Balance,
    decimal  AnnualRatePct,
    decimal  MinPayment,
    long     Id = 0);

/// <summary>Something being saved for.</summary>
public sealed record SavingsGoal(
    string          Name,
    decimal         Target,
    decimal         Saved,
    DateOnly?       By   = null,
    long            Id   = 0);

/// <summary>One month of a payoff simulation, for a single debt.</summary>
public sealed record PayoffStep(
    int      MonthIndex,
    string   DebtName,
    decimal  InterestCharged,
    decimal  Paid,
    decimal  ClosingBalance);

/// <summary>
/// The result of simulating a payoff order to completion.
/// <paramref name="Impossible"/> is the honest answer when the money available
/// does not cover the interest — the balance grows no matter what, and no
/// schedule exists. Saying so is the whole point.
/// </summary>
public sealed record PayoffPlan(
    string                     Strategy,
    int                        Months,
    decimal                    TotalInterest,
    IReadOnlyList<PayoffStep>  Steps,
    bool                       Impossible = false,
    string?                    Why        = null);

/// <summary>What a month looks like once everything is counted.</summary>
public sealed record MonthPicture(
    decimal Income,
    decimal FixedCosts,
    decimal DebtMinimums,
    decimal Surplus);

/// <summary>
/// Deterministic personal-money arithmetic. Pure functions — no storage, no
/// model, no clock beyond what is passed in, so every number is reproducible
/// and testable.
/// </summary>
public static class MoneyPlan
{
    /// <summary>Hard ceiling on simulation length so a pathological input cannot spin forever.</summary>
    public const int MaxMonths = 600;   // 50 years

    /// <summary>Normalises any pay cycle to a monthly figure.</summary>
    /// <remarks>
    /// Weekly is x52/12, NOT x4. The "four weeks in a month" shortcut loses a
    /// month's income a year — about 8% — which is exactly the size of error
    /// that makes a budget quietly impossible to keep.
    /// </remarks>
    public static decimal ToMonthly(decimal amount, PayCycle cycle) => cycle switch
    {
        PayCycle.Weekly      => amount * 52m / 12m,
        PayCycle.Fortnightly => amount * 26m / 12m,
        PayCycle.Monthly     => amount,
        _ => throw new ArgumentOutOfRangeException(nameof(cycle)),
    };

    /// <summary>Total monthly income across every source.</summary>
    public static decimal MonthlyIncome(IEnumerable<IncomeSource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        return sources.Sum(s => ToMonthly(s.Amount, s.Cycle));
    }

    /// <summary>
    /// What is actually left after the things that must be paid. Surplus may be
    /// negative; that is a real and common answer and is reported, not clamped.
    /// </summary>
    public static MonthPicture Picture(
        IEnumerable<IncomeSource> income,
        IEnumerable<decimal>      fixedCosts,
        IEnumerable<Debt>         debts)
    {
        ArgumentNullException.ThrowIfNull(income);
        ArgumentNullException.ThrowIfNull(fixedCosts);
        ArgumentNullException.ThrowIfNull(debts);

        var inc   = MonthlyIncome(income);
        var fixd  = fixedCosts.Sum();
        var mins  = debts.Sum(d => d.MinPayment);
        return new MonthPicture(inc, fixd, mins, inc - fixd - mins);
    }

    /// <summary>
    /// How many months of fixed costs the savings cover if income stopped today.
    /// Returns null when there are no costs to survive — dividing by zero months
    /// of runway is not "infinite", it is "not a meaningful question".
    /// </summary>
    public static decimal? RunwayMonths(decimal savings, decimal monthlyFixedCosts)
        => monthlyFixedCosts <= 0m ? null : decimal.Round(savings / monthlyFixedCosts, 1);

    /// <summary>
    /// Orders debts highest-rate-first. Costs the least in rands.
    /// </summary>
    public static IReadOnlyList<Debt> Avalanche(IEnumerable<Debt> debts)
        => debts?.OrderByDescending(d => d.AnnualRatePct).ThenBy(d => d.Balance).ToArray()
           ?? throw new ArgumentNullException(nameof(debts));

    /// <summary>
    /// Orders debts smallest-balance-first. Costs more, but closes an account
    /// sooner — which is what keeps people going.
    /// </summary>
    public static IReadOnlyList<Debt> Snowball(IEnumerable<Debt> debts)
        => debts?.OrderBy(d => d.Balance).ThenByDescending(d => d.AnnualRatePct).ToArray()
           ?? throw new ArgumentNullException(nameof(debts));

    /// <summary>
    /// Simulates paying <paramref name="ordered"/> to zero, month by month, with
    /// <paramref name="monthlyBudget"/> total available for all debt payments.
    /// Every debt receives its minimum; whatever is left attacks the first debt
    /// in the order, and rolls onto the next as each closes.
    /// </summary>
    /// <param name="ordered">Debts in the order they will be attacked.</param>
    /// <param name="monthlyBudget">Total available each month for all debt payments.</param>
    /// <param name="strategy">Label carried into the result ("avalanche"/"snowball").</param>
    public static PayoffPlan Simulate(
        IReadOnlyList<Debt> ordered,
        decimal             monthlyBudget,
        string              strategy = "avalanche")
    {
        ArgumentNullException.ThrowIfNull(ordered);

        if (ordered.Count == 0)
            return new PayoffPlan(strategy, 0, 0m, Array.Empty<PayoffStep>());

        var totalMin = ordered.Sum(d => d.MinPayment);
        if (monthlyBudget < totalMin)
        {
            return new PayoffPlan(strategy, 0, 0m, Array.Empty<PayoffStep>(), Impossible: true,
                Why: $"The minimum payments come to {totalMin.ToString("N2", CultureInfo.InvariantCulture)} a month " +
                     $"and the budget is {monthlyBudget.ToString("N2", CultureInfo.InvariantCulture)}. " +
                     "No order of payment fixes a shortfall this size — the balances grow whatever you do.");
        }

        // Working balances, in the given order.
        var names    = ordered.Select(d => d.Name).ToArray();
        var balances = ordered.Select(d => d.Balance).ToArray();
        var rates    = ordered.Select(d => d.AnnualRatePct / 100m / 12m).ToArray();
        var minimums = ordered.Select(d => d.MinPayment).ToArray();

        var steps         = new List<PayoffStep>();
        decimal totalInt  = 0m;
        int month         = 0;

        while (balances.Any(b => b > 0m))
        {
            if (++month > MaxMonths)
            {
                return new PayoffPlan(strategy, month, totalInt, steps, Impossible: true,
                    Why: "At this payment the debt does not clear within 50 years — the interest is " +
                         "consuming almost everything paid. This needs a lower rate or more money, not a better order.");
            }

            // 1. Interest first, on the opening balance — how a statement reads.
            var interestThisMonth = new decimal[balances.Length];
            for (var i = 0; i < balances.Length; i++)
            {
                if (balances[i] <= 0m) continue;
                interestThisMonth[i] = decimal.Round(balances[i] * rates[i], 2);
                balances[i]         += interestThisMonth[i];
                totalInt            += interestThisMonth[i];
            }

            // 2. Minimums on every live debt, capped at the balance.
            var budget = monthlyBudget;
            var paid   = new decimal[balances.Length];
            for (var i = 0; i < balances.Length; i++)
            {
                if (balances[i] <= 0m) continue;
                var pay      = Math.Min(Math.Min(minimums[i], balances[i]), budget);
                paid[i]      = pay;
                balances[i] -= pay;
                budget      -= pay;
            }

            // 3. Everything left attacks the earliest unpaid debt in the order.
            for (var i = 0; i < balances.Length && budget > 0m; i++)
            {
                if (balances[i] <= 0m) continue;
                var extra    = Math.Min(balances[i], budget);
                paid[i]     += extra;
                balances[i] -= extra;
                budget      -= extra;
            }

            for (var i = 0; i < balances.Length; i++)
            {
                if (paid[i] <= 0m && interestThisMonth[i] <= 0m) continue;
                steps.Add(new PayoffStep(month, names[i], interestThisMonth[i], paid[i],
                                         decimal.Round(balances[i], 2)));
            }
        }

        return new PayoffPlan(strategy, month, decimal.Round(totalInt, 2), steps);
    }

    /// <summary>
    /// Compares both orders on the same budget. The rand difference between them
    /// is the number worth telling somebody — it turns "avalanche is optimal"
    /// into "doing it this way keeps R2,300 that would otherwise be interest".
    /// </summary>
    public static (PayoffPlan Avalanche, PayoffPlan Snowball, decimal InterestSaved) Compare(
        IEnumerable<Debt> debts, decimal monthlyBudget)
    {
        ArgumentNullException.ThrowIfNull(debts);
        var list = debts.ToArray();
        var a = Simulate(Avalanche(list), monthlyBudget, "avalanche");
        var s = Simulate(Snowball(list),  monthlyBudget, "snowball");
        var saved = (a.Impossible || s.Impossible) ? 0m : decimal.Round(s.TotalInterest - a.TotalInterest, 2);
        return (a, s, saved);
    }

    /// <summary>
    /// Whether a new commitment fits, and what it leaves behind. Deliberately
    /// returns the remaining surplus rather than a yes/no — "you can, and it
    /// leaves you R80 a month" is a different decision from "you can".
    /// </summary>
    public static (bool Fits, decimal SurplusAfter) CanAfford(MonthPicture picture, decimal newMonthlyCost)
    {
        var after = picture.Surplus - newMonthlyCost;
        return (after >= 0m, decimal.Round(after, 2));
    }

    /// <summary>
    /// Months to reach a savings goal at a given monthly contribution, or null
    /// when the contribution is zero or less — never a divide-by-zero, never
    /// "infinity" dressed up as a number.
    /// </summary>
    public static int? MonthsToGoal(SavingsGoal goal, decimal monthlyContribution)
    {
        ArgumentNullException.ThrowIfNull(goal);
        var remaining = goal.Target - goal.Saved;
        if (remaining <= 0m) return 0;
        if (monthlyContribution <= 0m) return null;
        return (int)Math.Ceiling(remaining / monthlyContribution);
    }

    /// <summary>
    /// Where the money actually went, biggest first, with each category's share.
    /// Spending is stored as negative amounts; this reports positives.
    /// </summary>
    public static IReadOnlyList<(string Category, decimal Spent, decimal Share)> WhereItWent(
        IEnumerable<FinanceTransaction> transactions)
    {
        ArgumentNullException.ThrowIfNull(transactions);
        var outgoing = transactions.Where(t => t.Amount < 0m).ToArray();
        var total    = outgoing.Sum(t => -t.Amount);
        if (total <= 0m) return Array.Empty<(string, decimal, decimal)>();

        return outgoing
            .GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Category: g.Key,
                          Spent:    decimal.Round(g.Sum(t => -t.Amount), 2),
                          Share:    decimal.Round(g.Sum(t => -t.Amount) / total * 100m, 1)))
            .OrderByDescending(r => r.Spent)
            .ToArray();
    }
}
