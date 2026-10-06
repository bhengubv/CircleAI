// AgriculturePrimitives.cs — (3.3.0)
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace CircleAI.Agriculture;

public sealed record Field(string FieldId, double AreaHa, string SoilType, string IrrigationKind);
public sealed record Crop(string CropId, string FieldId, string Variety, DateTime PlantedOn, DateTime? ExpectedHarvest);
public sealed record YieldRecord(string CropId, double TonsPerHa, DateTime HarvestedOn);

public interface IFarmBoard
{
    void AddField(Field f);
    void Plant(Crop c);
    void RecordYield(YieldRecord y);
    Field? GetField(string id);
    IReadOnlyList<Crop> CropsForField(string fieldId);
    double AvgYieldOfVariety(string variety);
    int FieldCount { get; }
    bool RemoveField(string fieldId);
    double TotalAreaHa();
    IReadOnlyList<Field> FieldsBySoil(string soilType);
    IReadOnlyList<Crop> DueForHarvest(DateTime asOf);
    string? BestYieldingVariety();
}

public sealed class InMemoryFarmBoard : IFarmBoard
{
    private readonly ConcurrentDictionary<string, Field> _fields = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Crop> _crops = new(StringComparer.Ordinal);
    private readonly List<YieldRecord> _yields = new();
    private readonly object _lock = new();

    public void AddField(Field f) { ArgumentNullException.ThrowIfNull(f); _fields[f.FieldId] = f; }
    public void Plant(Crop c) { ArgumentNullException.ThrowIfNull(c); _crops[c.CropId] = c; }
    public void RecordYield(YieldRecord y) { ArgumentNullException.ThrowIfNull(y); lock (_lock) _yields.Add(y); }
    public Field? GetField(string id) => _fields.GetValueOrDefault(id);
    public IReadOnlyList<Crop> CropsForField(string fieldId)
        => _crops.Values.Where(c => c.FieldId == fieldId).OrderBy(c => c.PlantedOn).ToArray();

    public double AvgYieldOfVariety(string variety)
    {
        lock (_lock)
        {
            var rows = _yields.Where(y => _crops.TryGetValue(y.CropId, out var c) && string.Equals(c.Variety, variety, StringComparison.OrdinalIgnoreCase)).ToArray();
            return rows.Length == 0 ? 0.0 : rows.Average(r => r.TonsPerHa);
        }
    }

    public int FieldCount => _fields.Count;

    public bool RemoveField(string fieldId) => _fields.TryRemove(fieldId, out _);

    public double TotalAreaHa() => _fields.Values.Sum(f => f.AreaHa);

    public IReadOnlyList<Field> FieldsBySoil(string soilType)
        => _fields.Values.Where(f => string.Equals(f.SoilType, soilType, StringComparison.OrdinalIgnoreCase))
                         .OrderByDescending(f => f.AreaHa).ToArray();

    public IReadOnlyList<Crop> DueForHarvest(DateTime asOf)
        => _crops.Values.Where(c => c.ExpectedHarvest is DateTime h && h <= asOf)
                        .OrderBy(c => c.ExpectedHarvest).ToArray();

    public string? BestYieldingVariety()
    {
        lock (_lock)
        {
            return _yields.Where(y => _crops.ContainsKey(y.CropId))
                          .GroupBy(y => _crops[y.CropId].Variety, StringComparer.OrdinalIgnoreCase)
                          .Select(g => (Variety: g.Key, Avg: g.Average(r => r.TonsPerHa)))
                          .OrderByDescending(t => t.Avg)
                          .Select(t => t.Variety)
                          .FirstOrDefault();
        }
    }
}
