using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Tourism;
public sealed class TourismCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public TourismCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{TourismDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DesignItineraryAsync(string destination,int nights,string guestProfile,CancellationToken ct=default)=>_i.AgentAsync($"Design a {nights}-night itinerary for {destination} tailored to: {guestProfile}. Include daily schedule, accommodation category, transport, meals, and activities with timing.",ct);
    public Task<string> CostPackageAsync(string itinerary,int pax,CancellationToken ct=default)=>_i.AgentAsync($"Cost this tour package for {pax} passengers:\n{itinerary}\nProvide cost per person, breakeven point, and suggested selling price at 25% margin.",ct);

    public Task<string> BuildItineraryAsync(string destination, int days, string travelerProfile, CancellationToken ct=default)
        => _i.AgentAsync($"Build a {days}-day {destination} itinerary for {travelerProfile}. Day-by-day rhythm, must-sees, hidden gems, food.", ct);

    public Task<string> EstimateBudgetAsync(string destination, int travellers, int days, string standard, CancellationToken ct=default)
        => _i.AgentAsync($"Estimate budget for {travellers} pax, {days} days in {destination}, {standard} standard. Categories + total range.", ct);

    public Task<string> HandleTravelDisruptionAsync(string disruption, string itineraryContext, CancellationToken ct=default)
        => _i.AgentAsync($"Handle travel disruption: {disruption}. Itinerary context: {itineraryContext}. Recovery options, comms templates, rebook checklist.", ct);

    public Task<string> RecommendExperienceAsync(string interests, string timeOfDay, string location, CancellationToken ct=default)
        => _i.AgentAsync($"Recommend an experience for {interests} at {timeOfDay} in {location}. Why-it-fits + booking practicalities.", ct);

}
