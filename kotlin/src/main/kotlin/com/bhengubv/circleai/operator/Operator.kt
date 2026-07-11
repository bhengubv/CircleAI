// Operator.kt
//
// Kotlin port of CircleAI.Operator — the C# reference is the EXACT spec
// (Contracts.cs, InMemoryOperator.cs, NullImplementations.cs).
//
// Kubernetes-operator contracts (kagent-pattern). Reconciles model
// deployments against CRDs through a lifecycle state machine
// (Pending -> Downloading -> Loading -> Ready) and notifies subscribers on
// every phase transition. Hosts that integrate real Kubernetes / kagent swap
// in a real implementation behind the same contract.
//
// C# -> Kotlin conventions:
//   ValueTask / async        -> suspend fun
//   DateTimeOffset           -> (n/a here)
//   IDisposable (subscribe)  -> AutoCloseable
//   ConcurrentDictionary     -> synchronized MutableMap
//   Func<ModelStatus, ValueTask> -> suspend observer handler

package com.bhengubv.circleai.operator

// ===========================================================================
// Contracts  (Contracts.cs)
// ===========================================================================

/** Lifecycle phase of a model deployment. */
enum class ModelLifecyclePhase { Pending, Downloading, Loading, Ready, Brownout, Unloading, Failed }

/** Declarative desired state for a model deployment (a CRD spec). */
data class ModelDeployment(
    val modelId: String,
    val namespace: String,
    val replicas: Int,
    val targetTierLabel: String,
)

/** Observed status of a model deployment. */
data class ModelStatus(
    val modelId: String,
    val namespace: String,
    val phase: ModelLifecyclePhase,
    val readyReplicas: Int,
    val lastError: String?,
)

/** Reconcile model deployments against CRDs. */
interface IModelOperator {
    val backendId: String

    suspend fun apply(deployment: ModelDeployment)
    suspend fun delete(modelId: String, namespace: String)
    suspend fun getStatus(modelId: String, namespace: String): ModelStatus?
}

/** Lifecycle observer — fire when phase changes. */
interface IDeploymentObserver {
    val backendId: String

    /** Subscribe to phase transitions. Close the handle to unsubscribe. */
    fun subscribe(handler: suspend (ModelStatus) -> Unit): AutoCloseable
}

// ===========================================================================
// InMemoryModelOperator  (InMemoryOperator.cs)
// ===========================================================================

/** In-memory model deployment store + lifecycle observers. */
class InMemoryModelOperator : IModelOperator, IDeploymentObserver {
    private val statuses = HashMap<String, ModelStatus>()
    private val observers = ArrayList<suspend (ModelStatus) -> Unit>()
    private val lock = Any()

    override val backendId: String get() = "in-memory"

    override suspend fun apply(deployment: ModelDeployment) {
        require(deployment.modelId.isNotBlank()) { "ModelId required" }
        require(deployment.namespace.isNotBlank()) { "Namespace required" }
        require(deployment.replicas >= 0) { "Replicas must be non-negative" }

        val key = key(deployment.modelId, deployment.namespace)
        transition(key, deployment, ModelLifecyclePhase.Pending, 0)
        transition(key, deployment, ModelLifecyclePhase.Downloading, 0)
        transition(key, deployment, ModelLifecyclePhase.Loading, 0)
        transition(key, deployment, ModelLifecyclePhase.Ready, deployment.replicas)
    }

    override suspend fun delete(modelId: String, namespace: String) {
        require(modelId.isNotBlank()) { "modelId required" }
        require(namespace.isNotBlank()) { "namespace required" }
        synchronized(lock) { statuses.remove(key(modelId, namespace)) }
    }

    override suspend fun getStatus(modelId: String, namespace: String): ModelStatus? {
        require(modelId.isNotBlank()) { "modelId required" }
        require(namespace.isNotBlank()) { "namespace required" }
        return synchronized(lock) { statuses[key(modelId, namespace)] }
    }

    override fun subscribe(handler: suspend (ModelStatus) -> Unit): AutoCloseable {
        synchronized(lock) { observers.add(handler) }
        return AutoCloseable { synchronized(lock) { observers.remove(handler) } }
    }

    private suspend fun transition(key: String, d: ModelDeployment, phase: ModelLifecyclePhase, readyReplicas: Int) {
        val status = ModelStatus(d.modelId, d.namespace, phase, readyReplicas, lastError = null)
        val snapshot: List<suspend (ModelStatus) -> Unit>
        synchronized(lock) {
            statuses[key] = status
            snapshot = observers.toList()
        }
        for (o in snapshot) {
            try {
                o(status)
            } catch (ex: Exception) {
                // an unhealthy observer must not corrupt the operator
            }
        }
    }

    private fun key(id: String, ns: String): String = "$ns/$id"
}

// ===========================================================================
// Null implementations  (NullImplementations.cs)
// ===========================================================================

/** In-proc default — no k8s reconciliation. */
class NullModelOperator private constructor() : IModelOperator {
    override val backendId: String get() = "null"
    override suspend fun apply(deployment: ModelDeployment) {}
    override suspend fun delete(modelId: String, namespace: String) {}
    override suspend fun getStatus(modelId: String, namespace: String): ModelStatus? = null

    companion object {
        val Instance = NullModelOperator()
    }
}

/** In-proc default — no lifecycle observation. */
class NullDeploymentObserver private constructor() : IDeploymentObserver {
    override val backendId: String get() = "null"
    override fun subscribe(handler: suspend (ModelStatus) -> Unit): AutoCloseable = AutoCloseable { }

    companion object {
        val Instance = NullDeploymentObserver()
    }
}
