using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Safety;
public sealed class SafetyCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public SafetyCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{SafetyDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> CreateEmergencyPlanAsync(string householdSize,string location,CancellationToken ct=default)=>_i.AgentAsync($"Create a personalised emergency preparedness plan for a {householdSize}-person household in {location}. Include evacuation routes, emergency contacts, go-bag checklist, and 72-hour supply list.",ct);
    public Task<string> AssessSecurityAsync(string propertyType,string concerns,CancellationToken ct=default)=>_i.AgentAsync($"Assess home security for a {propertyType}. Concerns: {concerns}. Identify vulnerabilities and recommend physical, electronic, and procedural improvements.",ct);

    public Task<string> ConductRiskAssessmentAsync(string activity, string environment, CancellationToken ct=default)
        => _i.AgentAsync($"Conduct a risk assessment for {activity} in {environment}. Hazard, likelihood, severity, controls.", ct);

    public Task<string> DraftEmergencyResponseAsync(string incidentType, string siteContext, CancellationToken ct=default)
        => _i.AgentAsync($"Draft emergency response steps for {incidentType} at {siteContext}. Roles, escalation, comms, debrief.", ct);

    public Task<string> BriefSafetyToolboxAsync(string task, string topHazards, CancellationToken ct=default)
        => _i.AgentAsync($"Brief a 5-min toolbox talk for task: {task}. Top hazards: {topHazards}. Controls, PPE, sign-off.", ct);

    public Task<string> ReviewIncidentReportAsync(string incidentNarrative, CancellationToken ct=default)
        => _i.AgentAsync($"Review this incident narrative: {incidentNarrative}. Identify root cause, contributing factors, corrective + preventive actions.", ct);

}
