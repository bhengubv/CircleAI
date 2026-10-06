using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Relationships;
public sealed class RelationshipsCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public RelationshipsCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{RelationshipsDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> GuideConflictResolutionAsync(string situation,CancellationToken ct=default)=>_i.AgentAsync($"Guide me through resolving this conflict using Non-Violent Communication (NVC):\n{situation}\nHelp me identify observations, feelings, needs, and requests.",ct);
    public Task<string> DraftDifficultConversationAsync(string topic,string relationship,CancellationToken ct=default)=>_i.AgentAsync($"Help me prepare for a difficult conversation about {topic} with my {relationship}. Draft key points using assertive but empathetic language.",ct);

    public Task<string> PlanCheckInAsync(string relationship, string lastTouch, string occasion, CancellationToken ct=default)
        => _i.AgentAsync($"Plan a check-in with {relationship}, last touched {lastTouch}. Occasion: {occasion}. Suggest channel, opener, generous question.", ct);

    public Task<string> DraftMeaningfulMessageAsync(string recipient, string moment, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a heartfelt message to {recipient} for {moment}. Specific, not generic; refer to shared history.", ct);

    public Task<string> ResolveTensionAsync(string conflictSummary, string desiredOutcome, CancellationToken ct=default)
        => _i.AgentAsync($"Help resolve tension: {conflictSummary}. Desired outcome: {desiredOutcome}. NVC-style script + likely responses.", ct);

    public Task<string> RememberImportantDateAsync(string personName, string date, string history, CancellationToken ct=default)
        => _i.AgentAsync($"Prep for {personName}'s important date ({date}). History: {history}. Suggest gift, message, gesture.", ct);

}
