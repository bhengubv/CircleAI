/**
 * companion/herjarvis/stores
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// companion/herjarvis/stores.ts
//
// The store / math HerJarvis implementations, ported verbatim from
// HerJarvisRealImplementations.cs:
//
//   3.  JsonIdentitySync        — IIdentitySync
//   4.  EwaContinuousLearner    — IContinuousLearner
//   6.  InMemoryGoalPursuer     — IGoalPursuer
//   7.  TfEpisodicMemory        — IEpisodicMemory
//   9.  HistoricalCalibratedConfidence — ICalibratedConfidence
//   11. KeywordEmotionSensor    — IEmotionSensor
//   12. DemoStoreSkillAcquisition — ISkillAcquisition
//   15. AdjacencyPersonalKnowledgeGraph — IPersonalKnowledgeGraph
//
// All arithmetic in the C# is `double`; there is no `float` site in this file,
// so no Math.fround is needed. Wire-sensitive spots (the IdentitySync pull
// payload, the goal plan JSON) reproduce the C# byte layout exactly, including
// System.Text.Json string escaping via ../reasoning/stj_json.js.

import { stjEscape, stjDouble } from "../reasoning/index";
import { newGuidN } from "./guid";
import type {
  IIdentitySync,
  IContinuousLearner,
  IGoalPursuer,
  LongHorizonGoal,
  IEpisodicMemory,
  EpisodeRecord,
  ICalibratedConfidence,
  ConfidenceBand,
  IEmotionSensor,
  EmotionFrame,
  ISkillAcquisition,
  AcquiredSkill,
  IPersonalKnowledgeGraph,
  KnowledgeNode,
  KnowledgeRelation,
} from "./contracts";

/** `JsonSerializer.Serialize(string)` — STJ-escaped body wrapped in quotes. */
function stjSerializeString(s: string): string {
  return '"' + stjEscape(s) + '"';
}

// =====================================================================
// 3. IdentitySync — append-only delta log with monotonic cursor.
// =====================================================================

/**
 * An append-only delta log with a monotonic integer cursor. `pushAsync`
 * appends a delta with the next cursor; `pullAsync` returns a JSON envelope
 * `{"cursor":N,"deltas":[...]}` of every delta strictly after the supplied
 * cursor. The deltas are spliced in as raw JSON (the caller supplies JSON
 * fragments), exactly as the C# StringBuilder does.
 */
export class JsonIdentitySync implements IIdentitySync {
  private readonly log: { cursor: number; deltaJson: string }[] = [];
  private next = 0;

  async pushAsync(deltaJson: string): Promise<void> {
    if (deltaJson == null) throw new Error("deltaJson required");
    this.log.push({ cursor: ++this.next, deltaJson });
  }

  async pullAsync(sinceCursor: string): Promise<string> {
    // long.TryParse(...) ? s : 0 — non-numeric cursor is treated as 0.
    const parsed = Number.parseInt(sinceCursor, 10);
    const since = Number.isNaN(parsed) ? 0 : parsed;

    const taken = this.log.filter((e) => e.cursor > since);
    const maxCursor = taken.length === 0 ? since : taken[taken.length - 1].cursor;
    const deltas = taken.map((e) => e.deltaJson);

    let payload = '{"cursor":' + maxCursor + ',"deltas":[';
    for (let i = 0; i < deltas.length; i++) {
      if (i > 0) payload += ",";
      payload += deltas[i];
    }
    payload += "]}";
    return payload;
  }
}

// =====================================================================
// 4. ContinuousLearner — exponentially weighted average reward per id.
// =====================================================================

/**
 * Tracks an exponentially-weighted moving average reward per interaction id.
 * First observation seeds the average with the raw reward and weight 1; each
 * subsequent observation blends `avg*(1-alpha) + reward*alpha` and increments
 * the count.
 */
export class EwaContinuousLearner implements IContinuousLearner {
  private readonly state = new Map<string, { avg: number; weight: number }>();
  private readonly alpha: number;

  constructor(alpha = 0.2) {
    if (alpha <= 0 || alpha > 1) throw new Error("alpha out of range");
    this.alpha = alpha;
  }

