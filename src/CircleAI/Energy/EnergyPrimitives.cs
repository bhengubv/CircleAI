// EnergyPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Energy;

public sealed record MeterReading(string MeterId, double Kwh, DateTimeOffset AtUtc);
public sealed record EnergyTariff(string TariffId, string Name, double PeakKwhRate, double OffPeakKwhRate, string Currency);
public sealed record Outage(string OutageId, string Area, DateTimeOffset StartUtc, DateTimeOffset? EndUtc, string? Reason);

public interface IEnergyBoard
{
    void Record(MeterReading r);
    IReadOnlyList<MeterReading> ReadingsFor(string meterId, DateTimeOffset since);
    double TotalKwhSince(string meterId, DateTimeOffset since);
    void SetTariff(EnergyTariff t);
    EnergyTariff? GetTariff(string id);
    decimal EstimateCost(string meterId, string tariffId, DateTimeOffset since);
    void LogOutage(Outage o);
    IReadOnlyList<Outage> ActiveOutages();
}

public sealed class InMemoryEnergyBoard : IEnergyBoard
{
    private readonly List<MeterReading> _readings = new();
    private readonly ConcurrentDictionary<string, EnergyTariff> _tariffs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Outage> _outages = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Record(MeterReading r) { ArgumentNullException.ThrowIfNull(r); lock (_lock) _readings.Add(r); }
    public IReadOnlyList<MeterReading> ReadingsFor(string meterId, DateTimeOffset since)
    { lock (_lock) return _readings.Where(r => r.MeterId == meterId && r.AtUtc >= since).OrderBy(r => r.AtUtc).ToArray(); }
    public double TotalKwhSince(string meterId, DateTimeOffset since)
    {
        var rows = ReadingsFor(meterId, since);
        if (rows.Count < 2) return 0.0;
        return rows[^1].Kwh - rows[0].Kwh;
    }
    public void SetTariff(EnergyTariff t) { ArgumentNullException.ThrowIfNull(t); _tariffs[t.TariffId] = t; }
    public EnergyTariff? GetTariff(string id) => _tariffs.GetValueOrDefault(id);
    public decimal EstimateCost(string meterId, string tariffId, DateTimeOffset since)
    {
        if (!_tariffs.TryGetValue(tariffId, out var t)) throw new InvalidOperationException($"Unknown tariff {tariffId}");
        var kwh = TotalKwhSince(meterId, since);
        return (decimal)(kwh * t.PeakKwhRate);
    }
    public void LogOutage(Outage o) { ArgumentNullException.ThrowIfNull(o); _outages[o.OutageId] = o; }
    public IReadOnlyList<Outage> ActiveOutages() => _outages.Values.Where(o => o.EndUtc is null).ToArray();
}
