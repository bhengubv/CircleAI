// power_budget.go
//
// Ports CircleAI.Inference.KvCompressionMode + KvCompressionApplyResult
// (MnnInterop.cs) and CircleAI.Inference.PowerBudgetPolicy + Resolution
// (PowerBudget.cs).
//
// RT-11: the integrator declares HOW MUCH device energy a generation is worth
// (PowerBudget, already defined in inference.go) and the runtime decides WHAT
// to spend it on. PowerBudgetPolicy.Resolve is the single agreed mapping from
// a budget to concrete generation knobs (token cap + preferred KV mode +
// chain-model pick). KvCompressionMode mirrors the C ABI's integer encoding so
// managed and native layers agree without a translation table.

package circleai

// KvCompressionMode is the KV-cache compression mode. Mirrors the C ABI's
// integer encoding. Ports CircleAI.Inference.KvCompressionMode.
type KvCompressionMode int

const (
	// KvCompressionOff — full FP16 KV cache. Default, always supported.
	KvCompressionOff KvCompressionMode = 0
	// KvCompressionTurboQuant4Bit — TurboQuant 4 bits/channel — ~4× shrink, <1% loss.
	KvCompressionTurboQuant4Bit KvCompressionMode = 1
	// KvCompressionTurboQuant3Bit — TurboQuant 3 bits/channel — ~5× shrink, marginal loss.
	KvCompressionTurboQuant3Bit KvCompressionMode = 2
	// KvCompressionTurboQuant2Bit — TurboQuant 2 bits/channel — ~8× shrink, noticeable loss.
	KvCompressionTurboQuant2Bit KvCompressionMode = 3
)

// KvCompressionApplyResult is the typed outcome of applying a KV-compression
// mode. Mirrors the C ABI status codes. Ports
// CircleAI.Inference.KvCompressionApplyResult.
type KvCompressionApplyResult int

const (
	// KvApplyApplied — native path accepted the mode and will use it.
	KvApplyApplied KvCompressionApplyResult = 0
	// KvApplyInvalidMode — the mode value was outside the valid 0..3 range.
	KvApplyInvalidMode KvCompressionApplyResult = 1
	// KvApplyNotImplemented — legacy scaffolding-only response (mnnbridge ≤ 1.1.0).
	KvApplyNotImplemented KvCompressionApplyResult = 2
	// KvApplyHandleInvalid — handle pointer was invalid.
	KvApplyHandleInvalid KvCompressionApplyResult = -1
)

// IsValidKvCompressionMode reports whether raw is a defined mode ordinal (0..3).
func IsValidKvCompressionMode(raw int) bool { return raw >= 0 && raw <= 3 }

// KvCompressionApplyResultFromCode maps a raw C-ABI status integer to the typed
// result. Ports MnnKvCompression.Set's switch.
func KvCompressionApplyResultFromCode(raw int) KvCompressionApplyResult {
	switch raw {
	case 0:
		return KvApplyApplied
	case 1:
		return KvApplyInvalidMode
	case 2:
		return KvApplyNotImplemented
	default:
		return KvApplyHandleInvalid
	}
}

// KvCompressionModeFromCode reads a mode from a raw C-ABI integer, defaulting to
// KvCompressionOff on an out-of-range (invalid-handle) value. Ports
// MnnKvCompression.Get's clamp.
func KvCompressionModeFromCode(raw int) KvCompressionMode {
	if raw >= 0 && raw <= 3 {
		return KvCompressionMode(raw)
	}
	return KvCompressionOff
}

// PowerBudgetResolution is the runtime's translation of a PowerBudget into
// concrete generation knobs. Ports PowerBudgetPolicy.Resolution.
type PowerBudgetResolution struct {
	// MaxTokens caps the output tokens for this call.
	MaxTokens int
	// PreferredKvMode is the KV-compression mode the runtime prefers for this
	// budget. Advisory — current MNN builds set the mode at load() time, so it
	// is a HINT for future handles.
	PreferredKvMode KvCompressionMode
	// PreferSmallerModelInChain: when a fallback chain is configured, whether to
	// pick a smaller model than the chain head. True for PowerBudgetLow.
	PreferSmallerModelInChain bool
}

// ResolvePowerBudget maps a budget to concrete knobs. Callers pass the user's
// requested max-tokens; the returned resolution caps any over-budget value
// without mutating the caller's options. batteryLevelPercent is nil when the
// platform doesn't surface it (used to auto-downgrade Normal below 15%);
// thermalThrottled auto-downgrades High to Normal. Ports PowerBudgetPolicy.Resolve.
func ResolvePowerBudget(
	budget PowerBudget,
	requestedMaxTokens int,
	batteryLevelPercent *int,
	thermalThrottled bool,
) PowerBudgetResolution {
	// Auto-downgrade based on device state.
	if budget == PowerBudgetNormal && batteryLevelPercent != nil && *batteryLevelPercent < 15 {
		budget = PowerBudgetLow
	}
	if budget == PowerBudgetHigh && thermalThrottled {
		budget = PowerBudgetNormal
	}

	switch budget {
	case PowerBudgetNone:
		return PowerBudgetResolution{
			MaxTokens:                 requestedMaxTokens,
			PreferredKvMode:           KvCompressionTurboQuant4Bit,
			PreferSmallerModelInChain: false,
		}
	case PowerBudgetLow:
		return PowerBudgetResolution{
			MaxTokens:                 minInt(requestedMaxTokens, 64),
			PreferredKvMode:           KvCompressionTurboQuant4Bit,
			PreferSmallerModelInChain: true,
		}
	case PowerBudgetNormal:
		return PowerBudgetResolution{
			MaxTokens:                 minInt(requestedMaxTokens, 512),
			PreferredKvMode:           KvCompressionTurboQuant4Bit,
			PreferSmallerModelInChain: false,
		}
	case PowerBudgetHigh:
		return PowerBudgetResolution{
			MaxTokens:                 minInt(requestedMaxTokens, 2048),
			PreferredKvMode:           KvCompressionOff,
			PreferSmallerModelInChain: false,
		}
	default:
		return PowerBudgetResolution{
			MaxTokens:                 requestedMaxTokens,
			PreferredKvMode:           KvCompressionTurboQuant4Bit,
			PreferSmallerModelInChain: false,
		}
	}
}

func minInt(a, b int) int {
	if a < b {
		return a
	}
	return b
}
