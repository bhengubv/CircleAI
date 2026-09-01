/**
 * workflows/paca_realtime
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_realtime.ts
//
// (3.3.0) Realtime fan-out for paca workflows (PacaRealtime.cs): pub/sub with
// permission-aware rooms, query-invalidation events, collaborative document
// editing, agent activity feed. The Socket.IO / Valkey transport is
// host-supplied via IRealtimeBroadcaster.
//
// The C# abstract-record hierarchy (RealtimePacaEvent + 5 sealed subtypes) is
// modelled as a discriminated union tagged by `kind`. Constructor helpers keep
// call-sites terse and set the tag.

import { type ConversationStep } from "./conversations";

/** Discriminator tags for the {@link RealtimePacaEvent} union. */
export enum RealtimePacaEventKind {
  TaskUpdated = "task-updated",
  QueryInvalidation = "query-invalidation",
  DocCursorMove = "doc-cursor-move",
  AgentActivity = "agent-activity",
  ConversationStep = "conversation-step",
}

/** (3.3.0) Common shape of every realtime event. Mirrors C# `RealtimePacaEvent` base record. */
interface RealtimePacaEventBase {
  readonly projectId: string;
  readonly at: Date;
}

/** Task changed. Mirrors C# `TaskUpdatedEvent`. */
export interface TaskUpdatedEvent extends RealtimePacaEventBase {
  readonly kind: RealtimePacaEventKind.TaskUpdated;
  readonly taskNumber: number;
}

/** Explicit client query-cache invalidation. Mirrors C# `QueryInvalidationEvent`. */
export interface QueryInvalidationEvent extends RealtimePacaEventBase {
  readonly kind: RealtimePacaEventKind.QueryInvalidation;
  readonly queryKey: string;
}

/** Collaborator cursor moved in a doc. Mirrors C# `DocCursorMoveEvent`. */
export interface DocCursorMoveEvent extends RealtimePacaEventBase {
  readonly kind: RealtimePacaEventKind.DocCursorMove;
  readonly docId: string;
  readonly memberId: string;
  readonly cursorOffset: number;
}

/** Agent activity feed entry. Mirrors C# `AgentActivityEvent`. */
export interface AgentActivityEvent extends RealtimePacaEventBase {
  readonly kind: RealtimePacaEventKind.AgentActivity;
  readonly agentMemberId: string;
  readonly action: string;
  readonly detailJson: string;
}

/** A conversation produced a step. Mirrors C# `ConversationStepEvent`. */
export interface ConversationStepEvent extends RealtimePacaEventBase {
  readonly kind: RealtimePacaEventKind.ConversationStep;
  readonly conversationId: string;
  readonly step: ConversationStep;
}

/** (3.3.0) Realtime event union. Mirrors the C# `RealtimePacaEvent` record hierarchy. */
export type RealtimePacaEvent =
  | TaskUpdatedEvent
  | QueryInvalidationEvent
  | DocCursorMoveEvent
  | AgentActivityEvent
  | ConversationStepEvent;

/** Constructs a {@link TaskUpdatedEvent}. */
export function taskUpdatedEvent(projectId: string, at: Date, taskNumber: number): TaskUpdatedEvent {
  return { kind: RealtimePacaEventKind.TaskUpdated, projectId, at, taskNumber };
}

/** Constructs a {@link QueryInvalidationEvent}. */
export function queryInvalidationEvent(projectId: string, at: Date, queryKey: string): QueryInvalidationEvent {
  return { kind: RealtimePacaEventKind.QueryInvalidation, projectId, at, queryKey };
}

/** Constructs a {@link DocCursorMoveEvent}. */
export function docCursorMoveEvent(
  projectId: string,
  at: Date,
  docId: string,
  memberId: string,
  cursorOffset: number,
): DocCursorMoveEvent {
  return { kind: RealtimePacaEventKind.DocCursorMove, projectId, at, docId, memberId, cursorOffset };
}

/** Constructs an {@link AgentActivityEvent}. */
export function agentActivityEvent(
  projectId: string,
  at: Date,
  agentMemberId: string,
  action: string,
  detailJson: string,
): AgentActivityEvent {
  return { kind: RealtimePacaEventKind.AgentActivity, projectId, at, agentMemberId, action, detailJson };
}

