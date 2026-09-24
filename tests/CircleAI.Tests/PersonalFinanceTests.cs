// PersonalFinanceTests.cs
//
// The money module, tested on arithmetic alone — no phone, no model.
//
// The rule under test is that every figure a person is told about their own
// money is computed in C# and is reproducible. A budget that is 8% wrong
// because somebody wrote "four weeks in a month" is not approximately right,
// it is a plan that cannot be kept. A payoff schedule that cannot exist must
// say so rather than return a number.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CircleAI.Memory.Consolidation;
using CircleAI.Personal.Finance;
using Xunit;

namespace CircleAI.Tests;

public class MoneyPlanTests
{
    // ---- normalising pay cycles ----------------------------------------------

    [Fact]
    public void Weekly_is_fifty_two_over_twelve_not_four_a_month()
    {
        // The "4 weeks in a month" shortcut loses a month's income a year.
        var monthly = MoneyPlan.ToMonthly(1_000m, PayCycle.Weekly);
        Assert.Equal(1_000m * 52m / 12m, monthly);
        Assert.True(monthly > 4_000m, "x4 would understate weekly pay by about 8%");
    }

    [Fact]
    public void Fortnightly_is_twenty_six_over_twelve()
        => Assert.Equal(2_000m * 26m / 12m, MoneyPlan.ToMonthly(2_000m, PayCycle.Fortnightly));

    [Fact]
    public void Monthly_income_sums_across_mixed_cycles()
    {
        var income = new[]
        {
            new IncomeSource("Wages", 1_000m, PayCycle.Weekly),
            new IncomeSource("Grant", 2_000m, PayCycle.Monthly),
        };
        Assert.Equal(1_000m * 52m / 12m + 2_000m, MoneyPlan.MonthlyIncome(income));
    }

    // ---- the month ------------------------------------------------------------

    [Fact]
    public void Surplus_is_reported_negative_rather_than_clamped()
    {
        // Being short is a real answer and the one that matters most.
        var picture = MoneyPlan.Picture(
            new[] { new IncomeSource("Wages", 5_000m) },
            new[] { 4_000m, 1_500m },
            new[] { new Debt("Card", 1_000m, 20m, 300m) });

        Assert.Equal(5_000m, picture.Income);
        Assert.Equal(5_500m, picture.FixedCosts);
        Assert.Equal(300m,   picture.DebtMinimums);
        Assert.Equal(-800m,  picture.Surplus);
    }

    [Fact]
    public void Runway_is_null_when_there_are_no_costs_to_survive()
        => Assert.Null(MoneyPlan.RunwayMonths(10_000m, 0m));

    [Fact]
    public void Runway_counts_months_of_fixed_costs()
        => Assert.Equal(3.5m, MoneyPlan.RunwayMonths(7_000m, 2_000m));

    // ---- ordering -------------------------------------------------------------

    [Fact]
    public void Avalanche_puts_the_most_expensive_rate_first()
    {
        var ordered = MoneyPlan.Avalanche(new[]
        {
            new Debt("Loan",  9_000m,  12m, 400m),
            new Debt("Store", 1_500m, 29.5m, 150m),
        });
        Assert.Equal("Store", ordered[0].Name);
    }

    [Fact]
    public void Snowball_puts_the_smallest_balance_first()
    {
        var ordered = MoneyPlan.Snowball(new[]
        {
            new Debt("Loan",  9_000m,  12m, 400m),
            new Debt("Store", 1_500m, 29.5m, 150m),
        });
        Assert.Equal("Store", ordered[0].Name);
    }

    // ---- the simulation -------------------------------------------------------

    [Fact]
    public void Interest_free_debt_clears_in_exactly_the_arithmetic_number_of_months()
    {
        var plan = MoneyPlan.Simulate(new[] { new Debt("Friend", 1_000m, 0m, 100m) }, 100m);

        Assert.False(plan.Impossible);
        Assert.Equal(10, plan.Months);
        Assert.Equal(0m, plan.TotalInterest);
    }

