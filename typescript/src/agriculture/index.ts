// agriculture/index.ts
// Full-parity port of CircleAI.Agriculture (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Agriculture vertical: fields, crops,
// yield records, and an average-yield-of-variety rollup. Plus the static
// AgricultureDomainContext.
//
// NOTE: The C# AgricultureCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   double AreaHa / TonsPerHa        → number
//   DateTime PlantedOn / HarvestedOn → Date
//   DateTime? ExpectedHarvest        → Date | null
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   CropsForField    — crops for the field, PlantedOn ascending.
//   AvgYieldOfVariety— mean TonsPerHa over yields whose crop's Variety matches
//                      (ordinal case-insensitive); 0.0 when none.

/** A field. Mirrors C# `Field` record. */
export interface Field {
  readonly fieldId: string;
  readonly areaHa: number;
  readonly soilType: string;
  readonly irrigationKind: string;
}

/** Constructs a {@link Field}. */
export function field(fieldId: string, areaHa: number, soilType: string, irrigationKind: string): Field {
  return { fieldId, areaHa, soilType, irrigationKind };
}

/** A planted crop. Mirrors C# `Crop` record. */
export interface Crop {
  readonly cropId: string;
  readonly fieldId: string;
  readonly variety: string;
  /** Planting date (C# `DateTime PlantedOn`). */
  readonly plantedOn: Date;
  /** Expected harvest date, or null (C# `DateTime? ExpectedHarvest`). */
  readonly expectedHarvest: Date | null;
}

/** Constructs a {@link Crop}. */
export function crop(
  cropId: string,
  fieldId: string,
  variety: string,
  plantedOn: Date,
  expectedHarvest: Date | null,
): Crop {
  return { cropId, fieldId, variety, plantedOn, expectedHarvest };
}

/** A harvest yield record. Mirrors C# `YieldRecord` record. */
export interface YieldRecord {
  readonly cropId: string;
  readonly tonsPerHa: number;
  /** Harvest date (C# `DateTime HarvestedOn`). */
  readonly harvestedOn: Date;
}

/** Constructs a {@link YieldRecord}. */
export function yieldRecord(cropId: string, tonsPerHa: number, harvestedOn: Date): YieldRecord {
  return { cropId, tonsPerHa, harvestedOn };
}

/** The farm board contract. Mirrors C# `IFarmBoard`. */
export interface IFarmBoard {
  addField(f: Field): void;
  plant(c: Crop): void;
  recordYield(y: YieldRecord): void;
  getField(id: string): Field | undefined;
  cropsForField(fieldId: string): readonly Crop[];
  avgYieldOfVariety(variety: string): number;
  /** Number of fields registered. */
  readonly fieldCount: number;
  /** Remove a field by id. Returns whether one was removed. */
  removeField(fieldId: string): boolean;
  /** Total area across all fields, in hectares. */
  totalAreaHa(): number;
  /** Fields of a given soil type (case-insensitive), largest area first. */
  fieldsBySoil(soilType: string): readonly Field[];
  /** Crops whose expected harvest is on or before `asOf`, earliest first. */
  dueForHarvest(asOf: Date): readonly Crop[];
  /** The variety with the highest average yield, or undefined when none recorded. */
  bestYieldingVariety(): string | undefined;
}

/** Deterministic in-memory {@link IFarmBoard}. */
export class InMemoryFarmBoard implements IFarmBoard {
  private readonly fields = new Map<string, Field>();
  private readonly crops = new Map<string, Crop>();
  private readonly yields: YieldRecord[] = [];

  addField(f: Field): void {
    if (f == null) throw new Error("f required");
    this.fields.set(f.fieldId, f);
  }

  plant(c: Crop): void {
    if (c == null) throw new Error("c required");
    this.crops.set(c.cropId, c);
  }

  recordYield(y: YieldRecord): void {
    if (y == null) throw new Error("y required");
    this.yields.push(y);
  }

  getField(id: string): Field | undefined {
    return this.fields.get(id);
  }

