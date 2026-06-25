using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Home;
public sealed class HomeCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public HomeCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{HomeDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> PlanMaintenanceAsync(string homeType,CancellationToken ct=default)=>_i.AgentAsync($"Create an annual home maintenance schedule for a {homeType}. Include monthly, quarterly, bi-annual, and annual tasks with estimated time and cost per task.",ct);
    public Task<string> EstimateRenovationAsync(string scope,string area,CancellationToken ct=default)=>_i.AgentAsync($"Estimate the cost and timeline for this renovation: {scope} in {area}. Break down labour, materials, and contingency. Identify potential hidden costs.",ct);

    public Task<string> ScheduleMaintenanceAsync(string homeAge, string climate, CancellationToken ct=default)
        => _i.AgentAsync($"Generate a 12-month home maintenance schedule for a {homeAge}-year-old home in {climate} climate. Monthly tasks + seasonal big-ticket items.", ct);

    public Task<string> DiagnoseHomeIssueAsync(string symptom, string location, CancellationToken ct=default)
        => _i.AgentAsync($"Diagnose home issue: {symptom} in {location}. List 5 likely causes ranked by probability + a 1-minute check for each.", ct);

    public Task<string> DesignRoomLayoutAsync(string roomDimensions, string primaryUse, string furnitureList, CancellationToken ct=default)
        => _i.AgentAsync($"Design layout for {roomDimensions} room, primary use: {primaryUse}. Furniture: {furnitureList}. Cover circulation, lighting, focal point.", ct);

    public Task<string> EstimateRenovationCostAsync(string scope, string region, string finishLevel, CancellationToken ct=default)
        => _i.AgentAsync($"Estimate {finishLevel}-finish renovation cost for: {scope} in {region}. Range with 20% contingency + biggest cost drivers.", ct);

}
