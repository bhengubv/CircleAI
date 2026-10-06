using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Sports;
public sealed class SportsCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public SportsCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{SportsDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DesignTrainingProgramAsync(string sport,string athleteProfile,string goal,int weeks,CancellationToken ct=default)=>_i.AgentAsync($"Design a {weeks}-week periodised training programme for {sport}. Athlete: {athleteProfile}. Goal: {goal}. Include weekly volume, intensity zones, key sessions, and recovery weeks.",ct);
    public Task<string> AnalysePerformanceAsync(string athleteData,CancellationToken ct=default)=>_i.AgentAsync($"Analyse this athlete performance data and identify strengths, weaknesses, and priority interventions:\n{athleteData}",ct);

    public Task<string> DesignTrainingBlockAsync(string sport, string targetEvent, int weeks, CancellationToken ct=default)
        => _i.AgentAsync($"Design a {weeks}-week training block for {sport} peaking at {targetEvent}. Periodisation, key sessions, tapers.", ct);

    public Task<string> AnalysePerformanceAsync(string sport, string recentResults, string keyMetrics, CancellationToken ct=default)
        => _i.AgentAsync($"Analyse recent {sport} performance: {recentResults}. Key metrics: {keyMetrics}. Strengths to lean into, gaps to close.", ct);

    public Task<string> PlanRecoveryAsync(string sessionIntensity, string daysUntilNext, CancellationToken ct=default)
        => _i.AgentAsync($"Plan recovery between sessions: {sessionIntensity}, {daysUntilNext} days. Nutrition, sleep, mobility, modality picks.", ct);

    public Task<string> DraftPostMatchReportAsync(string match, string keyMoments, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a post-match report on {match}. Key moments: {keyMoments}. Tactical, individual standouts, areas to drill.", ct);

}
