using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.HR;
public sealed class HRCompanionAdapter : ICompanionSession {
    private readonly ICompanionSession _i;
    public HRCompanionAdapter(ICompanionSession i) => _i = i ?? throw new ArgumentNullException(nameof(i));
    public string SessionId  => _i.SessionId;
    public string IdentityId => _i.IdentityId;
    public InterfaceKind Interface => _i.Interface;
    public IReadOnlyList<CompanionTurn> History => _i.History;
    public CompanionContext GetContext() => _i.GetContext();
    public Task RefreshContextAsync(CancellationToken ct=default) => _i.RefreshContextAsync(ct);
    public Task SignalFeedbackAsync(bool p,string? n=null,CancellationToken ct=default) => _i.SignalFeedbackAsync(p,n,ct);
    public ValueTask DisposeAsync() => _i.DisposeAsync();
    public event EventHandler<CompanionProactiveEvent>? ProactiveMessageReady
    { add=>_i.ProactiveMessageReady+=value; remove=>_i.ProactiveMessageReady-=value; }
    public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
    public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
    public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
    private static string E(string m)=>$"{HRDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DraftJobDescriptionAsync(string role, string requirements, CancellationToken ct=default)
        =>_i.AgentAsync($"Draft a compelling, legally compliant job description for: {role}. Requirements: {requirements}. Include purpose, responsibilities, qualifications, and EEA statement.",ct);
    public Task<string> GeneratePerformanceReviewAsync(string employeeName, string role, string achievements, CancellationToken ct=default)
        =>_i.AgentAsync($"Generate a structured performance review for {employeeName} ({role}). Achievements: {achievements}. Include ratings, development areas, and SMART goals.",ct);
    public Task<string> AdviseOnDisciplinaryAsync(string misconduct, string employeeHistory, CancellationToken ct=default)
        =>_i.AgentAsync($"Advise on disciplinary action for: {misconduct}. Employee history: {employeeHistory}. Apply LRA progressive discipline principles and recommend appropriate sanction.",ct);

    public Task<string> DraftJobDescriptionAsync(string roleTitle, string seniority, string mustHaves, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a job description for {seniority} {roleTitle}. Must-haves: {mustHaves}. Inclusive language, outcomes-led not task-list.", ct);

    public Task<string> StructureInterviewLoopAsync(string role, int hoursAvailable, CancellationToken ct=default)
        => _i.AgentAsync($"Structure an interview loop for {role} in {hoursAvailable} hours. Map each stage to a competency, name the evaluator role.", ct);

    public Task<string> WritePerformanceFeedbackAsync(string employeeName, string strengths, string growthAreas, CancellationToken ct=default)
        => _i.AgentAsync($"Write performance feedback for {employeeName}. Strengths: {strengths}. Growth: {growthAreas}. SBI format, specific, future-focused.", ct);

    public Task<string> HandleSensitiveHrIssueAsync(string situation, string jurisdiction, CancellationToken ct=default)
        => _i.AgentAsync($"Suggest first-response plan for HR situation: {situation} in {jurisdiction}. Cover legal hold, witness, documentation, escalation path.", ct);

}
