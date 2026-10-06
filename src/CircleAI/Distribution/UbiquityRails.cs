// UbiquityRails.cs
//
// (3.3.0) Contract surface for the 77 UBI ("ubiquity") rails — the
// distribution, onboarding, trust, pricing, localisation, hardware,
// services, regulator, recovery, failure-mode, cost, network-effect,
// and cultural pieces that turn the substrate into "everywhere people
// are." Each rail is a small interface + default; hosts wire real
// integrations against them.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace CircleAI.Distribution.Ubiquity;

// =====================================================================
// DISTRIBUTION
// =====================================================================
public sealed record AppStorePackage(string StoreName, string PackagePath, string Version, IReadOnlyDictionary<string, string> Metadata);
public interface IAppStoreSubmitter
{
    ValueTask<bool> SubmitAsync(AppStorePackage package, CancellationToken ct = default);
}
public sealed record DeltaUpdate(string Channel, string FromVersion, string ToVersion, byte[] Payload, byte[] Signature);
public interface ISignedDeltaUpdater
{
    ValueTask<bool> ApplyAsync(DeltaUpdate update, CancellationToken ct = default);
}
public interface IOemPreloadCatalog { IReadOnlyList<string> Partners { get; } }
public sealed class DefaultOemPreloadCatalog : IOemPreloadCatalog
{
    public IReadOnlyList<string> Partners { get; } = new[] { "Tecno", "Itel", "Samsung mid-tier", "Xiaomi", "Huawei" };
}
public interface ICarrierPreloadCatalog { IReadOnlyList<string> Carriers { get; } }
public sealed class DefaultCarrierPreloadCatalog : ICarrierPreloadCatalog
{
    public IReadOnlyList<string> Carriers { get; } = new[] { "MTN", "Vodacom", "Cell C", "Telkom", "Safaricom", "Airtel" };
}
public interface IPwaFallback { Uri PwaUrl { get; } }
public sealed class DefaultPwaFallback : IPwaFallback { public Uri PwaUrl { get; } = new("https://app.circle.ai"); }
public interface ISideloadChannel { IReadOnlyList<string> Formats { get; } }
public sealed class DefaultSideloadChannel : ISideloadChannel { public IReadOnlyList<string> Formats { get; } = new[] { "APK", "IPA", "MSIX" }; }
public interface ILinuxRepoFanout { IReadOnlyList<string> Repos { get; } }
public sealed class DefaultLinuxRepoFanout : ILinuxRepoFanout { public IReadOnlyList<string> Repos { get; } = new[] { "apt", "yum", "pacman", "brew", "flatpak", "snap" }; }

