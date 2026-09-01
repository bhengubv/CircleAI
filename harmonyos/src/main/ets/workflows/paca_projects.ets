/**
 * workflows/paca_projects
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// workflows/paca_projects.ts
//
// (3.3.0) Project + task primitives ported from paca (PacaProjects.cs).
// Auto-generates task IDs as <PROJECT_PREFIX>-N. Soft deletes via
// deletedAtUtc. Row-level project scoping via every query taking a projectId.
//
// Type mappings (C# → TS):
//   DateTimeOffset  → Date
//   DateTimeOffset? → Date | null
//   record `with`   → object spread with overrides
//   ConcurrentDictionary / lock → plain Map (single-threaded runtime)

/** (3.3.0) A workspace that contains tasks. Mirrors C# `PacaProject`. */
export interface PacaProject {
  readonly id: string;
  readonly name: string;
  /** Task-id prefix (e.g. "PACA"). */
  readonly prefix: string;
  /** Free-form JSON configuration bag. */
  readonly settingsJson: string;
  readonly createdAtUtc: Date;
  /** Soft-delete timestamp; null = live. */
  readonly deletedAtUtc: Date | null;
}

/** (3.3.0) A unit of work inside a project. Mirrors C# `PacaTask`. */
export interface PacaTask {
  readonly projectId: string;
  /** Sequential id within the project (PACA-1, PACA-2, …). */
  readonly number: number;
  readonly title: string;
  /** Rich-text JSON body (BlockNote shape). */
  readonly descriptionJson: string;
  readonly status: string;
  readonly createdAtUtc: Date;
  readonly deletedAtUtc: Date | null;
}

/** Format a task reference like "PACA-3". Mirrors C# `PacaTask.Reference`. */
export function taskReference(task: PacaTask, prefix: string): string {
  return `${prefix}-${task.number}`;
}

/**
 * (3.3.0) In-memory project + task store. Replace for production storage.
 * Mirrors C# `InMemoryPacaStore`.
 */
export class InMemoryPacaStore {
  private readonly projects = new Map<string, PacaProject>();
  private readonly tasksByProject = new Map<string, PacaTask[]>();
  private readonly nextNumber = new Map<string, number>();
  private readonly clock: () => Date;

  constructor(clock?: (() => Date) | null) {
    this.clock = clock ?? ((): Date => new Date());
  }

  /** (3.3.0) Create a new project. Throws if the id already exists. */
  createProject(id: string, name: string, prefix: string, settingsJson?: string | null): PacaProject {
    if (isBlank(id)) throw new Error("id required");
    if (isBlank(name)) throw new Error("name required");
    if (isBlank(prefix)) throw new Error("prefix required");

    if (this.projects.has(id)) {
      throw new Error(`Project '${id}' already exists.`);
    }
    const project: PacaProject = {
      id,
      name,
      prefix,
      settingsJson: settingsJson ?? "{}",
      createdAtUtc: this.clock(),
      deletedAtUtc: null,
    };
    this.projects.set(id, project);
    this.tasksByProject.set(id, []);
    this.nextNumber.set(id, 1);
    return project;
  }

  /** (3.3.0) Get a live project by id (excludes soft-deleted). */
  getProject(id: string): PacaProject | null {
    const p = this.projects.get(id);
    return p !== undefined && p.deletedAtUtc === null ? p : null;
  }

  /** (3.3.0) Soft-delete a project. Idempotent. */
  deleteProject(id: string): void {
    const existing = this.projects.get(id);
    if (existing === undefined || existing.deletedAtUtc !== null) return;
    this.projects.set(id, { ...existing, deletedAtUtc: this.clock() });
  }

  /** (3.3.0) Update the JSON settings bag on a project. */
  updateProjectSettings(projectId: string, newSettingsJson: string): PacaProject {
    const existing = this.getProject(projectId);
    if (existing === null) throw new Error(`Project '${projectId}' not found.`);
    const updated: PacaProject = { ...existing, settingsJson: newSettingsJson ?? "{}" };
    this.projects.set(projectId, updated);
    return updated;
  }

  /** (3.3.0) Add a task to a project. Auto-numbers it. */
  addTask(projectId: string, title: string, descriptionJson?: string | null, status = "todo"): PacaTask {
    const project = this.getProject(projectId);
    if (project === null) throw new Error(`Project '${projectId}' not found.`);
    const number = this.nextNumber.get(projectId) ?? 1;
    this.nextNumber.set(projectId, number + 1);
    const task: PacaTask = {
      projectId,
      number,
      title: title ?? "",
      descriptionJson: descriptionJson ?? "{}",
      status: status ?? "todo",
      createdAtUtc: this.clock(),
      deletedAtUtc: null,
    };
    const list = this.tasksByProject.get(projectId);
    if (list !== undefined) list.push(task);
    return task;
  }

  /** (3.3.0) List live tasks for a project, ordered by number ascending. */
  listTasks(projectId: string): readonly PacaTask[] {
    const list = this.tasksByProject.get(projectId);
    if (list === undefined) return [];
    return list.filter((t) => t.deletedAtUtc === null).sort((a, b) => a.number - b.number);
  }

  /** (3.3.0) Find one task by reference like "PACA-3". */
  getTaskByReference(projectId: string, reference: string): PacaTask | null {
    const project = this.getProject(projectId);
    if (project === null) return null;
    const expectedPrefix = project.prefix + "-";
    if (!reference.toLowerCase().startsWith(expectedPrefix.toLowerCase())) return null;
    const n = Number.parseInt(reference.slice(expectedPrefix.length), 10);
    if (Number.isNaN(n)) return null;
    const list = this.tasksByProject.get(projectId);
    if (list === undefined) return null;
    return list.find((t) => t.number === n && t.deletedAtUtc === null) ?? null;
  }

  /** (3.3.0) Update a task in place. Caller mutates via object spread. */
  updateTask(updated: PacaTask): void {
    if (updated == null) throw new Error("updated required");
    const list = this.tasksByProject.get(updated.projectId);
    if (list === undefined) return;
    for (let i = 0; i < list.length; i++) {
      if (list[i].number === updated.number) {
        list[i] = updated;
        return;
      }
    }
  }

  /** (3.3.0) Soft-delete a task. */
  deleteTask(projectId: string, number: number): void {
    const list = this.tasksByProject.get(projectId);
    if (list === undefined) return;
    for (let i = 0; i < list.length; i++) {
      if (list[i].number === number) {
        list[i] = { ...list[i], deletedAtUtc: this.clock() };
        return;
      }
    }
  }
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}
