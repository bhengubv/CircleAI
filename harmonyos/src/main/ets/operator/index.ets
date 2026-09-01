/**
 * operator/index
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// operator/index.ts
// Full-parity port of CircleAI.Operator (C#). C# is the exact spec.
//
// Kubernetes-operator-shaped model-deployment contracts (kagent pattern):
// ModelDeployment / ModelStatus records, the ModelLifecyclePhase enum, the
// IModelOperator + IDeploymentObserver contracts, a deterministic in-memory
// operator that drives a Pending → Downloading → Loading → Ready lifecycle and
// notifies observers on every transition, and fail-closed Null* defaults.
//
// Type mappings (C# → TS):
//   ValueTask / ValueTask<T>        → Promise<void> / Promise<T>
//   Func<ModelStatus, ValueTask>    → (status: ModelStatus) => Promise<void>
//   IDisposable Subscribe(...)      → returns { dispose(): void }
//   ConcurrentDictionary (Ordinal)  → Map<string, ModelStatus>

/** A disposable subscription handle. Mirrors C# `IDisposable`. */
export interface Disposable {
  dispose(): void;
}

/** Model deployment lifecycle phase. Mirrors C# `ModelLifecyclePhase`. */
export enum ModelLifecyclePhase {
  Pending = 0,
  Downloading = 1,
  Loading = 2,
  Ready = 3,
  Brownout = 4,
  Unloading = 5,
  Failed = 6,
}

/** A requested model deployment. Mirrors C# `ModelDeployment` record. */
export interface ModelDeployment {
  readonly modelId: string;
  readonly namespace: string;
  readonly replicas: number;
  readonly targetTierLabel: string;
}

/** Constructs a {@link ModelDeployment}. */
export function modelDeployment(
  modelId: string,
  namespace: string,
  replicas: number,
  targetTierLabel: string,
): ModelDeployment {
  return { modelId, namespace, replicas, targetTierLabel };
}

/** Observed status of a deployed model. Mirrors C# `ModelStatus` record. */
export interface ModelStatus {
  readonly modelId: string;
  readonly namespace: string;
  readonly phase: ModelLifecyclePhase;
  readonly readyReplicas: number;
  readonly lastError: string | null;
}

/** Constructs a {@link ModelStatus}. */
export function modelStatus(
  modelId: string,
  namespace: string,
  phase: ModelLifecyclePhase,
  readyReplicas: number,
  lastError: string | null,
): ModelStatus {
  return { modelId, namespace, phase, readyReplicas, lastError };
}

/** Reconciles model deployments against CRDs. Mirrors C# `IModelOperator`. */
export interface IModelOperator {
  readonly backendId: string;
  applyAsync(deployment: ModelDeployment, signal?: AbortSignal): Promise<void>;
  deleteAsync(modelId: string, namespace: string, signal?: AbortSignal): Promise<void>;
  getStatusAsync(modelId: string, namespace: string, signal?: AbortSignal): Promise<ModelStatus | null>;
}

/** Lifecycle observer — fires when a phase changes. Mirrors C# `IDeploymentObserver`. */
export interface IDeploymentObserver {
  readonly backendId: string;
  subscribe(handler: (status: ModelStatus) => Promise<void>): Disposable;
}

/**
 * In-memory model-deployment store + lifecycle observers. Applies deployments
 * through a lifecycle state machine and notifies subscribers on every phase
 * transition. Mirrors C# `InMemoryModelOperator`.
 */
export class InMemoryModelOperator implements IModelOperator, IDeploymentObserver {
  private readonly statuses = new Map<string, ModelStatus>();
  private readonly observers: Array<(status: ModelStatus) => Promise<void>> = [];

  readonly backendId = "in-memory";

  async applyAsync(deployment: ModelDeployment, _signal?: AbortSignal): Promise<void> {
    if (deployment == null) throw new Error("deployment required");
    if (isBlank(deployment.modelId)) throw new Error("ModelId required");
    if (isBlank(deployment.namespace)) throw new Error("Namespace required");
    if (deployment.replicas < 0) throw new Error("replicas out of range");

    const key = keyOf(deployment.modelId, deployment.namespace);
    await this.transitionAsync(key, deployment, ModelLifecyclePhase.Pending, 0);
    await this.transitionAsync(key, deployment, ModelLifecyclePhase.Downloading, 0);
    await this.transitionAsync(key, deployment, ModelLifecyclePhase.Loading, 0);
    await this.transitionAsync(key, deployment, ModelLifecyclePhase.Ready, deployment.replicas);
  }

  deleteAsync(modelId: string, namespace: string, _signal?: AbortSignal): Promise<void> {
    if (isBlank(modelId)) throw new Error("modelId required");
    if (isBlank(namespace)) throw new Error("namespace required");
    this.statuses.delete(keyOf(modelId, namespace));
    return Promise.resolve();
  }

  getStatusAsync(
    modelId: string,
    namespace: string,
    _signal?: AbortSignal,
  ): Promise<ModelStatus | null> {
    if (isBlank(modelId)) throw new Error("modelId required");
    if (isBlank(namespace)) throw new Error("namespace required");
    return Promise.resolve(this.statuses.get(keyOf(modelId, namespace)) ?? null);
  }

  subscribe(handler: (status: ModelStatus) => Promise<void>): Disposable {
    if (handler == null) throw new Error("handler required");
    this.observers.push(handler);
    const observers = this.observers;
    return {
      dispose(): void {
        const idx = observers.indexOf(handler);
        if (idx >= 0) observers.splice(idx, 1);
      },
    };
  }

  private async transitionAsync(
    key: string,
    d: ModelDeployment,
    phase: ModelLifecyclePhase,
    readyReplicas: number,
  ): Promise<void> {
    const status = modelStatus(d.modelId, d.namespace, phase, readyReplicas, null);
    this.statuses.set(key, status);
    // Snapshot observers before awaiting so mutation during notification is safe.
    const snapshot = [...this.observers];
    for (const o of snapshot) {
      try {
        await o(status);
      } catch {
        /* a deployment observer must not break the reconcile loop */
      }
    }
  }
}

function keyOf(id: string, ns: string): string {
  return `${ns}/${id}`;
}

function isBlank(s: string | null | undefined): boolean {
  return s == null || s.trim().length === 0;
}

// ─────────────────────────────────────────────────────────────────────────────
// Null implementations — in-proc defaults, no reconciliation.
// ─────────────────────────────────────────────────────────────────────────────

/** Fail-closed {@link IModelOperator}. Mirrors C# `NullModelOperator`. */
export class NullModelOperator implements IModelOperator {
  static readonly instance = new NullModelOperator();
  readonly backendId = "null";
  applyAsync(_deployment: ModelDeployment, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  deleteAsync(_modelId: string, _namespace: string, _signal?: AbortSignal): Promise<void> {
    return Promise.resolve();
  }
  getStatusAsync(
    _modelId: string,
    _namespace: string,
    _signal?: AbortSignal,
  ): Promise<ModelStatus | null> {
    return Promise.resolve(null);
  }
}

/** Fail-closed {@link IDeploymentObserver}. Mirrors C# `NullDeploymentObserver`. */
export class NullDeploymentObserver implements IDeploymentObserver {
  static readonly instance = new NullDeploymentObserver();
  readonly backendId = "null";
  subscribe(_handler: (status: ModelStatus) => Promise<void>): Disposable {
    return { dispose(): void {} };
  }
}