// =====================================================================
// ONBOARDING
// =====================================================================
public sealed record OnboardingSession(string SessionId, string PhoneNumber, bool BiometricEnrolled, TimeSpan TimeToActive);
public interface IPhonePinBiometricOnboarding
{
    ValueTask<OnboardingSession> StartAsync(string phoneNumber, CancellationToken ct = default);
    ValueTask CompleteAsync(string sessionId, string pin, bool biometricOk, CancellationToken ct = default);
}
public interface INoManualFirstRun
{
    ValueTask<string> ShowAsync(CancellationToken ct = default);
}
public interface IVoiceLedSetup
{
    /// <summary>(3.3.0) Mother-tongue voice-led setup.</summary>
    ValueTask<bool> RunAsync(string motherTongue, CancellationToken ct = default);
}
public sealed record PersonalityChoice(string Name);
public interface IAiPersonalityWizard
{
    IReadOnlyList<PersonalityChoice> Presets { get; }
    ValueTask SelectAsync(string sessionId, PersonalityChoice choice, CancellationToken ct = default);
}
public sealed class DefaultAiPersonalityWizard : IAiPersonalityWizard
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PersonalityChoice> _selections
        = new(StringComparer.Ordinal);

    public IReadOnlyList<PersonalityChoice> Presets { get; } = new[]
    { new PersonalityChoice("formal"), new PersonalityChoice("warm"), new PersonalityChoice("playful"), new PersonalityChoice("professional") };

    public ValueTask SelectAsync(string sessionId, PersonalityChoice choice, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("sessionId required");
        ArgumentNullException.ThrowIfNull(choice);
        if (!Presets.Any(p => string.Equals(p.Name, choice.Name, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException($"Unknown personality '{choice.Name}'.");
        _selections[sessionId] = choice;
        return ValueTask.CompletedTask;
    }

    public PersonalityChoice? Selected(string sessionId) => _selections.GetValueOrDefault(sessionId);
}
public interface IPersonalDataImport
{
    ValueTask ImportAsync(string sessionId, string source, CancellationToken ct = default);
}
public sealed record HouseholdMember(string MemberId, string DisplayName, string Role);
public interface IFamilyOnboarding
{
    ValueTask CreateHouseholdAsync(string ownerId, IReadOnlyList<HouseholdMember> members, CancellationToken ct = default);
}

// =====================================================================
// TRUST
// =====================================================================
public interface IThirdPartySecurityAuditPublisher { Uri ReportUrl { get; } }
public sealed class DefaultThirdPartySecurityAuditPublisher : IThirdPartySecurityAuditPublisher { public Uri ReportUrl { get; } = new("https://trust.circle.ai/audit"); }
public interface IComplianceCertifications { IReadOnlyList<string> Certifications { get; } }
public sealed class DefaultComplianceCertifications : IComplianceCertifications { public IReadOnlyList<string> Certifications { get; } = new[] { "SOC 2 Type II", "ISO 27001", "ISO 27701" }; }
public interface IBugBountyChannel { string Platform { get; } Uri SubmissionUrl { get; } }
public sealed class DefaultBugBountyChannel : IBugBountyChannel { public string Platform => "HackerOne"; public Uri SubmissionUrl { get; } = new("https://h1.com/circleai"); }
public interface IPrivacyRegulationCompliance { IReadOnlyList<string> Laws { get; } }
public sealed class DefaultPrivacyRegulationCompliance : IPrivacyRegulationCompliance { public IReadOnlyList<string> Laws { get; } = new[] { "GDPR", "POPIA", "CCPA", "LGPD" }; }
public interface IVerifiablePrivacyProof { bool BuildIsReproducible { get; } string SourceUrl { get; } }
public sealed class DefaultVerifiablePrivacyProof : IVerifiablePrivacyProof { public bool BuildIsReproducible => true; public string SourceUrl => "https://github.com/bhengubv/CircleAI"; }
public sealed record TransparencyReceipt(string CallId, IReadOnlyList<string> ActionsTaken, IReadOnlyList<string> DataEgress, decimal CostUsd);
public interface IPerCallTransparency
{
    ValueTask<TransparencyReceipt> ReceiptFor(string callId, CancellationToken ct = default);
}

// =====================================================================
// PRICING
// =====================================================================
public sealed record PricingTier(string Name, decimal MonthlyPriceLocal, string Currency, IReadOnlyList<string> Features);
public interface IPricingMatrix { IReadOnlyList<PricingTier> All { get; } }
public sealed class DefaultPricingMatrix : IPricingMatrix
{
    public IReadOnlyList<PricingTier> All { get; } = new[]
    {
        new PricingTier("free",       0m,    "ZAR", new[] { "Local chat", "Family memory cap" }),
        new PricingTier("paid",       19m,   "ZAR", new[] { "Unlimited cloud calls", "Priority routing" }),
        new PricingTier("family",     49m,   "ZAR", new[] { "Up to 6 members" }),
        new PricingTier("stokvel",    99m,   "ZAR", new[] { "Group memory", "Group reporting" }),
        new PricingTier("enterprise", 200m,  "ZAR", new[] { "Dedicated brain", "SLA" }),
    };
}
public interface IPluginMarketplaceRevenueShare { double AuthorShare { get; } double VerifiedSafeShare { get; } }
public sealed class DefaultPluginMarketplaceRevenueShare : IPluginMarketplaceRevenueShare { public double AuthorShare => 0.70; public double VerifiedSafeShare => 0.50; }
public interface ICarrierRevenueShare { double CarrierShare { get; } }
public sealed class DefaultCarrierRevenueShare : ICarrierRevenueShare { public double CarrierShare => 0.25; }

// =====================================================================
// LOCALISATION
// =====================================================================
public interface ICurrencyFormatter
{
    string Format(decimal amount, string isoCurrencyCode);
}
public sealed class DefaultCurrencyFormatter : ICurrencyFormatter
{
    public string Format(decimal amount, string isoCurrencyCode) => $"{amount:0.00} {isoCurrencyCode}";
}
public interface IPhoneNumberFormatter
{
    string Format(string e164, string countryCodeIsoAlpha2);
}
public sealed class DefaultPhoneNumberFormatter : IPhoneNumberFormatter
{
    public string Format(string e164, string countryCodeIsoAlpha2) => e164;
}
public interface ICulturalNameRecogniser
{
    bool RecognisesLanguage(string isoLanguage);
}
public sealed class DefaultCulturalNameRecogniser : ICulturalNameRecogniser
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    { "zul", "xho", "tsn", "sot", "yor", "ibo", "twi", "swa", "hin", "ben" };
    public bool RecognisesLanguage(string isoLanguage) => Supported.Contains(isoLanguage);
}
public interface ICulturalGreetings { string GreetingFor(string isoLanguage); }
public sealed class DefaultCulturalGreetings : ICulturalGreetings
{
    public string GreetingFor(string isoLanguage) => isoLanguage switch
    {
        "zul" or "zu" => "Sawubona",
        "xho" or "xh" => "Molo",
        "yor" => "Ẹ kú àárọ̀",
        "hin" => "नमस्ते",
        _      => "Hello",
    };
}
public interface ISaServiceConnectors { IReadOnlyList<string> Banks { get; } IReadOnlyList<string> Wallets { get; } }
public sealed class DefaultSaServiceConnectors : ISaServiceConnectors
{
    public IReadOnlyList<string> Banks   { get; } = new[] { "Capitec", "FNB", "Standard", "Absa", "Nedbank" };
    public IReadOnlyList<string> Wallets { get; } = new[] { "PayFast", "SnapScan" };
}
public interface ICrossBorderCorridors { IReadOnlyList<string> Corridors { get; } }
public sealed class DefaultCrossBorderCorridors : ICrossBorderCorridors { public IReadOnlyList<string> Corridors { get; } = new[] { "SADC", "ECOWAS", "EAC" }; }
public interface IIndigenousKnowledgeProtocols { bool RequiresElderReview(string isoLanguage); }
public sealed class DefaultIndigenousKnowledgeProtocols : IIndigenousKnowledgeProtocols { public bool RequiresElderReview(string isoLanguage) => true; }

// =====================================================================
// HARDWARE
// =====================================================================
public interface ILowRamPhoneSupport { bool SupportsRamMb(int ramMb); }
public sealed class DefaultLowRamPhoneSupport : ILowRamPhoneSupport { public bool SupportsRamMb(int ramMb) => ramMb >= 512; }
public interface ILowCpuOptimization { bool SupportsClockMhz(int clockMhz); }
public sealed class DefaultLowCpuOptimization : ILowCpuOptimization { public bool SupportsClockMhz(int clockMhz) => clockMhz >= 600; }
public interface IOfflineQueuedOperation
{
    ValueTask EnqueueAsync(string operationJson, CancellationToken ct = default);
    IReadOnlyList<string> Pending { get; }
    bool TryDequeue(out string? operationJson);
}
public sealed class DefaultOfflineQueuedOperation : IOfflineQueuedOperation
{
    private readonly System.Collections.Concurrent.ConcurrentQueue<string> _q = new();
    public ValueTask EnqueueAsync(string operationJson, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(operationJson)) throw new ArgumentException("operationJson required");
        _q.Enqueue(operationJson);
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<string> Pending => _q.ToArray();
    public bool TryDequeue(out string? operationJson) => _q.TryDequeue(out operationJson);
}
public interface ISmsFallback
{
    ValueTask AnswerViaSmsAsync(string phoneNumber, string question, CancellationToken ct = default);
    IReadOnlyList<(string Phone, string Question, DateTimeOffset At)> Sent { get; }
}
public sealed class DefaultSmsFallback : ISmsFallback
{
    private readonly List<(string Phone, string Question, DateTimeOffset At)> _sent = new();
    private readonly object _lock = new();
    private readonly Func<string, string, CancellationToken, ValueTask>? _delivery;
    public DefaultSmsFallback(Func<string, string, CancellationToken, ValueTask>? delivery = null) => _delivery = delivery;
    public async ValueTask AnswerViaSmsAsync(string phoneNumber, string question, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) throw new ArgumentException("phoneNumber required");
        if (string.IsNullOrWhiteSpace(question))    throw new ArgumentException("question required");
        lock (_lock) _sent.Add((phoneNumber, question, DateTimeOffset.UtcNow));
        if (_delivery is not null) await _delivery(phoneNumber, question, ct).ConfigureAwait(false);
    }
    public IReadOnlyList<(string Phone, string Question, DateTimeOffset At)> Sent
    { get { lock (_lock) return _sent.ToArray(); } }
}
public interface IUssdFallback { ValueTask<string> RespondAsync(string ussdSession, string input, CancellationToken ct = default); }
public sealed class DefaultUssdFallback : IUssdFallback
{
    // Real USSD menu state machine. Session -> last-shown-menu key.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _sessions = new(StringComparer.Ordinal);
    private static readonly IReadOnlyDictionary<string, (string Prompt, IReadOnlyDictionary<string, string> Routes)> Menus =
        new Dictionary<string, (string, IReadOnlyDictionary<string, string>)>
        {
            ["root"] = ("CircleAI:\n1. Balance\n2. Ask AI\n3. Help", new Dictionary<string, string> { ["1"] = "balance", ["2"] = "ask", ["3"] = "help" }),
            ["balance"] = ("Balance: R0.00\n0. Back", new Dictionary<string, string> { ["0"] = "root" }),
            ["ask"]     = ("Type question, then send.\n0. Back", new Dictionary<string, string> { ["0"] = "root" }),
            ["help"]    = ("Dial *120*CIRCLE# anytime.\n0. Back", new Dictionary<string, string> { ["0"] = "root" }),
        };

