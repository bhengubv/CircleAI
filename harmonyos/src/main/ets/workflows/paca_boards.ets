/**
 * workflows/paca_boards
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_boards.ts
//
// (3.3.0) Sprintboard surface ported from paca (PacaBoards.cs): rich JSON
// description, custom fields, story points, importance, parent/child
// relations, status columns with position-ordered workflow, drag-and-drop
// status transitions, sprints with lifecycle states, Scrumban swimlanes,
// per-view persistent configs (filters + sort + visible fields), tags,
// lazy-load pagination per column.

import { InMemoryPacaStore, type PacaTask } from "./paca_projects";

/** (3.3.0) Sprint lifecycle. Mirrors C# `SprintState`. */
export enum SprintState {
  Planning = 0,
  Active = 1,
  Completed = 2,
}

/** (3.3.0) Status column in the workflow. Mirrors C# `StatusColumn`. */
export interface StatusColumn {
  /** "todo" / "in_progress" / "in_review" / "done". */
  readonly name: string;
  /** "open" / "in-flight" / "review" / "closed" / "cancelled" / "blocked". */
  readonly category: string;
  readonly position: number;
  readonly collapsed: boolean;
}

/** Constructs a {@link StatusColumn}. */
export function statusColumn(name: string, category: string, position: number, collapsed: boolean): StatusColumn {
  return { name, category, position, collapsed };
}

/** (3.3.0) Sprint. Mirrors C# `PacaSprint`. */
export interface PacaSprint {
  readonly id: string;
  readonly projectId: string;
  readonly name: string;
  readonly goal: string;
  readonly startDate: Date;
  readonly endDate: Date;
  readonly state: SprintState;
}

/** (3.3.0) Extra board-only metadata on top of {@link PacaTask}. Mirrors C# `TaskBoardMetadata`. */
export interface TaskBoardMetadata {
  readonly projectId: string;
  readonly number: number;
  readonly storyPoints: number;
  /** 0..5. */
  readonly importance: number;
  readonly assigneeMemberId: string | null;
  readonly reporterMemberId: string | null;
  readonly parentTaskNumber: number | null;
  readonly sprintId: string | null;
  readonly tags: readonly string[];
  readonly customFields: ReadonlyMap<string, string>;
  readonly positionInColumn: number;
}

/** (3.3.0) A per-user / per-board "named view". Mirrors C# `BoardView`. */
export interface BoardView {
  readonly name: string;
  readonly filterTagsCsv: string | null;
  readonly filterAssignee: string | null;
  /** "importance" / "story_points" / "newest". */
  readonly sortBy: string | null;
  readonly sortDescending: boolean;
  readonly visibleColumns: readonly string[];
  readonly visibleFields: readonly string[];
}

/**
 * (3.3.0) Board service over a project. Sprints + columns + per-task metadata
 * + views. Mirrors C# `PacaBoard`.
 */
export class PacaBoard {
  private readonly tasks: InMemoryPacaStore;
  private readonly columns = new Map<string, StatusColumn>();
  private readonly sprints = new Map<string, PacaSprint>();
  private readonly metadata = new Map<string, TaskBoardMetadata>();
  private readonly views = new Map<string, BoardView>();
  private readonly clock: () => Date;

  constructor(tasks: InMemoryPacaStore, clock?: (() => Date) | null) {
    if (tasks == null) throw new Error("tasks required");
    this.tasks = tasks;
    this.clock = clock ?? ((): Date => new Date());
    this.addDefaultColumns();
  }

  private addDefaultColumns(): void {
    this.columns.set("todo", statusColumn("todo", "open", 0, false));
    this.columns.set("in_progress", statusColumn("in_progress", "in-flight", 1, false));
    this.columns.set("in_review", statusColumn("in_review", "review", 2, false));
    this.columns.set("done", statusColumn("done", "closed", 3, false));
    this.columns.set("cancelled", statusColumn("cancelled", "cancelled", 4, false));
    this.columns.set("blocked", statusColumn("blocked", "blocked", 5, true));
  }

  get columnList(): readonly StatusColumn[] {
    return [...this.columns.values()].sort((a, b) => a.position - b.position);
  }

  addColumn(col: StatusColumn): void {
    if (col == null) throw new Error("col required");
    this.columns.set(col.name, col);
  }

