// distribution_ubiquity.go
//
// Ports the remaining CircleAI.Distribution.Ubiquity rails (UbiquityRails.cs +
// UbiquityRailsMissingDefaults.cs) beyond the DISTRIBUTION section in
// distribution.go: onboarding, trust, pricing, localisation, hardware, services,
// regulator, recovery, failure-mode, cost, network-effect and cultural rails.
//
// Each rail is a small interface (I-prefix dropped) plus its Default* in-memory
// implementation. Constant-returning rails are ported verbatim; stateful rails
// (onboarding sessions, USSD menu state machine, offline queue, quiet windows,
// transparency links, wipe certificates) keep their real behaviour.
//
// Monetary amounts (pricing tiers, referral reward, cost math) use the shared
// exact Decimal (C# decimal). URIs are plain strings. The C# RandomNumberGenerator
// nonce in DefaultVerifiableWipe is reproduced with crypto/rand.

package circleai

import (
	"context"
	"crypto/rand"
	"crypto/sha256"
	"encoding/base64"
	"encoding/hex"
	"errors"
	"regexp"
	"strings"
	"sync"
	"time"
)

// ── ONBOARDING ──────────────────────────────────────────────────────────────

// OnboardingSession is a phone-pin-biometric onboarding session. Ports the
// OnboardingSession record.
type OnboardingSession struct {
	SessionID         string
	PhoneNumber       string
	BiometricEnrolled bool
	TimeToActive      time.Duration
}

var e164Regex = regexp.MustCompile(`^\+?[1-9]\d{6,14}$`)

// PhonePinBiometricOnboarding starts + completes onboarding. Ports
// IPhonePinBiometricOnboarding.
type PhonePinBiometricOnboarding interface {
	Start(ctx context.Context, phoneNumber string) (OnboardingSession, error)
	Complete(ctx context.Context, sessionID, pin string, biometricOk bool) error
}

// DefaultPhonePinBiometricOnboarding tracks sessions + PIN hashes. Ports
// DefaultPhonePinBiometricOnboarding. Construct with
// NewDefaultPhonePinBiometricOnboarding.
type DefaultPhonePinBiometricOnboarding struct {
	mu        sync.Mutex
	sessions  map[string]OnboardingSession
	pinHashes map[string]string
}

// NewDefaultPhonePinBiometricOnboarding constructs an empty onboarding rail.
func NewDefaultPhonePinBiometricOnboarding() *DefaultPhonePinBiometricOnboarding {
	return &DefaultPhonePinBiometricOnboarding{
		sessions:  make(map[string]OnboardingSession),
		pinHashes: make(map[string]string),
	}
}

// Start opens a session for a valid E.164 phone. Ports StartAsync.
func (o *DefaultPhonePinBiometricOnboarding) Start(ctx context.Context, phoneNumber string) (OnboardingSession, error) {
	if strings.TrimSpace(phoneNumber) == "" {
		return OnboardingSession{}, errors.New("phoneNumber required")
	}
	if !e164Regex.MatchString(phoneNumber) {
		return OnboardingSession{}, errors.New("Invalid E.164 phone '" + phoneNumber + "'.")
	}
	sid := newHexGUID()
	session := OnboardingSession{SessionID: sid, PhoneNumber: phoneNumber}
	o.mu.Lock()
	o.sessions[sid] = session
	o.mu.Unlock()
	return session, nil
}

// Complete validates the PIN, stores its hash, and flags biometric enrolment.
// Ports CompleteAsync. Returns an error for a weak PIN or unknown session.
func (o *DefaultPhonePinBiometricOnboarding) Complete(ctx context.Context, sessionID, pin string, biometricOk bool) error {
	if strings.TrimSpace(sessionID) == "" {
		return errors.New("sessionId required")
	}
	if len(pin) < 4 || !allDigits(pin) {
		return errors.New("PIN must be at least 4 digits")
	}
	o.mu.Lock()
	defer o.mu.Unlock()
	s, ok := o.sessions[sessionID]
	if !ok {
		return errors.New("Unknown session " + sessionID)
	}
	o.pinHashes[s.PhoneNumber] = sha256HexUpper(pin + s.PhoneNumber)
	s.BiometricEnrolled = biometricOk
	s.TimeToActive = time.Minute // placeholder matching the C# elapsed placeholder
	o.sessions[sessionID] = s
	return nil
}

// VerifyPin reports whether pin matches the stored hash for phoneNumber. Ports
// VerifyPin.
func (o *DefaultPhonePinBiometricOnboarding) VerifyPin(phoneNumber, pin string) bool {
	o.mu.Lock()
	h, ok := o.pinHashes[phoneNumber]
	o.mu.Unlock()
	if !ok {
		return false
	}
	return h == sha256HexUpper(pin+phoneNumber)
}

// NoManualFirstRun shows a single welcome card. Ports INoManualFirstRun.
type NoManualFirstRun interface {
	Show(ctx context.Context) (string, error)
}

// DefaultNoManualFirstRun returns a fixed welcome card. Ports
// DefaultNoManualFirstRun. The zero value shows the default card.
type DefaultNoManualFirstRun struct{ Welcome string }

// Show returns the welcome card. Ports ShowAsync.
func (d DefaultNoManualFirstRun) Show(ctx context.Context) (string, error) {
	if d.Welcome == "" {
		return "Welcome to Circle AI. Tap the mic and say hello — that's it.", nil
	}
	return d.Welcome, nil
}

// VoiceLedSetup runs mother-tongue voice-led setup. Ports IVoiceLedSetup.
type VoiceLedSetup interface {
	Run(ctx context.Context, motherTongue string) (bool, error)
}

// DefaultVoiceLedSetup accepts supported mother tongues. Ports DefaultVoiceLedSetup.
type DefaultVoiceLedSetup struct{}

var voiceLedSupported = toSet(
	"en", "af", "zu", "xh", "st", "tn", "ts", "ss", "ve", "nr", "nso",
	"sw", "ha", "yo", "ig", "am", "fr", "pt", "ar", "hi", "bn", "es",
)

// Run reports whether motherTongue's prefix is supported. Ports RunAsync.
func (DefaultVoiceLedSetup) Run(ctx context.Context, motherTongue string) (bool, error) {
	if strings.TrimSpace(motherTongue) == "" {
		return false, errors.New("motherTongue required")
	}
	prefix := strings.SplitN(motherTongue, "-", 2)[0]
	return voiceLedSupported[strings.ToLower(prefix)], nil
}

// PersonalityChoice is a chosen AI personality. Ports the PersonalityChoice record.
type PersonalityChoice struct{ Name string }

// AiPersonalityWizard offers presets + records selections. Ports IAiPersonalityWizard.
type AiPersonalityWizard interface {
	Presets() []PersonalityChoice
	Select(ctx context.Context, sessionID string, choice PersonalityChoice) error
}

// DefaultAiPersonalityWizard validates against presets + records selections.
// Ports DefaultAiPersonalityWizard. Construct with NewDefaultAiPersonalityWizard.
type DefaultAiPersonalityWizard struct {
	mu         sync.Mutex
	selections map[string]PersonalityChoice
}

// NewDefaultAiPersonalityWizard constructs the wizard.
func NewDefaultAiPersonalityWizard() *DefaultAiPersonalityWizard {
	return &DefaultAiPersonalityWizard{selections: make(map[string]PersonalityChoice)}
}

// Presets returns the built-in personality presets. Ports the Presets property.
// Pointer receiver (the type holds a mutex); the value is not read.
func (*DefaultAiPersonalityWizard) Presets() []PersonalityChoice {
	return []PersonalityChoice{{"formal"}, {"warm"}, {"playful"}, {"professional"}}
}

// Select records a valid preset choice for a session. Ports SelectAsync.
func (w *DefaultAiPersonalityWizard) Select(ctx context.Context, sessionID string, choice PersonalityChoice) error {
	if strings.TrimSpace(sessionID) == "" {
		return errors.New("sessionId required")
	}
	known := false
	for _, p := range w.Presets() {
		if strings.EqualFold(p.Name, choice.Name) {
			known = true
			break
		}
	}
	if !known {
		return errors.New("Unknown personality '" + choice.Name + "'.")
	}
	w.mu.Lock()
	w.selections[sessionID] = choice
	w.mu.Unlock()
	return nil
}

