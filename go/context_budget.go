// context_budget.go
//
// Ports CircleAI.Inference.ContextWindowBudgetManager
// (ContextWindowBudgetManager.cs).
//
// Tracks token usage against a fixed context window and signals when the KV
// cache should be partially evicted to keep inference latency manageable.
// C# throws ArgumentOutOfRangeException on bad input; Go surfaces those as
// returned errors so the failure is explicit at the call site.

package circleai

import "errors"

// ContextWindowBudgetManager tracks token usage against a fixed context window
// and signals eviction. Ports CircleAI.Inference.ContextWindowBudgetManager.
// Not safe for concurrent use.
type ContextWindowBudgetManager struct {
	contextSize       int
	usedTokens        int
	evictionThreshold float64
}

// DefaultEvictionThreshold mirrors the C# default (0.85).
const DefaultEvictionThreshold = 0.85

// NewContextWindowBudgetManager builds a budget manager. contextSize must be
// > 0; evictionThreshold must be in [0,1]. Ports the C# constructor guards.
func NewContextWindowBudgetManager(contextSize int, evictionThreshold float64) (*ContextWindowBudgetManager, error) {
	if contextSize <= 0 {
		return nil, errors.New("context size must be greater than zero")
	}
	if evictionThreshold < 0.0 || evictionThreshold > 1.0 {
		return nil, errors.New("eviction threshold must be in the range [0, 1]")
	}
	return &ContextWindowBudgetManager{
		contextSize:       contextSize,
		evictionThreshold: evictionThreshold,
	}, nil
}

// ContextSize is the maximum number of tokens the model's context window holds.
func (m *ContextWindowBudgetManager) ContextSize() int { return m.contextSize }

// UsedTokens is the cumulative tokens consumed so far (prompt + completion).
func (m *ContextWindowBudgetManager) UsedTokens() int { return m.usedTokens }

// EvictionThreshold is the fill ratio at/above which ShouldEvict becomes true.
func (m *ContextWindowBudgetManager) EvictionThreshold() float64 { return m.evictionThreshold }

// RemainingTokens is the tokens still available before the window is full.
func (m *ContextWindowBudgetManager) RemainingTokens() int { return m.contextSize - m.usedTokens }

// FillRatio is the proportion of the window currently occupied (0–1).
func (m *ContextWindowBudgetManager) FillRatio() float64 {
	return float64(m.usedTokens) / float64(m.contextSize)
}

// ShouldEvict reports whether the fill ratio has reached the eviction threshold.
func (m *ContextWindowBudgetManager) ShouldEvict() bool {
	return m.FillRatio() >= m.evictionThreshold
}

// RecordExchange records the token cost of one exchange (a prompt + its
// completion). Both counts must be non-negative. Ports RecordExchange.
func (m *ContextWindowBudgetManager) RecordExchange(promptTokens, completionTokens int) error {
	if promptTokens < 0 || completionTokens < 0 {
		return errors.New("token counts must not be negative")
	}
	m.usedTokens += promptTokens + completionTokens
	return nil
}

// CalculateEvictionCount computes how many of the oldest tokens should be
// dropped so FillRatio returns to targetFillRatio. Returns 0 when already at or
// below the target. targetFillRatio must be in [0,1]. Ports CalculateEvictionCount.
func (m *ContextWindowBudgetManager) CalculateEvictionCount(targetFillRatio float64) (int, error) {
	if targetFillRatio < 0.0 || targetFillRatio > 1.0 {
		return 0, errors.New("target fill ratio must be in the range [0, 1]")
	}
	targetUsed := int(float64(m.contextSize) * targetFillRatio)
	evict := m.usedTokens - targetUsed
	if evict > 0 {
		return evict, nil
	}
	return 0, nil
}

// Reset zeroes the used-token counter. Call after clearing the KV cache.
func (m *ContextWindowBudgetManager) Reset() { m.usedTokens = 0 }
