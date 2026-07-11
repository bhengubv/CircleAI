// integration_util.go
//
// Small shared helpers the integration connectors use that aren't already in the
// tree: GUID-hex generation (Guid.NewGuid().ToString("N")), an injectable
// UTC-now clock (for ICS DTSTAMP determinism), and the string?/nullable helpers
// that bridge C# string? fields to Go *string.

package circleai

import (
	"encoding/json"
	"errors"
	"net/url"
	"regexp"
	"strconv"
	"strings"
	"time"

	"github.com/google/uuid"
)

// nowUTCFunc returns the current UTC instant. It is a package var so tests may
// override it for a deterministic DTSTAMP; production is time.Now().UTC(). Mirrors
// DateTime.UtcNow at the call sites that stamp generated payloads.
var nowUTCFunc = func() time.Time { return time.Now().UTC() }

// newGUIDHex returns a 32-char lower-case hex GUID (no dashes). Ports
// Guid.NewGuid().ToString("N").
func newGUIDHex() string { return strings.ReplaceAll(uuid.New().String(), "-", "") }

// strPtr returns a pointer to s. Bridges a non-null C# string to *string.
func strPtr(s string) *string { return &s }

// derefOr returns *p, or def when p is nil. Bridges "value ?? def".
func derefOr(p *string, def string) string {
	if p == nil {
		return def
	}
	return *p
}

// derefOrNil returns *p as an interface, or nil when p is nil, so a JSON body
// serialises the field as null (matching the C# anonymous object carrying a null
// string property).
func derefOrNil(p *string) interface{} {
	if p == nil {
		return nil
	}
	return *p
}

// containsFold reports whether values contains target case-insensitively. Mirrors
// List.Contains(x, StringComparer.OrdinalIgnoreCase).
func containsFold(values []string, target string) bool {
	for _, v := range values {
		if strings.EqualFold(v, target) {
			return true
		}
	}
	return false
}

// headerFold looks up a header by case-insensitive key. The header map is keyed
// lower-case (see the Gmail parser), so a lower-cased probe suffices; kept as a
// helper to mirror the C#'s OrdinalIgnoreCase Dictionary access at call sites.
func headerFold(headers map[string]string, key string) (string, bool) {
	v, ok := headers[strings.ToLower(key)]
	return v, ok
}

// parseInt64 parses a base-10 int64, returning 0 on failure. Mirrors
// long.TryParse(s, out ms) ? ms : 0.
func parseInt64(s string) int64 {
	n, err := strconv.ParseInt(strings.TrimSpace(s), 10, 64)
	if err != nil {
		return 0
	}
	return n
}

// unixMillisUTC converts Unix milliseconds to a UTC time.Time. Mirrors
// DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime.
func unixMillisUTC(ms int64) time.Time {
	return time.UnixMilli(ms).UTC()
}

// parseJSONArray parses body into a JSON array. Mirrors
// JsonDocument.Parse(...).RootElement for an array payload (UseNumber so numeric
// scalars keep their exact base-10 text). A non-array top level yields an error.
func parseJSONArray(body []byte) ([]interface{}, error) {
	dec := json.NewDecoder(strings.NewReader(string(body)))
	dec.UseNumber()
	var root interface{}
	if err := dec.Decode(&root); err != nil {
		return nil, err
	}
	arr, ok := root.([]interface{})
	if !ok {
		return nil, errJSONNotArray
	}
	return arr, nil
}

// errJSONNotArray is returned by parseJSONArray when the top level isn't an array.
var errJSONNotArray = errors.New("json: top-level value is not an array")

// rxHTMLTag matches an HTML/XML tag, for stripping to plain text.
var rxHTMLTag = regexp.MustCompile(`<[^>]+>`)

// stripHTMLTags replaces every tag with a space and trims. Ports
// Regex.Replace(html, "<[^>]+>", " ").Trim().
func stripHTMLTags(html string) string {
	return strings.TrimSpace(rxHTMLTag.ReplaceAllString(html, " "))
}

// absoluteOrBlank returns s when it parses as an absolute URI, else "about:blank".
// Ports Uri.TryCreate(s, UriKind.Absolute, out u) ? u : new Uri("about:blank").
func absoluteOrBlank(s string) string {
	u, err := url.Parse(s)
	if err != nil || !u.IsAbs() {
		return "about:blank"
	}
	return s
}

// uriHost returns the host component of an absolute URI, or "" when unparseable.
// Ports Uri.Host.
func uriHost(s string) string {
	u, err := url.Parse(s)
	if err != nil {
		return ""
	}
	return u.Hostname()
}

// nonNilStrings returns s, or an empty (non-nil) slice when s is nil. Bridges the
// C# arrays that are never null (e.g. category term lists).
func nonNilStrings(s []string) []string {
	if s == nil {
		return []string{}
	}
	return s
}
