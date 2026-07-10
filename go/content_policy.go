// content_policy.go
//
// Ports the CircleAI.ContentPolicy module:
//   Enums:      SafetyVerdict (Contracts.cs)
//   Records:    SafetyFinding, SafetyAuditEntry, KeywordRule (Contracts.cs,
//               KeywordContentFilter.cs)
//   Interfaces: IContentFilter, IRefusalPolicy, IPromptInjectionDetector,
//               ISafetyAuditLog (Contracts.cs)
//   Impls:      KeywordContentFilter + CommonKeywordRules, ThresholdRefusalPolicy,
//               KeywordPromptInjectionDetector (KeywordContentFilter.cs);
//               NullContentFilter, NullRefusalPolicy, NullPromptInjectionDetector,
//               NullSafetyAuditLog (NullImplementations.cs).
//
// These are production-grade fast checks, not LLM-grade safety models. Hosts that
// need a real safety LLM wrap one behind the same contract. Null* implementations
// are fail-closed defaults: when there is no real backend wired, content is
// refused (safest default).
//
// Async note: the C# surface is ValueTask-based with a CancellationToken. The Go
// port takes ctx context.Context and returns (result, error). The keyword
// implementations never fail, so their error is always nil; the shape is kept so
// a host can drop in an implementation that calls out to a real backend.

package circleai

import (
	"context"
	"regexp"
	"time"
)

// SafetyVerdict is the outcome of a content-safety classification. Ports
// SafetyVerdict; ordinals are load-bearing (Allow=0 < Flag=1 < Refuse=2).
type SafetyVerdict int

const (
	// SafetyVerdictAllow permits the content.
	SafetyVerdictAllow SafetyVerdict = 0
	// SafetyVerdictFlag permits the content but records a concern.
	SafetyVerdictFlag SafetyVerdict = 1
	// SafetyVerdictRefuse blocks the content.
	SafetyVerdictRefuse SafetyVerdict = 2
)

// String returns the C# enum member name for the verdict.
func (v SafetyVerdict) String() string {
	switch v {
	case SafetyVerdictAllow:
		return "Allow"
	case SafetyVerdictFlag:
		return "Flag"
	case SafetyVerdictRefuse:
		return "Refuse"
	default:
		return "Allow"
	}
}

// SafetyFinding is a single content-safety finding. Ports SafetyFinding.
type SafetyFinding struct {
	// Verdict is the classification outcome.
	Verdict SafetyVerdict
	// Category is the harm class label (e.g. "self-harm", "prompt-injection").
	Category string
	// Reason is a human-readable explanation of the finding.
	Reason string
	// Confidence is the classifier's confidence in [0, 1].
	Confidence float32
}

// SafetyAuditEntry is one append-only safety audit record. Ports SafetyAuditEntry.
type SafetyAuditEntry struct {
	// AtUTC is the UTC timestamp of the action.
	AtUTC time.Time
	// UserID is the identity the action was taken for.
	UserID string
	// Action is the logical action name.
	Action string
	// Verdict is the safety verdict reached.
	Verdict SafetyVerdict
	// Reason is a human-readable explanation.
	Reason string
}

// IContentFilter is a per-token / per-message content filter. Ports IContentFilter.
type IContentFilter interface {
	// BackendID identifies the implementation.
	BackendID() string
	// Classify inspects text and returns a single finding. Ports ClassifyAsync.
	Classify(ctx context.Context, text string) (SafetyFinding, error)
}

// IRefusalPolicy decides whether a set of findings becomes a refusal. Ports
// IRefusalPolicy.
type IRefusalPolicy interface {
	// BackendID identifies the implementation.
	BackendID() string
	// ShouldRefuse decides whether the findings warrant a refusal. Ports
	// ShouldRefuseAsync.
	ShouldRefuse(ctx context.Context, findings []SafetyFinding) (bool, error)
}

// IPromptInjectionDetector catches second-order attacks in untrusted content
// (RAG / web / tool output). Ports IPromptInjectionDetector.
type IPromptInjectionDetector interface {
	// BackendID identifies the implementation.
	BackendID() string
	// Inspect examines untrustedContent from sourceLabel for injection patterns.
	// Ports InspectAsync.
	Inspect(ctx context.Context, untrustedContent, sourceLabel string) (SafetyFinding, error)
}

