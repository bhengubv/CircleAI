using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.CommerceIntegrationXero;
public sealed class CommerceIntegrationXeroCompanionAdapter : ICompanionSession
{
    private readonly ICompanionSession _inner;
    public CommerceIntegrationXeroCompanionAdapter(ICompanionSession inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
    private static string Enrich(string m) => $"{CommerceIntegrationXeroDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> ExplainXeroCodeAsync(string transactionCode, CancellationToken ct = default)
        => _inner.AgentAsync($"Explain Xero transaction code '{transactionCode}' and suggest the correct account code mapping under South African chart of accounts.", ct);
    public Task<string> TroubleshootBankFeedAsync(string feedError, CancellationToken ct = default)
        => _inner.AgentAsync($"Troubleshoot this Xero bank feed error and provide resolution steps:\n{feedError}", ct);
    public Task<string> GenerateXeroReportingGuideAsync(string businessType, CancellationToken ct = default)
        => _inner.AgentAsync($"Generate a Xero reporting guide for a {businessType}. Include recommended reports, frequency, and key metrics to track.", ct);

    public Task<string> MapTransactionToXeroAsync(string transactionDescription, CancellationToken ct=default)
        => _inner.AgentAsync($"Map this transaction to a Xero entry: {transactionDescription}. Pick contact, account code, tax rate; output the API payload outline.", ct);

    public Task<string> ResolveXeroErrorAsync(string xeroErrorJson, CancellationToken ct=default)
        => _inner.AgentAsync($"Resolve this Xero API error: {xeroErrorJson}. Explain the root cause + the exact fix (header, scope, validation, etc.).", ct);

    public Task<string> GenerateXeroReportPromptAsync(string reportType, string period, CancellationToken ct=default)
        => _inner.AgentAsync($"Generate the Xero report request for a {reportType} for {period}. Include endpoint, query params, response fields to surface.", ct);

    public Task<string> MapVatToXeroTaxRateAsync(string countryIso, string supplyType, CancellationToken ct=default)
        => _inner.AgentAsync($"Map this VAT context to the correct Xero tax-rate code: country {countryIso}, supply {supplyType}. Show the code + a one-line justification.", ct);

}