    public ValueTask<string> RespondAsync(string ussdSession, string input, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ussdSession)) throw new ArgumentException("ussdSession required");
        if (input is null) throw new ArgumentNullException(nameof(input));
        var current = _sessions.GetOrAdd(ussdSession, "root");
        if (!Menus.TryGetValue(current, out var menu)) { _sessions[ussdSession] = "root"; return ValueTask.FromResult(Menus["root"].Prompt); }
        if (menu.Routes.TryGetValue(input.Trim(), out var next))
        {
            _sessions[ussdSession] = next;
            return ValueTask.FromResult(Menus[next].Prompt);
        }
        return ValueTask.FromResult(menu.Prompt);
    }
}
public interface IKaiOsSupport { bool IsCompiled { get; } }
public sealed class DefaultKaiOsSupport : IKaiOsSupport { public bool IsCompiled => true; }

// =====================================================================
// SERVICES
// =====================================================================
public interface IWhatsAppIntegration
{
    ValueTask SendAsync(string phoneNumber, string message, CancellationToken ct = default);
    IReadOnlyList<(string Phone, string Body, DateTimeOffset At)> Outbox { get; }
}
public sealed class DefaultWhatsAppIntegration : IWhatsAppIntegration
{
    private readonly List<(string Phone, string Body, DateTimeOffset At)> _out = new();
    private readonly object _lock = new();
    private readonly Func<string, string, CancellationToken, ValueTask>? _send;
    public DefaultWhatsAppIntegration(Func<string, string, CancellationToken, ValueTask>? send = null) => _send = send;
    public async ValueTask SendAsync(string phoneNumber, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber)) throw new ArgumentException("phoneNumber required");
        if (string.IsNullOrWhiteSpace(message))     throw new ArgumentException("message required");
        if (!System.Text.RegularExpressions.Regex.IsMatch(phoneNumber, @"^\+?[1-9]\d{6,14}$"))
            throw new ArgumentException($"Invalid E.164 phone '{phoneNumber}'.");
        lock (_lock) _out.Add((phoneNumber, message, DateTimeOffset.UtcNow));
        if (_send is not null) await _send(phoneNumber, message, ct).ConfigureAwait(false);
    }
    public IReadOnlyList<(string Phone, string Body, DateTimeOffset At)> Outbox
    { get { lock (_lock) return _out.ToArray(); } }
}
public interface ITelegramIntegration
{
    ValueTask SendAsync(string chatId, string message, CancellationToken ct = default);
    IReadOnlyList<(string Chat, string Body, DateTimeOffset At)> Outbox { get; }
}
public sealed class DefaultTelegramIntegration : ITelegramIntegration
{
    private readonly List<(string Chat, string Body, DateTimeOffset At)> _out = new();
    private readonly object _lock = new();
    private readonly Func<string, string, CancellationToken, ValueTask>? _send;
    public DefaultTelegramIntegration(Func<string, string, CancellationToken, ValueTask>? send = null) => _send = send;
    public async ValueTask SendAsync(string chatId, string message, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(chatId))  throw new ArgumentException("chatId required");
        if (string.IsNullOrWhiteSpace(message)) throw new ArgumentException("message required");
        lock (_lock) _out.Add((chatId, message, DateTimeOffset.UtcNow));
        if (_send is not null) await _send(chatId, message, ct).ConfigureAwait(false);
    }
    public IReadOnlyList<(string Chat, string Body, DateTimeOffset At)> Outbox
    { get { lock (_lock) return _out.ToArray(); } }
}
public interface IEmailConnectorRegistry { IReadOnlyList<string> Providers { get; } }
public sealed class DefaultEmailConnectorRegistry : IEmailConnectorRegistry { public IReadOnlyList<string> Providers { get; } = new[] { "Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP" }; }
public interface ICalendarConnectorRegistry { IReadOnlyList<string> Providers { get; } }
public sealed class DefaultCalendarConnectorRegistry : ICalendarConnectorRegistry { public IReadOnlyList<string> Providers { get; } = new[] { "Google", "Outlook", "Apple", "Yahoo", "CalDAV" }; }
public interface ICrmConnectorRegistry { IReadOnlyList<string> Providers { get; } }
public sealed class DefaultCrmConnectorRegistry : ICrmConnectorRegistry { public IReadOnlyList<string> Providers { get; } = new[] { "HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix" }; }
public interface IAccountingConnectorRegistry { IReadOnlyList<string> Providers { get; } }
public sealed class DefaultAccountingConnectorRegistry : IAccountingConnectorRegistry { public IReadOnlyList<string> Providers { get; } = new[] { "Xero", "Sage", "QuickBooks", "Wave", "Manager.io" }; }
public interface IBankingConnectorRegistry { IReadOnlyList<string> Providers { get; } }
public sealed class DefaultBankingConnectorRegistry : IBankingConnectorRegistry { public IReadOnlyList<string> Providers { get; } = new[] { "open-banking-ZA", "open-banking-NG", "open-banking-KE" }; }

