using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Family;
public sealed class FamilyCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public FamilyCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{FamilyDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> PlanFamilyActivityAsync(string ages,string budget,string interests,CancellationToken ct=default)=>_i.AgentAsync($"Plan a family activity for children aged {ages}. Budget: {budget}. Interests: {interests}. Include indoor and outdoor options with estimated cost and age-appropriateness.",ct);
    public Task<string> CreateFamilyBudgetAsync(string income,string expenses,string goals,CancellationToken ct=default)=>_i.AgentAsync($"Create a family budget. Combined income: {income}. Expenses: {expenses}. Goals: {goals}. Allocate to categories and identify savings opportunities.",ct);

    public Task<string> PlanFamilyMealsAsync(string familySize, string dietaryNotes, int daysCount, CancellationToken ct=default)
        => _i.AgentAsync($"Plan {daysCount} days of family meals for {familySize} people, dietary notes: {dietaryNotes}. Include shopping list grouped by aisle.", ct);

    public Task<string> MediateSiblingDisputeAsync(string ages, string dispute, CancellationToken ct=default)
        => _i.AgentAsync($"Mediate a sibling dispute between ages {ages}: {dispute}. Step-by-step script honouring each child's perspective.", ct);

    public Task<string> DesignHouseholdChoreRotaAsync(string members, string chores, CancellationToken ct=default)
        => _i.AgentAsync($"Design a fair, age-appropriate chore rota. Members: {members}. Chores: {chores}. Cover frequency and ownership.", ct);

    public Task<string> CelebrateMilestoneAsync(string milestone, string memberName, string budget, CancellationToken ct=default)
        => _i.AgentAsync($"Plan a {budget} milestone celebration for {memberName}: {milestone}. Ideas across activity / food / memento / message.", ct);

}