// Selected returns the recorded choice for a session and true, or (zero, false).
// Ports Selected.
func (w *DefaultAiPersonalityWizard) Selected(sessionID string) (PersonalityChoice, bool) {
	w.mu.Lock()
	c, ok := w.selections[sessionID]
	w.mu.Unlock()
	return c, ok
}

// PersonalDataImport records personal-data imports. Ports IPersonalDataImport.
type PersonalDataImport interface {
	Import(ctx context.Context, sessionID, source string) error
}

// DefaultPersonalDataImport accepts registered sources + records imports. Ports
// DefaultPersonalDataImport. Construct with NewDefaultPersonalDataImport.
type DefaultPersonalDataImport struct {
	mu      sync.Mutex
	imports map[string][]string
}

var personalImportSources = toSet(
	"google-takeout", "apple-data-export", "whatsapp-archive", "icloud", "csv", "vcard", "ics",
)

// NewDefaultPersonalDataImport constructs an empty import rail.
func NewDefaultPersonalDataImport() *DefaultPersonalDataImport {
	return &DefaultPersonalDataImport{imports: make(map[string][]string)}
}

// Import records a supported-source import. Ports ImportAsync.
func (d *DefaultPersonalDataImport) Import(ctx context.Context, sessionID, source string) error {
	if strings.TrimSpace(sessionID) == "" {
		return errors.New("sessionId required")
	}
	if strings.TrimSpace(source) == "" {
		return errors.New("source required")
	}
	if !personalImportSources[strings.ToLower(source)] {
		return errors.New("Unsupported import source '" + source + "'.")
	}
	d.mu.Lock()
	d.imports[sessionID] = append(d.imports[sessionID], source)
	d.mu.Unlock()
	return nil
}

// ImportsFor returns the recorded imports for a session. Ports ImportsFor.
func (d *DefaultPersonalDataImport) ImportsFor(sessionID string) []string {
	d.mu.Lock()
	defer d.mu.Unlock()
	if l, ok := d.imports[sessionID]; ok {
		out := make([]string, len(l))
		copy(out, l)
		return out
	}
	return []string{}
}

// HouseholdMember is a member of a household. Ports the HouseholdMember record.
type HouseholdMember struct {
	MemberID    string
	DisplayName string
	Role        string
}

// FamilyOnboarding creates households. Ports IFamilyOnboarding.
type FamilyOnboarding interface {
	CreateHousehold(ctx context.Context, ownerID string, members []HouseholdMember) error
}

// DefaultFamilyOnboarding validates roles + stores rosters. Ports
// DefaultFamilyOnboarding. Construct with NewDefaultFamilyOnboarding.
type DefaultFamilyOnboarding struct {
	mu         sync.Mutex
	households map[string][]HouseholdMember
}

var householdValidRoles = toSet("owner", "parent", "child", "guardian", "elder", "partner", "guest")

// NewDefaultFamilyOnboarding constructs an empty family onboarding rail.
func NewDefaultFamilyOnboarding() *DefaultFamilyOnboarding {
	return &DefaultFamilyOnboarding{households: make(map[string][]HouseholdMember)}
}

// CreateHousehold validates members and stores the roster. Ports CreateHouseholdAsync.
func (f *DefaultFamilyOnboarding) CreateHousehold(ctx context.Context, ownerID string, members []HouseholdMember) error {
	if strings.TrimSpace(ownerID) == "" {
		return errors.New("ownerId required")
	}
	for _, m := range members {
		if strings.TrimSpace(m.MemberID) == "" {
			return errors.New("MemberId required")
		}
		if strings.TrimSpace(m.DisplayName) == "" {
			return errors.New("DisplayName required")
		}
		if !householdValidRoles[strings.ToLower(m.Role)] {
			return errors.New("Unknown role '" + m.Role + "'.")
		}
	}
	roster := make([]HouseholdMember, len(members))
	copy(roster, members)
	f.mu.Lock()
	f.households[ownerID] = roster
	f.mu.Unlock()
	return nil
}

// MembersOf returns the roster for an owner. Ports MembersOf.
func (f *DefaultFamilyOnboarding) MembersOf(ownerID string) []HouseholdMember {
	f.mu.Lock()
	defer f.mu.Unlock()
	if l, ok := f.households[ownerID]; ok {
		out := make([]HouseholdMember, len(l))
		copy(out, l)
		return out
	}
	return []HouseholdMember{}
}

// ── TRUST ───────────────────────────────────────────────────────────────────

// ThirdPartySecurityAuditPublisher exposes the audit report URL. Ports
// IThirdPartySecurityAuditPublisher.
type ThirdPartySecurityAuditPublisher interface{ ReportURL() string }

// DefaultThirdPartySecurityAuditPublisher ports DefaultThirdPartySecurityAuditPublisher.
type DefaultThirdPartySecurityAuditPublisher struct{}

// ReportURL returns the default audit report URL.
func (DefaultThirdPartySecurityAuditPublisher) ReportURL() string {
	return "https://trust.circle.ai/audit"
}

// ComplianceCertifications lists certifications. Ports IComplianceCertifications.
type ComplianceCertifications interface{ Certifications() []string }

// DefaultComplianceCertifications ports DefaultComplianceCertifications.
type DefaultComplianceCertifications struct{}

// Certifications returns the default certifications.
func (DefaultComplianceCertifications) Certifications() []string {
	return []string{"SOC 2 Type II", "ISO 27001", "ISO 27701"}
}

// BugBountyChannel exposes the bug-bounty platform + URL. Ports IBugBountyChannel.
type BugBountyChannel interface {
	Platform() string
	SubmissionURL() string
}

// DefaultBugBountyChannel ports DefaultBugBountyChannel.
type DefaultBugBountyChannel struct{}

// Platform returns the bug-bounty platform.
func (DefaultBugBountyChannel) Platform() string { return "HackerOne" }

// SubmissionURL returns the submission URL.
func (DefaultBugBountyChannel) SubmissionURL() string { return "https://h1.com/circleai" }

// PrivacyRegulationCompliance lists privacy laws. Ports IPrivacyRegulationCompliance.
type PrivacyRegulationCompliance interface{ Laws() []string }

// DefaultPrivacyRegulationCompliance ports DefaultPrivacyRegulationCompliance.
type DefaultPrivacyRegulationCompliance struct{}

// Laws returns the default privacy laws.
func (DefaultPrivacyRegulationCompliance) Laws() []string {
	return []string{"GDPR", "POPIA", "CCPA", "LGPD"}
}

// VerifiablePrivacyProof exposes reproducible-build proof. Ports IVerifiablePrivacyProof.
type VerifiablePrivacyProof interface {
	BuildIsReproducible() bool
	SourceURL() string
}

// DefaultVerifiablePrivacyProof ports DefaultVerifiablePrivacyProof.
type DefaultVerifiablePrivacyProof struct{}

// BuildIsReproducible returns true.
func (DefaultVerifiablePrivacyProof) BuildIsReproducible() bool { return true }

// SourceURL returns the source repo URL.
func (DefaultVerifiablePrivacyProof) SourceURL() string {
	return "https://github.com/bhengubv/CircleAI"
}

// TransparencyReceipt is a per-call transparency receipt. Ports the
// TransparencyReceipt record. CostUsd uses exact Decimal.
type TransparencyReceipt struct {
	CallID       string
	ActionsTaken []string
	DataEgress   []string
	CostUsd      Decimal
}

// PerCallTransparency returns per-call receipts. Ports IPerCallTransparency.
type PerCallTransparency interface {
	ReceiptFor(ctx context.Context, callID string) (TransparencyReceipt, error)
}

// DefaultPerCallTransparency stores + returns receipts. Ports
// DefaultPerCallTransparency. Construct with NewDefaultPerCallTransparency.
type DefaultPerCallTransparency struct {
	mu       sync.Mutex
	receipts map[string]TransparencyReceipt
}

// NewDefaultPerCallTransparency constructs an empty receipt store.
func NewDefaultPerCallTransparency() *DefaultPerCallTransparency {
	return &DefaultPerCallTransparency{receipts: make(map[string]TransparencyReceipt)}
}

// Record stores a receipt. Ports Record.
func (t *DefaultPerCallTransparency) Record(receipt TransparencyReceipt) error {
	if strings.TrimSpace(receipt.CallID) == "" {
		return errors.New("CallId required")
	}
	t.mu.Lock()
	t.receipts[receipt.CallID] = receipt
	t.mu.Unlock()
	return nil
}