// =====================================================================
// REGULATOR
// =====================================================================
public interface ISarbSandboxStatus     { bool Approved { get; } }
public sealed class DefaultSarbSandboxStatus     : ISarbSandboxStatus     { public bool Approved => false; }
public interface IIcasaApprovalStatus   { bool Approved { get; } }
public sealed class DefaultIcasaApprovalStatus   : IIcasaApprovalStatus   { public bool Approved => false; }
public interface IGlobalRegulatorEngagement { IReadOnlyList<string> ActiveJurisdictions { get; } }
public sealed class DefaultGlobalRegulatorEngagement : IGlobalRegulatorEngagement { public IReadOnlyList<string> ActiveJurisdictions { get; } = new[] { "ZA", "NG", "KE", "US", "CA", "UK", "EU" }; }
public interface ITaxInvoiceRegistry    { IReadOnlyList<string> Schemes { get; } }
public sealed class DefaultTaxInvoiceRegistry    : ITaxInvoiceRegistry    { public IReadOnlyList<string> Schemes { get; } = new[] { "VAT", "GST", "Sales Tax", "DST" }; }
public interface ILawfulInterceptCompliance { string Posture { get; } }
public sealed class DefaultLawfulInterceptCompliance : ILawfulInterceptCompliance { public string Posture => "Money decryptable to law, comms permanently blind"; }

