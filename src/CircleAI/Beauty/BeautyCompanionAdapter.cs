using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Beauty;
public sealed class BeautyCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public BeautyCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{BeautyDomainContext.SystemPromptSnippet}\n\n{m}";
        public Task<string> BuildSkincareRoutineAsync(string skinType,string concerns,CancellationToken ct=default)=>_i.AgentAsync($"Build a skincare routine for {skinType} skin. Concerns: {concerns}. Include morning and evening steps, key ingredients, and ingredients to avoid.",ct);
    public Task<string> AnalyseIngredientAsync(string ingredient,CancellationToken ct=default)=>_i.AgentAsync($"Analyse the skincare ingredient: {ingredient}. Explain function, benefits, potential irritants, and who it suits best.",ct);

    public Task<string> RecommendRoutineAsync(string skinType, string concerns, string budget, CancellationToken ct=default)
        => _i.AgentAsync($"Recommend an AM/PM skincare routine for {skinType} skin with {concerns}, budget {budget}. Include ingredient targets and product categories (not brands).", ct);

    public Task<string> AssessIngredientCompatibilityAsync(string ingredientList, CancellationToken ct=default)
        => _i.AgentAsync($"Assess this ingredient list for layering safety + irritation risk: {ingredientList}. Flag known clashes (retinol+AHA, vit C+niacinamide, etc.).", ct);

    public Task<string> DesignTreatmentPlanAsync(string clientGoals, int sessionCount, CancellationToken ct=default)
        => _i.AgentAsync($"Design a {sessionCount}-session treatment plan to achieve: {clientGoals}. Specify modality, interval, expected progress, and at-home care.", ct);

    public Task<string> DraftBookingConfirmationAsync(string clientName, string treatment, string dateTime, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a warm booking confirmation message: {clientName}, {treatment}, {dateTime}. Include prep instructions, cancellation policy, location.", ct);

}