// ReceiptFor returns the receipt for callID, or an empty receipt when absent.
// Ports ReceiptFor.
func (t *DefaultPerCallTransparency) ReceiptFor(ctx context.Context, callID string) (TransparencyReceipt, error) {
	if strings.TrimSpace(callID) == "" {
		return TransparencyReceipt{}, errors.New("callId required")
	}
	t.mu.Lock()
	r, ok := t.receipts[callID]
	t.mu.Unlock()
	if !ok {
		return TransparencyReceipt{CallID: callID, ActionsTaken: []string{}, DataEgress: []string{}, CostUsd: Decimal{}}, nil
	}
	return r, nil
}

// ── PRICING ─────────────────────────────────────────────────────────────────

// PricingTier is one pricing tier. Ports the PricingTier record. MonthlyPriceLocal
// uses exact Decimal.
type PricingTier struct {
	Name              string
	MonthlyPriceLocal Decimal
	Currency          string
	Features          []string
}

// PricingMatrix lists pricing tiers. Ports IPricingMatrix.
type PricingMatrix interface{ All() []PricingTier }

// DefaultPricingMatrix ports DefaultPricingMatrix.
type DefaultPricingMatrix struct{}

// All returns the default pricing tiers. Ports the All property.
func (DefaultPricingMatrix) All() []PricingTier {
	return []PricingTier{
		{"free", DecimalFromFloat(0), "ZAR", []string{"Local chat", "Family memory cap"}},
		{"paid", DecimalFromFloat(19), "ZAR", []string{"Unlimited cloud calls", "Priority routing"}},
		{"family", DecimalFromFloat(49), "ZAR", []string{"Up to 6 members"}},
		{"stokvel", DecimalFromFloat(99), "ZAR", []string{"Group memory", "Group reporting"}},
		{"enterprise", DecimalFromFloat(200), "ZAR", []string{"Dedicated brain", "SLA"}},
	}
}

// PluginMarketplaceRevenueShare exposes revenue-share ratios. Ports
// IPluginMarketplaceRevenueShare.
type PluginMarketplaceRevenueShare interface {
	AuthorShare() float64
	VerifiedSafeShare() float64
}

// DefaultPluginMarketplaceRevenueShare ports DefaultPluginMarketplaceRevenueShare.
type DefaultPluginMarketplaceRevenueShare struct{}

// AuthorShare returns 0.70.
func (DefaultPluginMarketplaceRevenueShare) AuthorShare() float64 { return 0.70 }

// VerifiedSafeShare returns 0.50.
func (DefaultPluginMarketplaceRevenueShare) VerifiedSafeShare() float64 { return 0.50 }

// CarrierRevenueShare exposes the carrier revenue share. Ports ICarrierRevenueShare.
type CarrierRevenueShare interface{ CarrierShare() float64 }

// DefaultCarrierRevenueShare ports DefaultCarrierRevenueShare.
type DefaultCarrierRevenueShare struct{}

// CarrierShare returns 0.25.
func (DefaultCarrierRevenueShare) CarrierShare() float64 { return 0.25 }

// ── LOCALISATION ────────────────────────────────────────────────────────────

// CurrencyFormatter formats currency amounts. Ports ICurrencyFormatter.
type CurrencyFormatter interface {
	Format(amount Decimal, isoCurrencyCode string) string
}

// DefaultCurrencyFormatter formats as "0.00 CUR". Ports DefaultCurrencyFormatter.
type DefaultCurrencyFormatter struct{}

// Format renders the amount with two fractional digits + the ISO code. Ports
// Format ("{amount:0.00} {isoCurrencyCode}").
func (DefaultCurrencyFormatter) Format(amount Decimal, isoCurrencyCode string) string {
	return decimalFixed2(amount) + " " + isoCurrencyCode
}

// PhoneNumberFormatter formats phone numbers. Ports IPhoneNumberFormatter.
type PhoneNumberFormatter interface {
	Format(e164, countryCodeIsoAlpha2 string) string
}

// DefaultPhoneNumberFormatter returns E.164 unchanged. Ports DefaultPhoneNumberFormatter.
type DefaultPhoneNumberFormatter struct{}

// Format returns e164 unchanged. Ports Format.
func (DefaultPhoneNumberFormatter) Format(e164, countryCodeIsoAlpha2 string) string { return e164 }

// CulturalNameRecogniser reports recognised languages. Ports ICulturalNameRecogniser.
type CulturalNameRecogniser interface {
	RecognisesLanguage(isoLanguage string) bool
}

// DefaultCulturalNameRecogniser ports DefaultCulturalNameRecogniser.
type DefaultCulturalNameRecogniser struct{}

var culturalRecognised = toSet("zul", "xho", "tsn", "sot", "yor", "ibo", "twi", "swa", "hin", "ben")

// RecognisesLanguage reports whether isoLanguage is supported (case-insensitive).
// Ports RecognisesLanguage.
func (DefaultCulturalNameRecogniser) RecognisesLanguage(isoLanguage string) bool {
	return culturalRecognised[strings.ToLower(isoLanguage)]
}

// CulturalGreetings returns a greeting per language. Ports ICulturalGreetings.
type CulturalGreetings interface {
	GreetingFor(isoLanguage string) string
}

// DefaultCulturalGreetings ports DefaultCulturalGreetings.
type DefaultCulturalGreetings struct{}

// GreetingFor returns the greeting for a language. Ports GreetingFor (switch).
func (DefaultCulturalGreetings) GreetingFor(isoLanguage string) string {
	switch isoLanguage {
	case "zul", "zu":
		return "Sawubona"
	case "xho", "xh":
		return "Molo"
	case "yor":
		return "Ẹ kú àárọ̀"
	case "hin":
		return "नमस्ते"
	default:
		return "Hello"
	}
}

// SaServiceConnectors lists SA banks + wallets. Ports ISaServiceConnectors.
type SaServiceConnectors interface {
	Banks() []string
	Wallets() []string
}

// DefaultSaServiceConnectors ports DefaultSaServiceConnectors.
type DefaultSaServiceConnectors struct{}

// Banks returns the default SA banks.
func (DefaultSaServiceConnectors) Banks() []string {
	return []string{"Capitec", "FNB", "Standard", "Absa", "Nedbank"}
}

// Wallets returns the default SA wallets.
func (DefaultSaServiceConnectors) Wallets() []string { return []string{"PayFast", "SnapScan"} }

// CrossBorderCorridors lists corridors. Ports ICrossBorderCorridors.
type CrossBorderCorridors interface{ Corridors() []string }

// DefaultCrossBorderCorridors ports DefaultCrossBorderCorridors.
type DefaultCrossBorderCorridors struct{}

// Corridors returns the default corridors.
func (DefaultCrossBorderCorridors) Corridors() []string { return []string{"SADC", "ECOWAS", "EAC"} }

// IndigenousKnowledgeProtocols reports elder-review requirements. Ports
// IIndigenousKnowledgeProtocols.
type IndigenousKnowledgeProtocols interface {
	RequiresElderReview(isoLanguage string) bool
}

// DefaultIndigenousKnowledgeProtocols ports DefaultIndigenousKnowledgeProtocols.
type DefaultIndigenousKnowledgeProtocols struct{}

// RequiresElderReview always returns true. Ports RequiresElderReview.
func (DefaultIndigenousKnowledgeProtocols) RequiresElderReview(isoLanguage string) bool { return true }

// ── HARDWARE ────────────────────────────────────────────────────────────────

// LowRamPhoneSupport reports RAM support. Ports ILowRamPhoneSupport.
type LowRamPhoneSupport interface{ SupportsRamMb(ramMb int) bool }

// DefaultLowRamPhoneSupport ports DefaultLowRamPhoneSupport.
type DefaultLowRamPhoneSupport struct{}

// SupportsRamMb reports ramMb >= 512. Ports SupportsRamMb.
func (DefaultLowRamPhoneSupport) SupportsRamMb(ramMb int) bool { return ramMb >= 512 }

// LowCpuOptimization reports clock support. Ports ILowCpuOptimization.
type LowCpuOptimization interface{ SupportsClockMhz(clockMhz int) bool }

