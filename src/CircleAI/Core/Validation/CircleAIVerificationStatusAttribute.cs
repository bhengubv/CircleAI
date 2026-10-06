// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.Validation;

/// <summary>
/// How thoroughly a CircleAI component has been validated against the real
/// surface it claims to implement. Stamped onto every concrete
/// <c>I*Bridge</c> / <c>I*Provider</c> / <c>I*Store</c> / <c>I*Aggregator</c>
/// so consumers can opt out of running un-verified components in production
/// via the SDK startup gate.
///
/// <para>This is a real maturity gauge, not marketing copy:</para>
/// <list type="bullet">
///   <item><see cref="Reference"/> — an in-memory / mock / null implementation
///         shipped for testing and development. Correct on the happy path,
///         not durable, not multi-replica safe, often single-process only.</item>
///   <item><see cref="WireProven"/> — real, persistent code that is known to
///         work end-to-end against the real surface (file system, network,
///         hardware) it talks to. Single-process correctness verified.
///         May or may not be multi-replica safe.</item>
///   <item><see cref="ProductionDeployed"/> — has been deployed in a
///         production environment with audit + tenant + observability
///         primitives wired and is known to behave correctly under load
///         and across replicas.</item>
/// </list>
/// </summary>
public enum VerificationLevel
{
    /// <summary>In-memory / mock / null implementation. Not for production.</summary>
    Reference = 0,
    /// <summary>Verified against the real surface in single-process scenarios.</summary>
    WireProven = 1,
    /// <summary>Deployed in production with full observability + multi-replica behaviour confirmed.</summary>
    ProductionDeployed = 2,
}

/// <summary>
/// Stamp a CircleAI component class with its <see cref="VerificationLevel"/>.
/// Consumers can query via reflection at startup; the bundled startup
/// validator can refuse to run if any registered component is below a
/// chosen minimum.
///
/// <para>Mirrors <c>Bhengu.Finance.Payments.Core.Validation.ProviderVerificationStatusAttribute</c>.</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class CircleAIVerificationStatusAttribute : Attribute
{
    /// <summary>The verification level.</summary>
    public VerificationLevel Status { get; }

    /// <summary>
    /// Optional note. For <see cref="VerificationLevel.WireProven"/> /
    /// <see cref="VerificationLevel.ProductionDeployed"/> this should describe
    /// what was tested (e.g. "POSIX file system, atomic write-then-rename,
    /// per-Guid SemaphoreSlim correctness verified under contention").
    /// For <see cref="VerificationLevel.Reference"/> this should describe the
    /// scope and any known limitations (e.g. "in-process channel only;
    /// no persistence across restarts; not multi-replica safe").
    /// </summary>
    public string? Notes { get; set; }

    /// <summary>Stamp the verification level.</summary>
    public CircleAIVerificationStatusAttribute(VerificationLevel status)
    {
        Status = status;
    }
}
