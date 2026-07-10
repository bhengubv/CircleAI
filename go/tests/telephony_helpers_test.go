// telephony_helpers_test.go
//
// Shared helpers for the telephony test slice.

package circleai_test

import (
	"net/url"
	"testing"
)

// mustURL parses s into an absolute *url.URL or fails the test.
func mustURL(t *testing.T, s string) *url.URL {
	t.Helper()
	u, err := url.Parse(s)
	if err != nil {
		t.Fatalf("parse url %q: %v", s, err)
	}
	return u
}
