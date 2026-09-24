#nullable enable

// SqliteMoneyStore.cs
//
// The money record, on this device, in a file the person owns.
//
// WHY THIS REPLACES THE IN-MEMORY BOARD. InMemoryPersonalFinanceBoard loses
// everything when the process dies. For money that is not a limitation, it is a
// disqualification: a budget that forgets last month cannot show a trend, and a
// debt plan that forgets its own schedule cannot tell you whether you are ahead
// or behind. The in-memory board stays — it is the right test double — and this
// is what ships.
//
// MONEY IS STORED AS INTEGER CENTS. SQLite has no decimal type, and REAL would
// quietly turn 0.1 + 0.2 into something that is not 0.3. Cents as INTEGER is
// exact, sorts correctly, and sums correctly. Interest RATES are stored as TEXT
// and parsed invariantly, because a rate is not money and 24.5 must survive a
// round trip on a device whose locale writes 24,5.
//
// NOTHING HERE OPENS A SOCKET. Income, debts and spending are among the most
// sensitive facts a person has — enough to profile someone completely. This file
// is local, and there is no sync. See SqliteCareerStore, which holds the same
// line for employment history.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace CircleAI.Personal.Finance;

/// <summary>
/// The on-device money record. Implements <see cref="IPersonalFinanceBoard"/>
/// so it substitutes directly for the in-memory board, and adds the income,
/// debt and goal tables that <see cref="MoneyPlan"/> computes against.
/// </summary>
public sealed class SqliteMoneyStore : IPersonalFinanceBoard, IDisposable
{
    private readonly SqliteConnection _db;
    private bool _disposed;

    /// <summary>Opens (and creates) the store at a path under private storage.</summary>
    public SqliteMoneyStore(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);

