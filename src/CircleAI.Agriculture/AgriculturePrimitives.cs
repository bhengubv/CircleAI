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
}
