using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.CommerceIntegrationPayFast;
public sealed class CommerceIntegrationPayFastCompanionAdapter : ICompanionSession
{
    private readonly ICompanionSession _inner;
    public CommerceIntegrationPayFastCompanionAdapter(ICompanionSession inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public string SessionId  => _inner.SessionId;
    public string IdentityId => _inner.IdentityId;
    public InterfaceKind Interface => _inner.Interface;
    public IReadOnlyList<CompanionTurn> History => _inner.History;
    public CompanionContext GetContext() => _inner.GetContext();
    public Task RefreshContextAsync(CancellationToken ct = default) => _inner.RefreshContextAsync(ct);
    public Task SignalFeedbackAsync(bool positive, string? note = null, CancellationToken ct = default) => _inner.SignalFeedbackAsync(positive, note, ct);
    public ValueTask DisposeAsync() => _inner.DisposeAsync();
    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady
    { add => _inner.ProactiveMessageReady += value; remove => _inner.ProactiveMessageReady -= value; }
    public Task<string> SendAsync(string message, CancellationToken ct = default) => _inner.SendAsync(Enrich(message), ct);
    public IAsyncEnumerable<string> StreamAsync(string message, CancellationToken ct = default) => _inner.StreamAsync(Enrich(message), ct);
    public Task<string> AgentAsync(string instruction, CancellationToken ct = default) => _inner.AgentAsync(Enrich(instruction), ct);
    private static string Enrich(string m) => $"{CommerceIntegrationPayFastDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DiagnoseItnAsync(string itnPayload, CancellationToken ct = default)
        => _inner.AgentAsync($"Diagnose this PayFast ITN payload. Validate signature, check payment_status, and identify any issues:\n{itnPayload}", ct);
    public Task<string> GuideRefundAsync(string transactionId, string reason, CancellationToken ct = default)
        => _inner.AgentAsync($"Guide me through processing a PayFast refund for transaction {transactionId}. Reason: {reason}. Include API call, required fields, and customer communication.", ct);
    public Task<string> ReviewIntegrationAsync(string codeSnippet, CancellationToken ct = default)
        => _inner.AgentAsync($"Review this PayFast integration code for security, PCI-DSS compliance, and correctness:\n{codeSnippet}", ct);

    public Task<string> ExplainItnStatusAsync(string itnPayload, CancellationToken ct=default)
        => _inner.AgentAsync($"Decode this PayFast ITN payload and explain its status: {itnPayload}. Cover payment_status, m_payment_id, signature validity.", ct);

    public Task<string> DraftPayFastBuyButtonAsync(string itemName, decimal amount, string returnUrl, CancellationToken ct=default)
        => _inner.AgentAsync($"Draft a PayFast Buy Button form for '{itemName}' at {amount}, return to {returnUrl}. Include all required fields + signature placeholder.", ct);

    public Task<string> TroubleshootSignatureMismatchAsync(string requestParams, CancellationToken ct=default)
        => _inner.AgentAsync($"Troubleshoot a PayFast signature mismatch. Request params: {requestParams}. List the 5 most common causes + how to verify each.", ct);

    public Task<string> ReconcilePayoutAsync(string payoutId, decimal expectedAmount, decimal actualAmount, CancellationToken ct=default)
        => _inner.AgentAsync($"Reconcile PayFast payout {payoutId}: expected {expectedAmount}, actual {actualAmount}. List likely fee / refund / hold reasons.", ct);

}
