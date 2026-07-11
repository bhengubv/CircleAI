// business/index.ts
// Full-parity port of CircleAI.Business (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Business vertical: a business-unit
// hierarchy, KPI samples with a latest-value lookup, and quarterly targets with
// an achievement ratio. Plus the static BusinessDomainContext.
//
// NOTE: The C# BusinessCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports (healthcare/education/legal/commerce), which port only the
// board + DomainContext.
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   double Value / Target           → number
//   DateTimeOffset AtUtc            → Date
//   IReadOnlyList<string> KpiTags    → readonly string[]
//   double.NaN                       → Number.NaN
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   LatestKpi          — newest sample (AtUtc descending) for unit+metric, or
//                        NaN when none (C# `?.Value ?? double.NaN`).
//   TargetAchievement  — LatestKpi / target, or NaN when the target is missing
//                        or zero. Target key: "{unit}/{metric}/{year}Q{quarter}".

/** A business unit in the org hierarchy. Mirrors C# `BusinessUnit` record. */
export interface BusinessUnit {
  readonly unitId: string;
  readonly name: string;
  readonly parentUnitId: string;
  readonly kpiTags: readonly string[];
}

/** Constructs a {@link BusinessUnit}. */
export function businessUnit(
  unitId: string,
  name: string,
  parentUnitId: string,
  kpiTags: readonly string[],
): BusinessUnit {
  return { unitId, name, parentUnitId, kpiTags };
}

/** A single KPI observation. Mirrors C# `KpiSample` record. */
export interface KpiSample {
  readonly unitId: string;
  readonly metric: string;
  readonly value: number;
  /** UTC instant of the sample (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link KpiSample}. */
export function kpiSample(unitId: string, metric: string, value: number, atUtc: Date): KpiSample {
  return { unitId, metric, value, atUtc };
}

/** A quarterly KPI target. Mirrors C# `QuarterTarget` record. */
export interface QuarterTarget {
  readonly unitId: string;
  readonly metric: string;
  readonly year: number;
  readonly quarter: number;
  readonly target: number;
}

/** Constructs a {@link QuarterTarget}. */
export function quarterTarget(
  unitId: string,
  metric: string,
  year: number,
  quarter: number,
  target: number,
): QuarterTarget {
  return { unitId, metric, year, quarter, target };
}

/** The business board contract. Mirrors C# `IBusinessBoard`. */
export interface IBusinessBoard {
  add(u: BusinessUnit): void;
  getUnit(id: string): BusinessUnit | undefined;
  childrenOf(parentUnitId: string): readonly BusinessUnit[];
  record(s: KpiSample): void;
  latestKpi(unitId: string, metric: string): number;
  setTarget(t: QuarterTarget): void;
  targetAchievement(unitId: string, metric: string, year: number, quarter: number): number;
}

/** Deterministic in-memory {@link IBusinessBoard}. */
export class InMemoryBusinessBoard implements IBusinessBoard {
  private readonly units = new Map<string, BusinessUnit>();
  private readonly kpis: KpiSample[] = [];
  private readonly targets = new Map<string, QuarterTarget>();

  add(u: BusinessUnit): void {
    if (u == null) throw new Error("u required");
    this.units.set(u.unitId, u);
  }

  getUnit(id: string): BusinessUnit | undefined {
    return this.units.get(id);
  }

  childrenOf(parentUnitId: string): readonly BusinessUnit[] {
    return [...this.units.values()].filter((u) => u.parentUnitId === parentUnitId);
  }

  record(s: KpiSample): void {
    if (s == null) throw new Error("s required");
    this.kpis.push(s);
  }

  latestKpi(unitId: string, metric: string): number {
    const matches = this.kpis
      .filter((k) => k.unitId === unitId && k.metric === metric)
      .sort((a, b) => b.atUtc.getTime() - a.atUtc.getTime());
    return matches.length > 0 ? matches[0].value : Number.NaN;
  }

  setTarget(t: QuarterTarget): void {
    if (t == null) throw new Error("t required");
    this.targets.set(`${t.unitId}/${t.metric}/${t.year}Q${t.quarter}`, t);
  }

  targetAchievement(unitId: string, metric: string, year: number, quarter: number): number {
    const key = `${unitId}/${metric}/${year}Q${quarter}`;
    const target = this.targets.get(key);
    if (target === undefined || target.target === 0) return Number.NaN;
    return this.latestKpi(unitId, metric) / target.target;
  }
}

/**
 * Static domain context for the Business vertical. Mirrors C#
 * `BusinessDomainContext`.
 */
export const BusinessDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Business] You are a business strategy and operations expert. Help with OKRs, strategic planning, meeting facilitation, competitive analysis, and executive decision support. Structure advice with clear options and trade-offs. Compliance: POPIA data handling, general commercial law.",
  complianceFlags: ["POPIA", "Commercial_Law", "GDPR_aware"] as readonly string[],
  suggestedTools: ["calendar", "web_search", "document_editor", "task_manager"] as readonly string[],
} as const;