// DefaultLowCpuOptimization ports DefaultLowCpuOptimization.
type DefaultLowCpuOptimization struct{}

// SupportsClockMhz reports clockMhz >= 600. Ports SupportsClockMhz.
func (DefaultLowCpuOptimization) SupportsClockMhz(clockMhz int) bool { return clockMhz >= 600 }

// OfflineQueuedOperation queues operations while offline. Ports IOfflineQueuedOperation.
type OfflineQueuedOperation interface {
	Enqueue(ctx context.Context, operationJSON string) error
	Pending() []string
	// TryDequeue removes and returns the head, or ("", false) when empty.
	TryDequeue() (string, bool)
}

// DefaultOfflineQueuedOperation is a FIFO offline queue. Ports
// DefaultOfflineQueuedOperation. The zero value is ready to use.
type DefaultOfflineQueuedOperation struct {
	mu    sync.Mutex
	queue []string
}

// Enqueue appends an operation. Ports EnqueueAsync.
func (q *DefaultOfflineQueuedOperation) Enqueue(ctx context.Context, operationJSON string) error {
	if strings.TrimSpace(operationJSON) == "" {
		return errors.New("operationJson required")
	}
	q.mu.Lock()
	q.queue = append(q.queue, operationJSON)
	q.mu.Unlock()
	return nil
}

// Pending returns a snapshot of the queue. Ports the Pending property.
func (q *DefaultOfflineQueuedOperation) Pending() []string {
	q.mu.Lock()
	out := make([]string, len(q.queue))
	copy(out, q.queue)
	q.mu.Unlock()
	return out
}

// TryDequeue removes and returns the head. Ports TryDequeue.
func (q *DefaultOfflineQueuedOperation) TryDequeue() (string, bool) {
	q.mu.Lock()
	defer q.mu.Unlock()
	if len(q.queue) == 0 {
		return "", false
	}
	head := q.queue[0]
	q.queue = q.queue[1:]
	if len(q.queue) == 0 {
		q.queue = nil
	}
	return head, true
}

// SmsFallbackSent is one recorded SMS-fallback answer. Mirrors the C# tuple
// (Phone, Question, At).
type SmsFallbackSent struct {
	Phone    string
	Question string
	At       time.Time
}

// SmsFallback answers questions via SMS. Ports ISmsFallback.
type SmsFallback interface {
	AnswerViaSms(ctx context.Context, phoneNumber, question string) error
	Sent() []SmsFallbackSent
}

// DefaultSmsFallback records answers + optionally delivers via an injected
// callback. Ports DefaultSmsFallback. Construct with NewDefaultSmsFallback.
type DefaultSmsFallback struct {
	mu       sync.Mutex
	sent     []SmsFallbackSent
	delivery func(ctx context.Context, phone, question string) error
}

// NewDefaultSmsFallback constructs the rail with an optional delivery callback
// (pass nil to only record).
func NewDefaultSmsFallback(delivery func(ctx context.Context, phone, question string) error) *DefaultSmsFallback {
	return &DefaultSmsFallback{delivery: delivery}
}

// AnswerViaSms records and (if configured) delivers the answer. Ports
// AnswerViaSmsAsync.
func (s *DefaultSmsFallback) AnswerViaSms(ctx context.Context, phoneNumber, question string) error {
	if strings.TrimSpace(phoneNumber) == "" {
		return errors.New("phoneNumber required")
	}
	if strings.TrimSpace(question) == "" {
		return errors.New("question required")
	}
	s.mu.Lock()
	s.sent = append(s.sent, SmsFallbackSent{Phone: phoneNumber, Question: question, At: time.Now().UTC()})
	s.mu.Unlock()
	if s.delivery != nil {
		return s.delivery(ctx, phoneNumber, question)
	}
	return nil
}

// Sent returns a snapshot of the sent answers. Ports the Sent property.
func (s *DefaultSmsFallback) Sent() []SmsFallbackSent {
	s.mu.Lock()
	out := make([]SmsFallbackSent, len(s.sent))
	copy(out, s.sent)
	s.mu.Unlock()
	return out
}

// UssdFallback responds to a USSD menu session. Ports IUssdFallback.
type UssdFallback interface {
	Respond(ctx context.Context, ussdSession, input string) (string, error)
}

type ussdMenu struct {
	prompt string
	routes map[string]string
}

// DefaultUssdFallback is a USSD menu state machine. Ports DefaultUssdFallback.
// The zero value is ready to use (sessions default to the root menu).
type DefaultUssdFallback struct {
	mu       sync.Mutex
	sessions map[string]string
}

var ussdMenus = map[string]ussdMenu{
	"root":    {"CircleAI:\n1. Balance\n2. Ask AI\n3. Help", map[string]string{"1": "balance", "2": "ask", "3": "help"}},
	"balance": {"Balance: R0.00\n0. Back", map[string]string{"0": "root"}},
	"ask":     {"Type question, then send.\n0. Back", map[string]string{"0": "root"}},
	"help":    {"Dial *120*CIRCLE# anytime.\n0. Back", map[string]string{"0": "root"}},
}

// Respond advances the session's menu and returns the next prompt. Ports
// RespondAsync.
func (u *DefaultUssdFallback) Respond(ctx context.Context, ussdSession, input string) (string, error) {
	if strings.TrimSpace(ussdSession) == "" {
		return "", errors.New("ussdSession required")
	}
	u.mu.Lock()
	defer u.mu.Unlock()
	if u.sessions == nil {
		u.sessions = make(map[string]string)
	}
	current, ok := u.sessions[ussdSession]
	if !ok {
		current = "root"
		u.sessions[ussdSession] = "root"
	}
	menu, ok := ussdMenus[current]
	if !ok {
		u.sessions[ussdSession] = "root"
		return ussdMenus["root"].prompt, nil
	}
	if next, ok := menu.routes[strings.TrimSpace(input)]; ok {
		u.sessions[ussdSession] = next
		return ussdMenus[next].prompt, nil
	}
	return menu.prompt, nil
}

// KaiOsSupport reports KaiOS compilation. Ports IKaiOsSupport.
type KaiOsSupport interface{ IsCompiled() bool }

// DefaultKaiOsSupport ports DefaultKaiOsSupport.
type DefaultKaiOsSupport struct{}

// IsCompiled returns true.
func (DefaultKaiOsSupport) IsCompiled() bool { return true }

// ── SERVICES ────────────────────────────────────────────────────────────────

// MessagingOutboxEntry is one recorded outbound message. Mirrors the C# tuples
// (Phone/Chat, Body, At).
type MessagingOutboxEntry struct {
	Target string
	Body   string
	At     time.Time
}

// WhatsAppIntegration sends WhatsApp messages. Ports IWhatsAppIntegration.
type WhatsAppIntegration interface {
	Send(ctx context.Context, phoneNumber, message string) error
	Outbox() []MessagingOutboxEntry
}

// DefaultWhatsAppIntegration validates E.164 + records + optionally delivers.
// Ports DefaultWhatsAppIntegration. Construct with NewDefaultWhatsAppIntegration.
type DefaultWhatsAppIntegration struct {
	mu   sync.Mutex
	out  []MessagingOutboxEntry
	send func(ctx context.Context, phone, message string) error
}

// NewDefaultWhatsAppIntegration constructs the rail with an optional send
// callback (pass nil to only record).
func NewDefaultWhatsAppIntegration(send func(ctx context.Context, phone, message string) error) *DefaultWhatsAppIntegration {
	return &DefaultWhatsAppIntegration{send: send}
}

// Send validates and records (and optionally delivers) a message. Ports SendAsync.
func (w *DefaultWhatsAppIntegration) Send(ctx context.Context, phoneNumber, message string) error {
	if strings.TrimSpace(phoneNumber) == "" {
		return errors.New("phoneNumber required")
	}
	if strings.TrimSpace(message) == "" {
		return errors.New("message required")
	}
	if !e164Regex.MatchString(phoneNumber) {
		return errors.New("Invalid E.164 phone '" + phoneNumber + "'.")
	}
	w.mu.Lock()
	w.out = append(w.out, MessagingOutboxEntry{Target: phoneNumber, Body: message, At: time.Now().UTC()})
	w.mu.Unlock()
	if w.send != nil {
		return w.send(ctx, phoneNumber, message)
	}
	return nil
}

