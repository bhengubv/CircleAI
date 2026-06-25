using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Hospitality;
public sealed class HospitalityCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public HospitalityCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{HospitalityDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> OptimiseRevParAsync(string occupancyData,string rateData,CancellationToken ct=default)=>_i.AgentAsync($"Analyse RevPAR performance and recommend rate and distribution strategies:\nOccupancy: {occupancyData}\nRates: {rateData}",ct);
    public Task<string> HandleGuestComplaintAsync(string complaint,string context,CancellationToken ct=default)=>_i.AgentAsync($"Draft a service recovery response for this guest complaint. Complaint: {complaint}. Context: {context}. Apply LAST (Listen, Apologise, Solve, Thank) framework.",ct);

    public Task<string> DraftGuestWelcomeAsync(string guestName, string roomType, string lengthOfStay, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a warm welcome message for {guestName} in {roomType}, staying {lengthOfStay}. Include wifi, breakfast, local pick.", ct);

    public Task<string> HandleComplaintAsync(string complaint, string sentiment, CancellationToken ct=default)
        => _i.AgentAsync($"Handle this guest complaint ({sentiment}): {complaint}. Apologise, recover, prevent — concrete next step in each.", ct);

    public Task<string> SuggestExperienceAsync(string guestProfile, string lengthOfStay, decimal budget, CancellationToken ct=default)
        => _i.AgentAsync($"Suggest a {lengthOfStay} experience for guest: {guestProfile} on {budget} budget. Mix dining, activity, downtime.", ct);

    public Task<string> OptimiseHousekeepingRouteAsync(string roomList, int staffCount, CancellationToken ct=default)
        => _i.AgentAsync($"Optimise housekeeping route for rooms {roomList} with {staffCount} staff. Sequence for minimum dead-walk + checkout-priority first.", ct);

}
