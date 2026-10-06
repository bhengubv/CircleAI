// © 2024–2026 The Other Bhengu (Pty) Ltd t/a The Geek Network. MIT-licensed.

using System.Diagnostics;
using System.Runtime.CompilerServices;
using CircleAI.Core.Auditing;
using CircleAI.Core.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace CircleAI.Core.Components;

/// <summary>
/// Shared base for every CircleAI component that calls out to a stateful or
/// I/O-bearing surface (filesystem, HTTP, ContentProvider, channel, etc.).
/// Collapses the boilerplate every component would otherwise repeat:
/// open an OpenTelemetry activity, increment the right counter, record
/// the duration histogram, classify the outcome, emit an audit entry, and
/// re-throw the original exception.
///
/// <para>Components call <see cref="RunOperationAsync{T}"/> or
/// <see cref="RunStreamAsync{T}"/> instead of writing their own try/catch
/// + diagnostics blocks. Reduces ~140 nearly-identical wrappers across the
/// SDK's adapter zoo to four lines per method.</para>
///
/// <para>The wrappers also perform
/// <see cref="CancellationToken.ThrowIfCancellationRequested"/> before
/// invoking the inner operation, ensuring late cancellations are honoured
/// even when the inner code path doesn't poll the token between sequential
/// calls.</para>
///
/// <para>This mirrors <c>Bhengu.Finance.Payments.Core.Providers.BhenguProviderBase</c>.
/// Used by <c>DefaultSecurityWatchdog</c>, <c>JsonPersonaProvider</c>,
/// <c>FileSystemKnowledgeStore</c>, <c>LocalProcessInferenceBridge</c>,
/// <c>InMemoryAgentPeerProtocol</c>, <c>InMemoryFederationAggregator</c>
/// (and any future component).</para>
/// </summary>
public abstract class CircleAIComponentBase
{
    /// <summary>Component-supplied logger. Defaults to <see cref="NullLogger.Instance"/>.</summary>
    protected ILogger Logger { get; }

    /// <summary>
    /// Canonical component name. Stamped on every span tag, counter, and
    /// audit entry. Should be the unqualified class name in <c>PascalCase</c>
    /// (e.g. "JsonPersonaProvider", "DefaultSecurityWatchdog").
    /// </summary>
    public abstract string ComponentName { get; }

    /// <summary>Construct with an optional logger.</summary>
    protected CircleAIComponentBase(ILogger? logger = null)
    {
        Logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Wrap an async operation returning <typeparamref name="T"/>. Emits
    /// activity + counter + duration histogram + audit entry. Re-throws the
    /// original exception unchanged.
    /// </summary>
    /// <param name="operationName">Logical operation name (e.g. "GetAsync").</param>
    /// <param name="op">The operation to run.</param>
    /// <param name="ct">Cancellation token (checked before invoking).</param>
    /// <param name="uhidIdentityId">Optional UHID for audit scoping.</param>
    /// <param name="tenantId">Optional tenant id for audit scoping.</param>
    /// <param name="correlationId">Optional correlation id (session, request).</param>
    protected Task<T> RunOperationAsync<T>(
        string operationName,
        Func<Task<T>> op,
        CancellationToken ct,
        string? uhidIdentityId = null,
        string? tenantId = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(op);

        return RunAsyncCore(operationName, op, ct, uhidIdentityId, tenantId, correlationId);
    }

    /// <summary>
    /// Wrap a streaming operation returning <see cref="IAsyncEnumerable{T}"/>.
    /// Emits activity + counter + audit entry on enumeration completion
    /// (success, cancellation, or error). Yields each item from the source
    /// stream unchanged.
    /// </summary>
    protected async IAsyncEnumerable<T> RunStreamAsync<T>(
        string operationName,
        Func<CancellationToken, IAsyncEnumerable<T>> source,
        [EnumeratorCancellation] CancellationToken ct,
        string? uhidIdentityId = null,
        string? tenantId = null,
        string? correlationId = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operationName);
        ArgumentNullException.ThrowIfNull(source);
        ct.ThrowIfCancellationRequested();

        var activity = CircleAIDiagnostics.StartOperationActivity(ComponentName, operationName);
        var start = Stopwatch.GetTimestamp();
        string outcome = CircleAIDiagnostics.Outcomes.Error;
        string? errorType = null;

        IAsyncEnumerator<T> enumerator;
        try
        {
            enumerator = source(ct).GetAsyncEnumerator(ct);
        }
        catch (OperationCanceledException)
        {
            outcome = CircleAIDiagnostics.Outcomes.Cancelled;
            errorType = nameof(OperationCanceledException);
            CompleteRun(activity, operationName, outcome, errorType, errorCode: null,
                        start, uhidIdentityId, tenantId, correlationId);
            throw;
        }
        catch (Exception ex)
        {
            errorType = ex.GetType().Name;
            CompleteRun(activity, operationName, outcome, errorType, errorCode: null,
                        start, uhidIdentityId, tenantId, correlationId);
            throw;
        }

        await using (enumerator.ConfigureAwait(false))
        {
            while (true)
            {
                bool hasNext;
                try
                {
                    hasNext = await enumerator.MoveNextAsync().ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    outcome = CircleAIDiagnostics.Outcomes.Cancelled;
                    errorType = nameof(OperationCanceledException);
                    CompleteRun(activity, operationName, outcome, errorType, errorCode: null,
                                start, uhidIdentityId, tenantId, correlationId);
                    throw;
                }
                catch (Exception ex)
                {
                    errorType = ex.GetType().Name;
                    CompleteRun(activity, operationName, outcome, errorType, errorCode: null,
                                start, uhidIdentityId, tenantId, correlationId);
                    throw;
                }

                if (!hasNext)
                {
                    outcome = CircleAIDiagnostics.Outcomes.Success;
                    CompleteRun(activity, operationName, outcome, errorType: null, errorCode: null,
                                start, uhidIdentityId, tenantId, correlationId);
                    yield break;
                }

                yield return enumerator.Current;
            }
        }
    }

