// upgrade_test.go
//
// Parity test — 7 upgrade-detection cases matching the C#
// ModelUpgradeTests byte-for-byte.

package circleai_test

import (
	"os"
	"path/filepath"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func makeRegistry(t *testing.T, entries ...circleai.ModelEntry) *circleai.ModelRegistryService {
	t.Helper()
	svc := circleai.NewModelRegistryService()
	svc.SetRegistry(circleai.ModelRegistry{
		RegistryURL: "https://stub",
		LastUpdated: time.Now().UTC(),
		Models:      entries,
	})
	return svc
}

func makeEntry(name, version string, files ...circleai.BundleFile) circleai.ModelEntry {
	var total int64
	for _, f := range files {
		total += f.SizeBytes
	}
	return circleai.ModelEntry{
		Name:         name,
		Version:      version,
		Quantization: "Q4",
		Repo:         "MNN/" + name,
		TotalBytes:   total,
		BundleFiles:  files,
	}
}

func TestCheckForUpgrades_Case1_NotInstalled_Empty(t *testing.T) {
	d := t.TempDir()
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "def", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 0 {
		t.Fatalf("expected 0 upgrades, got %+v", ups)
	}
}

func TestCheckForUpgrades_Case2_NoManifest_Unknown(t *testing.T) {
	d := t.TempDir()
	mDir := filepath.Join(d, "Qwen3-0.6B-MNN")
	if err := os.MkdirAll(mDir, 0o755); err != nil {
		t.Fatal(err)
	}
	if err := os.WriteFile(filepath.Join(mDir, "config.json"), []byte("stub"), 0o644); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 1 {
		t.Fatalf("expected 1 upgrade, got %+v", ups)
	}
	if ups[0].Reason != circleai.UpgradeReasonUnknown {
		t.Fatalf("expected Unknown, got %v", ups[0].Reason)
	}
	if ups[0].InstalledVersion != "" {
		t.Fatalf("expected empty installed version, got %q", ups[0].InstalledVersion)
	}
}

func TestCheckForUpgrades_Case3_AllShasMatch_Empty(t *testing.T) {
	d := t.TempDir()
	if err := circleai.WriteInstalledManifest(filepath.Join(d, "Qwen3-0.6B-MNN"),
		"Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
		[]circleai.BundleFile{
			{Name: "config.json", Sha256: "abc", SizeBytes: 100},
			{Name: "llm.mnn", Sha256: "def", SizeBytes: 200},
		}); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "def", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 0 {
		t.Fatalf("expected 0 upgrades, got %+v", ups)
	}
}

func TestCheckForUpgrades_Case4_VersionDrift_VersionChanged_ZeroBytes(t *testing.T) {
	d := t.TempDir()
	if err := circleai.WriteInstalledManifest(filepath.Join(d, "Qwen3-0.6B-MNN"),
		"Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
		[]circleai.BundleFile{
			{Name: "config.json", Sha256: "abc", SizeBytes: 100},
			{Name: "llm.mnn", Sha256: "def", SizeBytes: 200},
		}); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.1.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "def", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 1 || ups[0].Reason != circleai.UpgradeReasonVersionChanged ||
		ups[0].InstalledVersion != "1.0.0" || ups[0].AvailableVersion != "1.1.0" ||
		ups[0].EstimatedDownloadBytes != 0 {
		t.Fatalf("Case 4 failed: %+v", ups)
	}
}

func TestCheckForUpgrades_Case5_ShaDrift_ShaChanged_OnlyDriftedBytes(t *testing.T) {
	d := t.TempDir()
	if err := circleai.WriteInstalledManifest(filepath.Join(d, "Qwen3-0.6B-MNN"),
		"Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
		[]circleai.BundleFile{
			{Name: "config.json", Sha256: "abc", SizeBytes: 100},
			{Name: "llm.mnn", Sha256: "OLD", SizeBytes: 200},
		}); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "NEW", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 1 || ups[0].Reason != circleai.UpgradeReasonShaChanged ||
		ups[0].EstimatedDownloadBytes != 200 {
		t.Fatalf("Case 5 failed: %+v", ups)
	}
}

func TestCheckForUpgrades_Case6_VersionAndSha_Both_TotalBytes(t *testing.T) {
	d := t.TempDir()
	if err := circleai.WriteInstalledManifest(filepath.Join(d, "Qwen3-0.6B-MNN"),
		"Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
		[]circleai.BundleFile{
			{Name: "config.json", Sha256: "abc", SizeBytes: 100},
			{Name: "llm.mnn", Sha256: "OLD", SizeBytes: 200},
		}); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "2.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc2", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "NEW", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 1 || ups[0].Reason != circleai.UpgradeReasonBoth ||
		ups[0].EstimatedDownloadBytes != 300 {
		t.Fatalf("Case 6 failed: %+v", ups)
	}
}

func TestCheckForUpgrades_Case7_WriteInstalledManifestRoundTrip_Empty(t *testing.T) {
	d := t.TempDir()
	if err := circleai.WriteInstalledManifest(filepath.Join(d, "Qwen3-0.6B-MNN"),
		"Qwen3-0.6B-MNN", "1.0.0", "MNN/Qwen3-0.6B-MNN",
		[]circleai.BundleFile{
			{Name: "config.json", Sha256: "abc", SizeBytes: 100},
			{Name: "llm.mnn", Sha256: "def", SizeBytes: 200},
		}); err != nil {
		t.Fatal(err)
	}
	svc := makeRegistry(t, makeEntry("Qwen3-0.6B-MNN", "1.0.0",
		circleai.BundleFile{Name: "config.json", Sha256: "abc", SizeBytes: 100},
		circleai.BundleFile{Name: "llm.mnn", Sha256: "def", SizeBytes: 200}))
	ups, err := svc.CheckForUpgrades(d)
	if err != nil {
		t.Fatal(err)
	}
	if len(ups) != 0 {
		t.Fatalf("expected 0 upgrades, got %+v", ups)
	}
}

func TestAgentMessage_CorrelationID_Autosynth(t *testing.T) {
	m := circleai.CreateAgentMessage(
		circleai.AgentMessageGreet, "a", "b", "text/plain",
		[]byte{1, 2, 3}, []byte{4, 5, 6}, "")
	if len(m.CorrelationID) != 32 {
		t.Fatalf("expected 32-char correlation ID, got %q", m.CorrelationID)
	}

	m2 := circleai.CreateAgentMessage(
		circleai.AgentMessageGreet, "a", "b", "text/plain",
		[]byte{1, 2, 3}, []byte{4, 5, 6}, "trace-abc")
	if m2.CorrelationID != "trace-abc" {
		t.Fatalf("expected trace-abc, got %q", m2.CorrelationID)
	}

	m3 := circleai.CreateAgentMessage(
		circleai.AgentMessageGreet, "a", "b", "text/plain",
		[]byte{1, 2, 3}, []byte{4, 5, 6}, "")
	if m.CorrelationID == m3.CorrelationID {
		t.Fatal("expected distinct correlation IDs")
	}
}