  async registerFeedbackAsync(interactionId: string, reward: number, _contextJson: string): Promise<void> {
    if (!interactionId || interactionId.trim().length === 0)
      throw new Error("interactionId required");
    const prev = this.state.get(interactionId);
    if (prev === undefined) {
      this.state.set(interactionId, { avg: reward, weight: 1.0 });
    } else {
      this.state.set(interactionId, {
        avg: prev.avg * (1 - this.alpha) + reward * this.alpha,
        weight: prev.weight + 1,
      });
    }
  }

  averageRewardOf(interactionId: string): number | null {
    const s = this.state.get(interactionId);
    return s ? s.avg : null;
  }

  observationsOf(interactionId: string): number {
    const s = this.state.get(interactionId);
    return s ? Math.trunc(s.weight) : 0;
  }
}

// =====================================================================
// 6. GoalPursuer — store goal + milestones; replan recalculates plan.
// =====================================================================

const MS_PER_DAY = 86_400_000;

/**
 * Registers long-horizon goals with an auto-generated milestone plan, and
 * replans (recomputes the plan from "now" to the deadline) on demand. The plan
 * JSON layout is byte-identical to the C# BuildPlan.
 */
export class InMemoryGoalPursuer implements IGoalPursuer {
  private readonly goals = new Map<string, LongHorizonGoal>();

  async registerAsync(description: string, deadlineUtc: Date): Promise<LongHorizonGoal> {
    if (!description || description.trim().length === 0) throw new Error("description required");
    const id = newGuidN();
    const now = new Date();
    if (deadlineUtc.getTime() <= now.getTime()) throw new Error("deadline must be in the future");
    const plan = buildPlan(description, now, deadlineUtc);
    const g: LongHorizonGoal = {
      id,
      description,
      deadlineUtc,
      planJson: plan,
      progressFraction: 0,
    };
    this.goals.set(id, g);
    return g;
  }

  async currentAsync(id: string): Promise<LongHorizonGoal | null> {
    return this.goals.get(id) ?? null;
  }

  async replanAsync(id: string): Promise<void> {
    const g = this.goals.get(id);
    if (g === undefined) throw new Error(`Unknown goal ${id}`);
    const plan = buildPlan(g.description, new Date(), g.deadlineUtc);
    this.goals.set(id, { ...g, planJson: plan });
  }

  progress(id: string, fraction: number): void {
    if (fraction < 0 || fraction > 1) throw new Error("fraction out of range");
    const g = this.goals.get(id);
    if (g === undefined) throw new Error(`Unknown goal ${id}`);
    this.goals.set(id, { ...g, progressFraction: fraction });
  }
}

/**
 * C# BuildPlan. Milestones = clamp(totalDays/14, 2, 8); each milestone due at
 * now + step*i where step = (deadline-now)/milestones. Dates are rendered in
 * the round-trip "O" format (ISO-8601 with 7 fractional digits + trailing 'Z'
 * for a UTC/zero-offset DateTimeOffset).
 */
function buildPlan(description: string, now: Date, deadlineUtc: Date): string {
  const spanMs = deadlineUtc.getTime() - now.getTime();
  const totalDays = Math.max(1, Math.trunc(spanMs / MS_PER_DAY));
  const milestones = Math.min(8, Math.max(2, Math.trunc(totalDays / 14)));
  const stepMs = spanMs / milestones; // TimeSpan division — fractional ms allowed
  let sb = '{"description":' + stjSerializeString(description) + ',"milestones":[';
  for (let i = 1; i <= milestones; i++) {
    if (i > 1) sb += ",";
    const dueMs = now.getTime() + stepMs * i;
    sb += '{"index":' + i + ',"due":"' + toRoundTripUtc(new Date(dueMs)) + '"}';
  }
  sb += "]}";
  return sb;
}

/**
 * Render a Date as .NET's DateTimeOffset "O" format for a zero offset:
 * `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 fractional digits). JS millisecond
 * precision means the last four fractional digits are always 0.
 */
