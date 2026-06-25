using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Gaming;
public sealed class GamingCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public GamingCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{GamingDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> BuildStrategyAsync(string game,string goal,string currentSetup,CancellationToken ct=default)=>_i.AgentAsync($"Build a competitive strategy for {game}. Goal: {goal}. Current setup: {currentSetup}. Include build recommendations, macro strategy, and key counters.",ct);
    public Task<string> WriteGameReviewAsync(string game,string playtime,string verdict,CancellationToken ct=default)=>_i.AgentAsync($"Write a structured game review for {game}. Playtime: {playtime}. My verdict: {verdict}. Include: graphics, gameplay, story, performance, value, and a score out of 10.",ct);

    public Task<string> RecommendGameAsync(string mood, string platform, int timeAvailableMin, CancellationToken ct=default)
        => _i.AgentAsync($"Recommend 3 games for mood '{mood}' on {platform}, with {timeAvailableMin} min. Mix indie/AAA, justify per pick.", ct);

    public Task<string> DesignSpeedrunRouteAsync(string gameTitle, string category, CancellationToken ct=default)
        => _i.AgentAsync($"Sketch a speedrun route outline for {gameTitle} ({category}). Cover key skips, glitches at high level, risk-vs-reward gates.", ct);

    public Task<string> DraftPatchNotesAsync(string changes, string audience, CancellationToken ct=default)
        => _i.AgentAsync($"Draft patch notes for changes: {changes}. Audience: {audience}. Group balance/QoL/bugfix, lead with player impact.", ct);

    public Task<string> AnalysePlayerRetentionAsync(string day1Pct, string day7Pct, string day30Pct, CancellationToken ct=default)
        => _i.AgentAsync($"Analyse retention: D1={day1Pct}, D7={day7Pct}, D30={day30Pct}. Diagnose the weakest curve segment + an experiment to lift it.", ct);

}