// Outbox returns a snapshot of the outbox. Ports the Outbox property.
func (w *DefaultWhatsAppIntegration) Outbox() []MessagingOutboxEntry {
	w.mu.Lock()
	out := make([]MessagingOutboxEntry, len(w.out))
	copy(out, w.out)
	w.mu.Unlock()
	return out
}

// TelegramIntegration sends Telegram messages. Ports ITelegramIntegration.
type TelegramIntegration interface {
	Send(ctx context.Context, chatID, message string) error
	Outbox() []MessagingOutboxEntry
}

// DefaultTelegramIntegration records + optionally delivers. Ports
// DefaultTelegramIntegration. Construct with NewDefaultTelegramIntegration.
type DefaultTelegramIntegration struct {
	mu   sync.Mutex
	out  []MessagingOutboxEntry
	send func(ctx context.Context, chatID, message string) error
}

// NewDefaultTelegramIntegration constructs the rail with an optional send
// callback (pass nil to only record).
func NewDefaultTelegramIntegration(send func(ctx context.Context, chatID, message string) error) *DefaultTelegramIntegration {
	return &DefaultTelegramIntegration{send: send}
}

// Send records (and optionally delivers) a message. Ports SendAsync.
func (t *DefaultTelegramIntegration) Send(ctx context.Context, chatID, message string) error {
	if strings.TrimSpace(chatID) == "" {
		return errors.New("chatId required")
	}
	if strings.TrimSpace(message) == "" {
		return errors.New("message required")
	}
	t.mu.Lock()
	t.out = append(t.out, MessagingOutboxEntry{Target: chatID, Body: message, At: time.Now().UTC()})
	t.mu.Unlock()
	if t.send != nil {
		return t.send(ctx, chatID, message)
	}
	return nil
}

// Outbox returns a snapshot of the outbox. Ports the Outbox property.
func (t *DefaultTelegramIntegration) Outbox() []MessagingOutboxEntry {
	t.mu.Lock()
	out := make([]MessagingOutboxEntry, len(t.out))
	copy(out, t.out)
	t.mu.Unlock()
	return out
}

// providerRegistry is the shared shape of the connector-registry rails.
type providerRegistry interface{ Providers() []string }

// EmailConnectorRegistry lists email providers. Ports IEmailConnectorRegistry.
type EmailConnectorRegistry = providerRegistry

// DefaultEmailConnectorRegistry ports DefaultEmailConnectorRegistry.
type DefaultEmailConnectorRegistry struct{}

// Providers returns the default email providers.
func (DefaultEmailConnectorRegistry) Providers() []string {
	return []string{"Gmail", "Outlook", "iCloud", "ProtonMail", "Yandex", "Yahoo", "IMAP"}
}

// CalendarConnectorRegistry lists calendar providers. Ports ICalendarConnectorRegistry.
type CalendarConnectorRegistry = providerRegistry

// DefaultCalendarConnectorRegistry ports DefaultCalendarConnectorRegistry.
type DefaultCalendarConnectorRegistry struct{}

// Providers returns the default calendar providers.
func (DefaultCalendarConnectorRegistry) Providers() []string {
	return []string{"Google", "Outlook", "Apple", "Yahoo", "CalDAV"}
}

// CrmConnectorRegistry lists CRM providers. Ports ICrmConnectorRegistry.
type CrmConnectorRegistry = providerRegistry

// DefaultCrmConnectorRegistry ports DefaultCrmConnectorRegistry.
type DefaultCrmConnectorRegistry struct{}

// Providers returns the default CRM providers.
func (DefaultCrmConnectorRegistry) Providers() []string {
	return []string{"HubSpot", "Salesforce", "Pipedrive", "Zoho", "Bitrix"}
}

// AccountingConnectorRegistry lists accounting providers. Ports
// IAccountingConnectorRegistry.
type AccountingConnectorRegistry = providerRegistry

// DefaultAccountingConnectorRegistry ports DefaultAccountingConnectorRegistry.
type DefaultAccountingConnectorRegistry struct{}

// Providers returns the default accounting providers.
func (DefaultAccountingConnectorRegistry) Providers() []string {
	return []string{"Xero", "Sage", "QuickBooks", "Wave", "Manager.io"}
}

// BankingConnectorRegistry lists banking providers. Ports IBankingConnectorRegistry.
type BankingConnectorRegistry = providerRegistry

// DefaultBankingConnectorRegistry ports DefaultBankingConnectorRegistry.
type DefaultBankingConnectorRegistry struct{}

// Providers returns the default banking providers.
func (DefaultBankingConnectorRegistry) Providers() []string {
	return []string{"open-banking-ZA", "open-banking-NG", "open-banking-KE"}
}

// ── REGULATOR ───────────────────────────────────────────────────────────────

// SarbSandboxStatus reports SARB sandbox approval. Ports ISarbSandboxStatus.
type SarbSandboxStatus interface{ Approved() bool }

// DefaultSarbSandboxStatus ports DefaultSarbSandboxStatus.
type DefaultSarbSandboxStatus struct{}

// Approved returns false.
func (DefaultSarbSandboxStatus) Approved() bool { return false }

// IcasaApprovalStatus reports ICASA approval. Ports IIcasaApprovalStatus.
type IcasaApprovalStatus interface{ Approved() bool }

// DefaultIcasaApprovalStatus ports DefaultIcasaApprovalStatus.
type DefaultIcasaApprovalStatus struct{}

// Approved returns false.
func (DefaultIcasaApprovalStatus) Approved() bool { return false }

// GlobalRegulatorEngagement lists active jurisdictions. Ports
// IGlobalRegulatorEngagement.
type GlobalRegulatorEngagement interface{ ActiveJurisdictions() []string }

// DefaultGlobalRegulatorEngagement ports DefaultGlobalRegulatorEngagement.
type DefaultGlobalRegulatorEngagement struct{}

// ActiveJurisdictions returns the default jurisdictions.
func (DefaultGlobalRegulatorEngagement) ActiveJurisdictions() []string {
	return []string{"ZA", "NG", "KE", "US", "CA", "UK", "EU"}
}

// TaxInvoiceRegistry lists tax schemes. Ports ITaxInvoiceRegistry.
type TaxInvoiceRegistry interface{ Schemes() []string }

// DefaultTaxInvoiceRegistry ports DefaultTaxInvoiceRegistry.
type DefaultTaxInvoiceRegistry struct{}

// Schemes returns the default tax schemes.
func (DefaultTaxInvoiceRegistry) Schemes() []string {
	return []string{"VAT", "GST", "Sales Tax", "DST"}
}

// LawfulInterceptCompliance exposes the intercept posture. Ports
// ILawfulInterceptCompliance.
type LawfulInterceptCompliance interface{ Posture() string }

// DefaultLawfulInterceptCompliance ports DefaultLawfulInterceptCompliance.
type DefaultLawfulInterceptCompliance struct{}

// Posture returns the intercept posture.
func (DefaultLawfulInterceptCompliance) Posture() string {
	return "Money decryptable to law, comms permanently blind"
}

// ── RECOVERY ────────────────────────────────────────────────────────────────

// LostDeviceFlow remote-wipes lost devices. Ports ILostDeviceFlow.
type LostDeviceFlow interface {
	RemoteWipe(ctx context.Context, deviceID string) error
	IsWiped(deviceID string) bool
}

// DefaultLostDeviceFlow tracks wiped devices. Ports DefaultLostDeviceFlow.
// The zero value is ready to use.
type DefaultLostDeviceFlow struct {
	mu    sync.Mutex
	wiped map[string]time.Time
}

// RemoteWipe records a device wipe. Ports RemoteWipeAsync.
func (d *DefaultLostDeviceFlow) RemoteWipe(ctx context.Context, deviceID string) error {
	if strings.TrimSpace(deviceID) == "" {
		return errors.New("deviceId required")
	}
	d.mu.Lock()
	if d.wiped == nil {
		d.wiped = make(map[string]time.Time)
	}
	d.wiped[deviceID] = time.Now().UTC()
	d.mu.Unlock()
	return nil
}

// IsWiped reports whether a device was wiped. Ports IsWiped.
func (d *DefaultLostDeviceFlow) IsWiped(deviceID string) bool {
	d.mu.Lock()
	_, ok := d.wiped[deviceID]
	d.mu.Unlock()
	return ok
}

