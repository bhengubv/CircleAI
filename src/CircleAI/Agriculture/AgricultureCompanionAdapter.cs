using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Agriculture;
public sealed class AgricultureCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public AgricultureCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{AgricultureDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DiagnosePestAsync(string cropType,string symptoms,CancellationToken ct=default)=>_i.AgentAsync($"Diagnose this crop problem and recommend treatment. Crop: {cropType}. Symptoms: {symptoms}. Include integrated pest management (IPM) options and registered chemical controls.",ct);
    public Task<string> PlanCropRotationAsync(string farmContext,int seasons,CancellationToken ct=default)=>_i.AgentAsync($"Design a {seasons}-season crop rotation plan for: {farmContext}. Optimise soil health, disease break cycles, and profitability.",ct);

    public Task<string> DiagnoseCropIssueAsync(string crop, string symptoms, string region, CancellationToken ct=default)
        => _i.AgentAsync($"Diagnose this {crop} issue in {region}: {symptoms}. Cover likely pests/disease/deficiency, confidence, and an integrated-pest-management plan.", ct);

    public Task<string> OptimisePlantingScheduleAsync(string crop, string climate, double areaHa, CancellationToken ct=default)
        => _i.AgentAsync($"Plan planting for {areaHa}ha of {crop} in {climate}. Include sowing dates, density, irrigation, fertiliser, and harvest window.", ct);

    public Task<string> EstimateYieldAsync(string crop, double areaHa, string conditions, CancellationToken ct=default)
        => _i.AgentAsync($"Estimate yield (t/ha and total tons) for {areaHa}ha of {crop} under: {conditions}. Show baseline, best, worst case.", ct);

    public Task<string> DraftSustainabilityReportAsync(string operationSummary, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a sustainability report for: {operationSummary}. Cover soil health, water use, biodiversity, GHG, and SDG alignment.", ct);

}