  cropsForField(fieldId: string): readonly Crop[] {
    return [...this.crops.values()]
      .filter((c) => c.fieldId === fieldId)
      .sort((a, b) => a.plantedOn.getTime() - b.plantedOn.getTime());
  }

  avgYieldOfVariety(variety: string): number {
    const target = variety.toLowerCase();
    const rows = this.yields.filter((y) => {
      const c = this.crops.get(y.cropId);
      return c !== undefined && c.variety.toLowerCase() === target;
    });
    if (rows.length === 0) return 0.0;
    return rows.reduce((sum, r) => sum + r.tonsPerHa, 0) / rows.length;
  }

  /** Number of fields registered. Mirrors C# `FieldCount`. */
  get fieldCount(): number {
    return this.fields.size;
  }

  /** Remove a field by id. Returns whether one was removed. Mirrors C# `RemoveField`. */
  removeField(fieldId: string): boolean {
    return this.fields.delete(fieldId);
  }

  /** Total area across all fields, in hectares. Mirrors C# `TotalAreaHa`. */
  totalAreaHa(): number {
    return [...this.fields.values()].reduce((sum, f) => sum + f.areaHa, 0);
  }

  /**
   * Fields of a given soil type (case-insensitive), largest area first.
   * Mirrors C# `FieldsBySoil`.
   */
  fieldsBySoil(soilType: string): readonly Field[] {
    const target = soilType.toLowerCase();
    return [...this.fields.values()]
      .filter((f) => f.soilType.toLowerCase() === target)
      .sort((a, b) => b.areaHa - a.areaHa);
  }

  /**
   * Crops whose expected harvest is on or before `asOf`, earliest first.
   * Crops without an expected-harvest date are excluded. Mirrors C# `DueForHarvest`.
   */
  dueForHarvest(asOf: Date): readonly Crop[] {
    const cutoff = asOf.getTime();
    return [...this.crops.values()]
      .filter((c) => c.expectedHarvest !== null && c.expectedHarvest.getTime() <= cutoff)
      .sort((a, b) => (a.expectedHarvest as Date).getTime() - (b.expectedHarvest as Date).getTime());
  }

  /**
   * The variety with the highest average yield across recorded yields, or
   * undefined when none recorded. Varieties are grouped case-insensitively
   * (first-seen casing wins); ties keep first-seen order. Mirrors C#
   * `BestYieldingVariety`.
   */
  bestYieldingVariety(): string | undefined {
    // Group yields by variety (OrdinalIgnoreCase), preserving first-seen order.
    const groups = new Map<string, { variety: string; sum: number; n: number }>();
    for (const y of this.yields) {
      const c = this.crops.get(y.cropId);
      if (c === undefined) continue;
      const key = c.variety.toLowerCase();
      const g = groups.get(key);
      if (g === undefined) groups.set(key, { variety: c.variety, sum: y.tonsPerHa, n: 1 });
      else {
        g.sum += y.tonsPerHa;
        g.n += 1;
      }
    }
    let best: { variety: string; avg: number } | undefined;
    for (const g of groups.values()) {
      const avg = g.sum / g.n;
      // Strict `>` keeps the first-seen group on ties (stable OrderByDescending).
      if (best === undefined || avg > best.avg) best = { variety: g.variety, avg };
    }
    return best?.variety;
  }
}

/**
 * Static domain context for the Agriculture vertical. Mirrors C#
 * `AgricultureDomainContext`.
 */
export const AgricultureDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Agriculture] Expert agricultural advisor. Help with crop planning, soil management, pest and disease identification, livestock health, market price analysis, irrigation scheduling, and agri-finance applications. Adapt advice to the specific region, climate zone, and crop type. Compliance: DAFF regulations, Conservation of Agricultural Resources Act, POPIA.",
  complianceFlags: ["DAFF_regs", "CARA", "Fertilizer_Act", "POPIA"] as readonly string[],
  suggestedTools: ["weather_api", "market_prices", "soil_data", "document_editor"] as readonly string[],
} as const;