// InheritanceProtocol designates account designees. Ports IInheritanceProtocol.
type InheritanceProtocol interface {
	Designate(ctx context.Context, ownerID, designeeID string) error
	// DesigneeFor returns the designee and true, or ("", false).
	DesigneeFor(ownerID string) (string, bool)
}

// DefaultInheritanceProtocol tracks designees. Ports DefaultInheritanceProtocol.
// The zero value is ready to use.
type DefaultInheritanceProtocol struct {
	mu        sync.Mutex
	designees map[string]string
}

// Designate records a designee for an owner. Ports DesignateAsync.
func (d *DefaultInheritanceProtocol) Designate(ctx context.Context, ownerID, designeeID string) error {
	if strings.TrimSpace(ownerID) == "" {
		return errors.New("ownerId required")
	}
	if strings.TrimSpace(designeeID) == "" {
		return errors.New("designeeId required")
	}
	if ownerID == designeeID {
		return errors.New("Designee cannot equal owner.")
	}
	d.mu.Lock()
	if d.designees == nil {
		d.designees = make(map[string]string)
	}
	d.designees[ownerID] = designeeID
	d.mu.Unlock()
	return nil
}

// DesigneeFor returns the designee for an owner. Ports DesigneeFor.
func (d *DefaultInheritanceProtocol) DesigneeFor(ownerID string) (string, bool) {
	d.mu.Lock()
	v, ok := d.designees[ownerID]
	d.mu.Unlock()
	return v, ok
}

// VerifiableWipe wipes + certifies. Ports IVerifiableWipe.
type VerifiableWipe interface {
	WipeAndCertify(ctx context.Context, ownerID string) ([]byte, error)
}

// DefaultVerifiableWipe returns a SHA-256 wipe certificate over
// "wipe|ownerId|iso-timestamp|nonce". Ports DefaultVerifiableWipe.
type DefaultVerifiableWipe struct{}

// WipeAndCertify produces the certificate. Ports WipeAndCertifyAsync.
func (DefaultVerifiableWipe) WipeAndCertify(ctx context.Context, ownerID string) ([]byte, error) {
	if strings.TrimSpace(ownerID) == "" {
		return nil, errors.New("ownerId required")
	}
	nonce := make([]byte, 16)
	if _, err := rand.Read(nonce); err != nil {
		return nil, err
	}
	payload := "wipe|" + ownerID + "|" + time.Now().UTC().Format(time.RFC3339Nano) + "|" + base64.StdEncoding.EncodeToString(nonce)
	sum := sha256.Sum256([]byte(payload))
	return sum[:], nil
}

// AccountCompromiseRecovery tracks compromise-recovery state. Ports
// IAccountCompromiseRecovery.
type AccountCompromiseRecovery interface {
	Begin(ctx context.Context, ownerID string) error
	InRecovery(ownerID string) bool
	Complete(ownerID string)
}

// DefaultAccountCompromiseRecovery ports DefaultAccountCompromiseRecovery.
// The zero value is ready to use.
type DefaultAccountCompromiseRecovery struct {
	mu     sync.Mutex
	active map[string]time.Time
}

// Begin marks an owner in recovery. Ports BeginAsync.
func (d *DefaultAccountCompromiseRecovery) Begin(ctx context.Context, ownerID string) error {
	if strings.TrimSpace(ownerID) == "" {
		return errors.New("ownerId required")
	}
	d.mu.Lock()
	if d.active == nil {
		d.active = make(map[string]time.Time)
	}
	d.active[ownerID] = time.Now().UTC()
	d.mu.Unlock()
	return nil
}

// InRecovery reports whether an owner is in recovery. Ports InRecovery.
func (d *DefaultAccountCompromiseRecovery) InRecovery(ownerID string) bool {
	d.mu.Lock()
	_, ok := d.active[ownerID]
	d.mu.Unlock()
	return ok
}

// Complete clears an owner's recovery state. Ports Complete.
func (d *DefaultAccountCompromiseRecovery) Complete(ownerID string) {
	d.mu.Lock()
	delete(d.active, ownerID)
	d.mu.Unlock()
}

// ── FAILURE MODES ───────────────────────────────────────────────────────────

// BrainUnreachableMode reports local-takeover state. Ports IBrainUnreachableMode.
type BrainUnreachableMode interface{ LocalTakeoverEnabled() bool }

// DefaultBrainUnreachableMode ports DefaultBrainUnreachableMode.
type DefaultBrainUnreachableMode struct{}

// LocalTakeoverEnabled returns true.
func (DefaultBrainUnreachableMode) LocalTakeoverEnabled() bool { return true }

// NoInternetCacheTarget exposes the offline cache hit-rate target. Ports
// INoInternetCacheTarget.
type NoInternetCacheTarget interface{ HitRateTarget() float32 }

// DefaultNoInternetCacheTarget ports DefaultNoInternetCacheTarget.
type DefaultNoInternetCacheTarget struct{}

// HitRateTarget returns 0.80.
func (DefaultNoInternetCacheTarget) HitRateTarget() float32 { return 0.80 }

// StorageFullDegradationPolicy exposes the degrade order. Ports
// IStorageFullDegradationPolicy.
type StorageFullDegradationPolicy interface{ DegradeOrder() string }

// DefaultStorageFullDegradationPolicy ports DefaultStorageFullDegradationPolicy.
type DefaultStorageFullDegradationPolicy struct{}

// DegradeOrder returns the degrade order.
func (DefaultStorageFullDegradationPolicy) DegradeOrder() string {
	return "cache > old-snapshots > chat-history > nothing"
}

// ImpairedUserMode tracks impaired-user engagement. Ports IImpairedUserMode.
type ImpairedUserMode interface {
	Engage(ctx context.Context, ownerID string) error
	IsEngaged(ownerID string) bool
	Disengage(ctx context.Context, ownerID string) error
}

// DefaultImpairedUserMode ports DefaultImpairedUserMode. The zero value is ready
// to use.
type DefaultImpairedUserMode struct {
	mu      sync.Mutex
	engaged map[string]bool
}

// Engage marks an owner engaged. Ports EngageAsync.
func (d *DefaultImpairedUserMode) Engage(ctx context.Context, ownerID string) error {
	if strings.TrimSpace(ownerID) == "" {
		return errors.New("ownerId required")
	}
	d.mu.Lock()
	if d.engaged == nil {
		d.engaged = make(map[string]bool)
	}
	d.engaged[ownerID] = true
	d.mu.Unlock()
	return nil
}

// IsEngaged reports engagement. Ports IsEngaged.
func (d *DefaultImpairedUserMode) IsEngaged(ownerID string) bool {
	d.mu.Lock()
	v := d.engaged[ownerID]
	d.mu.Unlock()
	return v
}

// Disengage clears engagement. Ports DisengageAsync.
func (d *DefaultImpairedUserMode) Disengage(ctx context.Context, ownerID string) error {
	d.mu.Lock()
	delete(d.engaged, ownerID)
	d.mu.Unlock()
	return nil
}

// AbusiveEnvironmentMode tracks abuse-safe engagement + safety phrases. Ports
// IAbusiveEnvironmentMode.
type AbusiveEnvironmentMode interface {
	Engage(ctx context.Context, ownerID string) error
	SafetyPhrase(ownerID string) string
	IsEngaged(ownerID string) bool
}

// DefaultAbusiveEnvironmentMode ports DefaultAbusiveEnvironmentMode. The zero
// value is ready to use.
type DefaultAbusiveEnvironmentMode struct {
	mu      sync.Mutex
	engaged map[string]bool
	phrases map[string]string
}

var abuseVocab = []string{"thunder", "river", "amber", "field", "rain", "stone", "harbor", "linen"}

// Engage marks an owner engaged. Ports EngageAsync.
func (d *DefaultAbusiveEnvironmentMode) Engage(ctx context.Context, ownerID string) error {
	if strings.TrimSpace(ownerID) == "" {
		return errors.New("ownerId required")
	}
	d.mu.Lock()
	if d.engaged == nil {
		d.engaged = make(map[string]bool)
	}
	d.engaged[ownerID] = true
	d.mu.Unlock()
	return nil
}

