// InMemoryBanking.cs
//
// (3.3.0) Real in-memory banking primitives: account store, ledger
// writer, payment processor with balance checks + double-entry
// bookkeeping (debit source, credit destination). Hosts that need
// durability swap in a database-backed implementation behind the same
// contract.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Banking;

/// <summary>(3.3.0) Concurrent in-memory bank shared by reader/ledger/payment.</summary>
public sealed class InMemoryBank
{
    private readonly ConcurrentDictionary<string, Account> _accounts = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, List<LedgerEntry>> _ledger = new(StringComparer.Ordinal);
    private readonly object _txLock = new();

    public void SeedAccount(Account account)
    {
        ArgumentNullException.ThrowIfNull(account);
        _accounts[account.AccountId] = account;
    }

    public Account? Get(string id) => _accounts.GetValueOrDefault(id);

    public IReadOnlyList<Account> ListForOwner(string ownerId)
        => _accounts.Values.Where(a => a.OwnerId == ownerId).ToArray();

    public LedgerEntry Append(LedgerEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        lock (_txLock)
        {
            if (!_accounts.TryGetValue(entry.AccountId, out var acct))
                throw new InvalidOperationException($"Unknown account {entry.AccountId}");

            _accounts[entry.AccountId] = acct with { Balance = acct.Balance + entry.Amount };
            var list = _ledger.GetOrAdd(entry.AccountId, _ => new List<LedgerEntry>());
            list.Add(entry);
            return entry;
        }
    }

    public IReadOnlyList<LedgerEntry> Read(string accountId, int limit)
    {
        if (!_ledger.TryGetValue(accountId, out var list)) return Array.Empty<LedgerEntry>();
        lock (_txLock)
        {
            return list.OrderByDescending(e => e.AtUtc).Take(limit).ToArray();
        }
    }

    public PaymentResult ProcessPayment(PaymentRequest req)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Amount <= 0) return new PaymentResult(Guid.NewGuid().ToString("n"), false, "Amount must be positive");
        lock (_txLock)
        {
            if (!_accounts.TryGetValue(req.FromAccount, out var src))
                return new PaymentResult(Guid.NewGuid().ToString("n"), false, "Unknown source account");
            if (!_accounts.TryGetValue(req.ToAccount, out var dst))
                return new PaymentResult(Guid.NewGuid().ToString("n"), false, "Unknown destination account");
            if (!string.Equals(src.Currency, req.Currency, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(dst.Currency, req.Currency, StringComparison.OrdinalIgnoreCase))
                return new PaymentResult(Guid.NewGuid().ToString("n"), false, "Currency mismatch");
            if (src.Balance < req.Amount)
                return new PaymentResult(Guid.NewGuid().ToString("n"), false, "Insufficient funds");

            var txId = Guid.NewGuid().ToString("n");
            var now  = DateTimeOffset.UtcNow;
            Append(new LedgerEntry(txId, req.FromAccount, -req.Amount, $"To {req.ToAccount}: {req.Memo}", now));
            Append(new LedgerEntry(txId, req.ToAccount,    req.Amount, $"From {req.FromAccount}: {req.Memo}", now));
            return new PaymentResult(txId, true, null);
        }
    }
}

public sealed class InMemoryAccountReader : IAccountReader
{
    private readonly InMemoryBank _bank;
    public InMemoryAccountReader(InMemoryBank bank) => _bank = bank ?? throw new ArgumentNullException(nameof(bank));
    public string BackendId => "in-memory";
    public ValueTask<Account?> GetAccountAsync(string id, CancellationToken ct = default) => ValueTask.FromResult(_bank.Get(id));
    public ValueTask<IReadOnlyList<Account>> ListForOwnerAsync(string owner, CancellationToken ct = default)
        => ValueTask.FromResult(_bank.ListForOwner(owner));
}

public sealed class InMemoryLedgerWriter : ILedgerWriter
{
    private readonly InMemoryBank _bank;
    public InMemoryLedgerWriter(InMemoryBank bank) => _bank = bank ?? throw new ArgumentNullException(nameof(bank));
    public string BackendId => "in-memory";
    public ValueTask<LedgerEntry> AppendAsync(LedgerEntry e, CancellationToken ct = default) => ValueTask.FromResult(_bank.Append(e));
    public ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(string acc, int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult(_bank.Read(acc, limit));
}

public sealed class InMemoryPaymentProcessor : IPaymentProcessor
{
    private readonly InMemoryBank _bank;
    public InMemoryPaymentProcessor(InMemoryBank bank) => _bank = bank ?? throw new ArgumentNullException(nameof(bank));
    public string BackendId => "in-memory";
    public ValueTask<PaymentResult> ProcessAsync(PaymentRequest req, CancellationToken ct = default)
        => ValueTask.FromResult(_bank.ProcessPayment(req));
}