// ISafetyAuditLog is an append-only safety audit log. Ports ISafetyAuditLog.
type ISafetyAuditLog interface {
	// BackendID identifies the implementation.
	BackendID() string
	// Log appends an entry. Ports LogAsync.
	Log(ctx context.Context, entry SafetyAuditEntry) error
	// Read returns up to limit entries, optionally filtered by userID (empty =
	// all users). Ports ReadAsync.
	Read(ctx context.Context, userID string, limit int) ([]SafetyAuditEntry, error)
}

// ─── KeywordContentFilter ──────────────────────────────────────────────────

// KeywordRule is a rule for the keyword content filter. Ports KeywordRule. The
// compiled regexp mirrors the C# record's computed Regex property.
type KeywordRule struct {
	// Category is the harm class label reported on a match.
	Category string
	// Pattern is the original regular-expression source.
	Pattern string
	// OnMatch is the verdict emitted when the pattern matches.
	OnMatch SafetyVerdict
	// Confidence is the finding confidence emitted on a match.
	Confidence float32

	regex *regexp.Regexp
}

// NewKeywordRule compiles pattern (case-insensitive, mirroring RegexOptions.
// IgnoreCase) and builds a rule. Ports the KeywordRule ctor. It panics if the
// pattern does not compile, matching the C# behaviour of throwing at
// construction time.
func NewKeywordRule(category, pattern string, onMatch SafetyVerdict, confidence float32) KeywordRule {
	return KeywordRule{
		Category:   category,
		Pattern:    pattern,
		OnMatch:    onMatch,
		Confidence: confidence,
		regex:      regexp.MustCompile("(?i)" + pattern),
	}
}

// Regex returns the compiled pattern. Ports KeywordRule.Regex. If the rule was
// constructed as a bare struct literal (no compiled regexp), it lazily compiles
// Pattern on first use.
func (r *KeywordRule) Regex() *regexp.Regexp {
	if r.regex == nil {
		r.regex = regexp.MustCompile("(?i)" + r.Pattern)
	}
	return r.regex
}

// CommonKeywordRulesDefault is the default rule set for everyday harm classes.
// Ports CommonKeywordRules.Default. Patterns are translated from .NET regex to
// Go's RE2 syntax; \b word boundaries and the alternations are preserved.
func CommonKeywordRulesDefault() []KeywordRule {
	return []KeywordRule{
		NewKeywordRule("self-harm", `\b(kill myself|suicide|self\s*-?\s*harm)\b`, SafetyVerdictRefuse, 0.95),
		NewKeywordRule("explicit-sexual", `\b(porn|sexual content|nsfw)\b`, SafetyVerdictFlag, 0.7),
		NewKeywordRule("violence", `\b(how to make a bomb|chemical weapon|murder)\b`, SafetyVerdictRefuse, 0.9),
		NewKeywordRule("hate", `\b(racial slur|hate speech)\b`, SafetyVerdictRefuse, 0.9),
		NewKeywordRule("pii-card", `\b(?:\d[ -]*?){13,19}\b`, SafetyVerdictFlag, 0.8),
	}
}

// KeywordContentFilter classifies text against an ordered rule set, returning the
// first match. Ports KeywordContentFilter.
type KeywordContentFilter struct {
	rules []KeywordRule
}

// NewKeywordContentFilter builds a filter over rules; when rules is nil the
// default rule set is used. Ports the KeywordContentFilter ctor.
func NewKeywordContentFilter(rules []KeywordRule) *KeywordContentFilter {
	if rules == nil {
		rules = CommonKeywordRulesDefault()
	}
	return &KeywordContentFilter{rules: rules}
}

// BackendID returns "keyword". Ports KeywordContentFilter.BackendId.
func (f *KeywordContentFilter) BackendID() string { return "keyword" }

// Classify returns the first matching rule's finding, or an Allow finding when
// nothing matches. Ports KeywordContentFilter.ClassifyAsync.
func (f *KeywordContentFilter) Classify(_ context.Context, text string) (SafetyFinding, error) {
	for i := range f.rules {
		r := &f.rules[i]
		if r.Regex().MatchString(text) {
			return SafetyFinding{
				Verdict:    r.OnMatch,
				Category:   r.Category,
				Reason:     "Matched rule '" + r.Category + "'",
				Confidence: r.Confidence,
			}, nil
		}
	}
	return SafetyFinding{Verdict: SafetyVerdictAllow, Category: "ok", Reason: "No rule matched", Confidence: 1}, nil
}