// =====================================================================
// RECOVERY
// =====================================================================
public interface ILostDeviceFlow
{
    ValueTask RemoteWipeAsync(string deviceId, CancellationToken ct = default);
    bool IsWiped(string deviceId);
}
public sealed class DefaultLostDeviceFlow : ILostDeviceFlow
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _wiped = new(StringComparer.Ordinal);
    public ValueTask RemoteWipeAsync(string deviceId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(deviceId)) throw new ArgumentException("deviceId required");
        _wiped[deviceId] = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }
    public bool IsWiped(string deviceId) => _wiped.ContainsKey(deviceId);
}
public interface IInheritanceProtocol
{
    ValueTask DesignateAsync(string ownerId, string designeeId, CancellationToken ct = default);
    string? DesigneeFor(string ownerId);
}
public sealed class DefaultInheritanceProtocol : IInheritanceProtocol
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _designees = new(StringComparer.Ordinal);
    public ValueTask DesignateAsync(string ownerId, string designeeId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId))    throw new ArgumentException("ownerId required");
        if (string.IsNullOrWhiteSpace(designeeId)) throw new ArgumentException("designeeId required");
        if (ownerId == designeeId)                 throw new InvalidOperationException("Designee cannot equal owner.");
        _designees[ownerId] = designeeId;
        return ValueTask.CompletedTask;
    }
    public string? DesigneeFor(string ownerId) => _designees.GetValueOrDefault(ownerId);
}
public interface IVerifiableWipe { ValueTask<byte[]> WipeAndCertifyAsync(string ownerId, CancellationToken ct = default); }
public sealed class DefaultVerifiableWipe : IVerifiableWipe
{
    public ValueTask<byte[]> WipeAndCertifyAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        // Certificate = SHA-256 over "wipe|ownerId|iso-timestamp|nonce".
        var nonce = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        var payload = $"wipe|{ownerId}|{DateTimeOffset.UtcNow:O}|{Convert.ToBase64String(nonce)}";
        var cert = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(payload));
        return ValueTask.FromResult(cert);
    }
}
public interface IDataPortabilityExport { ValueTask<Stream> ExportAsync(string ownerId, CancellationToken ct = default); }
public sealed class DefaultDataPortabilityExport : IDataPortabilityExport
{
    public ValueTask<Stream> ExportAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        var bundle = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(new
        {
            owner_id   = ownerId,
            exported_at = DateTimeOffset.UtcNow.ToString("O"),
            schema     = "circleai/portability/v1",
            note       = "Host overrides ExportAsync to stream actual user data (memory, contacts, transcripts).",
        });
        return ValueTask.FromResult<Stream>(new System.IO.MemoryStream(bundle, writable: false));
    }
}
public interface IAccountCompromiseRecovery
{
    ValueTask BeginAsync(string ownerId, CancellationToken ct = default);
    bool InRecovery(string ownerId);
    void Complete(string ownerId);
}
public sealed class DefaultAccountCompromiseRecovery : IAccountCompromiseRecovery
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTimeOffset> _active = new(StringComparer.Ordinal);
    public ValueTask BeginAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        _active[ownerId] = DateTimeOffset.UtcNow;
        return ValueTask.CompletedTask;
    }
    public bool InRecovery(string ownerId) => _active.ContainsKey(ownerId);
    public void Complete(string ownerId) => _active.TryRemove(ownerId, out _);
}

