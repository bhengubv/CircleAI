//! tenant.rs
//!
//! Port of:
//!   - `CircleAI.Core.MultiTenant.ICircleAITenantContext`
//!   - `CircleAI.Core.MultiTenant.NullTenantContext`
//!   - `CircleAI.Core.MultiTenant.SingleTenantContext`
//!
//! Ambient tenant context. The default [`NullTenantContext`] errors on any read —
//! intentional, so "forgot to wire tenant resolution" is a load-time error rather
//! than a silent cross-tenant data leak.

/// Error returned when no tenant is in scope. Maps to the C#
/// `InvalidOperationException`.
#[derive(Debug, Clone, PartialEq, Eq)]
pub struct NoTenantError(pub String);

impl std::fmt::Display for NoTenantError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(&self.0)
    }
}

impl std::error::Error for NoTenantError {}

/// Ambient tenant context. Mirrors `CircleAI.Core.MultiTenant.ICircleAITenantContext`.
pub trait ICircleAITenantContext {
    /// The tenant identifier for the current request / unit of work. Errors if no
    /// tenant is in scope — multi-tenant paths must never silently fall back.
    fn current_tenant_id(&self) -> Result<String, NoTenantError>;

    /// True when a tenant is currently in scope.
    fn has_tenant(&self) -> bool;
}

/// Default [`ICircleAITenantContext`] — errors on any read. Mirrors
/// `NullTenantContext`.
#[derive(Debug, Default, Clone, Copy)]
pub struct NullTenantContext;

impl NullTenantContext {
    /// The shared singleton instance.
    pub const INSTANCE: NullTenantContext = NullTenantContext;

    pub fn new() -> Self {
        NullTenantContext
    }
}

impl ICircleAITenantContext for NullTenantContext {
    fn current_tenant_id(&self) -> Result<String, NoTenantError> {
        Err(NoTenantError(
            "No CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext \
             (e.g. SingleTenantContext, or your own ClaimsPrincipal-backed resolver) before \
             using multi-tenant-aware components."
                .into(),
        ))
    }

    fn has_tenant(&self) -> bool {
        false
    }
}

/// Explicit single-tenant context — returns a fixed tenant id for every read.
/// Mirrors `SingleTenantContext`.
#[derive(Debug, Clone)]
pub struct SingleTenantContext {
    tenant_id: String,
}

impl SingleTenantContext {
    /// Construct with the fixed tenant id. Errors on null/whitespace, mirroring
    /// `ArgumentException.ThrowIfNullOrWhiteSpace`.
    pub fn new(tenant_id: impl Into<String>) -> Result<Self, NoTenantError> {
        let tenant_id = tenant_id.into();
        if tenant_id.trim().is_empty() {
            return Err(NoTenantError("tenantId".into()));
        }
        Ok(Self { tenant_id })
    }
}

impl ICircleAITenantContext for SingleTenantContext {
    fn current_tenant_id(&self) -> Result<String, NoTenantError> {
        Ok(self.tenant_id.clone())
    }

    fn has_tenant(&self) -> bool {
        true
    }
}