// ─── ThresholdRefusalPolicy ────────────────────────────────────────────────

// ThresholdRefusalPolicy refuses when any Refuse finding meets the confidence
// threshold, or when the count of Flag findings exceeds a ceiling. Ports
// ThresholdRefusalPolicy.
type ThresholdRefusalPolicy struct {
	refuseThreshold float32
	flagCeiling     int
}

// NewThresholdRefusalPolicy builds the policy. Ports the ThresholdRefusalPolicy
// ctor defaults (refuseThreshold 0.5, flagCeiling 3) — pass them explicitly.
func NewThresholdRefusalPolicy(refuseThreshold float32, flagCeiling int) *ThresholdRefusalPolicy {
	return &ThresholdRefusalPolicy{refuseThreshold: refuseThreshold, flagCeiling: flagCeiling}
}

// NewDefaultThresholdRefusalPolicy builds the policy with the C# ctor defaults
// (refuseThreshold 0.5, flagCeiling 3).
func NewDefaultThresholdRefusalPolicy() *ThresholdRefusalPolicy {
	return NewThresholdRefusalPolicy(0.5, 3)
}

// BackendID returns "threshold". Ports ThresholdRefusalPolicy.BackendId.
func (p *ThresholdRefusalPolicy) BackendID() string { return "threshold" }

// ShouldRefuse reports whether the findings warrant a refusal. Ports
// ThresholdRefusalPolicy.ShouldRefuseAsync.
func (p *ThresholdRefusalPolicy) ShouldRefuse(_ context.Context, findings []SafetyFinding) (bool, error) {
	flagCount := 0
	for _, f := range findings {
		if f.Verdict == SafetyVerdictRefuse && f.Confidence >= p.refuseThreshold {
			return true, nil
		}
		if f.Verdict == SafetyVerdictFlag {
			flagCount++
		}
	}
	return flagCount > p.flagCeiling, nil
}

// ─── KeywordPromptInjectionDetector ────────────────────────────────────────

// keywordInjectionPatterns are the compiled prompt-injection signatures. Ports
// the static Patterns array in KeywordPromptInjectionDetector.
var keywordInjectionPatterns = []*regexp.Regexp{
	regexp.MustCompile(`(?i)ignore (all|the|any) (previous|prior) instructions`),
	regexp.MustCompile(`(?i)forget (everything|all) (above|prior)`),
	regexp.MustCompile(`(?i)you (are now|will be|are no longer)`),
	regexp.MustCompile(`(?i)system prompt[:\s]`),
	regexp.MustCompile(`(?i)reveal (your|the) (instructions|system prompt|hidden context)`),
	regexp.MustCompile(`(?i)<\|im_(start|end)\|>`),
	regexp.MustCompile(`(?i)(BEGIN|END)\s+(SYSTEM|DEVELOPER|ASSISTANT)\s+MESSAGE`),
}

// KeywordPromptInjectionDetector detects common prompt-injection patterns in
// untrusted text from RAG / tool output / web. Ports
// KeywordPromptInjectionDetector.
type KeywordPromptInjectionDetector struct{}

// NewKeywordPromptInjectionDetector builds the detector.
func NewKeywordPromptInjectionDetector() *KeywordPromptInjectionDetector {
	return &KeywordPromptInjectionDetector{}
}

// BackendID returns "keyword". Ports KeywordPromptInjectionDetector.BackendId.
func (d *KeywordPromptInjectionDetector) BackendID() string { return "keyword" }

// Inspect returns a Refuse finding when any injection pattern matches, else an
// Allow finding. Ports KeywordPromptInjectionDetector.InspectAsync.
func (d *KeywordPromptInjectionDetector) Inspect(_ context.Context, untrustedContent, sourceLabel string) (SafetyFinding, error) {
	for _, p := range keywordInjectionPatterns {
		if loc := p.FindString(untrustedContent); loc != "" {
			return SafetyFinding{
				Verdict:    SafetyVerdictRefuse,
				Category:   "prompt-injection",
				Reason:     "Pattern matched in " + sourceLabel + ": \"" + truncateEllipsis(loc, 60) + "\"",
				Confidence: 0.9,
			}, nil
		}
	}
	return SafetyFinding{Verdict: SafetyVerdictAllow, Category: "ok", Reason: "No injection patterns", Confidence: 1}, nil
}

