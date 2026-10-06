using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Fitness;
public sealed class FitnessCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public FitnessCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{FitnessDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DesignWorkoutAsync(string goal,string equipment,string level,int daysPerWeek,CancellationToken ct=default)=>_i.AgentAsync($"Design a {daysPerWeek}-day/week workout programme. Goal: {goal}. Equipment: {equipment}. Level: {level}. Include warm-up, main sets with reps/sets/rest, and cool-down.",ct);
    public Task<string> AnalyseProgressAsync(string metrics,CancellationToken ct=default)=>_i.AgentAsync($"Analyse my fitness progress and recommend programme adjustments:\n{metrics}",ct);

    public Task<string> DesignWorkoutPlanAsync(string goal, string availableTime, string equipment, CancellationToken ct=default)
        => _i.AgentAsync($"Design a workout plan for goal '{goal}', {availableTime} per session, equipment: {equipment}. Periodise over 4 weeks.", ct);

    public Task<string> AnalysePersonalBestProgressionAsync(string exercise, string historyJson, CancellationToken ct=default)
        => _i.AgentAsync($"Analyse PB progression in {exercise}: {historyJson}. Identify plateaus, recommend deload + next mesocycle target.", ct);

    public Task<string> SuggestRecoveryProtocolAsync(string sorenessNotes, string sleepAvgHours, CancellationToken ct=default)
        => _i.AgentAsync($"Suggest recovery protocol for soreness: {sorenessNotes}, avg sleep {sleepAvgHours}h. Cover mobility, nutrition, sleep, deload.", ct);

    public Task<string> CritiqueFormCueAsync(string exercise, string formDescription, CancellationToken ct=default)
        => _i.AgentAsync($"Critique form for {exercise}: {formDescription}. Identify the 2 highest-leverage cues to fix first.", ct);

}