export function toRoundTripUtc(d: Date): string {
  const pad = (n: number, w = 2) => String(n).padStart(w, "0");
  const yyyy = pad(d.getUTCFullYear(), 4);
  const mo = pad(d.getUTCMonth() + 1);
  const da = pad(d.getUTCDate());
  const hh = pad(d.getUTCHours());
  const mi = pad(d.getUTCMinutes());
  const ss = pad(d.getUTCSeconds());
  const frac = pad(d.getUTCMilliseconds(), 3) + "0000"; // ms → 7-digit ticks fraction
  return `${yyyy}-${mo}-${da}T${hh}:${mi}:${ss}.${frac}Z`;
}

// =====================================================================
// 7. EpisodicMemory — TF-based similarity recall.
// =====================================================================

/**
 * Records episodes and recalls by raw term-frequency overlap: an episode's
 * score is the sum, over shared terms, of query-count × episode-count. Terms
 * are alnum runs of length >= 2, case-insensitive.
 */
export class TfEpisodicMemory implements IEpisodicMemory {
  private readonly episodes = new Map<string, EpisodeRecord>();
  private readonly terms = new Map<string, Map<string, number>>();

  async recordAsync(episode: EpisodeRecord): Promise<void> {
    if (episode == null) throw new Error("episode required");
    if (!episode.id || episode.id.trim().length === 0) throw new Error("Id required");
    this.episodes.set(episode.id, episode);
    this.terms.set(episode.id, toTermFrequency(episode.title + " " + episode.contentJson));
  }

  async recallAsync(query: string, take = 10): Promise<readonly EpisodeRecord[]> {
    if (query == null) throw new Error("query required");
    if (take <= 0) throw new Error("take out of range");
    const qTerms = toTermFrequency(query);
    if (qTerms.size === 0) return [];

    // Preserve insertion order (Map iteration) so the OrderByDescending stable
    // tie-break matches the C# (which iterates ConcurrentDictionary.Values —
    // whose order is not contractually defined, but our Map is insertion-order,
    // giving a deterministic, reproducible result).
    const scored = [...this.episodes.values()]
      .map((e) => ({ e, score: score(qTerms, this.terms.get(e.id)) }))
      .filter((x) => x.score > 0);

    const hits = stableSortByDesc(scored, (x) => x.score)
      .slice(0, take)
      .map((x) => x.e);
    return hits;
  }
}

/** Alnum tokens of length >= 2, counted case-insensitively (lower-cased keys). */
function toTermFrequency(text: string): Map<string, number> {
  const d = new Map<string, number>();
  for (const raw of (text ?? "").split(/[^A-Za-z0-9]+/)) {
    if (raw.length < 2) continue;
    const t = raw.toLowerCase();
    d.set(t, (d.get(t) ?? 0) + 1);
  }
  return d;
}

function score(q: Map<string, number>, d: Map<string, number> | undefined): number {
  if (d === undefined) return 0;
  let s = 0;
  for (const [k, v] of q) {
    const n = d.get(k);
    if (n !== undefined) s += v * n;
  }
  return s;
}

// =====================================================================
// 9. CalibratedConfidence — Platt-style logistic over correctness history.
// =====================================================================

/**
 * A history-calibrated confidence estimator. `recordOutcome` logs (rawScore,
 * wasCorrect). `evaluateAsync` computes a raw score from answer length /
 * hedging / context presence, then (once >=5 outcomes are logged) calibrates it
 * to the correctness rate of the 5 nearest-by-raw-score outcomes, and returns a
 * band whose half-width shrinks as calibrated confidence rises.
 */
export class HistoricalCalibratedConfidence implements ICalibratedConfidence {
  private readonly history: { rawScore: number; wasCorrect: boolean }[] = [];

  recordOutcome(rawScore: number, wasCorrect: boolean): void {
    this.history.push({ rawScore: clamp(rawScore, 0, 1), wasCorrect });
  }

  async evaluateAsync(answer: string, contextJson: string): Promise<ConfidenceBand> {
    if (answer == null) throw new Error("answer required");
    const raw = computeRawScore(answer, contextJson);
    let calibrated: number;
    if (this.history.length < 5) {
      calibrated = raw;
    } else {
      // OrderBy(|rawScore - raw|).Take(5) — stable nearest-5.
      const nearby = stableSortByAsc(this.history, (h) => Math.abs(h.rawScore - raw)).slice(0, 5);
      const correct = nearby.filter((h) => h.wasCorrect).length;
      calibrated = correct / nearby.length;
    }
    const halfBand = Math.max(0.05, 0.25 - calibrated * 0.2);
    return {
      lower: Math.max(0, calibrated - halfBand),
      upper: Math.min(1, calibrated + halfBand),
    };
  }
}

