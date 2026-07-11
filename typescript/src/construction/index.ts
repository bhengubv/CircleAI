// construction/index.ts
// Full-parity port of CircleAI.Construction (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Construction vertical: projects, tasks,
// cost entries, and spend / remaining-budget rollups. Plus the static
// ConstructionDomainContext.
//
// NOTE: The C# ConstructionCompanionAdapter (an ICompanionSession LLM-prompt
// wrapper) is intentionally NOT ported — consistent with the sibling
// domain-board ports.
//
// Type mappings (C# → TS):
//   record                           → readonly interface (+ positional factory)
//   decimal Budget / Amount          → number
//   bool Completed                   → boolean
//   DateTime StartOn / DueOn         → Date
//   DateTime? EndOn                  → Date | null
//   DateTimeOffset AtUtc             → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Complete                 — throws on unknown task; sets Completed=true.
//   OpenConstructionTasksFor — project's incomplete tasks, DueOn ascending.
//   SpendFor                 — sum of Amount over the project's cost entries.
//   RemainingBudget          — throws on unknown project; Budget − SpendFor.

/** A construction project. Mirrors C# `Project` record. */
export interface Project {
  readonly projectId: string;
  readonly name: string;
  /** Start date (C# `DateTime StartOn`). */
  readonly startOn: Date;
  /** End date, or null (C# `DateTime? EndOn`). */
  readonly endOn: Date | null;
  readonly budget: number;
  readonly currency: string;
}

/** Constructs a {@link Project}. */
export function project(
  projectId: string,
  name: string,
  startOn: Date,
  endOn: Date | null,
  budget: number,
  currency: string,
): Project {
  return { projectId, name, startOn, endOn, budget, currency };
}

/** A construction task. Mirrors C# `ConstructionTask` record. */
export interface ConstructionTask {
  readonly constructionTaskId: string;
  readonly projectId: string;
  readonly description: string;
  /** Due date (C# `DateTime DueOn`). */
  readonly dueOn: Date;
  readonly completed: boolean;
}

/** Constructs a {@link ConstructionTask}. */
export function constructionTask(
  constructionTaskId: string,
  projectId: string,
  description: string,
  dueOn: Date,
  completed: boolean,
): ConstructionTask {
  return { constructionTaskId, projectId, description, dueOn, completed };
}

/** A recorded project cost. Mirrors C# `CostEntry` record. */
export interface CostEntry {
  readonly entryId: string;
  readonly projectId: string;
  readonly category: string;
  readonly amount: number;
  /** UTC instant of the cost (C# `DateTimeOffset AtUtc`). */
  readonly atUtc: Date;
}

/** Constructs a {@link CostEntry}. */
export function costEntry(entryId: string, projectId: string, category: string, amount: number, atUtc: Date): CostEntry {
  return { entryId, projectId, category, amount, atUtc };
}

/** The construction board contract. Mirrors C# `IConstructionBoard`. */
export interface IConstructionBoard {
  create(p: Project): void;
  getProject(id: string): Project | undefined;
  add(t: ConstructionTask): void;
  complete(taskId: string): void;
  openConstructionTasksFor(projectId: string): readonly ConstructionTask[];
  recordCost(c: CostEntry): void;
  spendFor(projectId: string): number;
  remainingBudget(projectId: string): number;
}

/** Deterministic in-memory {@link IConstructionBoard}. */
export class InMemoryConstructionBoard implements IConstructionBoard {
  private readonly projects = new Map<string, Project>();
  private readonly tasks = new Map<string, ConstructionTask>();
  private readonly costs: CostEntry[] = [];

  create(p: Project): void {
    if (p == null) throw new Error("p required");
    this.projects.set(p.projectId, p);
  }

  getProject(id: string): Project | undefined {
    return this.projects.get(id);
  }

  add(t: ConstructionTask): void {
    if (t == null) throw new Error("t required");
    this.tasks.set(t.constructionTaskId, t);
  }

  complete(taskId: string): void {
    const t = this.tasks.get(taskId);
    if (t === undefined) throw new Error(`Unknown task ${taskId}`);
    this.tasks.set(taskId, { ...t, completed: true });
  }

  openConstructionTasksFor(projectId: string): readonly ConstructionTask[] {
    return [...this.tasks.values()]
      .filter((t) => t.projectId === projectId && !t.completed)
      .sort((a, b) => a.dueOn.getTime() - b.dueOn.getTime());
  }

  recordCost(c: CostEntry): void {
    if (c == null) throw new Error("c required");
    this.costs.push(c);
  }

  spendFor(projectId: string): number {
    return this.costs.filter((c) => c.projectId === projectId).reduce((sum, c) => sum + c.amount, 0);
  }

  remainingBudget(projectId: string): number {
    const p = this.projects.get(projectId);
    if (p === undefined) throw new Error(`Unknown project ${projectId}`);
    return p.budget - this.spendFor(projectId);
  }
}

/**
 * Static domain context for the Construction vertical. Mirrors C#
 * `ConstructionDomainContext`.
 */
export const ConstructionDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Construction] Expert construction project management assistant. Help with BOQ preparation, programme of works, site safety plans, NHBRC compliance, subcontractor management, and defect liability. Apply NEC/JBCC contract principles. Compliance: OHS Act, NHBRC Act, CIDB Act, ECSA, National Building Regulations.",
  complianceFlags: ["OHS_Act", "NHBRC_Act", "CIDB_Act", "National_Building_Regs", "POPIA"] as readonly string[],
  suggestedTools: ["project_scheduler", "document_editor", "map", "analytics"] as readonly string[],
} as const;
