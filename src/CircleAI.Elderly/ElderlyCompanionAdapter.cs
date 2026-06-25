using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Elderly;
public sealed class ElderlyCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public ElderlyCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{ElderlyDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> CreateMedScheduleAsync(string medications,CancellationToken ct=default)=>_i.AgentAsync($"Create a clear, simple medication schedule for these prescriptions:\n{medications}\nInclude time of day, food requirements, and what to do if a dose is missed.",ct);
    public Task<string> LocateSupportAsync(string need,string location,CancellationToken ct=default)=>_i.AgentAsync($"Find elderly support services for: {need} in {location}. Include government services, NGOs, and contact details.",ct);

    public Task<string> ReviewMedicationListAsync(string medicationList, string conditions, CancellationToken ct=default)
        => _i.AgentAsync($"Review this medication list for {conditions}: {medicationList}. Flag potential interactions, redundancies, and timing issues. Defer prescribing to clinician.", ct);

    public Task<string> SuggestFallPreventionAsync(string livingArrangement, string mobilityNotes, CancellationToken ct=default)
        => _i.AgentAsync($"Suggest fall-prevention measures for {livingArrangement}. Mobility: {mobilityNotes}. Cover home modifications, footwear, exercise, vision.", ct);

    public Task<string> DraftCheckInPromptsAsync(string residentName, string interestProfile, CancellationToken ct=default)
        => _i.AgentAsync($"Draft 5 warm, dignified check-in conversation prompts for {residentName}. Interests: {interestProfile}. Avoid talk-down language.", ct);

    public Task<string> SummariseCarerHandoverAsync(string shiftNotes, CancellationToken ct=default)
        => _i.AgentAsync($"Summarise these shift notes for the next carer: {shiftNotes}. SBAR format (Situation, Background, Assessment, Recommendation).", ct);

}
