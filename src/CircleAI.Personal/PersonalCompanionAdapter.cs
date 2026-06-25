using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Personal;
public sealed class PersonalCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public PersonalCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{PersonalDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> SetGoalAsync(string goal,CancellationToken ct=default)=>_i.AgentAsync($"Help me set a SMART goal for: {goal}. Break it into weekly milestones and suggest how to track progress.",ct);
    public Task<string> MakeDecisionAsync(string decision,string options,CancellationToken ct=default)=>_i.AgentAsync($"Help me decide: {decision}. Options: {options}. Use a pros/cons framework, identify the most important criteria, and give a clear recommendation.",ct);

    public Task<string> SetWeeklyIntentionsAsync(string longTermGoals, string thisWeekContext, CancellationToken ct=default)
        => _i.AgentAsync($"Set 3 weekly intentions aligned to: {longTermGoals}. Context this week: {thisWeekContext}. Each: outcome + one daily anchor.", ct);

    public Task<string> DraftDifficultMessageAsync(string recipient, string topic, string outcomeWanted, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a difficult message to {recipient} about: {topic}. Outcome: {outcomeWanted}. NVC-style: observation, feeling, need, request.", ct);

    public Task<string> DesignRoutineHabitAsync(string habit, string currentLifestyle, CancellationToken ct=default)
        => _i.AgentAsync($"Design a sustainable routine for habit: {habit}. Current lifestyle: {currentLifestyle}. Cue, action, reward, slip recovery.", ct);

    public Task<string> ReviewWeekAsync(string accomplishments, string challenges, CancellationToken ct=default)
        => _i.AgentAsync($"Lead a week review. Accomplishments: {accomplishments}. Challenges: {challenges}. Surface insight + one experiment for next week.", ct);

}