// (truncateEllipsis, which mirrors the private C# Truncate helper — appending the
// ellipsis character when s exceeds max runes — is the package-shared helper
// defined in hosting_util.go.)

// ─── Null (fail-closed) implementations ────────────────────────────────────

// NullContentFilter is a fail-closed content filter: it refuses everything.
// Ports NullContentFilter.
type NullContentFilter struct{}

// NullContentFilterInstance is the shared singleton. Mirrors
// NullContentFilter.Instance.
var NullContentFilterInstance = &NullContentFilter{}

// BackendID returns "null".
func (*NullContentFilter) BackendID() string { return "null" }

// Classify always refuses. Ports NullContentFilter.ClassifyAsync.
func (*NullContentFilter) Classify(context.Context, string) (SafetyFinding, error) {
	return SafetyFinding{
		Verdict:    SafetyVerdictRefuse,
		Category:   "no-filter-configured",
		Reason:     "Fail-closed default — wire a real IContentFilter to relax.",
		Confidence: 1,
	}, nil
}

// NullRefusalPolicy is a fail-closed refusal policy: it always refuses. Ports
// NullRefusalPolicy.
type NullRefusalPolicy struct{}

// NullRefusalPolicyInstance is the shared singleton. Mirrors
// NullRefusalPolicy.Instance.
var NullRefusalPolicyInstance = &NullRefusalPolicy{}

// BackendID returns "null".
func (*NullRefusalPolicy) BackendID() string { return "null" }

// ShouldRefuse always returns true. Ports NullRefusalPolicy.ShouldRefuseAsync.
func (*NullRefusalPolicy) ShouldRefuse(context.Context, []SafetyFinding) (bool, error) {
	return true, nil
}

// NullPromptInjectionDetector is a fail-closed injection detector: it refuses
// everything. Ports NullPromptInjectionDetector.
type NullPromptInjectionDetector struct{}

// NullPromptInjectionDetectorInstance is the shared singleton. Mirrors
// NullPromptInjectionDetector.Instance.
var NullPromptInjectionDetectorInstance = &NullPromptInjectionDetector{}

// BackendID returns "null".
func (*NullPromptInjectionDetector) BackendID() string { return "null" }

// Inspect always refuses. Ports NullPromptInjectionDetector.InspectAsync.
func (*NullPromptInjectionDetector) Inspect(context.Context, string, string) (SafetyFinding, error) {
	return SafetyFinding{
		Verdict:    SafetyVerdictRefuse,
		Category:   "no-detector-configured",
		Reason:     "Fail-closed default.",
		Confidence: 1,
	}, nil
}

// NullSafetyAuditLog silently discards every entry and returns no results. Ports
// NullSafetyAuditLog.
type NullSafetyAuditLog struct{}

// NullSafetyAuditLogInstance is the shared singleton. Mirrors
// NullSafetyAuditLog.Instance.
var NullSafetyAuditLogInstance = &NullSafetyAuditLog{}

// BackendID returns "null".
func (*NullSafetyAuditLog) BackendID() string { return "null" }

// Log discards the entry. Ports NullSafetyAuditLog.LogAsync.
func (*NullSafetyAuditLog) Log(context.Context, SafetyAuditEntry) error { return nil }

// Read returns an empty result. Ports NullSafetyAuditLog.ReadAsync.
func (*NullSafetyAuditLog) Read(context.Context, string, int) ([]SafetyAuditEntry, error) {
	return []SafetyAuditEntry{}, nil
}

// Compile-time assertions that the implementations satisfy the contracts.
var (
	_ IContentFilter           = (*KeywordContentFilter)(nil)
	_ IContentFilter           = (*NullContentFilter)(nil)
	_ IRefusalPolicy           = (*ThresholdRefusalPolicy)(nil)
	_ IRefusalPolicy           = (*NullRefusalPolicy)(nil)
	_ IPromptInjectionDetector = (*KeywordPromptInjectionDetector)(nil)
	_ IPromptInjectionDetector = (*NullPromptInjectionDetector)(nil)
	_ ISafetyAuditLog          = (*NullSafetyAuditLog)(nil)
)
