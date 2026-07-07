// companion/session.ts
// The conscious loop: a concrete ICompanionSession that recalls from fused
// memory, persists each turn, and encodes it into the graph off the hot path.
// Ported from Circle.AI.Companion (CompanionSession) — the C# reference.
//
// This is the wire that makes the memory-brain a brain instead of a stub: on
// every turn it (1) recalls the most relevant memories + the user's own facts
// and injects them into the system prompt, (2) calls the generator, (3) persists
// the exchange to episodic memory, and (4) hands it to the background encoder so
// the knowledge graph fills for future associative recall.

import type {
  CompanionContext,
  CompanionTurn,
  ICompanionSession,
  InterfaceKind,
  ProactiveMessageHandler,
} from "./index.js";
import type { ChatMessage, IChatGenerator } from "../inference/index.js";
import type { EpisodicMemoryEntry, IEpisodicMemoryStore } from "../memory/index.js";
import type { IRecall } from "../memory/recall.js";
import type { CompanionMemoryEncoder } from "./memory_encoder.js";
import type { SelfBeliefStore } from "./belief.js";
import { randomUUID } from "node:crypto";

/** Construction-time configuration for a {@link CompanionSession}. */
export interface CompanionSessionOptions {
  readonly sessionId: string;
  readonly identityId: string;
  readonly interface: InterfaceKind;
  readonly displayName?: string;
  readonly preferredLanguage?: string | null;
  /** Static persona hint block prepended to the system prompt. */
  readonly personaHints?: string;
  /** Static affect hint block prepended to the system prompt. */
  readonly affectSummary?: string;
  readonly activeGoals?: readonly string[];
  /** How many memories to recall per turn. Default 5. */
  readonly recallTopK?: number;
  /** Optional app context stamped onto persisted episodes. */
  readonly appContext?: string;
  /** Background graph/belief encoder. When null, turns are not encoded. */
  readonly encoder?: CompanionMemoryEncoder | null;
  /** The user's own facts, surfaced into the system prompt. */
  readonly beliefs?: SelfBeliefStore | null;
  /** Optional embedder for associative episodic recall; null → recency recall. */
  readonly embedder?: ((text: string) => Promise<number[] | null>) | null;
}

/** A companion session that thinks with fused memory and remembers what it learns. */
export class CompanionSession implements ICompanionSession {
  readonly sessionId: string;
  readonly identityId: string;
  readonly interface: InterfaceKind;
  onProactiveMessageReady: ProactiveMessageHandler | null = null;

  private readonly generator: IChatGenerator;
  private readonly episodic: IEpisodicMemoryStore;
  private readonly recall: IRecall;
  private readonly opts: CompanionSessionOptions;
  private readonly _history: CompanionTurn[] = [];
  private _context: CompanionContext;

  constructor(
    generator: IChatGenerator,
    episodic: IEpisodicMemoryStore,
    recall: IRecall,
    opts: CompanionSessionOptions,
  ) {
    if (!generator) throw new Error("generator required");
    if (!episodic) throw new Error("episodic required");
    if (!recall) throw new Error("recall required");
    this.generator = generator;
    this.episodic = episodic;
    this.recall = recall;
    this.opts = opts;
    this.sessionId = opts.sessionId;
    this.identityId = opts.identityId;
    this.interface = opts.interface;
    this._context = this.buildContext([]);
  }

  get history(): readonly CompanionTurn[] {
    return this._history;
  }

  async sendAsync(message: string): Promise<string> {
    const prepared = await this.prepareAsync(message);
    const reply = await this.generator.generateAsync(prepared.messages);
    await this.recordTurnAsync(message, reply, prepared.queryEmbedding, prepared.snippets);
    return reply;
  }

  async *streamAsync(message: string): AsyncGenerator<string> {
    const prepared = await this.prepareAsync(message);
    let reply = "";
    for await (const chunk of this.generator.streamAsync(prepared.messages)) {
      reply += chunk;
      yield chunk;
    }
    await this.recordTurnAsync(message, reply, prepared.queryEmbedding, prepared.snippets);
  }