// SafetyPhrase returns a deterministic per-owner safety phrase. Ports
// SafetyPhrase. The word selection uses FNV-1a-32 over ownerID's UTF-8 bytes —
// the identical algorithm the C# reference uses (NOT string.GetHashCode, which
// .NET randomizes per process) — so the phrase is stable across runs AND
// byte-identical across every language port.
func (d *DefaultAbusiveEnvironmentMode) SafetyPhrase(ownerID string) string {
	if strings.TrimSpace(ownerID) == "" {
		panic("ownerId required")
	}
	d.mu.Lock()
	defer d.mu.Unlock()
	if d.phrases == nil {
		d.phrases = make(map[string]string)
	}
	if p, ok := d.phrases[ownerID]; ok {
		return p
	}
	h := fnv1a32(ownerID)
	phrase := "the " + abuseVocab[h%8] + " " + abuseVocab[(h>>8)%8] + " is " + abuseVocab[(h>>16)%8]
	d.phrases[ownerID] = phrase
	return phrase
}

// IsEngaged reports engagement. Ports IsEngaged.
func (d *DefaultAbusiveEnvironmentMode) IsEngaged(ownerID string) bool {
	d.mu.Lock()
	v := d.engaged[ownerID]
	d.mu.Unlock()
	return v
}

// PublicDisasterMode exposes the current disaster state. Ports IPublicDisasterMode.
type PublicDisasterMode interface{ CurrentState() string }

// DefaultPublicDisasterMode ports DefaultPublicDisasterMode.
type DefaultPublicDisasterMode struct{}

// CurrentState returns "normal".
func (DefaultPublicDisasterMode) CurrentState() string { return "normal" }

// ── COST ────────────────────────────────────────────────────────────────────

// SustainablePerUserCostMath exposes per-user revenue + marginal cost. Ports
// ISustainablePerUserCostMath. Amounts use exact Decimal.
type SustainablePerUserCostMath interface {
	MonthlyRevenuePerUser() Decimal
	MonthlyMarginalCostPerUser() Decimal
}

// DefaultSustainablePerUserCostMath ports DefaultSustainablePerUserCostMath.
type DefaultSustainablePerUserCostMath struct{}

// MonthlyRevenuePerUser returns 19.
func (DefaultSustainablePerUserCostMath) MonthlyRevenuePerUser() Decimal { return DecimalFromFloat(19) }

// MonthlyMarginalCostPerUser returns 3.8.
func (DefaultSustainablePerUserCostMath) MonthlyMarginalCostPerUser() Decimal {
	return DecimalFromFloat(3.8)
}

// PerCallCostCeiling exposes the per-call cost ceiling. Ports IPerCallCostCeiling.
type PerCallCostCeiling interface{ CeilingUsd() Decimal }

// DefaultPerCallCostCeiling ports DefaultPerCallCostCeiling.
type DefaultPerCallCostCeiling struct{}

// CeilingUsd returns 0.40.
func (DefaultPerCallCostCeiling) CeilingUsd() Decimal { return DecimalFromFloat(0.40) }

// FreeTierCostCapping exposes the free-tier monthly cap. Ports IFreeTierCostCapping.
type FreeTierCostCapping interface{ MonthlyCapUsd() Decimal }

// DefaultFreeTierCostCapping ports DefaultFreeTierCostCapping.
type DefaultFreeTierCostCapping struct{}

// MonthlyCapUsd returns 0.20.
func (DefaultFreeTierCostCapping) MonthlyCapUsd() Decimal { return DecimalFromFloat(0.20) }

// LocalFirstRouting reports the local-first preference. Ports ILocalFirstRouting.
type LocalFirstRouting interface{ Preferred() bool }

// DefaultLocalFirstRouting ports DefaultLocalFirstRouting.
type DefaultLocalFirstRouting struct{}

// Preferred returns true.
func (DefaultLocalFirstRouting) Preferred() bool { return true }

// ── NETWORK EFFECTS ─────────────────────────────────────────────────────────

// ReferralProgramme exposes the referral reward. Ports IReferralProgramme. The
// reward uses exact Decimal.
type ReferralProgramme interface {
	RewardLocal() Decimal
	Currency() string
}

// DefaultReferralProgramme ports DefaultReferralProgramme.
type DefaultReferralProgramme struct{}

// RewardLocal returns 19.
func (DefaultReferralProgramme) RewardLocal() Decimal { return DecimalFromFloat(19) }

// Currency returns "ZAR".
func (DefaultReferralProgramme) Currency() string { return "ZAR" }

// FamilyAiSharing exposes the family member cap. Ports IFamilyAiSharing.
type FamilyAiSharing interface{ MaxMembers() int }

// DefaultFamilyAiSharing ports DefaultFamilyAiSharing.
type DefaultFamilyAiSharing struct{}

// MaxMembers returns 6.
func (DefaultFamilyAiSharing) MaxMembers() int { return 6 }

// CrossProviderFederation reports whether cross-provider federation is enabled.
// Ports ICrossProviderFederation.
type CrossProviderFederation interface{ Enabled() bool }

// DefaultCrossProviderFederation ports DefaultCrossProviderFederation.
type DefaultCrossProviderFederation struct{}

// Enabled returns true.
func (DefaultCrossProviderFederation) Enabled() bool { return true }

// GroupNetworkEffects lists group types. Ports IGroupNetworkEffects.
type GroupNetworkEffects interface{ GroupTypes() []string }

// DefaultGroupNetworkEffects ports DefaultGroupNetworkEffects.
type DefaultGroupNetworkEffects struct{}

// GroupTypes returns the default group types.
func (DefaultGroupNetworkEffects) GroupTypes() []string {
	return []string{"Stokvel", "Church", "Community"}
}

// UserGrowthFlywheel exposes the growth mechanic. Ports IUserGrowthFlywheel.
type UserGrowthFlywheel interface{ Mechanic() string }

// DefaultUserGrowthFlywheel ports DefaultUserGrowthFlywheel.
type DefaultUserGrowthFlywheel struct{}

// Mechanic returns the growth mechanic.
func (DefaultUserGrowthFlywheel) Mechanic() string {
	return "user invites friend; both get a month free"
}

// ── CULTURAL ────────────────────────────────────────────────────────────────

// ThirdPartyHarmLiability exposes the liability framework. Ports
// IThirdPartyHarmLiability.
type ThirdPartyHarmLiability interface{ Framework() string }

// DefaultThirdPartyHarmLiability ports DefaultThirdPartyHarmLiability.
type DefaultThirdPartyHarmLiability struct{}

// Framework returns the liability framework.
func (DefaultThirdPartyHarmLiability) Framework() string {
	return "Operator-of-record indemnity backed by insurance pool"
}

// QuietModeWindow is one active quiet window. Mirrors the C# tuple
// (Reason, StartedAt, EndsAt).
type QuietModeWindow struct {
	Reason    string
	StartedAt time.Time
	EndsAt    time.Time
}

// QuietMode engages quiet windows. Ports IQuietMode.
type QuietMode interface {
	Engage(ctx context.Context, reason string, duration time.Duration) error
	IsQuietAt(moment time.Time) bool
	ActiveWindows() []QuietModeWindow
}

// DefaultQuietMode tracks quiet windows. Ports DefaultQuietMode. The zero value
// is ready to use.
type DefaultQuietMode struct {
	mu      sync.Mutex
	windows []QuietModeWindow
}

// Engage adds a quiet window. Ports EngageAsync.
func (q *DefaultQuietMode) Engage(ctx context.Context, reason string, duration time.Duration) error {
	if strings.TrimSpace(reason) == "" {
		return errors.New("reason required")
	}
	if duration <= 0 {
		return errors.New("duration must be positive")
	}
	now := time.Now().UTC()
	q.mu.Lock()
	q.windows = append(q.windows, QuietModeWindow{Reason: reason, StartedAt: now, EndsAt: now.Add(duration)})
	q.mu.Unlock()
	return nil
}

// IsQuietAt reports whether moment falls inside any window. Ports IsQuietAt.
func (q *DefaultQuietMode) IsQuietAt(moment time.Time) bool {
	q.mu.Lock()
	defer q.mu.Unlock()
	for _, w := range q.windows {
		if !moment.Before(w.StartedAt) && !moment.After(w.EndsAt) {
			return true
		}
	}
	return false
}

