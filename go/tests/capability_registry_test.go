// capability_registry_test.go
//
// Verifies ExternalCapabilityRegistry (ported from CapabilityRegistry.cs)
// against fixtures/capability_registry.json: exact count + id order, per-package
// membership, and case-insensitive Find with spot-checked fields.

package circleai_test

import (
	"encoding/json"
	"os"
	"path/filepath"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type capabilityFixture struct {
	ExpectedCount int      `json:"expectedCount"`
	IDs           []string `json:"ids"`
	ByPackage     []struct {
		Package string   `json:"package"`
		Count   int      `json:"count"`
		IDs     []string `json:"ids"`
	} `json:"byPackage"`
	Find []struct {
		Query         string `json:"query"`
		Found         bool   `json:"found"`
		ID            string `json:"id"`
		License       string `json:"license"`
		Strategy      string `json:"strategy"`
		TargetPackage string `json:"targetPackage"`
	} `json:"find"`
}

func TestExternalCapabilityRegistry_Fixtures(t *testing.T) {
	data, err := os.ReadFile(filepath.Join(fixturesDir(t), "capability_registry.json"))
	if err != nil {
		t.Fatalf("read fixture: %v", err)
	}
	var fix capabilityFixture
	if err := json.Unmarshal(data, &fix); err != nil {
		t.Fatalf("parse fixture: %v", err)
	}

	all := circleai.ExternalCapabilityRegistryAll()
	if len(all) != fix.ExpectedCount {
		t.Errorf("count: got %d want %d", len(all), fix.ExpectedCount)
	}
	if len(all) != len(fix.IDs) {
		t.Fatalf("id list length mismatch: got %d want %d", len(all), len(fix.IDs))
	}
	for i, want := range fix.IDs {
		if all[i].ID != want {
			t.Errorf("id[%d]: got %q want %q", i, all[i].ID, want)
		}
	}
	// Every entry has a repo + at least one value bullet (spec parity).
	for _, e := range all {
		if e.Repo == nil || *e.Repo == "" {
			t.Errorf("entry %q has no repo", e.ID)
		}
		if len(e.ValueBullets) == 0 {
			t.Errorf("entry %q has no value bullets", e.ID)
		}
		if e.License == "" || e.Strategy == "" || e.TargetPackage == "" {
			t.Errorf("entry %q has empty classification: %+v", e.ID, e)
		}
	}

	for _, bp := range fix.ByPackage {
		got := circleai.ExternalCapabilityRegistryByPackage(bp.Package)
		if len(got) != bp.Count {
			t.Errorf("ByPackage(%q): got %d want %d", bp.Package, len(got), bp.Count)
		}
		gotIDs := map[string]bool{}
		for _, e := range got {
			gotIDs[e.ID] = true
		}
		for _, id := range bp.IDs {
			if !gotIDs[id] {
				t.Errorf("ByPackage(%q) missing %q", bp.Package, id)
			}
		}
	}

	for _, f := range fix.Find {
		got, ok := circleai.ExternalCapabilityRegistryFind(f.Query)
		if ok != f.Found {
			t.Errorf("Find(%q): found=%v want %v", f.Query, ok, f.Found)
			continue
		}
		if !f.Found {
			continue
		}
		if got.ID != f.ID || got.License != f.License || got.Strategy != f.Strategy || got.TargetPackage != f.TargetPackage {
			t.Errorf("Find(%q): got %+v, want id=%s license=%s strategy=%s pkg=%s",
				f.Query, got, f.ID, f.License, f.Strategy, f.TargetPackage)
		}
	}
}
