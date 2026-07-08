// hosting_util.go
//
// Small shared helpers used across the CircleAI.Hosting Go port. These mirror
// idioms the C# code gets from the BCL (ArgumentException.ThrowIfNullOrWhiteSpace,
// string truncation with an ellipsis, ex.GetType().Name).

package circleai

import (
	"errors"
	"fmt"
	"reflect"
)

// (isBlank lives in sync_channel.go — reused here, not redeclared.)

// errNilArg builds an argument-nil error naming the parameter.
func errNilArg(name string) error {
	return fmt.Errorf("%s must not be nil", name)
}

// errArg builds a generic argument error.
func errArg(msg string) error {
	return errors.New(msg)
}

// truncateEllipsis returns s unchanged when its rune length is <= max, else the
// first max runes plus an ellipsis. Mirrors the C# `msg[..max] + "…"` pattern
// (which operates on UTF-16 code units; we truncate on runes so multi-byte
// characters are never split).
func truncateEllipsis(s string, max int) string {
	r := []rune(s)
	if len(r) <= max {
		return s
	}
	return string(r[:max]) + "…"
}

// errorTypeName returns the concrete type name of err, approximating the C#
// `ex.GetType().Name`. Pointer wrappers are unwrapped to the element name.
func errorTypeName(err error) string {
	if err == nil {
		return ""
	}
	t := reflect.TypeOf(err)
	for t != nil && t.Kind() == reflect.Ptr {
		t = t.Elem()
	}
	if t == nil {
		return "error"
	}
	if t.Name() != "" {
		return t.Name()
	}
	return t.String()
}