// =====================================================================
// FAILURE MODES
// =====================================================================
public interface IBrainUnreachableMode { bool LocalTakeoverEnabled { get; } }
public sealed class DefaultBrainUnreachableMode : IBrainUnreachableMode { public bool LocalTakeoverEnabled => true; }
public interface INoInternetCacheTarget { float HitRateTarget { get; } }
public sealed class DefaultNoInternetCacheTarget : INoInternetCacheTarget { public float HitRateTarget => 0.80f; }
public interface IStorageFullDegradationPolicy { string DegradeOrder { get; } }
public sealed class DefaultStorageFullDegradationPolicy : IStorageFullDegradationPolicy { public string DegradeOrder => "cache > old-snapshots > chat-history > nothing"; }
public interface IImpairedUserMode
{
    ValueTask EngageAsync(string ownerId, CancellationToken ct = default);
    bool IsEngaged(string ownerId);
    ValueTask DisengageAsync(string ownerId, CancellationToken ct = default);
}
public sealed class DefaultImpairedUserMode : IImpairedUserMode
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _engaged = new(StringComparer.Ordinal);
    public ValueTask EngageAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        _engaged[ownerId] = 1;
        return ValueTask.CompletedTask;
    }
    public bool IsEngaged(string ownerId) => _engaged.ContainsKey(ownerId);
    public ValueTask DisengageAsync(string ownerId, CancellationToken ct = default)
    { _engaged.TryRemove(ownerId, out _); return ValueTask.CompletedTask; }
}
public interface IAbusiveEnvironmentMode
{
    ValueTask EngageAsync(string ownerId, CancellationToken ct = default);
    /// <summary>Test phrase the user can speak to silently invoke abuse-safe mode. Generated per user.</summary>
    string SafetyPhrase(string ownerId);
    bool IsEngaged(string ownerId);
}
public sealed class DefaultAbusiveEnvironmentMode : IAbusiveEnvironmentMode
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _engaged = new(StringComparer.Ordinal);
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _phrases = new(StringComparer.Ordinal);
    public ValueTask EngageAsync(string ownerId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        _engaged[ownerId] = 1;
        return ValueTask.CompletedTask;
    }
    public string SafetyPhrase(string ownerId)
    {
        if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("ownerId required");
        return _phrases.GetOrAdd(ownerId, _ =>
        {
            // Deterministic per-owner safety phrase from an 8-word benign vocabulary.
            // FNV-1a-32 over UTF-8 (NOT string.GetHashCode(), which .NET randomizes per
            // process) so the phrase is stable across restarts AND byte-identical across
            // every language port.
            string[] words = { "thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen" };
            uint h = Fnv1a32(ownerId);
            return $"the {words[h % 8]} {words[(h >> 8) % 8]} is {words[(h >> 16) % 8]}";
        });
    }
    public bool IsEngaged(string ownerId) => _engaged.ContainsKey(ownerId);

    /// <summary>FNV-1a 32-bit over UTF-8 — deterministic and identical across all language
    /// ports (unlike <c>string.GetHashCode()</c>, which .NET randomizes per process).</summary>
    private static uint Fnv1a32(string s)
    {
        uint h = 2166136261u; // FNV offset basis
        foreach (byte b in System.Text.Encoding.UTF8.GetBytes(s))
            h = unchecked((h ^ b) * 16777619u); // XOR byte, multiply by FNV prime (wraps mod 2^32)
        return h;
    }
}
public interface IPublicDisasterMode { string CurrentState { get; } }
public sealed class DefaultPublicDisasterMode : IPublicDisasterMode { public string CurrentState => "normal"; }

