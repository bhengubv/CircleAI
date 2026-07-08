// hosting/proactive_reasoning.ts
//
// Port of CircleAI.Hosting.IProactiveReasoningService +
// ProactiveReasoningService — B!'s ability to initiate contact rather than
// merely respond. Evaluates a prioritised list of ITriggerCondition; the first
// condition that fires drives a butler.askAsync check-in and raises a message
// event. Only one trigger fires per checkAsync call.

import type { AffectState, Goal, IAffectStore, IGoalStore } from "../memory/index.js";
import type { IAIService } from "./service.js";
import type { ITriggerCondition, ProactiveContext } from "./triggers.js";

/**
 * Event arguments emitted when B! generates a proactive message. Mirrors
 * CircleAI.Hosting.ProactiveMessageEventArgs.
 */
export interface ProactiveMessageEventArgs {
  readonly userId: string;
  readonly message: string;
  readonly triggerName: string;
  /** When the message was generated (ISO 8601 UTC). */
  readonly generatedUtc: string;
}

/** Handler for {@link IProactiveReasoningService.onProactiveMessageReady}. */
export type ProactiveMessageHandler = (args: ProactiveMessageEventArgs) => void;

/**
 * Evaluates trigger conditions and, when any fires, generates a proactive
 * check-in message unprompted. Mirrors
 * CircleAI.Hosting.IProactiveReasoningService.
 */
export interface IProactiveReasoningService {
  /** Evaluates all triggers and, when any fires, generates + raises a message. */
  checkAsync(userId: string): Promise<void>;
  /** Subscribe to proactive-message events. Returns an unsubscribe function. */
  onProactiveMessageReady(handler: ProactiveMessageHandler): () => void;
}

/**
 * Default {@link IProactiveReasoningService}. Mirrors
 * CircleAI.Hosting.ProactiveReasoningService.
 */
export class ProactiveReasoningService implements IProactiveReasoningService {
  private readonly butler: IAIService;
  private readonly goalStore: IGoalStore | null;
  private readonly affectStore: IAffectStore | null;
  private readonly triggers: readonly ITriggerCondition[];
  private readonly handlers = new Set<ProactiveMessageHandler>();

  constructor(
    butler: IAIService,
    goalStore: IGoalStore | null,
    affectStore: IAffectStore | null,
    triggers: readonly ITriggerCondition[],
  ) {
    if (!butler) throw new Error("butler required");
    if (!triggers) throw new Error("triggers required");
    this.butler = butler;
    this.goalStore = goalStore;
    this.affectStore = affectStore;
    this.triggers = triggers;
  }

  onProactiveMessageReady(handler: ProactiveMessageHandler): () => void {
    this.handlers.add(handler);
    return () => this.handlers.delete(handler);
  }

  async checkAsync(userId: string): Promise<void> {
    if (userId == null || userId.trim().length === 0)
      throw new Error("userId required");

    if (this.triggers.length === 0) return;

    // 1. Load affect state.
    let affect: AffectState | null = null;
    if (this.affectStore !== null) {
      try {
        affect = await this.affectStore.loadAsync(userId);
      } catch {
        /* affect load failed; continue */
      }
    }

    // 2. Load active goals.
    let activeGoals: readonly Goal[] = [];
    if (this.goalStore !== null) {
      try {
        activeGoals = await this.goalStore.getActiveAsync(userId);
      } catch {
        /* goal load failed; continue */
      }
    }

    // 3. Build context snapshot.
    const now = new Date();
    const timeSinceLastMs =
      affect !== null ? now.getTime() - affect.lastUpdatedUtc.getTime() : 0;

    const context: ProactiveContext = {
      userId,
      nowUtc: now,
      timeSinceLastInteractionMs: timeSinceLastMs,
      affectState: affect,
      activeGoals,
    };

    // 4. Check triggers in order — fire only the first one.
    for (const trigger of this.triggers) {
      let met: boolean;
      try {
        met = await trigger.isMetAsync(context);
      } catch {
        // trigger threw; skip.
        continue;
      }

      if (!met) continue;

      // 5. Build a proactive prompt.
      const prompt = buildProactivePrompt(userId, timeSinceLastMs, activeGoals);

      // 6. Generate the message.
      let message: string;
      try {
        message = await this.butler.askAsync(prompt);
      } catch {
        // butler.askAsync failed for this trigger; give up.
        return;
      }

      // 7. Raise the event.
      const args: ProactiveMessageEventArgs = {
        userId,
        message,
        triggerName: trigger.name,
        generatedUtc: new Date().toISOString(),
      };

      for (const h of this.handlers) {
        try {
          h(args);
        } catch {
          /* handler threw; non-fatal */
        }
      }

      // Only fire one trigger per call.
      return;
    }
  }
}

/** Mirrors ProactiveReasoningService.BuildProactivePrompt. */
function buildProactivePrompt(
  _userId: string,
  timeSinceLastInteractionMs: number,
  activeGoals: readonly Goal[],
): string {
  const parts: string[] = [];
  parts.push("You are B!. ");

  const totalMinutes = timeSinceLastInteractionMs / 60000;
  if (totalMinutes > 5) {
    const hours = Math.trunc(timeSinceLastInteractionMs / 3_600_000);
    const minutes = Math.trunc(totalMinutes % 60);
    if (hours > 0)
      parts.push(
        `The user has been away for approximately ${hours} hour${hours === 1 ? "" : "s"}. `,
      );
    else
      parts.push(
        `The user has been away for approximately ${minutes} minute${minutes === 1 ? "" : "s"}. `,
      );
  }

  if (activeGoals.length > 0) {
    parts.push(
      `They have ${activeGoals.length} active goal${activeGoals.length === 1 ? "" : "s"}: `,
    );
    for (let i = 0; i < activeGoals.length; i++) {
      parts.push('"');
      parts.push(activeGoals[i].title);
      parts.push('"');
      if (i < activeGoals.length - 1) parts.push(", ");
    }
    parts.push(". ");
  }

  parts.push("Generate a brief, friendly check-in message (1-2 sentences). ");
  parts.push(
    "Be warm, specific to their goals if you know them, and not intrusive.",
  );

  return parts.join("");
}
