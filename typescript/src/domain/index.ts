// domain/index.ts
//
// Full-parity port of CircleAI.Domain (C#). C# is the exact spec.
//
// Domain-specialist plug points: food embeddings, finance retrieval + agent,
// presentation generator, job-search pipeline, mem-palace + HippoRAG memory,
// swarm coordination, and personal LoRA. Contracts + deterministic in-memory
// implementations + fail-safe Null* defaults.
//
// Type mappings (C# → TS):
//   record                                → readonly interface (+ positional factory)
//   float[]                               → Float32Array (Math.fround at float sites)
//   IReadOnlyList<T>                      → readonly T[]
//   IReadOnlyDictionary<string,string>?   → Readonly<Record<string,string>> | null
//   ValueTask<T>                          → Promise<T>
//   ConcurrentDictionary (Ordinal)        → Map<string, T>
//   ConcurrentDictionary (OrdinalIgnore)  → case-folded key Map

// ─────────────────────────────────────────────────────────────────────────────
// Food (EPICure)
// ─────────────────────────────────────────────────────────────────────────────

/** One ingredient with optional canonical form + quantity. Mirrors C# `Ingredient`. */
export interface Ingredient {
  readonly name: string;
  readonly canonical: string | null;
  readonly quantity: string | null;
}

/** Constructs an {@link Ingredient}. */
export function ingredient(name: string, canonical: string | null = null, quantity: string | null = null): Ingredient {
  return { name, canonical, quantity };
}

