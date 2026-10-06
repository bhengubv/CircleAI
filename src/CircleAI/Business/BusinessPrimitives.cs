// BusinessPrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Business;

public sealed record BusinessUnit(string UnitId, string Name, string ParentUnitId, IReadOnlyList<string> KpiTags);
public sealed record KpiSample(string UnitId, string Metric, double Value, DateTimeOffset AtUtc);
public sealed record QuarterTarget(string UnitId, string Metric, int Year, int Quarter, double Target);

public interface IBusinessBoard
{
    void Add(BusinessUnit u);
    BusinessUnit? GetUnit(string id);
    IReadOnlyList<BusinessUnit> ChildrenOf(string parentUnitId);
    void Record(KpiSample s);
    double LatestKpi(string unitId, string metric);
    void SetTarget(QuarterTarget t);
    double TargetAchievement(string unitId, string metric, int year, int quarter);
}

public sealed class InMemoryBusinessBoard : IBusinessBoard
{
    private readonly ConcurrentDictionary<string, BusinessUnit> _units = new(StringComparer.Ordinal);
    private readonly List<KpiSample> _kpis = new();
    private readonly ConcurrentDictionary<string, QuarterTarget> _targets = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    public void Add(BusinessUnit u) { ArgumentNullException.ThrowIfNull(u); _units[u.UnitId] = u; }
    public BusinessUnit? GetUnit(string id) => _units.GetValueOrDefault(id);
    public IReadOnlyList<BusinessUnit> ChildrenOf(string parentUnitId)
        => _units.Values.Where(u => u.ParentUnitId == parentUnitId).ToArray();
    public void Record(KpiSample s) { ArgumentNullException.ThrowIfNull(s); lock (_lock) _kpis.Add(s); }
    public double LatestKpi(string unitId, string metric)
    { lock (_lock) return _kpis.Where(k => k.UnitId == unitId && k.Metric == metric).OrderByDescending(k => k.AtUtc).FirstOrDefault()?.Value ?? double.NaN; }
    public void SetTarget(QuarterTarget t)
    { ArgumentNullException.ThrowIfNull(t); _targets[$"{t.UnitId}/{t.Metric}/{t.Year}Q{t.Quarter}"] = t; }
    public double TargetAchievement(string unitId, string metric, int year, int quarter)
    {
        var key = $"{unitId}/{metric}/{year}Q{quarter}";
        if (!_targets.TryGetValue(key, out var target) || target.Target == 0) return double.NaN;
        return LatestKpi(unitId, metric) / target.Target;
    }
}
