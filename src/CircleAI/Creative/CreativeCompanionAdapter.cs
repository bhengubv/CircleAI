using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Creative;
public sealed class CreativeCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public CreativeCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{CreativeDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> GenerateWritingPromptAsync(string genre,string mood,CancellationToken ct=default)=>_i.AgentAsync($"Generate 5 unique {genre} writing prompts with a {mood} tone. For each, include a character seed, central conflict, and opening line.",ct);
    public Task<string> OvercomeBlockAsync(string project,string blockDescription,CancellationToken ct=default)=>_i.AgentAsync($"Help me overcome creative block on {project}. Block: {blockDescription}. Use lateral thinking techniques and suggest 3 unconventional approaches to re-ignite momentum.",ct);

    public Task<string> GenerateBriefAsync(string project, string audience, string deadline, CancellationToken ct=default)
        => _i.AgentAsync($"Generate a creative brief for '{project}' aimed at {audience}, due {deadline}. Include problem, success, tone, constraints, deliverables.", ct);

    public Task<string> CritiqueWorkAsync(string workDescription, string criteria, CancellationToken ct=default)
        => _i.AgentAsync($"Critique this work: {workDescription} against {criteria}. Use 'I notice / I wonder / I suggest', no destructive framing.", ct);

    public Task<string> SuggestStyleReferencesAsync(string aesthetic, string medium, CancellationToken ct=default)
        => _i.AgentAsync($"Suggest 5 style references for {aesthetic} in {medium}. For each: who/when/why-fits.", ct);

    public Task<string> UnblockCreativeAsync(string currentState, string blocker, CancellationToken ct=default)
        => _i.AgentAsync($"Help unblock this creative state: {currentState}. Blocker: {blocker}. Offer 3 different reframes + one micro-exercise.", ct);

}
