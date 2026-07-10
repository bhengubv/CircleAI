// video_test.go
//
// Verifies the CircleAI.Video Go port (video_contracts.go):
//   - StyleID.String, VideoResolution statics (P480/P720/P1080)
//   - VideoGenerationRequest / StyleScriptRequest defaults
//   - NullVideoGenerator (empty mp4, echoes resolution)
//   - NullStyleScript (echoes source, zero duration)
//   - InMemoryStyleReference: register/get, case-insensitive lookup, replace,
//     miss, deterministic ListAsync ordering

package circleai_test

import (
	"context"
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

// ── Primitives ──────────────────────────────────────────────────────────────

func TestStyleID_String(t *testing.T) {
	id := circleai.StyleID{Value: "pooh-1926"}
	if id.String() != "pooh-1926" {
		t.Errorf("String = %q", id.String())
	}
}

func TestVideoResolution_Statics(t *testing.T) {
	if r := circleai.VideoResolutionP480(); r.Width != 720 || r.Height != 480 {
		t.Errorf("P480 = %+v", r)
	}
	if r := circleai.VideoResolutionP720(); r.Width != 1280 || r.Height != 720 {
		t.Errorf("P720 = %+v", r)
	}
	if r := circleai.VideoResolutionP1080(); r.Width != 1920 || r.Height != 1080 {
		t.Errorf("P1080 = %+v", r)
	}
}

func TestVideoGenerationRequest_Defaults(t *testing.T) {
	r := circleai.NewVideoGenerationRequest("hello", 5*time.Second, circleai.VideoResolutionP720())
	if r.FrameRate != 24 {
		t.Errorf("FrameRate = %d want 24", r.FrameRate)
	}
	if r.StyleID != nil || r.ReferenceImage != nil || r.AudioTrack != nil || r.Seed != nil {
		t.Error("optional members must default to nil")
	}
	if r.Duration != 5*time.Second || r.Resolution.Width != 1280 {
		t.Errorf("request = %+v", r)
	}
}

// ── NullVideoGenerator ──────────────────────────────────────────────────────

func TestNullVideoGenerator(t *testing.T) {
	ctx := context.Background()
	g := circleai.NullVideoGeneratorInstance
	if g.BackendID() != "null" {
		t.Errorf("backend = %q", g.BackendID())
	}
	req := circleai.NewVideoGenerationRequest("msg", 3*time.Second, circleai.VideoResolutionP1080())
	res, err := g.GenerateAsync(ctx, req)
	if err != nil {
		t.Fatal(err)
	}
	if len(res.VideoBytes) != 0 || res.MimeType != "video/mp4" || res.Duration != 0 || res.FrameCount != 0 {
		t.Errorf("result = %+v", res)
	}
	if res.Resolution.Width != 1920 || res.Resolution.Height != 1080 {
		t.Errorf("resolution should echo request: %+v", res.Resolution)
	}
	if res.BackendID != "null" {
		t.Errorf("result backend = %q", res.BackendID)
	}
}

// ── NullStyleScript ─────────────────────────────────────────────────────────

func TestNullStyleScript(t *testing.T) {
	ctx := context.Background()
	s := circleai.NullStyleScriptInstance
	if s.BackendID() != "null" {
		t.Errorf("backend = %q", s.BackendID())
	}
	req := circleai.StyleScriptRequest{SourceMessage: "come home soon", Style: circleai.StyleID{Value: "noir"}}
	res, err := s.RewriteAsync(ctx, req)
	if err != nil {
		t.Fatal(err)
	}
	if res.RewrittenText != "come home soon" {
		t.Errorf("rewritten = %q want unchanged", res.RewrittenText)
	}
	if res.Style.Value != "noir" || res.VoicePersonaID != "" || res.EstimatedSpokenDuration != 0 {
		t.Errorf("result = %+v", res)
	}
}

// ── InMemoryStyleReference ──────────────────────────────────────────────────

func mkStyle(id, name string) circleai.StyleReference {
	return circleai.StyleReference{
		ID:               circleai.StyleID{Value: id},
		DisplayName:      name,
		ShortDescription: name + " desc",
		Attribution:      circleai.StyleAttribution{Source: "public domain", License: "CC0"},
		VoicePersonaID:   "voice-" + id,
		Frames: []circleai.StyleReferenceFrame{
			{ImageBytes: []byte{1, 2}, MimeType: "image/png", Caption: "frame"},
		},
	}
}

func TestInMemoryStyleReference_RegisterGetList(t *testing.T) {
	ctx := context.Background()
	cat := circleai.NewInMemoryStyleReference()
	if cat.BackendID() != "in-memory" {
		t.Errorf("backend = %q", cat.BackendID())
	}

	// Empty list.
	if list, err := cat.ListAsync(ctx); err != nil || len(list) != 0 {
		t.Errorf("empty list = %v,%v", list, err)
	}
	// Miss.
	if _, ok, err := cat.GetAsync(ctx, circleai.StyleID{Value: "nope"}); err != nil || ok {
		t.Errorf("miss = ok:%v err:%v", ok, err)
	}

	if err := cat.RegisterAsync(ctx, mkStyle("space-opera", "Space Opera")); err != nil {
		t.Fatal(err)
	}
	if err := cat.RegisterAsync(ctx, mkStyle("noir-detective", "Noir")); err != nil {
		t.Fatal(err)
	}

	got, ok, err := cat.GetAsync(ctx, circleai.StyleID{Value: "space-opera"})
	if err != nil || !ok {
		t.Fatalf("get = ok:%v err:%v", ok, err)
	}
	if got.DisplayName != "Space Opera" || got.VoicePersonaID != "voice-space-opera" || len(got.Frames) != 1 {
		t.Errorf("got = %+v", got)
	}

	list, err := cat.ListAsync(ctx)
	if err != nil {
		t.Fatal(err)
	}
	if len(list) != 2 {
		t.Fatalf("list len = %d want 2", len(list))
	}
	// Deterministic ordering: sorted by id → "noir-detective" before "space-opera".
	if list[0].ID.Value != "noir-detective" || list[1].ID.Value != "space-opera" {
		t.Errorf("list order = %q,%q want noir-detective,space-opera", list[0].ID.Value, list[1].ID.Value)
	}
}

func TestInMemoryStyleReference_CaseInsensitiveAndReplace(t *testing.T) {
	ctx := context.Background()
	cat := circleai.NewInMemoryStyleReference()
	if err := cat.RegisterAsync(ctx, mkStyle("Pooh-1926", "Pooh v1")); err != nil {
		t.Fatal(err)
	}

	// Case-insensitive lookup (OrdinalIgnoreCase).
	got, ok, err := cat.GetAsync(ctx, circleai.StyleID{Value: "pooh-1926"})
	if err != nil || !ok || got.DisplayName != "Pooh v1" {
		t.Fatalf("case-insensitive get = %+v ok:%v err:%v", got, ok, err)
	}

	// Register with different-cased id → replaces the same entry (no dup).
	if err := cat.RegisterAsync(ctx, mkStyle("POOH-1926", "Pooh v2")); err != nil {
		t.Fatal(err)
	}
	list, _ := cat.ListAsync(ctx)
	if len(list) != 1 {
		t.Fatalf("list len = %d want 1 (replace, not add)", len(list))
	}
	got2, _, _ := cat.GetAsync(ctx, circleai.StyleID{Value: "pooh-1926"})
	if got2.DisplayName != "Pooh v2" {
		t.Errorf("after replace = %q want Pooh v2", got2.DisplayName)
	}
}