function computeRawScore(answer: string, contextJson: string): number {
  const len = Math.max(1, answer.trim().length);
  // \b(maybe|perhaps|might|possibly|unclear|don't know)\b, IgnoreCase.
  const hedges = (answer.match(/\b(maybe|perhaps|might|possibly|unclear|don't know)\b/gi) ?? [])
    .length;
  const hedgePenalty = Math.min(0.5, hedges * 0.1);
  const hasContext = contextJson != null && contextJson.trim().length > 0 && contextJson.length > 2;
  return clamp(Math.log(len) / 10.0 + (hasContext ? 0.1 : 0) - hedgePenalty, 0, 1);
}

// =====================================================================
// 11. EmotionSensor — keyword + arousal-valence inference from fused JSON.
// =====================================================================

interface EmotionPattern {
  readonly label: string;
  readonly arousal: number;
  readonly valence: number;
  readonly rx: RegExp;
}

const EMOTION_PATTERNS: readonly EmotionPattern[] = [
  { label: "joy", arousal: 0.8, valence: 0.9, rx: /\b(happy|joy|delight|excited|love|wonderful)\b/gi },
  { label: "anger", arousal: 0.9, valence: -0.8, rx: /\b(angry|furious|rage|hate|annoyed)\b/gi },
  { label: "sad", arousal: 0.3, valence: -0.7, rx: /\b(sad|lonely|grief|cry|depressed|down)\b/gi },
  { label: "fear", arousal: 0.85, valence: -0.6, rx: /\b(afraid|scared|terrified|anxious|worried)\b/gi },
  { label: "surprise", arousal: 0.7, valence: 0.3, rx: /\b(surprised|amazed|astonished|wow)\b/gi },
  { label: "calm", arousal: 0.1, valence: 0.5, rx: /\b(calm|peaceful|relaxed|content|fine)\b/gi },
];

/**
 * Infers an emotion frame from fused-signal JSON by counting keyword hits per
 * emotion; arousal/valence are hit-count-weighted averages and the label is the
 * highest-count emotion. No hits → neutral (0,0).
 */
export class KeywordEmotionSensor implements IEmotionSensor {
  async senseAsync(fusedJson: string): Promise<EmotionFrame> {
    if (fusedJson == null) throw new Error("fusedJson required");
    const hits = EMOTION_PATTERNS.map((p) => ({
      label: p.label,
      arousal: p.arousal,
      valence: p.valence,
      count: countMatches(p.rx, fusedJson),
    })).filter((t) => t.count > 0);

    if (hits.length === 0) return { label: "neutral", arousal: 0, valence: 0 };

    const totalWeight = hits.reduce((s, t) => s + t.count, 0);
    const arousal = hits.reduce((s, t) => s + t.arousal * t.count, 0) / totalWeight;
    const valence = hits.reduce((s, t) => s + t.valence * t.count, 0) / totalWeight;
    const top = stableSortByDesc(hits, (t) => t.count)[0].label;
    return { label: top, arousal, valence };
  }
}

/** Count non-overlapping regex matches (equivalent to C# Regex.Matches(...).Count). */
function countMatches(rx: RegExp, input: string): number {
  rx.lastIndex = 0;
  let n = 0;
  while (rx.exec(input) !== null) n++;
  return n;
}

// =====================================================================
// 12. SkillAcquisition — demo-store with name extraction.
// =====================================================================

/**
 * Stores acquired skills keyed by a generated id; `acquireAsync` extracts a
 * `name` from the demonstration JSON (falling back to `skill-<first6>`), and
 * `listAsync` returns skills ordered by name.
 */
export class DemoStoreSkillAcquisition implements ISkillAcquisition {
  private readonly skills = new Map<string, AcquiredSkill>();

  async acquireAsync(demonstrationJson: string): Promise<AcquiredSkill> {
    if (demonstrationJson == null) throw new Error("demonstrationJson required");
    const id = newGuidN();
    const name = extractName(demonstrationJson) ?? "skill-" + id.substring(0, 6);
    const skill: AcquiredSkill = { id, name, descriptionJson: demonstrationJson };
    this.skills.set(id, skill);
    return skill;
  }

  async listAsync(): Promise<readonly AcquiredSkill[]> {
    // OrderBy(s => s.Name) — ordinal string sort, stable.
    return stableSortBy([...this.skills.values()], (s) => s.name);
  }
}

function extractName(demonstrationJson: string): string | null {
  try {
    const doc = JSON.parse(demonstrationJson);
    if (
      doc !== null &&
      typeof doc === "object" &&
      !Array.isArray(doc) &&
      typeof (doc as Record<string, unknown>).name === "string"
    ) {
      return (doc as Record<string, string>).name;
    }
  } catch {
    /* JsonException → null */
  }
  return null;
}

// =====================================================================
// 15. PersonalKnowledgeGraph — adjacency-list graph with relation kinds.
// =====================================================================

/**
 * An adjacency-list personal knowledge graph. Nodes are upserted by id;
 * relations are stored as out-edges (deduplicated on (toId, relation));
 * `neighboursAsync` resolves the out-edge targets to their nodes.
 */
export class AdjacencyPersonalKnowledgeGraph implements IPersonalKnowledgeGraph {
  private readonly nodes = new Map<string, KnowledgeNode>();
  private readonly outEdges = new Map<string, KnowledgeRelation[]>();

  async upsertNodeAsync(node: KnowledgeNode): Promise<void> {
    if (node == null) throw new Error("node required");
    if (!node.id || node.id.trim().length === 0) throw new Error("Id required");
    this.nodes.set(node.id, node);
  }

  async upsertRelationAsync(rel: KnowledgeRelation): Promise<void> {
    if (rel == null) throw new Error("rel required");
    let list = this.outEdges.get(rel.fromId);
    if (list === undefined) {
      list = [];
      this.outEdges.set(rel.fromId, list);
    }
    // RemoveAll(r => r.ToId == rel.ToId && r.Relation == rel.Relation) then Add.
    for (let i = list.length - 1; i >= 0; i--) {
      if (list[i].toId === rel.toId && list[i].relation === rel.relation) list.splice(i, 1);
    }
    list.push(rel);
  }

  async neighboursAsync(id: string): Promise<readonly KnowledgeNode[]> {
    if (!id || id.trim().length === 0) throw new Error("id required");
    const rels = this.outEdges.get(id);
    if (rels === undefined) return [];
    return rels
      .map((r) => this.nodes.get(r.toId))
      .filter((n): n is KnowledgeNode => n !== undefined);
  }
}

// ─── shared sort helpers (LINQ-stable) ───────────────────────────────

function clamp(v: number, lo: number, hi: number): number {
  return Math.min(hi, Math.max(lo, v));
}

/** Descending numeric sort, stable (LINQ OrderByDescending). */
function stableSortByDesc<T>(items: T[], key: (t: T) => number): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const ka = key(a.item);
      const kb = key(b.item);
      if (kb > ka) return 1;
      if (kb < ka) return -1;
      return a.index - b.index;
    })
    .map((x) => x.item);
}

/** Ascending numeric sort, stable (LINQ OrderBy). */
function stableSortByAsc<T>(items: T[], key: (t: T) => number): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const ka = key(a.item);
      const kb = key(b.item);
      if (ka > kb) return 1;
      if (ka < kb) return -1;
      return a.index - b.index;
    })
    .map((x) => x.item);
}

/** Ascending ordinal string sort, stable (LINQ OrderBy on string). */
function stableSortBy<T>(items: T[], key: (t: T) => string): T[] {
  return items
    .map((item, index) => ({ item, index }))
    .sort((a, b) => {
      const ka = key(a.item);
      const kb = key(b.item);
      // Ordinal (UTF-16 code unit) comparison, matching StringComparer.Ordinal.
      if (ka > kb) return 1;
      if (ka < kb) return -1;
      return a.index - b.index;
    })
    .map((x) => x.item);
}
