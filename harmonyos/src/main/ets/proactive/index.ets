/**
 * proactive/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

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
} from "./primitives";
export {
  proactiveTrigger,
  proactiveTask,
  proactiveTaskRunResult,
  proactiveTaskLoadError,
} from "./primitives";

// Contracts.
export type {
  IProactiveTaskSource,
  IProactiveTaskRunner,
  IProactiveScheduler,
} from "./contracts";

// Cron parser.
export { CronExpression } from "./cron_expression";

// Scheduler.
export { ProactiveScheduler } from "./scheduler";

// Default / test implementations.
export {
  NullProactiveTaskSource,
  NullProactiveTaskRunner,
  InMemoryProactiveTaskSource,
  DelegateProactiveTaskRunner,
} from "./implementations";
export type { ProactiveTaskHandler } from "./implementations";

// Background-service driver.
export { ProactiveSchedulerBackgroundService } from "./background_service";
export type { ProactiveSchedulerOptions } from "./background_service";
