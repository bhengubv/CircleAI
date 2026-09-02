// identity_test.go
//
// Validates:
//   - IdentityTier constants exist and are ordered correctly.
//   - Three fixture examples from fixtures/identity.json can be decoded
//     and round-tripped through CircleIdentity / RegisteredDevice.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ---------------------------------------------------------------------------
// Fixture types
// ---------------------------------------------------------------------------

type identityFixture struct {
	IdentityTiers []string          `json:"identityTiers"`
	Examples      []identityExample `json:"examples"`
}

type identityExample struct {
	ID          string       `json:"id"`
	Description string       `json:"description"`
	Identity    identityJSON `json:"identity"`
	Devices     []deviceJSON `json:"devices"`
}

type identityJSON struct {
	IdentityID        string   `json:"identityId"`
	DisplayName       string   `json:"displayName"`
	PreferredLanguage *string  `json:"preferredLanguage"`
	Tier              string   `json:"tier"`
	DeviceIDs         []string `json:"deviceIds"`
	CreatedAt         string   `json:"createdAt"`
	LastSeenAt        string   `json:"lastSeenAt"`
}

type deviceJSON struct {
	DeviceID     string  `json:"deviceId"`
	IdentityID   string  `json:"identityId"`
	Platform     string  `json:"platform"`
	DeviceName   *string `json:"deviceName"`
	RegisteredAt string  `json:"registeredAt"`
	LastActiveAt string  `json:"lastActiveAt"`
}

func loadIdentityFixture(t *testing.T) identityFixture {
	t.Helper()
	path := filepath.Join(fixturesDir(t), "identity.json")
	data, err := os.ReadFile(path)
	if err != nil {
		t.Fatalf("failed to read identity.json: %v", err)
	}
	var fix identityFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("failed to parse identity.json: %v", err)
	}
	return fix
}

func tierFromString(s string) circleai.IdentityTier {
	switch s {
	case "Anonymous":
		return circleai.IdentityTierAnonymous
	case "Pseudonymous":
		return circleai.IdentityTierPseudonymous
	case "Verified":
		return circleai.IdentityTierVerified
	default:
		panic("unknown tier: " + s)
	}
}

func mustParseTime(t *testing.T, s string) time.Time {
	t.Helper()
	ts, err := time.Parse(time.RFC3339, s)
	if err != nil {
		t.Fatalf("failed to parse time %q: %v", s, err)
	}
	return ts.UTC()
}

// ---------------------------------------------------------------------------
// Tier constant tests
// ---------------------------------------------------------------------------

func TestIdentityTierOrder(t *testing.T) {
	// Anonymous < Pseudonymous < Verified
	if circleai.IdentityTierAnonymous >= circleai.IdentityTierPseudonymous {
		t.Error("Anonymous should be less than Pseudonymous")
	}
	if circleai.IdentityTierPseudonymous >= circleai.IdentityTierVerified {
		t.Error("Pseudonymous should be less than Verified")
	}
}

func TestIdentityTierValues(t *testing.T) {
	fix := loadIdentityFixture(t)
	// Fixture declares 3 tier names; ensure all three map without panic.
	for _, name := range fix.IdentityTiers {
		_ = tierFromString(name)
	}
}

// ---------------------------------------------------------------------------
// Fixture example tests
// ---------------------------------------------------------------------------

func TestIdentityFixtureExamples(t *testing.T) {
	fix := loadIdentityFixture(t)

	if len(fix.Examples) < 3 {
		t.Fatalf("expected at least 3 examples, got %d", len(fix.Examples))
	}

	for _, ex := range fix.Examples {
		ex := ex
		t.Run(ex.ID, func(t *testing.T) {
			// Build CircleIdentity from fixture.
			identity := circleai.CircleIdentity{
				IdentityID:        ex.Identity.IdentityID,
				DisplayName:       ex.Identity.DisplayName,
				PreferredLanguage: ex.Identity.PreferredLanguage,
				Tier:              tierFromString(ex.Identity.Tier),
				DeviceIDs:         ex.Identity.DeviceIDs,
				CreatedAt:         mustParseTime(t, ex.Identity.CreatedAt),
				LastSeenAt:        mustParseTime(t, ex.Identity.LastSeenAt),
			}

			if identity.IdentityID == "" {
				t.Error("IdentityID must not be empty")
			}
			if identity.DisplayName == "" {
				t.Error("DisplayName must not be empty")
			}
			if len(identity.DeviceIDs) != len(ex.Devices) {
				t.Errorf("DeviceIDs count: got %d, want %d",
					len(identity.DeviceIDs), len(ex.Devices))
			}

			// Build RegisteredDevices from fixture.
			for _, dj := range ex.Devices {
				device := circleai.RegisteredDevice{
					DeviceID:     dj.DeviceID,
					IdentityID:   dj.IdentityID,
					Platform:     dj.Platform,
					DeviceName:   dj.DeviceName,
					RegisteredAt: mustParseTime(t, dj.RegisteredAt),
					LastActiveAt: mustParseTime(t, dj.LastActiveAt),
				}
				if device.DeviceID == "" {
					t.Error("DeviceID must not be empty")
				}
				if device.IdentityID != identity.IdentityID {
					t.Errorf("device.IdentityID %q != identity.IdentityID %q",
						device.IdentityID, identity.IdentityID)
				}
			}
		})
	}
}

// ---------------------------------------------------------------------------
// Specific example assertions
// ---------------------------------------------------------------------------

func TestIdentityFixture_Verified(t *testing.T) {
	fix := loadIdentityFixture(t)
	var ex *identityExample
	for i := range fix.Examples {
		if fix.Examples[i].ID == "verified_multi_device" {
			ex = &fix.Examples[i]
			break
		}
	}
	if ex == nil {
		t.Fatal("verified_multi_device example not found")
	}
	if ex.Identity.Tier != "Verified" {
		t.Errorf("tier: got %q, want Verified", ex.Identity.Tier)
	}
	if len(ex.Devices) != 3 {
		t.Errorf("device count: got %d, want 3", len(ex.Devices))
	}
	if ex.Identity.PreferredLanguage == nil || *ex.Identity.PreferredLanguage != "zu" {
		t.Errorf("preferredLanguage: got %v, want \"zu\"", ex.Identity.PreferredLanguage)
	}
}

func TestIdentityFixture_Anonymous(t *testing.T) {
	fix := loadIdentityFixture(t)
	var ex *identityExample
	for i := range fix.Examples {
		if fix.Examples[i].ID == "anonymous_iot" {
			ex = &fix.Examples[i]
			break
		}
	}
	if ex == nil {
		t.Fatal("anonymous_iot example not found")
	}
	if ex.Identity.Tier != "Anonymous" {
		t.Errorf("tier: got %q, want Anonymous", ex.Identity.Tier)
	}
	if ex.Identity.PreferredLanguage != nil {
		t.Errorf("preferredLanguage: got %v, want nil", ex.Identity.PreferredLanguage)
	}
	if len(ex.Devices) != 1 || ex.Devices[0].Platform != "iot" {
		t.Errorf("expected 1 iot device, got %v", ex.Devices)
	}
}