    [Fact]
    public void A_budget_below_the_minimums_is_reported_impossible_not_scheduled()
    {
        // The honest answer. No ordering fixes a shortfall this size.
        var plan = MoneyPlan.Simulate(new[]
        {
            new Debt("A", 5_000m, 20m, 500m),
            new Debt("B", 5_000m, 20m, 500m),
        }, monthlyBudget: 900m);

        Assert.True(plan.Impossible);
        Assert.Contains("minimum", plan.Why, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(plan.Steps);
    }

    [Fact]
    public void A_payment_the_interest_swallows_is_reported_impossible()
    {
        // R100 against R10,000 at 100% a year — interest alone is ~R833 a month.
        var plan = MoneyPlan.Simulate(new[] { new Debt("Shark", 10_000m, 100m, 100m) }, 100m);

        Assert.True(plan.Impossible);
        Assert.Contains("50 years", plan.Why);
    }

    [Fact]
    public void Interest_accrues_on_the_opening_balance()
    {
        // 12% a year = 1% a month. R1,000 opens, R10 interest, R110 paid.
        var plan = MoneyPlan.Simulate(new[] { new Debt("Card", 1_000m, 12m, 110m) }, 110m);
        var first = plan.Steps.First(s => s.MonthIndex == 1);

        Assert.Equal(10m,  first.InterestCharged);
        Assert.Equal(110m, first.Paid);
        Assert.Equal(900m, first.ClosingBalance);
    }

    [Fact]
    public void Avalanche_never_costs_more_interest_than_snowball()
    {
        var debts = new[]
        {
            new Debt("Store", 8_000m, 30m, 200m),   // expensive, large
            new Debt("Aunt",  2_000m,  5m, 100m),   // cheap, small
        };

        var (avalanche, snowball, saved) = MoneyPlan.Compare(debts, 600m);

        Assert.False(avalanche.Impossible);
        Assert.False(snowball.Impossible);
        Assert.True(avalanche.TotalInterest <= snowball.TotalInterest);
        Assert.True(saved >= 0m);
    }

    [Fact]
    public void Empty_debts_is_a_zero_month_plan_not_a_crash()
    {
        var plan = MoneyPlan.Simulate(Array.Empty<Debt>(), 500m);
        Assert.Equal(0, plan.Months);
        Assert.False(plan.Impossible);
    }

    // ---- affordability and goals ---------------------------------------------

    [Fact]
    public void Affordability_reports_what_is_left_not_just_yes()
    {
        var picture = MoneyPlan.Picture(
            new[] { new IncomeSource("Wages", 6_000m) },
            new[] { 4_000m },
            Array.Empty<Debt>());

        var (fits, after) = MoneyPlan.CanAfford(picture, 1_920m);

        Assert.True(fits);
        Assert.Equal(80m, after);   // "you can, and it leaves you R80" is the real answer
    }

    [Fact]
    public void Goal_with_no_contribution_is_null_not_infinity()
        => Assert.Null(MoneyPlan.MonthsToGoal(new SavingsGoal("Deposit", 10_000m, 0m), 0m));

    [Fact]
    public void Goal_already_met_is_zero_months()
        => Assert.Equal(0, MoneyPlan.MonthsToGoal(new SavingsGoal("Deposit", 1_000m, 1_200m), 100m));

    [Fact]
    public void Goal_rounds_up_because_a_part_month_does_not_pay_it()
        => Assert.Equal(4, MoneyPlan.MonthsToGoal(new SavingsGoal("Tyres", 1_000m, 0m), 300m));

    // ---- where it went --------------------------------------------------------

    [Fact]
    public void Spending_breakdown_ignores_income_and_orders_by_size()
    {
        var now = DateTimeOffset.UtcNow;
        var txns = new[]
        {
            new FinanceTransaction("1", "a",  9_000m, "Salary",    null, now),  // income, ignored
            new FinanceTransaction("2", "a", -3_000m, "Rent",      null, now),
            new FinanceTransaction("3", "a", -1_000m, "Transport", null, now),
        };

        var rows = MoneyPlan.WhereItWent(txns);

        Assert.Equal(2, rows.Count);
        Assert.Equal("Rent", rows[0].Category);
        Assert.Equal(3_000m, rows[0].Spent);
        Assert.Equal(75m, rows[0].Share);
        Assert.Equal(100m, rows.Sum(r => r.Share));
    }

    [Fact]
    public void Spending_breakdown_of_nothing_is_empty_not_a_divide_by_zero()
        => Assert.Empty(MoneyPlan.WhereItWent(Array.Empty<FinanceTransaction>()));
}

public class MoneyMemoryTests
{
    private static MoneyMemory New() => new(new InMemoryCoreMemoryStore());

    [Fact]
    public async Task A_fact_learned_twice_is_reinforced_not_duplicated()
    {
        var core = new InMemoryCoreMemoryStore();
        var mem  = new MoneyMemory(core);

        Assert.True(await mem.RememberAsync("Rent is ", "Rent is R4,500.00 a month."));
        Assert.False(await mem.RememberAsync("Rent is ", "Rent is R4,500.00 a month."));

        var held = await mem.RecallAsync();
        Assert.Single(held);
        Assert.True(held[0].ReinforcementCount >= 1);
    }

    [Fact]
    public async Task A_changed_value_replaces_the_old_sentence()
    {
        // Otherwise core ends up holding two rents and the assistant contradicts itself.
        var mem = New();
        await mem.RememberAsync("Rent is ", "Rent is R4,200.00 a month.");
        await mem.RememberAsync("Rent is ", "Rent is R4,500.00 a month.");

        var held = await mem.RecallAsync();
        Assert.Single(held);
        Assert.Contains("4,500", held[0].Statement);
    }

    [Fact]
    public async Task Debt_memory_keeps_the_rate_because_it_decides_the_order()
    {
        var mem = New();
        await mem.LearnDebtsAsync(new[] { new Debt("Store card", 3_000m, 24.5m, 250m) });

        var held = await mem.RecallAsync();
        Assert.Contains(held, m => m.Statement.Contains("24.5%"));
    }

