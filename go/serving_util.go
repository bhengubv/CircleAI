// serving_util.go
//
// Tiny shared helpers used by the serving/agents/runtime module ports
// (pipelines.go, buildfarm.go, operator_model.go, etc.). No C# type is
// introduced here — these reproduce small BCL idioms:
//   - itoa64: int64 -> decimal string (for "run-{seq}" / "job-{seq}" ids the
//     C# builds with Interlocked.Increment + string interpolation).
//   - hasPrefixFold / indexFold: case-insensitive StartsWith / IndexOf used by
//     InMemoryDatabaseQueryTool's SELECT parser (StringComparison.OrdinalIgnoreCase).

package circleai

import "strings"

// itoa64 formats an int64 as a base-10 string.
func itoa64(v int64) string {
	if v == 0 {
		return "0"
	}
	neg := v < 0
	// Handle math.MinInt64 without overflow by working on the unsigned magnitude.
	var u uint64
	if neg {
		u = uint64(^v) + 1
	} else {
		u = uint64(v)
	}
	var buf [20]byte
	i := len(buf)
	for u > 0 {
		i--
		buf[i] = byte('0' + u%10)
		u /= 10
	}
	if neg {
		i--
		buf[i] = '-'
	}
	return string(buf[i:])
}

// hasPrefixFold reports whether s starts with prefix, ignoring ASCII case.
func hasPrefixFold(s, prefix string) bool {
	if len(s) < len(prefix) {
		return false
	}
	return strings.EqualFold(s[:len(prefix)], prefix)
}

// indexFold returns the index of the first case-insensitive occurrence of sub
// in s, or -1. Mirrors string.IndexOf(sub, StringComparison.OrdinalIgnoreCase).
func indexFold(s, sub string) int {
	if sub == "" {
		return 0
	}
	n := len(s) - len(sub)
	for i := 0; i <= n; i++ {
		if strings.EqualFold(s[i:i+len(sub)], sub) {
			return i
		}
	}
	return -1
}