  collapseColumn(name: string, collapsed: boolean): void {
    const col = this.columns.get(name);
    if (col !== undefined) {
      this.columns.set(name, { ...col, collapsed });
    }
  }

  /** (3.3.0) Move a task between status columns, updating its in-column position. */
  moveTask(projectId: string, number: number, newStatus: string, newPosition: number): void {
    const task =
      this.tasks.getTaskByReference(projectId, `${projectId}-${number}`) ??
      this.tasks.listTasks(projectId).find((t) => t.number === number) ??
      null;
    if (task === null) throw new Error("Task not found.");
    if (!this.columns.has(newStatus)) throw new Error(`Unknown status '${newStatus}'.`);

    this.tasks.updateTask({ ...task, status: newStatus });
    const meta: TaskBoardMetadata = { ...this.getOrCreateMetadata(projectId, number), positionInColumn: newPosition };
    this.metadata.set(PacaBoard.metaKey(projectId, number), meta);
  }

  /** (3.3.0) Attach board metadata to an existing task. */
  setTaskMetadata(metadata: TaskBoardMetadata): void {
    if (metadata == null) throw new Error("metadata required");
    this.metadata.set(PacaBoard.metaKey(metadata.projectId, metadata.number), metadata);
  }

  getTaskMetadata(projectId: string, number: number): TaskBoardMetadata | null {
    return this.metadata.get(PacaBoard.metaKey(projectId, number)) ?? null;
  }

  /** (3.3.0) Paginated column read for lazy loading. */
  tasksInColumn(projectId: string, status: string, skip = 0, take = 50): readonly PacaTask[] {
    const live = this.tasks.listTasks(projectId).filter((t) => t.status === status);
    return live
      .slice()
      .sort(
        (a, b) =>
          this.getOrCreateMetadata(a.projectId, a.number).positionInColumn -
          this.getOrCreateMetadata(b.projectId, b.number).positionInColumn,
      )
      .slice(skip, skip + take);
  }

  /** (3.3.0) Tasks bucketed by sprint, useful for the Scrumban board. */
  tasksInSprint(sprintId: string): readonly PacaTask[] {
    const out: PacaTask[] = [];
    for (const m of this.metadata.values()) {
      if (m.sprintId !== sprintId) continue;
      const t = this.tasks.listTasks(m.projectId).find((x) => x.number === m.number);
      if (t !== undefined) out.push(t);
    }
    return out;
  }

  /** (3.3.0) Create a sprint in Planning. */
  createSprint(id: string, projectId: string, name: string, goal: string, start: Date, end: Date): PacaSprint {
    const s: PacaSprint = { id, projectId, name, goal, startDate: start, endDate: end, state: SprintState.Planning };
    this.sprints.set(id, s);
    return s;
  }

  getSprint(id: string): PacaSprint | null {
    return this.sprints.get(id) ?? null;
  }

  startSprint(id: string): PacaSprint {
    return this.transition(id, SprintState.Active);
  }

  completeSprint(id: string): PacaSprint {
    return this.transition(id, SprintState.Completed);
  }

  private transition(id: string, to: SprintState): PacaSprint {
    const sprint = this.sprints.get(id);
    if (sprint === undefined) throw new Error(`Sprint '${id}' not found.`);
    const updated: PacaSprint = { ...sprint, state: to };
    this.sprints.set(id, updated);
    return updated;
  }

  /** (3.3.0) Save a named view (filters + sort + visible fields). */
  saveView(view: BoardView): void {
    this.views.set(view.name, view);
  }

  getView(name: string): BoardView | null {
    return this.views.get(name) ?? null;
  }

  listViews(): readonly BoardView[] {
    return [...this.views.values()].sort((a, b) => a.name.localeCompare(b.name));
  }

  private getOrCreateMetadata(projectId: string, number: number): TaskBoardMetadata {
    const key = PacaBoard.metaKey(projectId, number);
    let meta = this.metadata.get(key);
    if (meta === undefined) {
      meta = {
        projectId,
        number,
        storyPoints: 0,
        importance: 3,
        assigneeMemberId: null,
        reporterMemberId: null,
        parentTaskNumber: null,
        sprintId: null,
        tags: [],
        customFields: new Map<string, string>(),
        positionInColumn: 0,
      };
      this.metadata.set(key, meta);
    }
    return meta;
  }

  private static metaKey(projectId: string, number: number): string {
    return `${projectId}/${number}`;
  }
}