    [Fact]
    public async Task Payday_is_remembered_in_words()
    {
        var mem = New();
        await mem.LearnIncomeAsync(new[] { new IncomeSource("Wages", 8_000m, PayCycle.Monthly, 25) });

        var held = await mem.RecallAsync();
        Assert.Contains(held, m => m.Statement.Contains("25th"));
    }

    [Fact]
    public async Task Small_costs_are_not_promoted_to_core()
    {
        // Core is a small tier; a R30 subscription is not a forever-fact.
        var mem = New();
        await mem.LearnFixedCostsAsync(new[]
        {
            new BudgetLine("Rent", 4_500m),
            new BudgetLine("Streaming", 99m),
        });

        var held = await mem.RecallAsync();
        Assert.Contains(held, m => m.Statement.StartsWith("Rent costs"));
        Assert.DoesNotContain(held, m => m.Statement.StartsWith("Streaming costs"));
    }

    [Fact]
    public async Task No_spending_claim_is_made_from_noise()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = New();

        // Three roughly equal categories - no category dominates.
        var claimed = await mem.LearnSpendingShapeAsync(new[]
        {
            new FinanceTransaction("1", "a", -100m, "Food",      null, now),
            new FinanceTransaction("2", "a", -100m, "Transport", null, now),
            new FinanceTransaction("3", "a", -100m, "Airtime",   null, now),
        });

        Assert.False(claimed);
        Assert.Empty(await mem.RecallAsync());
    }

    [Fact]
    public async Task A_dominant_category_is_inferred_not_asserted()
    {
        var now = DateTimeOffset.UtcNow;
        var mem = New();

        await mem.LearnSpendingShapeAsync(new[]
        {
            new FinanceTransaction("1", "a", -900m, "Transport", null, now),
            new FinanceTransaction("2", "a", -100m, "Food",      null, now),
        });

        var held = await mem.RecallAsync();
        Assert.Single(held);
        Assert.Equal(CoreMemoryKind.PatternInferred, held[0].Kind);
    }

    [Fact]
    public async Task Brief_is_empty_when_nothing_is_known()
        => Assert.Equal(string.Empty, await New().BriefAsync());
}

public class SqliteMoneyStoreTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"money-{Guid.NewGuid():N}.db");

    [Fact]
    public void Transactions_survive_reopening_which_is_the_whole_point()
    {
        var now = DateTimeOffset.UtcNow;
        using (var store = new SqliteMoneyStore(_path))
        {
            store.Upsert(new Account("main", "Cheque", 1_000m, "ZAR"));
            store.Record(new FinanceTransaction("t1", "main", -250m, "Food", null, now));
        }

        using var reopened = new SqliteMoneyStore(_path);
        Assert.Equal(750m, reopened.GetAccount("main")!.Balance);
        Assert.Single(reopened.ListForMonth("main", now.Year, now.Month));
    }

    [Fact]
    public void A_rate_survives_the_round_trip_exactly()
    {
        // 24.5 must not come back as 24,5 or 24.499999.
        using var store = new SqliteMoneyStore(_path);
        store.AddDebt(new Debt("Store card", 3_000m, 24.5m, 250m));

        Assert.Equal(24.5m, store.Debts().Single().AnnualRatePct);
    }

    [Fact]
    public void Cents_are_exact_across_many_small_amounts()
    {
        using var store = new SqliteMoneyStore(_path);
        store.Upsert(new Account("main", "Cheque", 0m, "ZAR"));
        for (var i = 0; i < 10; i++)
            store.Record(new FinanceTransaction($"t{i}", "main", -0.10m, "Airtime", null, DateTimeOffset.UtcNow));

        Assert.Equal(-1.00m, store.GetAccount("main")!.Balance);
    }

    [Fact]
    public void A_transaction_against_an_unknown_account_is_refused()
    {
        using var store = new SqliteMoneyStore(_path);
        Assert.Throws<InvalidOperationException>(() =>
            store.Record(new FinanceTransaction("t1", "nope", -10m, "Food", null, DateTimeOffset.UtcNow)));
    }

    [Fact]
    public void The_stored_month_feeds_the_arithmetic_directly()
    {
        using var store = new SqliteMoneyStore(_path);
        store.AddIncome(new IncomeSource("Wages", 6_000m));
        store.SetBudget(new BudgetLine("Rent", 4_000m));
        store.AddDebt(new Debt("Card", 2_000m, 20m, 300m));

        var picture = store.Picture();

        Assert.Equal(6_000m, picture.Income);
        Assert.Equal(4_000m, picture.FixedCosts);
        Assert.Equal(300m,   picture.DebtMinimums);
        Assert.Equal(1_700m, picture.Surplus);
    }

    public void Dispose()
    {
        try { if (File.Exists(_path)) File.Delete(_path); } catch { /* temp file */ }
        GC.SuppressFinalize(this);
    }
}
