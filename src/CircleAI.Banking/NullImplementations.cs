// NullImplementations.cs — (2.8.0) Fail-closed banking defaults.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Banking;

public sealed class NullAccountReader : IAccountReader
{
    public static readonly NullAccountReader Instance = new();
    public string BackendId => "null";
    public ValueTask<Account?> GetAccountAsync(string id, CancellationToken ct = default) => ValueTask.FromResult<Account?>(null);
    public ValueTask<IReadOnlyList<Account>> ListForOwnerAsync(string owner, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Account>>(Array.Empty<Account>());
}

public sealed class NullLedgerWriter : ILedgerWriter
{
    public static readonly NullLedgerWriter Instance = new();
    public string BackendId => "null";
    public ValueTask<LedgerEntry> AppendAsync(LedgerEntry e, CancellationToken ct = default) => ValueTask.FromResult(e);
    public ValueTask<IReadOnlyList<LedgerEntry>> ReadAsync(string acc, int limit = 100, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<LedgerEntry>>(Array.Empty<LedgerEntry>());
}

public sealed class NullPaymentProcessor : IPaymentProcessor
{
    public static readonly NullPaymentProcessor Instance = new();
    public string BackendId => "null";
    public ValueTask<PaymentResult> ProcessAsync(PaymentRequest req, CancellationToken ct = default)
        => ValueTask.FromResult(new PaymentResult(Guid.Empty.ToString(), false, "NullPaymentProcessor."));
}
