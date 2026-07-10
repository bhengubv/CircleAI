// speech_voice_helpers_test.go
//
// Shared helpers for the Speech/Voice test files.

package circleai_test

import "time"

// timeoutC returns a channel that fires after the given number of seconds. Used to
// bound tests that could hang if a concurrency-safety invariant is violated.
func timeoutC(seconds int) <-chan time.Time {
	return time.After(time.Duration(seconds) * time.Second)
}
