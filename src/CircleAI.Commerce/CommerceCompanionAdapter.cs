using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Commerce;
public sealed class CommerceCompanionAdapter : ICompanionSession
{
    private readonly ICompanionSession _inner;
    public CommerceCompanionAdapter(ICompanionSession inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
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
    private static string Enrich(string m) => $"{CommerceDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> OptimiseListingAsync(string productDetails, CancellationToken ct = default)
        => _inner.AgentAsync($"Optimise this product listing for search discovery and conversions:\n{productDetails}", ct);
    public Task<string> AnalysePricingAsync(string product, decimal currentPrice, CancellationToken ct = default)
        => _inner.AgentAsync($"Analyse pricing for: {product} at {currentPrice:C}. Recommend optimal pricing considering margins, competition, and demand.", ct);
    public Task<string> GenerateSupplierBriefAsync(string productRequirements, CancellationToken ct = default)
        => _inner.AgentAsync($"Write a supplier brief for: {productRequirements}. Include quantity, specs, quality standards, delivery terms, and pricing expectations.", ct);

    public Task<string> WriteProductDescriptionAsync(string productName, string features, string targetCustomer, CancellationToken ct=default)
        => _inner.AgentAsync($"Write a product description for {productName} aimed at {targetCustomer}. Features: {features}. Use the 'feature → benefit' pattern, end with a CTA.", ct);

    public Task<string> AnalyseConversionFunnelAsync(string funnelMetrics, CancellationToken ct=default)
        => _inner.AgentAsync($"Analyse this funnel: {funnelMetrics}. Identify the biggest drop-off, the likely cause, and the test to validate.", ct);

    public Task<string> SuggestUpsellAsync(string cartContents, decimal cartTotal, CancellationToken ct=default)
        => _inner.AgentAsync($"Suggest 1-2 upsells for this cart: {cartContents} (total {cartTotal}). Justify each with attach rate intuition + margin notes.", ct);

    public Task<string> DraftReturnPolicyAsync(string category, string region, CancellationToken ct=default)
        => _inner.AgentAsync($"Draft a return policy for {category} sold in {region}. Comply with local consumer law, balance customer trust with fraud prevention.", ct);

}
