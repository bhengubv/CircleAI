// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

namespace CircleAI.Core.Auditing;

/// <summary>
/// Process-wide ambient access point for the audit sink. The
/// <see cref="CircleAI.Core.Components.CircleAIComponentBase"/> Run wrappers
/// emit through <see cref="Default"/> without depending on the DI container —
/// this lets components inside DI-aware hosts AND components instantiated
/// directly (tests, scripts, MAUI apps that build their own ServiceProvider)
/// emit audit entries uniformly.
///
/// <para>Initial value is <see cref="NoopAuditLog.Instance"/>. Hosts wire the
/// real sink by calling <see cref="SetDefault"/> during startup (typically
/// from <c>AddCircleAI</c> after resolving the configured
/// <see cref="ICircleAIAuditLog"/>).</para>
///
/// <para>Mirrors <c>Bhengu.Finance.Payments.Core.Auditing.BhenguPaymentAuditing</c>.</para>
/// </summary>
public static class CircleAIAuditing
{
    private static ICircleAIAuditLog _default = NoopAuditLog.Instance;

    /// <summary>
    /// The current ambient audit sink. Defaults to <see cref="NoopAuditLog"/>
    /// — replace via <see cref="SetDefault"/> during host startup.
    /// </summary>
    public static ICircleAIAuditLog Default => _default;

    /// <summary>
    /// Replace the ambient audit sink. Idempotent — calling repeatedly with
    /// the same instance is safe. Hosts typically call this once during
    /// startup; tests may call it per-fixture and restore via
    /// <see cref="ResetToNoop"/>.
    /// </summary>
    public static void SetDefault(ICircleAIAuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(audit);
        _default = audit;
    }

    /// <summary>Restore the default to <see cref="NoopAuditLog"/>. Test-helper.</summary>
    public static void ResetToNoop() => _default = NoopAuditLog.Instance;
}
