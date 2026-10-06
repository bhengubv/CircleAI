// CommercePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Commerce;

public sealed record CommerceCustomer(string CustomerId, string Name, string? Email, DateTimeOffset CreatedUtc);
public sealed record CommerceOrder(string OrderId, string CustomerId, decimal Total, string Currency, string Status, DateTimeOffset AtUtc);
public sealed record CommerceLineItem(string LineId, string OrderId, string Sku, int Quantity, decimal UnitPrice);

public interface ICommerceBoard
{
    void AddCustomer(CommerceCustomer c);
    CommerceCustomer? GetCustomer(string id);
    void Place(CommerceOrder o);
    void AddLine(CommerceLineItem l);
    void UpdateStatus(string orderId, string status);
    IReadOnlyList<CommerceOrder> OrdersFor(string customerId);
    IReadOnlyList<CommerceLineItem> LinesFor(string orderId);
    decimal LifetimeValue(string customerId);
}

public sealed class InMemoryCommerceBoard : ICommerceBoard
{
    private readonly ConcurrentDictionary<string, CommerceCustomer> _customers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CommerceOrder> _orders = new(StringComparer.Ordinal);
    private readonly List<CommerceLineItem> _lines = new();
    private readonly object _lock = new();

    public void AddCustomer(CommerceCustomer c) { ArgumentNullException.ThrowIfNull(c); _customers[c.CustomerId] = c; }
    public CommerceCustomer? GetCustomer(string id) => _customers.GetValueOrDefault(id);
    public void Place(CommerceOrder o) { ArgumentNullException.ThrowIfNull(o); _orders[o.OrderId] = o; }
    public void AddLine(CommerceLineItem l) { ArgumentNullException.ThrowIfNull(l); lock (_lock) _lines.Add(l); }
    public void UpdateStatus(string orderId, string status)
    {
        if (!_orders.TryGetValue(orderId, out var o)) throw new InvalidOperationException($"Unknown order {orderId}");
        _orders[orderId] = o with { Status = status };
    }
    public IReadOnlyList<CommerceOrder> OrdersFor(string customerId)
        => _orders.Values.Where(o => o.CustomerId == customerId).OrderByDescending(o => o.AtUtc).ToArray();
    public IReadOnlyList<CommerceLineItem> LinesFor(string orderId)
    { lock (_lock) return _lines.Where(l => l.OrderId == orderId).ToArray(); }
    public decimal LifetimeValue(string customerId)
        => OrdersFor(customerId).Sum(o => o.Total);
}
