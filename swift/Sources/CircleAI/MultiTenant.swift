// MultiTenant.swift
//
// Port of CircleAI.Core.MultiTenant — ICircleAITenantContext, NullTenantContext,
// SingleTenantContext.
//
// Ambient tenant context. Implementations resolve the current tenant from
// whatever signal the host uses. Multi-tenant wrappers around CircleAI's
// stateful stores read `currentTenantId` to scope the on-disk root directory.
//
// The default registration is NullTenantContext — an accessor that throws on
// access. This is intentional: there is no safe default for "which tenant is
// this request for", and silently failing open causes cross-tenant data leaks.
//
// Note on the C# → Swift mapping: C#'s `string CurrentTenantId` throws from its
// getter. Swift protocol properties cannot be declared `throws`, so the
// fail-loud contract is preserved as a throwing accessor
// `currentTenantId() throws -> String` rather than a trapping property.

import Foundation

/// Error thrown when a tenant id is requested but none is in scope.
public struct NoTenantInScopeError: Error, Sendable {
    public let message: String
    public init(_ message: String) { self.message = message }
}

/// Ambient tenant context.
public protocol ICircleAITenantContext: Sendable {
    /// The tenant identifier for the current request / unit of work. Throws if
    /// no tenant is in scope — multi-tenant code paths must NEVER silently fall
    /// back to a default.
    func currentTenantId() throws -> String

    /// True when a tenant is currently in scope. Use to gate optional behaviour.
    var hasTenant: Bool { get }
}

/// Default `ICircleAITenantContext` — throws on any read.
///
/// This is what the host registers when it has not wired a real tenant
/// resolver. The throw is intentional: it makes "I forgot to wire tenant
/// resolution" a load-time error rather than a silent data-leak at runtime.
///
/// For a genuine single-tenant deployment, register `SingleTenantContext`
/// explicitly instead.
public struct NullTenantContext: ICircleAITenantContext {
    /// Shared singleton instance.
    public static let instance = NullTenantContext()

    public init() {}

    public func currentTenantId() throws -> String {
        throw NoTenantInScopeError(
            "No CircleAI tenant context is in scope. Register a concrete ICircleAITenantContext " +
            "(e.g. SingleTenantContext, or your own ClaimsPrincipal-backed resolver) before " +
            "using multi-tenant-aware components.")
    }

    public var hasTenant: Bool { false }
}

/// Explicit single-tenant context. Returns a fixed tenant id for every read.
/// Use this when the deployment genuinely has one tenant and the throwing
/// default would just be ceremony.
public struct SingleTenantContext: ICircleAITenantContext {
    private let tenantId: String

    /// Construct with the fixed tenant id.
    public init(tenantId: String) {
        precondition(
            !tenantId.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty,
            "tenantId must not be null or whitespace.")
        self.tenantId = tenantId
    }

    public func currentTenantId() throws -> String { tenantId }

    public var hasTenant: Bool { true }
}