    private async Task<T> RunAsyncCore<T>(
        string operationName,
        Func<Task<T>> op,
        CancellationToken ct,
        string? uhidIdentityId,
        string? tenantId,
        string? correlationId)
    {
        ct.ThrowIfCancellationRequested();

        var activity = CircleAIDiagnostics.StartOperationActivity(ComponentName, operationName);
        var start = Stopwatch.GetTimestamp();
        string outcome = CircleAIDiagnostics.Outcomes.Error;
        string? errorType = null;
        string? errorCode = null;
        try
        {
            var result = await op().ConfigureAwait(false);
            outcome = CircleAIDiagnostics.Outcomes.Success;
            return result;
        }
        catch (OperationCanceledException)
        {
            outcome = CircleAIDiagnostics.Outcomes.Cancelled;
            errorType = nameof(OperationCanceledException);
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            outcome = CircleAIDiagnostics.Outcomes.Invalid;
            errorType = ex.GetType().Name;
            throw;
        }
        catch (ArgumentException ex)
        {
            outcome = CircleAIDiagnostics.Outcomes.Invalid;
            errorType = ex.GetType().Name;
            throw;
        }
        catch (HttpRequestException ex)
        {
            outcome = CircleAIDiagnostics.Outcomes.Unavailable;
            errorType = ex.GetType().Name;
            throw;
        }
        catch (IOException ex)
        {
            outcome = CircleAIDiagnostics.Outcomes.Unavailable;
            errorType = ex.GetType().Name;
            throw;
        }
        catch (Exception ex)
        {
            outcome = CircleAIDiagnostics.Outcomes.Error;
            errorType = ex.GetType().Name;
            throw;
        }
        finally
        {
            CompleteRun(activity, operationName, outcome, errorType, errorCode,
                        start, uhidIdentityId, tenantId, correlationId);
        }
    }

    private void CompleteRun(
        Activity? activity,
        string operationName,
        string outcome,
        string? errorType,
        string? errorCode,
        long startTimestamp,
        string? uhidIdentityId,
        string? tenantId,
        string? correlationId)
    {
        var elapsedMs = Stopwatch.GetElapsedTime(startTimestamp).TotalMilliseconds;

        // Activity + counter + histogram.
        activity.SetOutcome(outcome);
        activity?.Dispose();
        CircleAIDiagnostics.OperationsTotal.Add(1,
            new KeyValuePair<string, object?>("component", ComponentName),
            new KeyValuePair<string, object?>("operation", operationName),
            new KeyValuePair<string, object?>("outcome",   outcome));
        CircleAIDiagnostics.OperationDurationMs.Record(elapsedMs,
            new KeyValuePair<string, object?>("component", ComponentName),
            new KeyValuePair<string, object?>("operation", operationName),
            new KeyValuePair<string, object?>("outcome",   outcome));

        // Fire-and-forget audit emission. Defaults to NoopAuditLog when not
        // wired — never throws.
        try
        {
            var entry = new CircleAIAuditEntry
            {
                At              = DateTimeOffset.UtcNow,
                Component       = ComponentName,
                Operation       = operationName,
                Outcome         = outcome,
                TenantId        = tenantId,
                UhidIdentityId  = uhidIdentityId,
                CorrelationId   = correlationId,
                DurationMs      = elapsedMs,
                ErrorType       = errorType,
                ErrorCode       = errorCode,
            };
            _ = CircleAIAuditing.Default.RecordAsync(entry, CancellationToken.None);
        }
        catch
        {
            // Audit failure must never affect the original operation outcome.
        }
    }
}
