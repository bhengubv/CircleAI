// integration_time.go
//
// Date/time formatting + parsing helpers the integration connectors need to
// reproduce the C# BCL wire behaviour faithfully:
//
//   isoRoundTrip   -> DateTimeOffset.ToString("O")  (round-trip, 7 fractional
//                     digits, offset suffix) — used for the Google / MS Graph
//                     query params and JSON bodies.
//   isoDateOnly    -> DateTime.ToString("yyyy-MM-dd") — Google all-day date.
//   caldavStamp    -> DateTime.ToString("yyyyMMddTHHmmssZ") — CalDAV time-range
//                     + ICS DTSTART/DTEND/DTSTAMP.
//   parseDateTimeOffsetUTC -> DateTimeOffset.TryParse(..., AssumeUniversal)
//                     .ToUniversalTime() — response timestamp parsing; returns
//                     the zero Time (mirrors DateTimeOffset.MinValue used as the
//                     C# fallback) when unparseable.
//   parseCaldavTime / parseDateOnly — the minimal ICS parser's exact-format paths.
//
// C# DateTimeOffset.MinValue is 0001-01-01T00:00:00+00:00; Go's zero time.Time is
// 0001-01-01T00:00:00Z. Both are used purely as the "unknown" sentinel and are
// value-comparable, so the Go zero time stands in for MinValue throughout.

package circleai

import (
	"strings"
	"time"
)

// csharpRoundTripLayout is the Go layout matching C#'s "O" round-trip format for
// an offset instant: 7 fractional-second digits + numeric offset (Z for UTC).
const csharpRoundTripLayout = "2006-01-02T15:04:05.0000000Z07:00"

// caldavLayout matches C#'s "yyyyMMddTHHmmssZ" (UTC basic ISO, no separators).
const caldavLayout = "20060102T150405Z"

// dateOnlyLayout matches C#'s "yyyy-MM-dd".
const dateOnlyLayout = "2006-01-02"

// isoRoundTrip renders t (in UTC) as DateTimeOffset.ToString("O"). The instant is
// first normalised to UTC so the offset renders as "Z", matching how the C#
// connectors format UtcDateTime / a UTC DateTimeOffset.
func isoRoundTrip(t time.Time) string {
	return t.UTC().Format(csharpRoundTripLayout)
}

// isoDateOnly renders t's UTC date as "yyyy-MM-dd".
func isoDateOnly(t time.Time) string {
	return t.UTC().Format(dateOnlyLayout)
}

// caldavStamp renders t's UTC instant as "yyyyMMddTHHmmssZ".
func caldavStamp(t time.Time) string {
	return t.UTC().Format(caldavLayout)
}

// parseDateTimeOffsetUTC parses an ISO-8601 timestamp, assuming UTC when it has
// no offset, and returns it normalised to UTC. Mirrors
// DateTimeOffset.TryParse(s, AssumeUniversal).ToUniversalTime(); an empty or
// unparseable value yields the zero Time (C# DateTimeOffset.MinValue sentinel).
func parseDateTimeOffsetUTC(s string) time.Time {
	s = strings.TrimSpace(s)
	if s == "" {
		return time.Time{}
	}
	// Layouts in preference order: offset-bearing first, then assume-UTC. Covers
	// ISO-8601 (Google/Graph/Open-Meteo/JSON), RFC-1123/822 (RSS pubDate + Atom),
	// and bare date/space-separated forms, matching the breadth of
	// DateTimeOffset.TryParse.
	layouts := []string{
		time.RFC3339Nano,                 // 2026-07-11T12:00:00.123+02:00 / ...Z
		time.RFC3339,                     // 2026-07-11T12:00:00+02:00 / ...Z
		time.RFC1123Z,                    // Fri, 11 Jul 2026 06:00:00 +0200
		time.RFC1123,                     // Fri, 11 Jul 2026 06:00:00 GMT
		time.RFC822Z,                     // 11 Jul 26 06:00 +0200
		time.RFC822,                      // 11 Jul 26 06:00 GMT
		"Mon, 2 Jan 2006 15:04:05 -0700", // RFC-1123Z, non-padded day
		"Mon, 2 Jan 2006 15:04:05 MST",   // RFC-1123, non-padded day
		"2006-01-02T15:04:05.999999999",  // no offset -> assume UTC
		"2006-01-02T15:04:05",            // no offset, no frac -> assume UTC
		"2006-01-02T15:04",               // minute precision
		"2006-01-02 15:04:05Z07:00",      // space separator, offset
		"2006-01-02 15:04:05",            // space separator, assume UTC
		dateOnlyLayout,                   // date-only -> midnight UTC
	}
	for _, layout := range layouts {
		hasOffset := strings.Contains(layout, "Z07:00")
		if hasOffset {
			if t, err := time.Parse(layout, s); err == nil {
				return t.UTC()
			}
		} else {
			// Parse in UTC (AssumeUniversal): interpret the wall-clock as UTC.
			if t, err := time.ParseInLocation(layout, s, time.UTC); err == nil {
				return t.UTC()
			}
		}
	}
	return time.Time{}
}

// parseCaldavTime parses a CalDAV/ICS time value. Tries "yyyyMMddTHHmmssZ"
// (assume-universal) first, then "yyyyMMdd" (all-day date at midnight UTC),
// mirroring the C# ParseIcs Time() helper. Returns the zero Time when neither
// matches (DateTimeOffset.MinValue sentinel).
func parseCaldavTime(v string) time.Time {
	v = strings.TrimSpace(v)
	if v == "" {
		return time.Time{}
	}
	if t, err := time.ParseInLocation(caldavLayout, v, time.UTC); err == nil {
		return t.UTC()
	}
	if t, err := time.ParseInLocation("20060102", v, time.UTC); err == nil {
		return t.UTC()
	}
	return time.Time{}
}