/** Food / ingredient embedding store (EPICure-backed). Mirrors C# `IFoodEmbeddings`. */
export interface IFoodEmbeddings {
  readonly backendId: string;
  embedAsync(ingredient: Ingredient): Promise<Float32Array>;
  substitutesAsync(ingredient: Ingredient, topK?: number): Promise<readonly Ingredient[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Finance (quant-mind + dexter)
// ─────────────────────────────────────────────────────────────────────────────

/** A finance snippet. Mirrors C# `FinanceSnippet`. */
export interface FinanceSnippet {
  readonly text: string;
  readonly source: string;
  readonly score: number;
}

/** Constructs a {@link FinanceSnippet}. */
export function financeSnippet(text: string, source: string, score: number): FinanceSnippet {
  return { text, source, score: Math.fround(score) };
}

/** Quant-finance RAG retrieval. Mirrors C# `IFinanceRetrieval`. */
export interface IFinanceRetrieval {
  readonly backendId: string;
  retrieveAsync(query: string, topK?: number): Promise<readonly FinanceSnippet[]>;
}

/** A finance finding. Mirrors C# `FinanceFinding`. */
export interface FinanceFinding {
  readonly subject: string;
  readonly summary: string;
  readonly citations: readonly string[];
}

/** Constructs a {@link FinanceFinding}. */
export function financeFinding(subject: string, summary: string, citations: readonly string[]): FinanceFinding {
  return { subject, summary, citations };
}

/** Autonomous financial-research agent (dexter pattern). Mirrors C# `IFinancialAgent`. */
export interface IFinancialAgent {
  readonly backendId: string;
  researchAsync(question: string): Promise<readonly FinanceFinding[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Presentations (presenton)
// ─────────────────────────────────────────────────────────────────────────────

/** A slide outline. Mirrors C# `SlideOutline`. */
export interface SlideOutline {
  readonly title: string;
  readonly body: string;
  readonly bullets: readonly string[] | null;
}

/** Constructs a {@link SlideOutline}. */
export function slideOutline(title: string, body: string, bullets: readonly string[] | null = null): SlideOutline {
  return { title, body, bullets };
}

/** A generated presentation. Mirrors C# `GeneratedPresentation`. */
export interface GeneratedPresentation {
  readonly slides: readonly SlideOutline[];
  readonly theme: string;
  readonly format: string;
}

/** Constructs a {@link GeneratedPresentation}. */
export function generatedPresentation(
  slides: readonly SlideOutline[],
  theme: string,
  format: string,
): GeneratedPresentation {
  return { slides, theme, format };
}

/** AI presentation generator (presenton pattern). Mirrors C# `IPresentationGenerator`. */
export interface IPresentationGenerator {
  readonly backendId: string;
  generateAsync(topic: string, targetSlideCount?: number, theme?: string | null): Promise<GeneratedPresentation>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Job search (career-ops)
// ─────────────────────────────────────────────────────────────────────────────

/** A job-application draft. Mirrors C# `JobApplicationDraft`. */
export interface JobApplicationDraft {
  readonly resumeText: string;
  readonly coverLetterText: string;
  readonly keyMatches: readonly string[];
}

/** Constructs a {@link JobApplicationDraft}. */
export function jobApplicationDraft(
  resumeText: string,
  coverLetterText: string,
  keyMatches: readonly string[],
): JobApplicationDraft {
  return { resumeText, coverLetterText, keyMatches };
}

/** Job-search pipeline (career-ops). Mirrors C# `IJobSearchPipeline`. */
export interface IJobSearchPipeline {
  readonly backendId: string;
  draftApplicationAsync(roleDescription: string, candidateProfileText: string): Promise<JobApplicationDraft>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Memory upgrades (mempalace + HippoRAG)
// ─────────────────────────────────────────────────────────────────────────────

/** A memory item. Mirrors C# `MemoryItem`. */
export interface MemoryItem {
  readonly id: string;
  readonly text: string;
  readonly metadata: Readonly<Record<string, string>> | null;
}

/** Constructs a {@link MemoryItem}. */
export function memoryItem(id: string, text: string, metadata: Readonly<Record<string, string>> | null = null): MemoryItem {
  return { id, text, metadata };
}

/** A memory recall hit. Mirrors C# `MemoryHit`. */
export interface MemoryHit {
  readonly item: MemoryItem;
  readonly score: number;
}

/** Constructs a {@link MemoryHit}. */
export function memoryHit(item: MemoryItem, score: number): MemoryHit {
  return { item, score: Math.fround(score) };
}

/** MemPalace-pattern long-term memory. Mirrors C# `IMemPalaceStore`. */
export interface IMemPalaceStore {
  readonly backendId: string;
  upsertAsync(item: MemoryItem): Promise<void>;
  recallAsync(query: string, topK?: number): Promise<readonly MemoryHit[]>;
}

/** HippoRAG-pattern memory + KG + Personalized PageRank. Mirrors C# `IHippoRagStore`. */
export interface IHippoRagStore {
  readonly backendId: string;
  indexAsync(item: MemoryItem): Promise<void>;
  multiHopRecallAsync(query: string, topK?: number): Promise<readonly MemoryHit[]>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Swarm (MiroFish)
// ─────────────────────────────────────────────────────────────────────────────

/** A swarm peer. Mirrors C# `SwarmPeer`. */
export interface SwarmPeer {
  readonly peerId: string;
  readonly capability: string;
  readonly health: number;
}

/** Constructs a {@link SwarmPeer}. */
export function swarmPeer(peerId: string, capability: string, health: number): SwarmPeer {
  return { peerId, capability, health: Math.fround(health) };
}

/** Multi-device coordination over AetherNet (MiroFish-pattern). Mirrors C# `ISwarmCoordinator`. */
export interface ISwarmCoordinator {
  readonly backendId: string;
  listPeersAsync(): Promise<readonly SwarmPeer[]>;
  chooseDelegateAsync(capability: string): Promise<string | null>;
}

// ─────────────────────────────────────────────────────────────────────────────
// Personal LoRA (RT-10)
// ─────────────────────────────────────────────────────────────────────────────

/** A LoRA training summary. Mirrors C# `LoRATrainingSummary`. */
export interface LoRATrainingSummary {
  readonly adapterId: string;
  readonly stepsTrained: number;
  readonly finalLoss: number;
}

/** Constructs a {@link LoRATrainingSummary}. */
export function loRATrainingSummary(adapterId: string, stepsTrained: number, finalLoss: number): LoRATrainingSummary {
  return { adapterId, stepsTrained, finalLoss: Math.fround(finalLoss) };
}

/** LoRA adapter state. Mirrors C# `LoRAAdapterState`. */
export interface LoRAAdapterState {
  readonly adapterId: string;
  readonly steps: number;
  readonly finalLoss: number;
  readonly trainedAtUtc: Date;
}

/** On-device personalisation via LoRA fine-tuning (RT-10). Mirrors C# `IPersonalLoRA`. */
export interface IPersonalLoRA {
  readonly backendId: string;
  trainAsync(adapterId: string, conversationSamples: readonly string[]): Promise<LoRATrainingSummary>;
  loadAdapterAsync(adapterId: string): Promise<void>;
  unloadAdapterAsync(adapterId: string): Promise<void>;
}

// ═════════════════════════════════════════════════════════════════════════════
// In-memory implementations
// ═════════════════════════════════════════════════════════════════════════════

/** Case-insensitive keyed map helper (mirrors ConcurrentDictionary OrdinalIgnoreCase). */
class CaseFoldMap<V> {
  private readonly inner = new Map<string, V>();
  get(key: string): V | undefined {
    return this.inner.get(key.toLowerCase());
  }
  set(key: string, value: V): void {
    this.inner.set(key.toLowerCase(), value);
  }
  has(key: string): boolean {
    return this.inner.has(key.toLowerCase());
  }
}

/** Food substitutes by canonical name. Mirrors C# `InMemoryFoodEmbeddings`. */
export class InMemoryFoodEmbeddings implements IFoodEmbeddings {
  private readonly embeds = new CaseFoldMap<Float32Array>();
  private readonly subs = new CaseFoldMap<Ingredient[]>();

  get backendId(): string {
    return "in-memory";
  }

  registerEmbedding(name: string, v: Float32Array): void {
    if (v == null) throw new Error("v required");
    this.embeds.set(name, v);
  }

  registerSubstitute(name: string, alt: Ingredient): void {
    if (alt == null) throw new Error("alt required");
    let list = this.subs.get(name);
    if (list === undefined) {
      list = [];
      this.subs.set(name, list);
    }
    list.push(alt);
  }

  async embedAsync(i: Ingredient): Promise<Float32Array> {
    if (i == null) throw new Error("i required");
    const v = this.embeds.get(i.name);
    if (v !== undefined) return v;
    // Deterministic hash-based 8-dim vector if no embedding was registered.
    const v2 = new Float32Array(8);
    const h = stringHashCodeIgnoreCase(i.name);
    for (let k = 0; k < 8; k++) v2[k] = Math.fround(((h >> (k * 4)) & 0xf) / 15);
    return v2;
  }

  async substitutesAsync(i: Ingredient, topK = 5): Promise<readonly Ingredient[]> {
    if (i == null) throw new Error("i required");
    if (topK <= 0) throw new Error("topK out of range");
    const list = this.subs.get(i.name);
    if (list === undefined) return [];
    return list.slice(0, topK);
  }
}

/** Finance retrieval over an in-memory corpus. Mirrors C# `InMemoryFinanceRetrieval`. */
export class InMemoryFinanceRetrieval implements IFinanceRetrieval {
  private readonly corpus: FinanceSnippet[] = [];

  get backendId(): string {
    return "in-memory";
  }

  add(s: FinanceSnippet): void {
    if (s == null) throw new Error("s required");
    this.corpus.push(s);
  }

  async retrieveAsync(query: string, topK = 5): Promise<readonly FinanceSnippet[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    const q = query.toLowerCase();
    return this.corpus
      .filter((s) => s.text.toLowerCase().includes(q))
      .sort((a, b) => b.score - a.score)
      .slice(0, topK);
  }
}

/** Multi-pass financial agent. Mirrors C# `MultiPassFinancialAgent`. */
export class MultiPassFinancialAgent implements IFinancialAgent {
  private readonly retr: IFinanceRetrieval;
  constructor(r: IFinanceRetrieval) {
    if (r == null) throw new Error("r required");
    this.retr = r;
  }

  get backendId(): string {
    return "multi-pass";
  }

  async researchAsync(question: string): Promise<readonly FinanceFinding[]> {
    if (question == null) throw new Error("question required");
    const subQuestions = MultiPassFinancialAgent.decompose(question);
    const findings: FinanceFinding[] = [];
    for (const sub of subQuestions) {
      const snippets = await this.retr.retrieveAsync(sub, 5);
      if (snippets.length === 0) continue;
      const bySource = groupBy(snippets, (s) => s.source);
      for (const [key, grp] of bySource) {
        const summary = [...grp]
          .sort((a, b) => b.score - a.score)
          .slice(0, 3)
          .map((s) => s.text)
          .join(" | ");
        findings.push(financeFinding(sub, summary, [key]));
      }
    }
    return findings;
  }

  private static decompose(question: string): readonly string[] {
    const subs: string[] = [question];
    if (question.toLowerCase().includes(" and ")) {
      for (const part of question.split(/ and /i)) {
        if (part.trim().length > 6) subs.push(part.trim());
      }
    }
    if (question.length > 60) {
      subs.push(question.split(",")[0].trim());
    }
    return [...new Set(subs)];
  }
}

/** Template presentation generator. Mirrors C# `TemplatePresentationGenerator`. */
export class TemplatePresentationGenerator implements IPresentationGenerator {
  get backendId(): string {
    return "template";
  }

  async generateAsync(topic: string, targetSlideCount = 10, theme: string | null = null): Promise<GeneratedPresentation> {
    if (topic == null || topic.trim().length === 0) throw new Error("topic required");
    if (targetSlideCount <= 0) throw new Error("targetSlideCount out of range");
    const slides: SlideOutline[] = [];
    slides.push(slideOutline(topic, "Overview", ["What is " + topic, "Why it matters", "What we'll cover"]));
    for (let i = 2; i < targetSlideCount; i++) {
      slides.push(slideOutline(`${topic} — Part ${i - 1}`, `Detail for part ${i - 1}`, ["Point A", "Point B", "Point C"]));
    }
    slides.push(slideOutline("Conclusion", `Summary of ${topic}`, ["Recap", "Next steps", "Questions"]));
    return generatedPresentation(slides, theme ?? "default", "markdown");
  }
}

/** Template job-search pipeline. Mirrors C# `TemplateJobSearchPipeline`. */
export class TemplateJobSearchPipeline implements IJobSearchPipeline {
  get backendId(): string {
    return "template";
  }

  async draftApplicationAsync(roleDescription: string, candidateProfileText: string): Promise<JobApplicationDraft> {
    if (roleDescription == null) throw new Error("roleDescription required");
    if (candidateProfileText == null) throw new Error("candidateProfileText required");
    const roleWords = TemplateJobSearchPipeline.extractKeyWords(roleDescription);
    const candWords = new Set(TemplateJobSearchPipeline.extractKeyWords(candidateProfileText));
    const matches = roleWords.filter((w) => candWords.has(w)).slice(0, 10);
    const resume = `${candidateProfileText.trim()}\n\nMatched skills: ${matches.join(", ")}`;
    const cover = `Dear Hiring Team,\n\nI am applying because my background (${matches
      .slice(0, 3)
      .join(", ")}) fits the role.\n\nRegards.`;
    return jobApplicationDraft(resume, cover, matches);
  }

  private static extractKeyWords(text: string): string[] {
    const seen = new Set<string>();
    const out: string[] = [];
    for (const w of text.split(/[ \n\r\t,.;:()]+/)) {
      if (w.length > 3) {
        const lw = w.trim().toLowerCase();
        if (!seen.has(lw)) {
          seen.add(lw);
          out.push(lw);
        }
      }
    }
    return out;
  }
}

/** In-memory MemPalace store. Mirrors C# `InMemoryMemPalaceStore`. */
export class InMemoryMemPalaceStore implements IMemPalaceStore {
  private readonly items = new Map<string, MemoryItem>();

  get backendId(): string {
    return "in-memory";
  }

  async upsertAsync(item: MemoryItem): Promise<void> {
    if (item == null) throw new Error("item required");
    if (item.id == null || item.id.trim().length === 0) throw new Error("Id required");
    this.items.set(item.id, item);
  }

  async recallAsync(query: string, topK = 5): Promise<readonly MemoryHit[]> {
    if (query == null) throw new Error("query required");
    if (topK <= 0) throw new Error("topK out of range");
    return [...this.items.values()]
      .map((i) => memoryHit(i, InMemoryMemPalaceStore.score(i.text, query)))
      .filter((h) => h.score > 0)
      .sort((a, b) => b.score - a.score)
      .slice(0, topK);
  }

  static score(body: string, query: string): number {
    if (!body || !query) return 0;
    const q = query.trim();
    const idx = body.toLowerCase().indexOf(q.toLowerCase());
    return idx < 0 ? 0 : Math.fround(1 / (1 + idx));
  }
}

/** In-memory HippoRAG store (multi-hop recall). Mirrors C# `InMemoryHippoRagStore`. */
export class InMemoryHippoRagStore implements IHippoRagStore {
  private readonly base = new InMemoryMemPalaceStore();

  get backendId(): string {
    return "in-memory";
  }

  async indexAsync(item: MemoryItem): Promise<void> {
    return this.base.upsertAsync(item);
  }

  async multiHopRecallAsync(query: string, topK = 5): Promise<readonly MemoryHit[]> {
    const first = await this.base.recallAsync(query, topK);
    if (first.length === 0) return first;
    const seed = first[0].item.text;
    const second = await this.base.recallAsync(seed, topK);
    // Union by item id (first occurrence wins), then top-K by score desc.
    const byId = new Map<string, MemoryHit>();
    for (const h of [...first, ...second]) {
      if (!byId.has(h.item.id)) byId.set(h.item.id, h);
    }
    return [...byId.values()].sort((a, b) => b.score - a.score).slice(0, topK);
  }
}

/** In-memory swarm coordinator. Mirrors C# `InMemorySwarmCoordinator`. */
export class InMemorySwarmCoordinator implements ISwarmCoordinator {
  private readonly peers = new Map<string, SwarmPeer>();

  get backendId(): string {
    return "in-memory";
  }

  register(p: SwarmPeer): void {
    if (p == null) throw new Error("p required");
    this.peers.set(p.peerId, p);
  }

  async listPeersAsync(): Promise<readonly SwarmPeer[]> {
    return [...this.peers.values()];
  }

  async chooseDelegateAsync(capability: string): Promise<string | null> {
    if (capability == null || capability.trim().length === 0) throw new Error("capability required");
    const cap = capability.toLowerCase();
    const candidates = [...this.peers.values()]
      .filter((p) => p.capability.toLowerCase() === cap)
      .sort((a, b) => b.health - a.health);
    return candidates.length > 0 ? candidates[0].peerId : null;
  }
}

/** In-memory personal LoRA with a simulated training loop. Mirrors C# `InMemoryPersonalLoRA`. */
export class InMemoryPersonalLoRA implements IPersonalLoRA {
  private readonly adapters = new Map<string, LoRAAdapterState>();
  private readonly loaded = new Set<string>();

  get backendId(): string {
    return "in-memory";
  }

  async trainAsync(adapterId: string, samples: readonly string[]): Promise<LoRATrainingSummary> {
    if (adapterId == null || adapterId.trim().length === 0) throw new Error("adapterId required");
    if (samples == null) throw new Error("samples required");
    if (samples.length === 0) throw new Error("at least one sample required");
    // Simulated training loop: each sample contributes a step. Final loss
    // decreases logarithmically with sample count.
    const steps = samples.length;
    const totalChars = samples.reduce((acc, s) => acc + (s?.length ?? 0), 0);
    const finalLoss = Math.fround(1 / (1 + Math.log(1 + steps)) + 1 / (1 + totalChars / 1000));
    const state: LoRAAdapterState = { adapterId, steps, finalLoss, trainedAtUtc: new Date() };
    this.adapters.set(adapterId, state);
    return loRATrainingSummary(adapterId, steps, finalLoss);
  }

  async loadAdapterAsync(adapterId: string): Promise<void> {
    if (adapterId == null || adapterId.trim().length === 0) throw new Error("adapterId required");
    if (!this.adapters.has(adapterId)) throw new Error(`Adapter '${adapterId}' not trained.`);
    this.loaded.add(adapterId);
  }

  async unloadAdapterAsync(adapterId: string): Promise<void> {
    if (adapterId == null || adapterId.trim().length === 0) throw new Error("adapterId required");
    this.loaded.delete(adapterId);
  }

  isLoaded(adapterId: string): boolean {
    return this.loaded.has(adapterId);
  }

  stateOf(adapterId: string): LoRAAdapterState | null {
    return this.adapters.get(adapterId) ?? null;
  }
}

// ═════════════════════════════════════════════════════════════════════════════
// Null* defaults
// ═════════════════════════════════════════════════════════════════════════════

/** Fail-safe {@link IFoodEmbeddings}. */
export class NullFoodEmbeddings implements IFoodEmbeddings {
  static readonly instance = new NullFoodEmbeddings();
  get backendId(): string {
    return "null";
  }
  async embedAsync(): Promise<Float32Array> {
    return new Float32Array(300);
  }
  async substitutesAsync(): Promise<readonly Ingredient[]> {
    return [];
  }
}

/** Fail-safe {@link IFinanceRetrieval}. */
export class NullFinanceRetrieval implements IFinanceRetrieval {
  static readonly instance = new NullFinanceRetrieval();
  get backendId(): string {
    return "null";
  }
  async retrieveAsync(): Promise<readonly FinanceSnippet[]> {
    return [];
  }
}

/** Fail-safe {@link IFinancialAgent}. */
export class NullFinancialAgent implements IFinancialAgent {
  static readonly instance = new NullFinancialAgent();
  get backendId(): string {
    return "null";
  }
  async researchAsync(): Promise<readonly FinanceFinding[]> {
    return [];
  }
}

/** Fail-safe {@link IPresentationGenerator}. */
export class NullPresentationGenerator implements IPresentationGenerator {
  static readonly instance = new NullPresentationGenerator();
  get backendId(): string {
    return "null";
  }
  async generateAsync(_topic: string, _targetSlideCount = 10, theme: string | null = null): Promise<GeneratedPresentation> {
    return generatedPresentation([], theme ?? "default", "json");
  }
}

/** Fail-safe {@link IJobSearchPipeline}. */
export class NullJobSearchPipeline implements IJobSearchPipeline {
  static readonly instance = new NullJobSearchPipeline();
  get backendId(): string {
    return "null";
  }
  async draftApplicationAsync(): Promise<JobApplicationDraft> {
    return jobApplicationDraft("", "", []);
  }
}

/** Fail-safe {@link IMemPalaceStore}. */
export class NullMemPalaceStore implements IMemPalaceStore {
  static readonly instance = new NullMemPalaceStore();
  get backendId(): string {
    return "null";
  }
  async upsertAsync(): Promise<void> {
    /* no-op */
  }
  async recallAsync(): Promise<readonly MemoryHit[]> {
    return [];
  }
}

/** Fail-safe {@link IHippoRagStore}. */
export class NullHippoRagStore implements IHippoRagStore {
  static readonly instance = new NullHippoRagStore();
  get backendId(): string {
    return "null";
  }
  async indexAsync(): Promise<void> {
    /* no-op */
  }
  async multiHopRecallAsync(): Promise<readonly MemoryHit[]> {
    return [];
  }
}

/** Fail-safe {@link ISwarmCoordinator}. */
export class NullSwarmCoordinator implements ISwarmCoordinator {
  static readonly instance = new NullSwarmCoordinator();
  get backendId(): string {
    return "null";
  }
  async listPeersAsync(): Promise<readonly SwarmPeer[]> {
    return [];
  }
  async chooseDelegateAsync(): Promise<string | null> {
    return null;
  }
}

/** Fail-safe {@link IPersonalLoRA}. */
export class NullPersonalLoRA implements IPersonalLoRA {
  static readonly instance = new NullPersonalLoRA();
  get backendId(): string {
    return "null";
  }
  async trainAsync(id: string): Promise<LoRATrainingSummary> {
    return loRATrainingSummary(id, 0, 0);
  }
  async loadAdapterAsync(): Promise<void> {
    /* no-op */
  }
  async unloadAdapterAsync(): Promise<void> {
    /* no-op */
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Helpers
// ─────────────────────────────────────────────────────────────────────────────

/** Groups items by a string key, preserving first-seen key order (LINQ GroupBy). */
function groupBy<T>(items: readonly T[], keyOf: (t: T) => string): Map<string, T[]> {
  const m = new Map<string, T[]>();
  for (const it of items) {
    const k = keyOf(it);
    const l = m.get(k) ?? [];
    l.push(it);
    m.set(k, l);
  }
  return m;
}

/**
 * Reproduces C# `string.GetHashCode(StringComparison.OrdinalIgnoreCase)` shape
 * only as a stable deterministic 32-bit hash of the upper-cased string. The C#
 * value itself is not portable; only determinism (same input → same 8-dim
 * vector within this runtime) is required by the fallback.
 */
function stringHashCodeIgnoreCase(s: string): number {
  const u = s.toUpperCase();
  let h = 0;
  for (let i = 0; i < u.length; i++) {
    h = (Math.imul(31, h) + u.charCodeAt(i)) | 0;
  }
  return h;
}
