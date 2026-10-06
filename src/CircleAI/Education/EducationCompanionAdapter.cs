using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Education;
public sealed class EducationCompanionAdapter:ICompanionSession{private readonly ICompanionSession _i;
public EducationCompanionAdapter(ICompanionSession i)=>_i=i??throw new ArgumentNullException(nameof(i));
public string SessionId=>_i.SessionId;public string IdentityId=>_i.IdentityId;public InterfaceKind Interface=>_i.Interface;
public IReadOnlyList<CompanionTurn> History=>_i.History;public CompanionContext GetContext()=>_i.GetContext();
public Task RefreshContextAsync(CancellationToken ct=default)=>_i.RefreshContextAsync(ct);
public Task SignalFeedbackAsync(bool p,string?n=null,CancellationToken ct=default)=>_i.SignalFeedbackAsync(p,n,ct);
public ValueTask DisposeAsync()=>_i.DisposeAsync();
public event EventHandler<CompanionProactiveEvent>?ProactiveMessageReady{add=>_i.ProactiveMessageReady+=value;remove=>_i.ProactiveMessageReady-=value;}
public Task<string> SendAsync(string m,CancellationToken ct=default)=>_i.SendAsync(E(m),ct);
public IAsyncEnumerable<string> StreamAsync(string m,CancellationToken ct=default)=>_i.StreamAsync(E(m),ct);
public Task<string> AgentAsync(string m,CancellationToken ct=default)=>_i.AgentAsync(E(m),ct);
private static string E(string m)=>$"{EducationDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> CreateLessonPlanAsync(string subject,string grade,string topic,string duration,CancellationToken ct=default)=>_i.AgentAsync($"Create a CAPS-aligned lesson plan for Grade {grade} {subject}: {topic}. Duration: {duration}. Include LTSM, activities, differentiation strategies, and assessment criteria.",ct);
    public Task<string> GenerateRubricAsync(string assessmentTask,string grade,CancellationToken ct=default)=>_i.AgentAsync($"Generate an assessment rubric for Grade {grade}: {assessmentTask}. Include criteria, descriptors for 4 performance levels, and weighting.",ct);

    public Task<string> DesignLessonPlanAsync(string topic, string gradeBand, int minutes, CancellationToken ct=default)
        => _i.AgentAsync($"Design a {minutes}-minute lesson plan on '{topic}' for {gradeBand}. Include objectives, hook, instruction, practice, exit ticket.", ct);

    public Task<string> GenerateAssessmentAsync(string topic, string bloomsLevel, int itemCount, CancellationToken ct=default)
        => _i.AgentAsync($"Generate {itemCount} assessment items on '{topic}' at Bloom's {bloomsLevel} level. Mix MCQ + short-answer + one performance task.", ct);

    public Task<string> DiagnoseMisconceptionAsync(string topic, string studentResponse, CancellationToken ct=default)
        => _i.AgentAsync($"Diagnose the misconception in this student response on '{topic}': {studentResponse}. Identify the rule the student is following + a corrective move.", ct);

    public Task<string> DraftParentUpdateAsync(string studentName, string period, string progressNotes, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a parent update for {studentName} covering {period}. Notes: {progressNotes}. Warm, specific, actionable.", ct);

}
