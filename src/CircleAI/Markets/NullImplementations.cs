// NullImplementations.cs — (2.8.0) Fail-closed markets defaults.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Markets;

public sealed class NullMarketDataFeed : IMarketDataFeed
{
    public static readonly NullMarketDataFeed Instance = new();
    public string BackendId => "null";
    public ValueTask<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default) => ValueTask.FromResult<Quote?>(null);
    public IDisposable SubscribeQuotes(string symbol, Func<Quote, ValueTask> h) => EmptyDisposable.Instance;

    private sealed class EmptyDisposable : IDisposable
    {
        public static readonly EmptyDisposable Instance = new();
        public void Dispose() { }
    }
}

public sealed class NullInstrumentCatalog : IInstrumentCatalog
{
    public static readonly NullInstrumentCatalog Instance = new();
    public string BackendId => "null";
    public ValueTask<Instrument?> GetAsync(string symbol, CancellationToken ct = default) => ValueTask.FromResult<Instrument?>(null);
    public ValueTask<IReadOnlyList<Instrument>> SearchAsync(string q, int topK = 20, CancellationToken ct = default)
        => ValueTask.FromResult<IReadOnlyList<Instrument>>(Array.Empty<Instrument>());
}

public sealed class NullOrderRouter : IOrderRouter
{
    public static readonly NullOrderRouter Instance = new();
    public string BackendId => "null";
    public ValueTask<OrderResult> SubmitAsync(OrderRequest req, CancellationToken ct = default)
        => ValueTask.FromResult(new OrderResult(Guid.Empty.ToString(), false, "NullOrderRouter — fail-closed."));
}
