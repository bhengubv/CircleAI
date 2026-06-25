using System.Runtime.CompilerServices;
using CircleAI.Companion;
namespace CircleAI.Healthcare;
public sealed class HealthcareCompanionAdapter : ICompanionSession {
    private readonly ICompanionSession _i;
    public HealthcareCompanionAdapter(ICompanionSession i) => _i = i ?? throw new ArgumentNullException(nameof(i));
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
    private static string E(string m)=>$"{HealthcareDomainContext.SystemPromptSnippet}\n\n{m}";
    public Task<string> DocumentClinicalNoteAsync(string patientVisitSummary, CancellationToken ct=default)
        =>_i.AgentAsync($"Format this patient visit summary into a structured SOAP clinical note:\n{patientVisitSummary}",ct);
    public Task<string> SuggestIcd10CodesAsync(string diagnosis, CancellationToken ct=default)
        =>_i.AgentAsync($"Suggest relevant ICD-10-CM codes for the following diagnosis/condition: {diagnosis}. Include primary and secondary codes with descriptions.",ct);
    public Task<string> DraftPatientCommunicationAsync(string purpose, string patientContext, CancellationToken ct=default)
        =>_i.AgentAsync($"Draft a clear, empathetic patient communication for: {purpose}. Patient context: {patientContext}. Keep language accessible (Grade 8 reading level).",ct);

    public Task<string> TriageSymptomsAsync(string patientAge, string symptoms, string duration, CancellationToken ct=default)
        => _i.AgentAsync($"Triage symptoms for {patientAge}-year-old: {symptoms}, duration {duration}. Output urgency (emergency/urgent/routine), red flags, next step. Defer diagnosis to clinician.", ct);

    public Task<string> ExplainMedicationAsync(string medication, string indication, CancellationToken ct=default)
        => _i.AgentAsync($"Explain {medication} prescribed for {indication} to a patient. Cover purpose, dose schedule, common side effects, when to call.", ct);

    public Task<string> DraftReferralLetterAsync(string fromProvider, string toSpecialty, string clinicalSummary, CancellationToken ct=default)
        => _i.AgentAsync($"Draft a referral letter from {fromProvider} to {toSpecialty}. Clinical summary: {clinicalSummary}. Include reason, history, exam, ask.", ct);

    public Task<string> CounselOnAdherenceAsync(string medication, string patientConcerns, CancellationToken ct=default)
        => _i.AgentAsync($"Counsel on adherence to {medication}. Patient concerns: {patientConcerns}. Address each with evidence + practical strategies.", ct);

}