        var dir = System.IO.Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);

        _db = new SqliteConnection($"Data Source={databasePath}");
        _db.Open();
        CreateSchema();
    }

    private void CreateSchema() => Execute("""
        CREATE TABLE IF NOT EXISTS accounts (
            id        TEXT PRIMARY KEY,
            name      TEXT    NOT NULL,
            cents     INTEGER NOT NULL DEFAULT 0,
            currency  TEXT    NOT NULL DEFAULT 'ZAR'
        );

        -- Spending is negative, income positive, matching the in-memory board
        -- and how a bank statement reads.
        CREATE TABLE IF NOT EXISTS txns (
            id         TEXT PRIMARY KEY,
            account_id TEXT    NOT NULL,
            cents      INTEGER NOT NULL,
            category   TEXT    NOT NULL,
            note       TEXT,
            at_utc     TEXT    NOT NULL
        );
        CREATE INDEX IF NOT EXISTS ix_txns_account_time ON txns(account_id, at_utc);

        CREATE TABLE IF NOT EXISTS budgets (
            category    TEXT PRIMARY KEY,
            limit_cents INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS income (
            id           INTEGER PRIMARY KEY AUTOINCREMENT,
            name         TEXT    NOT NULL,
            cents        INTEGER NOT NULL,
            cycle        TEXT    NOT NULL,
            day_of_month INTEGER
        );

        CREATE TABLE IF NOT EXISTS debts (
            id             INTEGER PRIMARY KEY AUTOINCREMENT,
            name           TEXT    NOT NULL,
            balance_cents  INTEGER NOT NULL,
            rate_pct       TEXT    NOT NULL,
            min_cents      INTEGER NOT NULL
        );

        CREATE TABLE IF NOT EXISTS goals (
            id            INTEGER PRIMARY KEY AUTOINCREMENT,
            name          TEXT    NOT NULL,
            target_cents  INTEGER NOT NULL,
            saved_cents   INTEGER NOT NULL DEFAULT 0,
            by_date       TEXT
        );
        """);

    // ---- IPersonalFinanceBoard ------------------------------------------------

    /// <inheritdoc/>
    public void Upsert(Account a)
    {
        ArgumentNullException.ThrowIfNull(a);
        Execute("""
            INSERT INTO accounts (id, name, cents, currency) VALUES ($i, $n, $c, $u)
            ON CONFLICT(id) DO UPDATE SET name = $n, cents = $c, currency = $u;
            """,
            ("$i", a.AccountId), ("$n", a.Name), ("$c", Cents(a.Balance)), ("$u", a.Currency));
    }

    /// <inheritdoc/>
    public Account? GetAccount(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        using var r = Query("SELECT id, name, cents, currency FROM accounts WHERE id = $i;", ("$i", id));
        return r.Read()
            ? new Account(r.GetString(0), r.GetString(1), Money(r.GetInt64(2)), r.GetString(3))
            : null;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Rejects an unknown account rather than inventing one — a transaction
    /// against an account that does not exist is a bug upstream, and silently
    /// creating it would hide the bug behind a wrong balance.
    /// </remarks>
    public void Record(FinanceTransaction t)
    {
        ArgumentNullException.ThrowIfNull(t);
        if (GetAccount(t.AccountId) is null)
            throw new InvalidOperationException($"Unknown account {t.AccountId}");

        // The row and the balance move together or not at all — a recorded
        // transaction that failed to change the balance is a silently wrong
        // account, which is the one failure a money ledger must never have.
        using var tx = _db.BeginTransaction();
        _tx = tx;
        try
        {
            Execute("""
                INSERT INTO txns (id, account_id, cents, category, note, at_utc)
                VALUES ($i, $a, $c, $g, $n, $t);
                """,
                ("$i", t.TxId), ("$a", t.AccountId), ("$c", Cents(t.Amount)),
                ("$g", t.Category), ("$n", (object?)t.Note ?? DBNull.Value),
                ("$t", t.AtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture)));

            Execute("UPDATE accounts SET cents = cents + $c WHERE id = $a;",
                ("$c", Cents(t.Amount)), ("$a", t.AccountId));
            tx.Commit();
        }
        finally { _tx = null; }
    }

    /// <inheritdoc/>
    public IReadOnlyList<FinanceTransaction> ListForMonth(string accountId, int year, int month)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(accountId);
        var from = new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        var to   = from.AddMonths(1);

        var rows = new List<FinanceTransaction>();
        using var r = Query("""
            SELECT id, account_id, cents, category, note, at_utc FROM txns
            WHERE account_id = $a AND at_utc >= $f AND at_utc < $t
            ORDER BY at_utc;
            """,
            ("$a", accountId),
            ("$f", from.ToString("O", CultureInfo.InvariantCulture)),
            ("$t", to.ToString("O", CultureInfo.InvariantCulture)));

        while (r.Read())
        {
            rows.Add(new FinanceTransaction(
                r.GetString(0), r.GetString(1), Money(r.GetInt64(2)), r.GetString(3),
                r.IsDBNull(4) ? null : r.GetString(4),
                DateTimeOffset.Parse(r.GetString(5), CultureInfo.InvariantCulture,
                                     DateTimeStyles.RoundtripKind)));
        }
        return rows;
    }

    /// <inheritdoc/>
    public void SetBudget(BudgetLine b)
    {
        ArgumentNullException.ThrowIfNull(b);
        Execute("""
            INSERT INTO budgets (category, limit_cents) VALUES ($c, $l)
            ON CONFLICT(category) DO UPDATE SET limit_cents = $l;
            """, ("$c", b.Category), ("$l", Cents(b.MonthlyLimit)));
    }

    /// <inheritdoc/>
    public IReadOnlyList<BudgetLine> Budgets
    {
        get
        {
            var rows = new List<BudgetLine>();
            using var r = Query("SELECT category, limit_cents FROM budgets ORDER BY category;");
            while (r.Read()) rows.Add(new BudgetLine(r.GetString(0), Money(r.GetInt64(1))));
            return rows;
        }
    }

    /// <inheritdoc/>
    public MonthSummary Summarise(string accountId, int year, int month)
    {
        var rows  = ListForMonth(accountId, year, month);
        var byCat = rows.GroupBy(t => t.Category, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(g => g.Key, g => g.Sum(t => t.Amount), StringComparer.OrdinalIgnoreCase);
        var inSum  =  rows.Where(t => t.Amount > 0).Sum(t => t.Amount);
        var outSum = -rows.Where(t => t.Amount < 0).Sum(t => t.Amount);
        return new MonthSummary(year, month, inSum, outSum, byCat);
    }

    // ---- what the arithmetic needs -------------------------------------------

    /// <summary>Adds an income source and returns its id.</summary>
    public long AddIncome(IncomeSource s)
    {
        ArgumentNullException.ThrowIfNull(s);
        Execute("INSERT INTO income (name, cents, cycle, day_of_month) VALUES ($n, $c, $y, $d);",
            ("$n", s.Name), ("$c", Cents(s.Amount)), ("$y", s.Cycle.ToString()),
            ("$d", (object?)s.DayOfMonth ?? DBNull.Value));
        return LastId();
    }

    /// <summary>Every income source on record.</summary>
    public IReadOnlyList<IncomeSource> Income()
    {
        var rows = new List<IncomeSource>();
        using var r = Query("SELECT id, name, cents, cycle, day_of_month FROM income ORDER BY id;");
        while (r.Read())
        {
            rows.Add(new IncomeSource(
                r.GetString(1), Money(r.GetInt64(2)),
                Enum.Parse<PayCycle>(r.GetString(3)),
                r.IsDBNull(4) ? null : r.GetInt32(4),
                r.GetInt64(0)));
        }
        return rows;
    }

    /// <summary>Adds a debt and returns its id.</summary>
    public long AddDebt(Debt d)
    {
        ArgumentNullException.ThrowIfNull(d);
        Execute("INSERT INTO debts (name, balance_cents, rate_pct, min_cents) VALUES ($n, $b, $r, $m);",
            ("$n", d.Name), ("$b", Cents(d.Balance)),
            ("$r", d.AnnualRatePct.ToString(CultureInfo.InvariantCulture)), ("$m", Cents(d.MinPayment)));
        return LastId();
    }

    /// <summary>Every debt on record.</summary>
    public IReadOnlyList<Debt> Debts()
    {
        var rows = new List<Debt>();
        using var r = Query("SELECT id, name, balance_cents, rate_pct, min_cents FROM debts ORDER BY id;");
        while (r.Read())
        {
            rows.Add(new Debt(
                r.GetString(1), Money(r.GetInt64(2)),
                decimal.Parse(r.GetString(3), CultureInfo.InvariantCulture),
                Money(r.GetInt64(4)), r.GetInt64(0)));
        }
        return rows;
    }

    /// <summary>Updates a debt's balance — what a payment actually changes.</summary>
    public void SetDebtBalance(long id, decimal balance)
        => Execute("UPDATE debts SET balance_cents = $b WHERE id = $i;", ("$b", Cents(balance)), ("$i", id));

    /// <summary>Adds a savings goal and returns its id.</summary>
    public long AddGoal(SavingsGoal g)
    {
        ArgumentNullException.ThrowIfNull(g);
        Execute("INSERT INTO goals (name, target_cents, saved_cents, by_date) VALUES ($n, $t, $s, $d);",
            ("$n", g.Name), ("$t", Cents(g.Target)), ("$s", Cents(g.Saved)),
            ("$d", (object?)g.By?.ToString("yyyy-MM-dd") ?? DBNull.Value));
        return LastId();
    }

    /// <summary>Every savings goal on record.</summary>
    public IReadOnlyList<SavingsGoal> Goals()
    {
        var rows = new List<SavingsGoal>();
        using var r = Query("SELECT id, name, target_cents, saved_cents, by_date FROM goals ORDER BY id;");
        while (r.Read())
        {
            rows.Add(new SavingsGoal(
                r.GetString(1), Money(r.GetInt64(2)), Money(r.GetInt64(3)),
                r.IsDBNull(4) ? null : DateOnly.ParseExact(r.GetString(4), "yyyy-MM-dd", CultureInfo.InvariantCulture),
                r.GetInt64(0)));
        }
        return rows;
    }

    /// <summary>
    /// The whole month as the arithmetic sees it, from what is stored — the one
    /// call a screen needs before it can say anything true.
    /// </summary>
    public MonthPicture Picture()
        => MoneyPlan.Picture(Income(), Budgets.Select(b => b.MonthlyLimit), Debts());

    // ---- plumbing -------------------------------------------------------------

    private static long Cents(decimal amount) => (long)decimal.Round(amount * 100m, 0, MidpointRounding.AwayFromZero);
    private static decimal Money(long cents)  => cents / 100m;

    private long LastId()
    {
        using var r = Query("SELECT last_insert_rowid();");
        return r.Read() ? r.GetInt64(0) : 0;
    }

    // Microsoft.Data.Sqlite REFUSES to execute a command while a transaction is
    // pending on the connection unless the command is told about it. Every
    // command therefore goes through here and inherits the open transaction, if
    // any — otherwise Record() throws the moment it tries its second statement.
    private SqliteTransaction? _tx;

    private void Execute(string sql, params (string Name, object? Value)[] args)
    {
        using var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    private SqliteDataReader Query(string sql, params (string Name, object? Value)[] args)
    {
        var cmd = _db.CreateCommand();
        cmd.Transaction = _tx;
        cmd.CommandText = sql;
        foreach (var (n, v) in args) cmd.Parameters.AddWithValue(n, v ?? DBNull.Value);
        return cmd.ExecuteReader();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _db.Dispose();
    }
}