// ActiveWindows returns windows that have not yet ended. Ports the ActiveWindows
// property.
func (q *DefaultQuietMode) ActiveWindows() []QuietModeWindow {
	now := time.Now().UTC()
	q.mu.Lock()
	defer q.mu.Unlock()
	out := make([]QuietModeWindow, 0)
	for _, w := range q.windows {
		if !w.EndsAt.Before(now) {
			out = append(out, w)
		}
	}
	return out
}

// ChildProtectionMode reports child-protection compliance. Ports
// IChildProtectionMode.
type ChildProtectionMode interface {
	CoppaCompliant() bool
	GdprKCompliant() bool
}

// DefaultChildProtectionMode ports DefaultChildProtectionMode.
type DefaultChildProtectionMode struct{}

// CoppaCompliant returns true.
func (DefaultChildProtectionMode) CoppaCompliant() bool { return true }

// GdprKCompliant returns true.
func (DefaultChildProtectionMode) GdprKCompliant() bool { return true }

// ReligiousAccommodation lists supported modes. Ports IReligiousAccommodation.
type ReligiousAccommodation interface{ SupportedModes() []string }

// DefaultReligiousAccommodation ports DefaultReligiousAccommodation.
type DefaultReligiousAccommodation struct{}

// SupportedModes returns the default religious-accommodation modes.
func (DefaultReligiousAccommodation) SupportedModes() []string {
	return []string{"prayer times", "Shabbat mode", "Eid silence"}
}

// IndigenousDataSovereignty exposes the sovereignty standard. Ports
// IIndigenousDataSovereignty.
type IndigenousDataSovereignty interface{ Standard() string }

// DefaultIndigenousDataSovereignty ports DefaultIndigenousDataSovereignty.
type DefaultIndigenousDataSovereignty struct{}

// Standard returns "CARE Principles".
func (DefaultIndigenousDataSovereignty) Standard() string { return "CARE Principles" }

// PublicTransparencyLink is one linked evidence entry. Mirrors the C# tuple
// (Claim, Evidence, At).
type PublicTransparencyLink struct {
	Claim    string
	Evidence string
	At       time.Time
}

// PublicTransparency links claims to evidence. Ports IPublicTransparency.
type PublicTransparency interface {
	LinkEvidence(ctx context.Context, claim, evidenceURL string) error
	Linked() []PublicTransparencyLink
}

// DefaultPublicTransparency validates + records evidence links. Ports
// DefaultPublicTransparency. The zero value is ready to use.
type DefaultPublicTransparency struct {
	mu    sync.Mutex
	links []PublicTransparencyLink
}

// LinkEvidence records a claim + absolute http/https evidence URL. Ports
// LinkEvidenceAsync.
func (p *DefaultPublicTransparency) LinkEvidence(ctx context.Context, claim, evidenceURL string) error {
	if strings.TrimSpace(claim) == "" {
		return errors.New("claim required")
	}
	if !isAbsoluteHTTPURL(evidenceURL) {
		return errors.New("evidenceUrl must be absolute http/https")
	}
	p.mu.Lock()
	p.links = append(p.links, PublicTransparencyLink{Claim: claim, Evidence: evidenceURL, At: time.Now().UTC()})
	p.mu.Unlock()
	return nil
}

// Linked returns a snapshot of the evidence links. Ports the Linked property.
func (p *DefaultPublicTransparency) Linked() []PublicTransparencyLink {
	p.mu.Lock()
	out := make([]PublicTransparencyLink, len(p.links))
	copy(out, p.links)
	p.mu.Unlock()
	return out
}

// ── small local helpers ─────────────────────────────────────────────────────

// toSet builds a lower-cased lookup set (the C# rails use OrdinalIgnoreCase
// HashSets; callers here already lower-case the probe value).
func toSet(items ...string) map[string]bool {
	m := make(map[string]bool, len(items))
	for _, s := range items {
		m[strings.ToLower(s)] = true
	}
	return m
}

// decimalFixed2 formats a Decimal with exactly two fractional digits, rounding
// half-away-from-zero at the second decimal (matches C#'s "{d:0.00}").
func decimalFixed2(d Decimal) string {
	micro := d.Micro() // value * 10^6
	neg := micro < 0
	if neg {
		micro = -micro
	}
	// Round to hundredths: 10^6 / 10^2 = 10^4 per hundredth.
	hundredths := (micro + 5000) / 10000
	intPart := hundredths / 100
	frac := hundredths % 100
	sign := ""
	if neg {
		sign = "-"
	}
	f0 := byte('0' + frac/10)
	f1 := byte('0' + frac%10)
	return sign + itoa64(intPart) + "." + string([]byte{f0, f1})
}

// allDigits reports whether s is non-empty and all ASCII digits.
func allDigits(s string) bool {
	if s == "" {
		return false
	}
	for i := 0; i < len(s); i++ {
		if s[i] < '0' || s[i] > '9' {
			return false
		}
	}
	return true
}

// sha256HexUpper returns the upper-case hex SHA-256 of s (matches C#
// Convert.ToHexString(SHA256.HashData(...))).
func sha256HexUpper(s string) string {
	sum := sha256.Sum256([]byte(s))
	return strings.ToUpper(hex.EncodeToString(sum[:]))
}

// newHexGUID returns a 32-char lower-case hex UUID (matches Guid.NewGuid("n")).
func newHexGUID() string {
	var b [16]byte
	_, _ = rand.Read(b[:])
	return hex.EncodeToString(b[:])
}

// fnv1a32 is the 32-bit FNV-1a hash over s's UTF-8 bytes (Go strings are UTF-8,
// so ranging the bytes is exact). It matches the C# reference's Fnv1a32 byte for
// byte — offset basis 2166136261, prime 16777619, uint32 wraparound — so the
// deterministic abuse-safe phrase is identical across every language port.
func fnv1a32(s string) uint32 {
	const offset = 2166136261
	const prime = 16777619
	h := uint32(offset)
	for i := 0; i < len(s); i++ {
		h ^= uint32(s[i])
		h *= prime
	}
	return h
}

// isAbsoluteHTTPURL reports whether u is an absolute http/https URL. Mirrors the
// C# check (IsAbsoluteUri && scheme is http/https).
func isAbsoluteHTTPURL(u string) bool {
	l := strings.ToLower(u)
	if strings.HasPrefix(l, "https://") {
		return len(u) > len("https://")
	}
	if strings.HasPrefix(l, "http://") {
		return len(u) > len("http://")
	}
	return false
}

// Interface guards for the Ubiquity rails.
var (
	_ PhonePinBiometricOnboarding = (*DefaultPhonePinBiometricOnboarding)(nil)
	_ NoManualFirstRun            = DefaultNoManualFirstRun{}
	_ VoiceLedSetup               = DefaultVoiceLedSetup{}
	_ AiPersonalityWizard         = (*DefaultAiPersonalityWizard)(nil)
	_ PersonalDataImport          = (*DefaultPersonalDataImport)(nil)
	_ FamilyOnboarding            = (*DefaultFamilyOnboarding)(nil)
	_ PerCallTransparency         = (*DefaultPerCallTransparency)(nil)
	_ PricingMatrix               = DefaultPricingMatrix{}
	_ CurrencyFormatter           = DefaultCurrencyFormatter{}
	_ CulturalGreetings           = DefaultCulturalGreetings{}
	_ OfflineQueuedOperation      = (*DefaultOfflineQueuedOperation)(nil)
	_ SmsFallback                 = (*DefaultSmsFallback)(nil)
	_ UssdFallback                = (*DefaultUssdFallback)(nil)
	_ WhatsAppIntegration         = (*DefaultWhatsAppIntegration)(nil)
	_ TelegramIntegration         = (*DefaultTelegramIntegration)(nil)
	_ LostDeviceFlow              = (*DefaultLostDeviceFlow)(nil)
	_ InheritanceProtocol         = (*DefaultInheritanceProtocol)(nil)
	_ VerifiableWipe              = DefaultVerifiableWipe{}
	_ AccountCompromiseRecovery   = (*DefaultAccountCompromiseRecovery)(nil)
	_ ImpairedUserMode            = (*DefaultImpairedUserMode)(nil)
	_ AbusiveEnvironmentMode      = (*DefaultAbusiveEnvironmentMode)(nil)
	_ QuietMode                   = (*DefaultQuietMode)(nil)
	_ PublicTransparency          = (*DefaultPublicTransparency)(nil)
	_ EmailConnectorRegistry      = DefaultEmailConnectorRegistry{}
)
