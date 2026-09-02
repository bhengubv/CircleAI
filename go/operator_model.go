// operator_model.go
//
// Ports CircleAI.Operator (Contracts.cs + InMemoryOperator.cs +
// NullImplementations.cs): the Kubernetes-operator-style model-deployment
// contracts and a real in-memory reconciler.
//
//	ModelLifecyclePhase        -> int enum (stable ordinals)
//	ModelDeployment / ModelStatus (records) -> value structs
//	IModelOperator             -> ModelOperator interface (I-prefix dropped)
//	IDeploymentObserver        -> DeploymentObserver interface
//	InMemoryModelOperator      -> InMemoryModelOperator (real state machine)
//	NullModelOperator          -> NullModelOperator
//	NullDeploymentObserver     -> NullDeploymentObserver
//
// The C# InMemoryModelOperator applies a deployment through the lifecycle
// (Pending -> Downloading -> Loading -> Ready) and notifies subscribers on every
// transition. The C# IDeploymentObserver.Subscribe returns IDisposable; the Go
// idiom returns an unsubscribe func (matching aether_events.go / games_runtime.go).
//
// CONCURRENCY: TransitionAsync snapshots the observer slice UNDER the lock and
// invokes callbacks OUTSIDE it, so an observer that (un)subscribes from its own
// handler cannot self-deadlock the transition. Observer errors are swallowed
// (matching the C# try/catch + Debug.WriteLine).

package circleai

import (
	"context"
	"errors"
	"strings"
	"sync"
)

// ModelLifecyclePhase is the lifecycle phase of a model deployment. Ports
// CircleAI.Operator.ModelLifecyclePhase (stable ordinals).
type ModelLifecyclePhase int

const (
	// ModelLifecyclePending — deployment recorded, not yet acted on.
	ModelLifecyclePending ModelLifecyclePhase = 0
	// ModelLifecycleDownloading — model bytes being fetched.
	ModelLifecycleDownloading ModelLifecyclePhase = 1
	// ModelLifecycleLoading — model being loaded into the runtime.
	ModelLifecycleLoading ModelLifecyclePhase = 2
	// ModelLifecycleReady — replicas serving.
	ModelLifecycleReady ModelLifecyclePhase = 3
	// ModelLifecycleBrownout — degraded/brownout state.
	ModelLifecycleBrownout ModelLifecyclePhase = 4
	// ModelLifecycleUnloading — being torn down.
	ModelLifecycleUnloading ModelLifecyclePhase = 5
	// ModelLifecycleFailed — reconcile failed.
	ModelLifecycleFailed ModelLifecyclePhase = 6
)

// ModelDeployment is a desired model deployment (CRD-style). Ports the
// ModelDeployment record.
type ModelDeployment struct {
	ModelID         string
	Namespace       string
	Replicas        int
	TargetTierLabel string
}

// ModelStatus is the observed status of a deployment. Ports the ModelStatus
// record. LastError is empty when there is no error (C# nullable string).
type ModelStatus struct {
	ModelID       string
	Namespace     string
	Phase         ModelLifecyclePhase
	ReadyReplicas int
	LastError     string
}

// ModelOperator reconciles model deployments against CRDs. Ports IModelOperator.
type ModelOperator interface {
	// BackendID identifies the backing implementation.
	BackendID() string
	// Apply drives the deployment through its lifecycle to Ready.
	Apply(ctx context.Context, deployment ModelDeployment) error
	// Delete removes a deployment's status by (modelId, namespace).
	Delete(ctx context.Context, modelID, namespace string) error
	// GetStatus returns the status and true, or (zero, false) if absent.
	GetStatus(ctx context.Context, modelID, namespace string) (ModelStatus, bool)
}

// DeploymentObserver is a lifecycle observer — fired on every phase change.
// Ports IDeploymentObserver. Subscribe returns an unsubscribe func in place of
// the C# IDisposable handle.
type DeploymentObserver interface {
	BackendID() string
	// Subscribe registers handler for every status transition and returns an
	// idempotent unsubscribe func.
	Subscribe(handler func(ModelStatus)) (unsubscribe func())
}

// InMemoryModelOperator is a real in-memory IModelOperator + IDeploymentObserver.
// Ports InMemoryModelOperator. The zero value is not usable — construct with
// NewInMemoryModelOperator.
type InMemoryModelOperator struct {
	mu        sync.Mutex
	statuses  map[string]ModelStatus
	observers []*deploymentObserverSub
}

type deploymentObserverSub struct {
	handler func(ModelStatus)
}

// NewInMemoryModelOperator constructs an empty operator.
func NewInMemoryModelOperator() *InMemoryModelOperator {
	return &InMemoryModelOperator{statuses: make(map[string]ModelStatus)}
}

// BackendID returns "in-memory". Ports the BackendId property.
func (o *InMemoryModelOperator) BackendID() string { return "in-memory" }

