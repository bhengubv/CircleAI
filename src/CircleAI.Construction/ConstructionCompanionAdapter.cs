using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Construction;
public sealed class ConstructionCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public ConstructionCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{ConstructionDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DraftSafetyPlanAsync(string projectType,string risks,CancellationToken ct=default)=>_i.AgentAsync($"Draft an OHS Act-compliant safety plan for a {projectType} project. Key risks: {risks}. Include risk assessment, control measures, emergency procedures, and competency requirements.",ct);
    public Task<string> PrepareBoqAsync(string scope,CancellationToken ct=default)=>_i.AgentAsync($"Prepare a Bill of Quantities structure for: {scope}. Include trade sections, measurement units, and provisional sums guidance per ASAQS standards.",ct);

    public Task<string> EstimateCostAsync(string scope, double areaM2, string finishLevel, CancellationToken ct=default)
        => _i.AgentAsync($"Estimate cost for {areaM2}m² of {scope}, finish level {finishLevel}. Break by trade, contingency 10%, exclusions.", ct);

    public Task<string> GenerateSafetyToolboxAsync(string activity, string siteHazards, CancellationToken ct=default)
        => _i.AgentAsync($"Generate a toolbox talk for '{activity}' with hazards: {siteHazards}. Format: hazards, controls, PPE, sign-off.", ct);

    public Task<string> SequenceCriticalPathAsync(string projectScope, int durationDays, CancellationToken ct=default)
        => _i.AgentAsync($"Sequence the critical path for: {projectScope} in {durationDays} days. List tasks, dependencies, slack, and 2 risks per phase.", ct);

    public Task<string> DraftSnagListAsync(string area, string observations, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a snag list for {area}. Observations: {observations}. Order by trade, severity, and access requirement.", ct);

}