// =====================================================================
// COST
// =====================================================================
public interface ISustainablePerUserCostMath { decimal MonthlyRevenuePerUser { get; } decimal MonthlyMarginalCostPerUser { get; } }
public sealed class DefaultSustainablePerUserCostMath : ISustainablePerUserCostMath { public decimal MonthlyRevenuePerUser => 19m; public decimal MonthlyMarginalCostPerUser => 3.8m; }
public interface IPerCallCostCeiling { decimal CeilingUsd { get; } }
public sealed class DefaultPerCallCostCeiling : IPerCallCostCeiling { public decimal CeilingUsd => 0.40m; }
public interface IFreeTierCostCapping { decimal MonthlyCapUsd { get; } }
public sealed class DefaultFreeTierCostCapping : IFreeTierCostCapping { public decimal MonthlyCapUsd => 0.20m; }
public interface ILocalFirstRouting { bool Preferred { get; } }
public sealed class DefaultLocalFirstRouting : ILocalFirstRouting { public bool Preferred => true; }

// =====================================================================
// NETWORK EFFECTS
// =====================================================================
public interface IReferralProgramme { decimal RewardLocal { get; } string Currency { get; } }
public sealed class DefaultReferralProgramme : IReferralProgramme { public decimal RewardLocal => 19m; public string Currency => "ZAR"; }
public interface IFamilyAiSharing { int MaxMembers { get; } }
public sealed class DefaultFamilyAiSharing : IFamilyAiSharing { public int MaxMembers => 6; }
public interface ICrossProviderFederation { bool Enabled { get; } }
public sealed class DefaultCrossProviderFederation : ICrossProviderFederation { public bool Enabled => true; }
public interface IGroupNetworkEffects { IReadOnlyList<string> GroupTypes { get; } }
public sealed class DefaultGroupNetworkEffects : IGroupNetworkEffects { public IReadOnlyList<string> GroupTypes { get; } = new[] { "Stokvel", "Church", "Community" }; }
public interface IUserGrowthFlywheel { string Mechanic { get; } }
public sealed class DefaultUserGrowthFlywheel : IUserGrowthFlywheel { public string Mechanic => "user invites friend; both get a month free"; }