// Apply drives deployment through Pending -> Downloading -> Loading -> Ready,
// notifying subscribers on every transition. Ports ApplyAsync.
func (o *InMemoryModelOperator) Apply(ctx context.Context, deployment ModelDeployment) error {
	if strings.TrimSpace(deployment.ModelID) == "" {
		return errors.New("ModelId required")
	}
	if strings.TrimSpace(deployment.Namespace) == "" {
		return errors.New("Namespace required")
	}
	if deployment.Replicas < 0 {
		return errors.New("Replicas out of range")
	}
	key := operatorKey(deployment.ModelID, deployment.Namespace)
	o.transition(key, deployment, ModelLifecyclePending, 0)
	o.transition(key, deployment, ModelLifecycleDownloading, 0)
	o.transition(key, deployment, ModelLifecycleLoading, 0)
	o.transition(key, deployment, ModelLifecycleReady, deployment.Replicas)
	return nil
}

// Delete removes a deployment's status. Ports DeleteAsync.
func (o *InMemoryModelOperator) Delete(ctx context.Context, modelID, namespace string) error {
	if strings.TrimSpace(modelID) == "" {
		return errors.New("modelId required")
	}
	if strings.TrimSpace(namespace) == "" {
		return errors.New("namespace required")
	}
	o.mu.Lock()
	delete(o.statuses, operatorKey(modelID, namespace))
	o.mu.Unlock()
	return nil
}

// GetStatus returns the status for (modelId, namespace). Ports GetStatusAsync
// (C# returns ModelStatus? -> (value, ok)).
func (o *InMemoryModelOperator) GetStatus(ctx context.Context, modelID, namespace string) (ModelStatus, bool) {
	o.mu.Lock()
	s, ok := o.statuses[operatorKey(modelID, namespace)]
	o.mu.Unlock()
	return s, ok
}

// Subscribe registers handler for every phase transition and returns an
// idempotent unsubscribe func. Ports Subscribe (IDisposable -> func()).
func (o *InMemoryModelOperator) Subscribe(handler func(ModelStatus)) (unsubscribe func()) {
	if handler == nil {
		panic("handler must not be nil")
	}
	sub := &deploymentObserverSub{handler: handler}
	o.mu.Lock()
	o.observers = append(o.observers, sub)
	o.mu.Unlock()
	var once sync.Once
	return func() { once.Do(func() { o.unsubscribe(sub) }) }
}

func (o *InMemoryModelOperator) unsubscribe(sub *deploymentObserverSub) {
	o.mu.Lock()
	defer o.mu.Unlock()
	for i, s := range o.observers {
		if s == sub {
			o.observers = append(o.observers[:i], o.observers[i+1:]...)
			return
		}
	}
}

// transition records a new status under the lock, snapshots the observer slice
// under the lock, then fans out OUTSIDE the lock. Ports TransitionAsync.
func (o *InMemoryModelOperator) transition(key string, d ModelDeployment, phase ModelLifecyclePhase, readyReplicas int) {
	status := ModelStatus{
		ModelID:       d.ModelID,
		Namespace:     d.Namespace,
		Phase:         phase,
		ReadyReplicas: readyReplicas,
	}
	o.mu.Lock()
	o.statuses[key] = status
	snap := make([]*deploymentObserverSub, len(o.observers))
	copy(snap, o.observers)
	o.mu.Unlock()
	for _, s := range snap {
		func() {
			defer func() { _ = recover() }() // an observer must not corrupt the operator
			s.handler(status)
		}()
	}
}

func operatorKey(id, ns string) string { return ns + "/" + id }

// NullModelOperator is a no-op IModelOperator (no k8s reconciliation). Ports
// NullModelOperator.
type NullModelOperator struct{}

// NullModelOperatorInstance is the shared singleton, mirroring
// NullModelOperator.Instance.
var NullModelOperatorInstance = NullModelOperator{}

// BackendID returns "null".
func (NullModelOperator) BackendID() string { return "null" }

// Apply is a no-op. Ports ApplyAsync.
func (NullModelOperator) Apply(context.Context, ModelDeployment) error { return nil }

// Delete is a no-op. Ports DeleteAsync.
func (NullModelOperator) Delete(context.Context, string, string) error { return nil }

// GetStatus always returns (zero, false). Ports GetStatusAsync (null).
func (NullModelOperator) GetStatus(context.Context, string, string) (ModelStatus, bool) {
	return ModelStatus{}, false
}

// NullDeploymentObserver is a no-op IDeploymentObserver. Ports
// NullDeploymentObserver.
type NullDeploymentObserver struct{}

// NullDeploymentObserverInstance is the shared singleton, mirroring
// NullDeploymentObserver.Instance.
var NullDeploymentObserverInstance = NullDeploymentObserver{}

// BackendID returns "null".
func (NullDeploymentObserver) BackendID() string { return "null" }

// Subscribe returns a no-op unsubscribe; no events are ever emitted. Ports
// Subscribe (EmptyDisposable).
func (NullDeploymentObserver) Subscribe(handler func(ModelStatus)) (unsubscribe func()) {
	return func() {}
}

// Interface guards.
var (
	_ ModelOperator      = (*InMemoryModelOperator)(nil)
	_ DeploymentObserver = (*InMemoryModelOperator)(nil)
	_ ModelOperator      = NullModelOperator{}
	_ DeploymentObserver = NullDeploymentObserver{}
)
