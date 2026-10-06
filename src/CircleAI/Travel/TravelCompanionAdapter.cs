using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Travel;
public sealed class TravelCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public TravelCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{TravelDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> PlanTripAsync(string destination,int nights,string travellers,string budget,CancellationToken ct=default)=>_i.AgentAsync($"Plan a {nights}-night trip to {destination} for {travellers}. Budget: {budget}. Include flights, accommodation tiers, daily activities, transport, and estimated total cost.",ct);
    public Task<string> CreatePackingListAsync(string destination,string duration,string activities,CancellationToken ct=default)=>_i.AgentAsync($"Create a packing list for {duration} in {destination}. Activities: {activities}. Organise by category (clothing, toiletries, documents, tech, emergency) and note carry-on vs checked restrictions.",ct);

    public Task<string> OptimiseTripAsync(string origin, string destinations, string constraints, CancellationToken ct=default)
        => _i.AgentAsync($"Optimise trip from {origin} through {destinations}. Constraints: {constraints}. Route, mode mix, lodging, pace.", ct);

    public Task<string> DraftExpenseClaimAsync(string tripSummary, string expenses, CancellationToken ct=default)
        => _i.AgentAsync($"Draft expense claim for trip: {tripSummary}. Items: {expenses}. Categorise per company policy, flag missing receipts.", ct);

    public Task<string> PackingListAsync(string destination, int days, string activities, CancellationToken ct=default)
        => _i.AgentAsync($"Generate packing list for {days} days in {destination}, activities: {activities}. By category + weight optimisation.", ct);

    public Task<string> HandleVisaQueryAsync(string fromCountry, string toCountry, string travelPurpose, CancellationToken ct=default)
        => _i.AgentAsync($"Outline visa requirements: {fromCountry} → {toCountry} for {travelPurpose}. Process, documents, timeline, common pitfalls.", ct);

}
