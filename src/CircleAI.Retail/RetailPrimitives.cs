// RetailPrimitives.cs
//
// (3.3.0) Real domain types + in-memory store for the Retail
// vertical: products, stock levels, sales, daily summary.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Retail;

public sealed record Product(string Sku, string Name, decimal Price, string Currency, string? Category);
public sealed record StockLevel(string Sku, int Quantity);
public sealed record Sale(string SaleId, string Sku, int Quantity, decimal UnitPrice, DateTimeOffset AtUtc);

public interface IRetailBoard
{
    void AddProduct(Product p);
    Product? GetProduct(string sku);
    void SetStock(StockLevel l);
    int Stock(string sku);
    void RecordSale(Sale s);
    decimal RevenueToday(DateTimeOffset now);
    IReadOnlyList<(string Sku, int Sold)> TopSellersSince(DateTimeOffset since, int topK = 5);
}

public sealed class InMemoryRetailBoard : IRetailBoard
{
    private readonly ConcurrentDictionary<string, Product> _products = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _stock = new(StringComparer.Ordinal);
    private readonly List<Sale> _sales = new();
    private readonly object _lock = new();

    public void AddProduct(Product p) { ArgumentNullException.ThrowIfNull(p); _products[p.Sku] = p; }
    public Product? GetProduct(string sku) => _products.GetValueOrDefault(sku);

    public void SetStock(StockLevel l) { ArgumentNullException.ThrowIfNull(l); _stock[l.Sku] = l.Quantity; }
    public int Stock(string sku) => _stock.TryGetValue(sku, out var q) ? q : 0;

    public void RecordSale(Sale s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (!_products.ContainsKey(s.Sku)) throw new InvalidOperationException($"Unknown SKU {s.Sku}");
        lock (_lock)
        {
            _sales.Add(s);
            _stock[s.Sku] = Stock(s.Sku) - s.Quantity;
        }
    }

    public decimal RevenueToday(DateTimeOffset now)
    {
        lock (_lock)
        {
            return _sales.Where(s => s.AtUtc.Date == now.Date)
                         .Sum(s => s.UnitPrice * s.Quantity);
        }
    }

    public IReadOnlyList<(string Sku, int Sold)> TopSellersSince(DateTimeOffset since, int topK = 5)
    {
        if (topK <= 0) throw new ArgumentOutOfRangeException(nameof(topK));
        lock (_lock)
        {
            return _sales.Where(s => s.AtUtc >= since)
                         .GroupBy(s => s.Sku)
                         .Select(g => (g.Key, g.Sum(s => s.Quantity)))
                         .OrderByDescending(t => t.Item2)
                         .Take(topK)
                         .ToArray();
        }
    }
}
