// home/index.ts
// Full-parity port of CircleAI.Home (C#). C# is the exact spec.
//
// Domain types + in-memory store for the Home vertical: rooms, smart-home
// devices with on/off toggle + per-room and active views, and maintenance tasks
// with completion and an upcoming-by-date query. Plus the static
// HomeDomainContext.
//
// NOTE: The C# HomeCompanionAdapter (an ICompanionSession LLM-prompt wrapper) is
// intentionally NOT ported — consistent with the sibling domain-board ports
// (healthcare/education/legal/commerce).
//
// Type mappings (C# → TS):
//   record                          → readonly interface (+ positional factory)
//   double AreaM2                    → number
//   bool IsOn / Completed            → boolean
//   DateTime DueOn                   → Date
//   ConcurrentDictionary (Ordinal)   → Map<string,T>
//
// SEMANTICS PARITY:
//   Rooms          — ordered by Name ascending (default comparer / ordinal).
//   Toggle         — throws on unknown device.
//   CompleteTask   — throws on unknown task.
//   UpcomingTasks  — not-completed tasks with DueOn <= by, ordered by DueOn asc.

/** A room in the home. Mirrors C# `Room` record. */
export interface Room {
  readonly roomId: string;
  readonly name: string;
  readonly areaM2: number;
}

/** Constructs a {@link Room}. */
export function room(roomId: string, name: string, areaM2: number): Room {
  return { roomId, name, areaM2 };
}

/** A smart-home device. Mirrors C# `HomeDevice` record. */
export interface HomeDevice {
  readonly deviceId: string;
  readonly name: string;
  readonly kind: string;
  readonly roomId: string | null;
  readonly isOn: boolean;
}

/** Constructs a {@link HomeDevice}. */
export function homeDevice(
  deviceId: string,
  name: string,
  kind: string,
  roomId: string | null,
  isOn: boolean,
): HomeDevice {
  return { deviceId, name, kind, roomId, isOn };
}

/** A scheduled maintenance task. Mirrors C# `MaintenanceTask` record. */
export interface MaintenanceTask {
  readonly taskId: string;
  readonly description: string;
  readonly dueOn: Date;
  readonly completed: boolean;
}

/** Constructs a {@link MaintenanceTask}. */
export function maintenanceTask(taskId: string, description: string, dueOn: Date, completed: boolean): MaintenanceTask {
  return { taskId, description, dueOn, completed };
}

/** The home board contract. Mirrors C# `IHomeBoard`. */
export interface IHomeBoard {
  addRoom(r: Room): void;
  getRoom(id: string): Room | undefined;
  readonly rooms: readonly Room[];
  addDevice(d: HomeDevice): void;
  toggle(deviceId: string, on: boolean): void;
  devicesIn(roomId: string): readonly HomeDevice[];
  readonly activeDevices: readonly HomeDevice[];
  scheduleTask(t: MaintenanceTask): void;
  completeTask(taskId: string): void;
  upcomingTasks(by: Date): readonly MaintenanceTask[];
}

/** Ordinal (code-unit) string comparison, matching C# StringComparer.Ordinal. */
function ordinalCompare(a: string, b: string): number {
  return a < b ? -1 : a > b ? 1 : 0;
}

/** Deterministic in-memory {@link IHomeBoard}. */
export class InMemoryHomeBoard implements IHomeBoard {
  private readonly roomsById = new Map<string, Room>();
  private readonly devices = new Map<string, HomeDevice>();
  private readonly tasks = new Map<string, MaintenanceTask>();

  addRoom(r: Room): void {
    if (r == null) throw new Error("r required");
    this.roomsById.set(r.roomId, r);
  }

  getRoom(id: string): Room | undefined {
    return this.roomsById.get(id);
  }

  get rooms(): readonly Room[] {
    return [...this.roomsById.values()].sort((a, b) => ordinalCompare(a.name, b.name));
  }

  addDevice(d: HomeDevice): void {
    if (d == null) throw new Error("d required");
    this.devices.set(d.deviceId, d);
  }

  toggle(deviceId: string, on: boolean): void {
    const d = this.devices.get(deviceId);
    if (d === undefined) throw new Error(`Unknown device ${deviceId}`);
    this.devices.set(deviceId, { ...d, isOn: on });
  }

  devicesIn(roomId: string): readonly HomeDevice[] {
    return [...this.devices.values()].filter((d) => d.roomId === roomId);
  }

  get activeDevices(): readonly HomeDevice[] {
    return [...this.devices.values()].filter((d) => d.isOn);
  }

  scheduleTask(t: MaintenanceTask): void {
    if (t == null) throw new Error("t required");
    this.tasks.set(t.taskId, t);
  }

  completeTask(taskId: string): void {
    const t = this.tasks.get(taskId);
    if (t === undefined) throw new Error(`Unknown task ${taskId}`);
    this.tasks.set(taskId, { ...t, completed: true });
  }

  upcomingTasks(by: Date): readonly MaintenanceTask[] {
    return [...this.tasks.values()]
      .filter((t) => !t.completed && t.dueOn.getTime() <= by.getTime())
      .sort((a, b) => a.dueOn.getTime() - b.dueOn.getTime());
  }
}

/**
 * Static domain context for the Home vertical. Mirrors C# `HomeDomainContext`.
 */
export const HomeDomainContext = {
  systemPromptSnippet:
    "[DOMAIN: Home] Expert home management assistant. Help with maintenance schedules, renovation planning and budgeting, appliance troubleshooting, utility cost optimisation, and smart home setup. Practical, no-nonsense advice. Compliance: NHBRC, National Building Regulations, POPIA.",
  complianceFlags: ["NHBRC", "National_Building_Regs", "POPIA"] as readonly string[],
  suggestedTools: ["home_inventory", "task_manager", "web_search", "calculator"] as readonly string[],
} as const;
