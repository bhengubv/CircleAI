// proactive/index.ts
//
// Barrel for the CircleAI.Companion.Proactive project ported to TypeScript:
// the scheduling primitives, contracts, cron parser, scheduler, default
// implementations, and the background-service driver.

// Primitives (records + constructor helpers).
export type {
  ProactiveTrigger,
  ProactiveTask,
  ProactiveTaskRunResult,
  ProactiveTaskLoadError,
} from "./primitives.js";
export {
  proactiveTrigger,
  proactiveTask,
  proactiveTaskRunResult,
  proactiveTaskLoadError,
} from "./primitives.js";

// Contracts.
export type {
  IProactiveTaskSource,
  IProactiveTaskRunner,
  IProactiveScheduler,
} from "./contracts.js";

// Cron parser.
export { CronExpression } from "./cron_expression.js";

// Scheduler.
export { ProactiveScheduler } from "./scheduler.js";

// Default / test implementations.
export {
  NullProactiveTaskSource,
  NullProactiveTaskRunner,
  InMemoryProactiveTaskSource,
  DelegateProactiveTaskRunner,
} from "./implementations.js";
export type { ProactiveTaskHandler } from "./implementations.js";

// Background-service driver.
export { ProactiveSchedulerBackgroundService } from "./background_service.js";
export type { ProactiveSchedulerOptions } from "./background_service.js";