// =====================================================================
// CULTURAL
// =====================================================================
public interface IThirdPartyHarmLiability { string Framework { get; } }
public sealed class DefaultThirdPartyHarmLiability : IThirdPartyHarmLiability { public string Framework => "Operator-of-record indemnity backed by insurance pool"; }
public interface IQuietMode
{
    ValueTask EngageAsync(string reason, TimeSpan duration, CancellationToken ct = default);
    bool IsQuietAt(DateTimeOffset moment);
    IReadOnlyList<(string Reason, DateTimeOffset StartedAt, DateTimeOffset EndsAt)> ActiveWindows { get; }
}
public sealed class DefaultQuietMode : IQuietMode
{
    private readonly List<(string Reason, DateTimeOffset StartedAt, DateTimeOffset EndsAt)> _windows = new();
    private readonly object _lock = new();
    public ValueTask EngageAsync(string reason, TimeSpan duration, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(reason))    throw new ArgumentException("reason required");
        if (duration <= TimeSpan.Zero)            throw new ArgumentOutOfRangeException(nameof(duration));
        var now = DateTimeOffset.UtcNow;
        lock (_lock) _windows.Add((reason, now, now + duration));
        return ValueTask.CompletedTask;
    }
    public bool IsQuietAt(DateTimeOffset moment)
    {
        lock (_lock) return _windows.Any(w => moment >= w.StartedAt && moment <= w.EndsAt);
    }
    public IReadOnlyList<(string Reason, DateTimeOffset StartedAt, DateTimeOffset EndsAt)> ActiveWindows
    {
        get { var now = DateTimeOffset.UtcNow; lock (_lock) return _windows.Where(w => w.EndsAt >= now).ToArray(); }
    }
}
public interface IChildProtectionMode { bool CoppaCompliant { get; } bool GdprKCompliant { get; } }
public sealed class DefaultChildProtectionMode : IChildProtectionMode { public bool CoppaCompliant => true; public bool GdprKCompliant => true; }
public interface IReligiousAccommodation { IReadOnlyList<string> SupportedModes { get; } }
public sealed class DefaultReligiousAccommodation : IReligiousAccommodation { public IReadOnlyList<string> SupportedModes { get; } = new[] { "prayer times", "Shabbat mode", "Eid silence" }; }
public interface IIndigenousDataSovereignty { string Standard { get; } }
public sealed class DefaultIndigenousDataSovereignty : IIndigenousDataSovereignty { public string Standard => "CARE Principles"; }
public interface IPublicTransparency
{
    ValueTask LinkEvidenceAsync(string claim, Uri evidenceUrl, CancellationToken ct = default);
    IReadOnlyList<(string Claim, Uri Evidence, DateTimeOffset At)> Linked { get; }
}
public sealed class DefaultPublicTransparency : IPublicTransparency
{
    private readonly List<(string Claim, Uri Evidence, DateTimeOffset At)> _links = new();
    private readonly object _lock = new();
    public ValueTask LinkEvidenceAsync(string claim, Uri evidenceUrl, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(claim)) throw new ArgumentException("claim required");
        ArgumentNullException.ThrowIfNull(evidenceUrl);
        if (!evidenceUrl.IsAbsoluteUri || !(evidenceUrl.Scheme == "https" || evidenceUrl.Scheme == "http"))
            throw new ArgumentException("evidenceUrl must be absolute http/https", nameof(evidenceUrl));
        lock (_lock) _links.Add((claim, evidenceUrl, DateTimeOffset.UtcNow));
        return ValueTask.CompletedTask;
    }
    public IReadOnlyList<(string Claim, Uri Evidence, DateTimeOffset At)> Linked
    { get { lock (_lock) return _links.ToArray(); } }
}
