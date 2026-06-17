// Contracts.cs
//
// (2.8.0) Banking contracts. Real backends 2.8.1.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Banking;

public sealed record Account(string AccountId, string OwnerId, string Currency, decimal Balance);
public sealed record LedgerEntry(string TxId, string AccountId, decimal Amount, string Memo, DateTimeOffset AtUtc);
public sealed record PaymentRequest(string FromAccount, string ToAccount, decimal Amount, string Currency, string Memo);
public sealed record PaymentResult(string TxId, bool Accepted, string? FailureReason);

public interface IAccountReader
{
    string BackendId { get; }
    ValueTask<Account?> GetAccountAsync(string accountId, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Account>> ListForOwnerAsync(string ownerId, CancellationToken ct = default);
}

public interface ILedgerWriter
{
    string BackendId { get; }
    ValueTask<LedgerEntry> AppendAsync(LedgerEntry entry, CancellationToken ct = default);
    ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(string accountId, int limit = 100, CancellationToken ct = default);
}

public interface IPaymentProcessor
{
    string BackendId { get; }
    ValueTask<PaymentResult> ProcessAsync(PaymentRequest req, CancellationToken ct = default);
}
