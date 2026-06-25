// InMemoryMarkets.cs
//
// (3.3.0) Real in-memory market-data feed + instrument catalog + order
// router. The feed supports subscribe/broadcast quote pushes; the
// order router accepts and rejects based on simple rules (positive
// quantity, known instrument, valid limit price for limit orders).

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Markets;

public sealed class InMemoryInstrumentCatalog : IInstrumentCatalog
{
    private readonly ConcurrentDictionary<string, Instrument> _items = new(StringComparer.OrdinalIgnoreCase);

    public string BackendId => "in-memory";

    public void Add(Instrument item) => _items[item.Symbol] = item ?? throw new ArgumentNullException(nameof(item));

    public ValueTask<Instrument?> GetAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
        _items.TryGetValue(symbol, out var i);
        return ValueTask.FromResult(i);
    }

    public ValueTask<IReadOnlyList<Instrument>> SearchAsync(string query, int topK = 20, CancellationToken ct = default)
    {
        if (query is null) throw new ArgumentNullException(nameof(query));
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        var hits = _items.Values
            .Where(i => i.Symbol.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(i => i.Symbol)
            .Take(topK)
            .ToArray();
        return ValueTask.FromResult<IReadOnlyList<Instrument>>(hits);
    }
}

public sealed class InMemoryMarketDataFeed : IMarketDataFeed
{
    private readonly ConcurrentDictionary<string, Quote> _quotes = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, List<Func<Quote, ValueTask>>> _subs = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public string BackendId => "in-memory";

    public void Publish(Quote q)
    {
        ArgumentNullException.ThrowIfNull(q);
        _quotes[q.Symbol] = q;
        if (_subs.TryGetValue(q.Symbol, out var list))
        {
            Func<Quote, ValueTask>[] snap;
            lock (_gate) snap = list.ToArray();
            foreach (var s in snap)
            {
                try { _ = s(q); }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[CircleAI.Markets] quote subscriber threw: {ex.Message}"); }
            }
        }
    }

    public ValueTask<Quote?> GetQuoteAsync(string symbol, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
        _quotes.TryGetValue(symbol, out var q);
        return ValueTask.FromResult(q);
    }

    public IDisposable SubscribeQuotes(string symbol, Func<Quote, ValueTask> handler)
    {
        if (string.IsNullOrWhiteSpace(symbol)) throw new ArgumentException("symbol required", nameof(symbol));
        ArgumentNullException.ThrowIfNull(handler);
        var list = _subs.GetOrAdd(symbol, _ => new List<Func<Quote, ValueTask>>());
        lock (_gate) list.Add(handler);
        return new Subscription(this, symbol, handler);
    }

    private sealed class Subscription : IDisposable
    {
        private readonly InMemoryMarketDataFeed _owner;
        private readonly string _sym;
        private readonly Func<Quote, ValueTask> _h;
        public Subscription(InMemoryMarketDataFeed o, string s, Func<Quote, ValueTask> h) { _owner = o; _sym = s; _h = h; }
        public void Dispose()
        {
            if (_owner._subs.TryGetValue(_sym, out var list))
            {
                lock (_owner._gate) list.Remove(_h);
            }
        }
    }
}

public sealed class InMemoryOrderRouter : IOrderRouter
{
    private readonly IInstrumentCatalog _catalog;
    private long _seq;

    public InMemoryOrderRouter(IInstrumentCatalog catalog)
        => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));

    public string BackendId => "in-memory";

    public async ValueTask<OrderResult> SubmitAsync(OrderRequest req, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(req);
        if (req.Quantity <= 0)
            return new OrderResult(NextId(), false, "Quantity must be positive");
        if (req.Type == OrderType.Limit && (req.LimitPrice is null || req.LimitPrice <= 0))
            return new OrderResult(NextId(), false, "Limit order requires positive LimitPrice");

        var inst = await _catalog.GetAsync(req.Symbol, ct).ConfigureAwait(false);
        if (inst is null)
            return new OrderResult(NextId(), false, "Unknown symbol");

        return new OrderResult(NextId(), true, null);
    }

    private string NextId() => $"ord-{Interlocked.Increment(ref _seq)}";
}
