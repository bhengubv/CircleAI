// energy/index.ts
// Full-parity port of CircleAI.Energy (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Energy vertical: meter readings,
// tariffs, outages, and consumption + cost rollups. Plus the static
// EnergyDomainContext.
//
// NOTE: The C# EnergyCompanionAdapter (an ICompanionSession LLM-prompt wrapper)
// is intentionally NOT ported — consistent with the sibling domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   double Kwh / *Rate               → number
//   decimal EstimateCost (return)    → number ((decimal)(kwh * PeakKwhRate))
//   string? Reason                   → string | null
//   DateTimeOffset AtUtc/StartUtc    → Date
//   DateTimeOffset? EndUtc           → Date | null
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   ReadingsFor   — meter's readings with AtUtc >= since, AtUtc ascending.
//   TotalKwhSince — 0.0 when < 2 readings; else last.Kwh − first.Kwh.
//   EstimateCost  — throws on unknown tariff; TotalKwhSince × PeakKwhRate.
//   ActiveOutages — outages with EndUtc == null (map insertion order).

/** A meter reading. Mirrors C# `MeterReading` record. */
export interface MeterReading {
  readonly meterId: string;
  readonly kwh: number;
  /** UTC instant of the reading (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link MeterReading}. */
export function meterReading(meterId: string, kwh: number, atUtc: Date): MeterReading {
  return { meterId, kwh, atUtc };
}

/** An energy tariff. Mirrors C# `EnergyTariff` record. */
export interface EnergyTariff {
  readonly tariffId: string;
  readonly name: string;
  readonly peakKwhRate: number;
  readonly offPeakKwhRate: number;
  readonly currency: string;
}

/** Constructs an {@link EnergyTariff}. */
export function energyTariff(
  tariffId: string,
  name: string,
  peakKwhRate: number,
  offPeakKwhRate: number,
  currency: string,
): EnergyTariff {
  return { tariffId, name, peakKwhRate, offPeakKwhRate, currency };
}

/** A power outage. Mirrors C# `Outage` record. */
export interface Outage {
  readonly outageId: string;
  readonly area: string;
  /** UTC start instant (C# `DateTimeOffset StartUtc`). */
  readonly startUtc: Date;
  /** UTC end instant, or null while ongoing (C# `DateTimeOffset? EndUtc`). */
  readonly endUtc: Date | null;
  readonly reason: string | null;
}

/** Constructs an {@link Outage}. */
export function outage(
  outageId: string,
  area: string,
  startUtc: Date,
  endUtc: Date | null,
  reason: string | null,
): Outage {
  return { outageId, area, startUtc, endUtc, reason };
}

/** The energy board contract. Mirrors C# `IEnergyBoard`. */
export interface IEnergyBoard {
  record(r: MeterReading): void;
  readingsFor(meterId: string, since: Date): readonly MeterReading[];
  totalKwhSince(meterId: string, since: Date): number;
  setTariff(t: EnergyTariff): void;
  getTariff(id: string): EnergyTariff | undefined;
  estimateCost(meterId: string, tariffId: string, since: Date): number;
  logOutage(o: Outage): void;
  activeOutages(): readonly Outage[];
}

/** Deterministic in-memory {@link IEnergyBoard}. */
export class InMemoryEnergyBoard implements IEnergyBoard {
  private readonly readings: MeterReading[] = [];
  private readonly tariffs = new Map<string, EnergyTariff>();
  private readonly outages = new Map<string, Outage>();

  record(r: MeterReading): void {
    if (r == null) throw new Error("r required");
    this.readings.push(r);
  }

  readingsFor(meterId: string, since: Date): readonly MeterReading[] {
    const sinceMs = since.getTime();
    return this.readings
      .filter((r) => r.meterId === meterId && r.atUtc.getTime() >= sinceMs)
      .sort((a, b) => a.atUtc.getTime() - b.atUtc.getTime());
  }

  totalKwhSince(meterId: string, since: Date): number {
    const rows = this.readingsFor(meterId, since);
    if (rows.length < 2) return 0.0;
    return rows[rows.length - 1].kwh - rows[0].kwh;
  }

  setTariff(t: EnergyTariff): void {
    if (t == null) throw new Error("t required");
    this.tariffs.set(t.tariffId, t);
  }

  getTariff(id: string): EnergyTariff | undefined {
    return this.tariffs.get(id);
  }

  estimateCost(meterId: string, tariffId: string, since: Date): number {
    const t = this.tariffs.get(tariffId);
    if (t === undefined) throw new Error(`Unknown tariff ${tariffId}`);
    const kwh = this.totalKwhSince(meterId, since);
    return kwh * t.peakKwhRate;
  }

  logOutage(o: Outage): void {
    if (o == null) throw new Error("o required");
    this.outages.set(o.outageId, o);
  }

  activeOutages(): readonly Outage[] {
    return [...this.outages.values()].filter((o) => o.endUtc === null);
  }
}

/**
 * Static domain context for the Energy vertical. Mirrors C#
 * `EnergyDomainContext`.
 */
export const EnergyDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Energy] Expert energy management and renewable energy assistant. Help with solar/wind feasibility, load flow analysis, tariff optimisation, battery storage sizing, grid connection requirements, and energy efficiency audits. Apply NERSA and SABS standards. Compliance: Electricity Act, NERSA regulations, Municipal By-laws, Renewable Energy IPP.",
  complianceFlags: ["Electricity_Act", "NERSA", "SABS", "Municipal_Energy_By_laws", "POPIA"] as readonly string[],
  suggestedTools: ["energy_model", "analytics", "document_editor", "web_search"] as readonly string[],
} as const;
