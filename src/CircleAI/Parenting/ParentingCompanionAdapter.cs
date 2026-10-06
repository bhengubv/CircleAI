using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Parenting;
public sealed class ParentingCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public ParentingCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{ParentingDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> AdviseOnBehaviourAsync(string childAge,string behaviour,string context,CancellationToken ct=default)=>_i.AgentAsync($"Advise on managing this behaviour in a {childAge}-year-old: {behaviour}. Context: {context}. Use positive discipline principles and suggest age-appropriate strategies.",ct);
    public Task<string> DraftSchoolEmailAsync(string purpose,string teacherName,CancellationToken ct=default)=>_i.AgentAsync($"Draft a professional, respectful email to teacher {teacherName} regarding: {purpose}. Balance parental advocacy with collaborative tone.",ct);

    public Task<string> RespondToBehaviourAsync(string childAge, string behaviour, string context, CancellationToken ct=default)
        => _i.AgentAsync($"Respond to {childAge}-year-old {behaviour} in context: {context}. Provide a calm script + the developmental rationale.", ct);

    public Task<string> DesignRoutineAsync(string childAge, string targetWindow, CancellationToken ct=default)
        => _i.AgentAsync($"Design a {targetWindow} routine for a {childAge}-year-old. Cover transitions, sensory needs, choice points.", ct);

    public Task<string> MilestoneCheckInAsync(string childAge, string observations, CancellationToken ct=default)
        => _i.AgentAsync($"Sanity-check milestones for {childAge}: {observations}. Flag what's normal-range vs worth-discussing-with-pediatrician.", ct);

    public Task<string> PrepareSchoolConferenceAsync(string childName, string grade, string concerns, CancellationToken ct=default)
        => _i.AgentAsync($"Prepare {childName}'s parent-teacher conference ({grade}). Concerns: {concerns}. Draft questions + advocacy points.", ct);

}