/** Constructs a {@link ConversationStepEvent}. */
export function conversationStepEvent(
  projectId: string,
  at: Date,
  conversationId: string,
  step: ConversationStep,
): ConversationStepEvent {
  return { kind: RealtimePacaEventKind.ConversationStep, projectId, at, conversationId, step };
}

/** (3.3.0) Host-supplied broadcaster (Socket.IO / Valkey Streams / etc.). Mirrors C# `IRealtimeBroadcaster`. */
export interface IRealtimeBroadcaster {
  broadcastAsync(room: string, ev: RealtimePacaEvent, signal?: AbortSignal): Promise<void>;
}

/** (3.3.0) Permission check — resolves true if the member may join the room. Mirrors C# `PermissionCheck` delegate. */
export type PermissionCheck = (memberId: string, room: string, signal?: AbortSignal) => Promise<boolean>;

/** (3.3.0) Realtime hub: routes events into rooms, gates joins with a permission check. Mirrors C# `PacaRealtimeHub`. */
export class PacaRealtimeHub {
  private readonly broadcaster: IRealtimeBroadcaster;
  private readonly permission: PermissionCheck;
  private readonly membersByRoom = new Map<string, Set<string>>();

  constructor(broadcaster: IRealtimeBroadcaster, permission?: PermissionCheck | null) {
    if (broadcaster == null) throw new Error("broadcaster required");
    this.broadcaster = broadcaster;
    this.permission = permission ?? ((): Promise<boolean> => Promise.resolve(true));
  }

  /** (3.3.0) Member tries to join a room. Resolves true if permission allowed. */
  async joinAsync(memberId: string, room: string, signal?: AbortSignal): Promise<boolean> {
    if (memberId == null) throw new Error("memberId required");
    if (room == null) throw new Error("room required");
    if (!(await this.permission(memberId, room, signal))) return false;
    let members = this.membersByRoom.get(room);
    if (members === undefined) {
      members = new Set<string>();
      this.membersByRoom.set(room, members);
    }
    members.add(memberId);
    return true;
  }

  leave(memberId: string, room: string): void {
    const bucket = this.membersByRoom.get(room);
    if (bucket !== undefined) bucket.delete(memberId);
  }

  members(room: string): readonly string[] {
    const bucket = this.membersByRoom.get(room);
    return bucket === undefined ? [] : [...bucket];
  }

  /** (3.3.0) Publish an event to the project's main room. */
  publishAsync(ev: RealtimePacaEvent, signal?: AbortSignal): Promise<void> {
    if (ev == null) throw new Error("ev required");
    return this.broadcaster.broadcastAsync(`project:${ev.projectId}`, ev, signal);
  }

  /** (3.3.0) Publish to a doc collaboration sub-room. */
  publishToDocAsync(docId: string, ev: RealtimePacaEvent, signal?: AbortSignal): Promise<void> {
    return this.broadcaster.broadcastAsync(`doc:${docId}`, ev, signal);
  }
}

/** (3.3.0) Helper that maps known events to query-invalidation keys for client UIs. Mirrors C# `QueryInvalidation`. */
export const QueryInvalidation = {
  keysFor(ev: RealtimePacaEvent): readonly string[] {
    switch (ev.kind) {
      case RealtimePacaEventKind.TaskUpdated:
        return [`tasks/${ev.projectId}`, `task/${ev.projectId}/${ev.taskNumber}`];
      case RealtimePacaEventKind.AgentActivity:
        return [`activity/${ev.projectId}`, `agent/${ev.agentMemberId}`];
      case RealtimePacaEventKind.ConversationStep:
        return [`conversation/${ev.conversationId}`, `conversations/${ev.projectId}`];
      case RealtimePacaEventKind.DocCursorMove:
        return [`doc/${ev.docId}/cursors`];
      case RealtimePacaEventKind.QueryInvalidation:
        return [ev.queryKey];
      default:
        return [];
    }
  },
} as const;
