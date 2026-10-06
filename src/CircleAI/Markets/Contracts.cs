// Contracts.cs — (2.8.0) Markets contracts.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Markets;

public enum OrderSide  { Buy, Sell }
public enum OrderType  { Market, Limit }

public sealed record Instrument(string Symbol, string Exchange, string Currency, string AssetClass);
public sealed record Quote(string Symbol, decimal Bid, decimal Ask, decimal Last, DateTimeOffset AtUtc);
public sealed record OrderRequest(string Symbol, OrderSide Side, OrderType Type, decimal Quantity, decimal? LimitPrice);
public sealed record OrderResult(string OrderId, bool Accepted, string? FailureReason);

public interface IMarketDataFeed
{
    string BackendId { get; }
    ValueTask<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default);
    IDisposable SubscribeQuotes(string symbol, Func<Quote, ValueTask> handler);
}

public interface IInstrumentCatalog
{
    string BackendId { get; }
    ValueTask<Instrument?> GetAsync(string symbol, CancellationToken ct = default);
    ValueTask<IReadOnlyList<Instrument>> SearchAsync(string query, int topK = 20, CancellationToken ct = default);
}

public interface IOrderRouter
{
    string BackendId { get; }
    ValueTask<OrderResult> SubmitAsync(OrderRequest req, CancellationToken ct = default);
}