  async agentAsync(instruction: string): Promise<string> {
    // Pilot: no tool-execution loop yet — agentic tool calling is a later slice.
    // Falls back to a plain reply so the surface is complete.
    return this.sendAsync(instruction);
  }

  getContext(): CompanionContext {
    return this._context;
  }

  async refreshContextAsync(): Promise<void> {
    const hits = await this.recall.recallAsync("", null, this.recallTopK());
    this._context = this.buildContext(hits.map((h) => h.item.text));
  }

  async signalFeedbackAsync(_positive: boolean, _note?: string): Promise<void> {
    // Pilot: feedback is accepted but not yet routed to a feedback store / affect
    // update. Wired in a later slice.
  }

  // ── internals ──────────────────────────────────────────────────────────────

  private async prepareAsync(
    message: string,
  ): Promise<{ messages: ChatMessage[]; queryEmbedding: number[] | null; snippets: string[] }> {
    // Recall runs BEFORE the current turn is persisted, so it draws on prior
    // memory, never echoes the message back.
    const queryEmbedding = this.opts.embedder ? await this.opts.embedder(message) : null;
    const hits = await this.recall.recallAsync(message, queryEmbedding, this.recallTopK());
    const snippets = hits.map((h) => h.item.text);

    const messages: ChatMessage[] = [{ role: "system", content: this.buildSystemPrompt(snippets) }];
    for (const turn of this._history) messages.push({ role: turn.role, content: turn.content });
    messages.push({ role: "user", content: message });

    return { messages, queryEmbedding, snippets };
  }

  private async recordTurnAsync(
    userText: string,
    reply: string,
    queryEmbedding: number[] | null,
    snippets: string[],
  ): Promise<void> {
    const episodeId = randomUUID();
    const entry: EpisodicMemoryEntry = {
      id: episodeId,
      recordedAtUtc: new Date(),
      userText,
      assistantText: reply,
      appContext: this.opts.appContext,
      embedding: queryEmbedding ?? undefined,
    };
    await this.episodic.addAsync(entry);

    // Off the hot path: fill the graph + form attributed beliefs for next time.
    this.opts.encoder?.enqueue(userText, reply, episodeId);

    const now = new Date();
    this._history.push({ role: "user", content: userText, timestamp: now });
    this._history.push({ role: "assistant", content: reply, timestamp: now });
    this._context = this.buildContext(snippets);
  }

  private buildSystemPrompt(snippets: string[]): string {
    const parts: string[] = [];
    if (this.opts.personaHints) parts.push(this.opts.personaHints.trim());
    if (this.opts.affectSummary) parts.push(this.opts.affectSummary.trim());

    const facts = this.userFacts();
    if (facts.length > 0) {
      parts.push("[What you know about the user]\n" + facts.map((f) => "- " + f).join("\n"));
    }
    if (snippets.length > 0) {
      parts.push("[Relevant memories]\n" + snippets.map((s) => "- " + s).join("\n"));
    }
    return parts.join("\n\n");
  }

  private userFacts(): string[] {
    if (!this.opts.beliefs) return [];
    return this.opts.beliefs.selfFacts().map((f) => f.object);
  }

  private buildContext(snippets: string[]): CompanionContext {
    return {
      identityId: this.identityId,
      displayName: this.opts.displayName ?? "",
      preferredLanguage: this.opts.preferredLanguage ?? null,
      interface: this.interface,
      personaHints: this.opts.personaHints ?? "",
      affectSummary: this.opts.affectSummary ?? "",
      recentMemorySnippets: snippets,
      activeGoals: this.opts.activeGoals ?? [],
      contextBuiltAt: new Date(),
    };
  }

  private recallTopK(): number {
    return this.opts.recallTopK ?? 5;
  }
}
