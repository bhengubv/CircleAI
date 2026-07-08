// tenant_context.go
//
// Ports CircleAI.Core.MultiTenant.ICircleAITenantContext
// (ICircleAITenantContext.cs) and CircleAI.Core.MultiTenant.NullTenantContext /
// SingleTenantContext (NullTenantContext.cs).
//
// Ambient tenant scope. The default NullTenantContext throws on any read so a
// forgotten tenant wiring is a loud failure rather than a silent cross-tenant
// data leak. SingleTenantContext is the explicit single-tenant opt-in.

package circleai

import (
	"errors"
	"strings"
)

// ErrNoTenantInScope is returned by NullTenantContext.CurrentTenantID. Mirrors
// the InvalidOperationException the C# NullTenantContext throws.
var ErrNoTenantInScope = errors.New(
	"no CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext " +
		"(e.g. SingleTenantContext, or your own principal-backed resolver) before " +
		"using multi-tenant-aware components")

// ICircleAITenantContext resolves the current tenant. Ports
// CircleAI.Core.MultiTenant.ICircleAITenantContext.
//
// C# exposes CurrentTenantId as a throwing property getter; Go returns
// (string, error) so the "no tenant in scope" failure is explicit at the call
// site rather than a panic.
type ICircleAITenantContext interface {
	// CurrentTenantID returns the tenant id for the current unit of work, or
	// ErrNoTenantInScope when none is in scope. Never silently defaults.
	CurrentTenantID() (string, error)

	// HasTenant reports whether a tenant is currently in scope.
	HasTenant() bool
}

// NullTenantContext is the default ICircleAITenantContext — every read fails
// with ErrNoTenantInScope. Ports CircleAI.Core.MultiTenant.NullTenantContext.
type NullTenantContext struct{}

// NullTenantContextInstance is the shared singleton. Mirrors NullTenantContext.Instance.
var NullTenantContextInstance = NullTenantContext{}

// CurrentTenantID always fails.
func (NullTenantContext) CurrentTenantID() (string, error) { return "", ErrNoTenantInScope }

// HasTenant is always false.
func (NullTenantContext) HasTenant() bool { return false }

// SingleTenantContext is the explicit single-tenant context: it returns a fixed
// tenant id for every read. Ports CircleAI.Core.MultiTenant.SingleTenantContext.
type SingleTenantContext struct {
	tenantID string
}

// NewSingleTenantContext builds a context with a fixed, non-blank tenant id.
func NewSingleTenantContext(tenantID string) (*SingleTenantContext, error) {
	if strings.TrimSpace(tenantID) == "" {
		return nil, errors.New("tenantID must not be blank")
	}
	return &SingleTenantContext{tenantID: tenantID}, nil
}

// CurrentTenantID returns the fixed tenant id.
func (s *SingleTenantContext) CurrentTenantID() (string, error) { return s.tenantID, nil }

// HasTenant is always true.
func (s *SingleTenantContext) HasTenant() bool { return true }

var (
	_ ICircleAITenantContext = NullTenantContext{}
	_ ICircleAITenantContext = (*SingleTenantContext)(nil)
)
