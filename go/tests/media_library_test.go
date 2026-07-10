// media_library_test.go
//
// Verifies the CircleAI.Media port (media_library.go): MediaKind ordinals/names,
// MediaAsset value semantics, and InMemoryMediaLibrary Add/Get/ListByKind/Search
// including ordering, case-insensitive title match, topK cap, and validation.

package circleai_test

import (
	"testing"
	"time"

	circleai "github.com/bhengubv/CircleAI/go"
)

func mediaDur(d time.Duration) *time.Duration { return &d }

func mediaAsset(id, title string, kind circleai.MediaKind, created time.Time) circleai.MediaAsset {
	return circleai.MediaAsset{
		AssetId:      id,
		Title:        title,
		Kind:         kind,
		Bytes:        1000,
		Mime:         "audio/mpeg",
		CreatedAtUtc: created,
	}
}

func TestMediaKind_Ordinals(t *testing.T) {
	if circleai.MediaKindAudio != 0 || circleai.MediaKindVideo != 1 || circleai.MediaKindImage != 2 {
		t.Fatalf("ordinals: audio=%d video=%d image=%d", circleai.MediaKindAudio, circleai.MediaKindVideo, circleai.MediaKindImage)
	}
	if circleai.MediaKindAudio.String() != "Audio" || circleai.MediaKindVideo.String() != "Video" || circleai.MediaKindImage.String() != "Image" {
		t.Fatalf("names: %s/%s/%s", circleai.MediaKindAudio, circleai.MediaKindVideo, circleai.MediaKindImage)
	}
}

func TestMediaAsset_NullableDuration(t *testing.T) {
	img := circleai.MediaAsset{AssetId: "i1", Title: "photo", Kind: circleai.MediaKindImage}
	if img.Duration != nil {
		t.Fatalf("image duration should be nil, got %v", img.Duration)
	}
	d := 3 * time.Minute
	song := circleai.MediaAsset{AssetId: "a1", Title: "song", Kind: circleai.MediaKindAudio, Duration: mediaDur(d)}
	if song.Duration == nil || *song.Duration != d {
		t.Fatalf("audio duration = %v, want %v", song.Duration, d)
	}
}

func TestInMemoryMediaLibrary_AddGet(t *testing.T) {
	lib := circleai.NewInMemoryMediaLibrary()
	now := time.Date(2026, 7, 10, 12, 0, 0, 0, time.UTC)
	if err := lib.Add(mediaAsset("a1", "Track One", circleai.MediaKindAudio, now)); err != nil {
		t.Fatalf("add: %v", err)
	}

	got, ok := lib.Get("a1")
	if !ok || got.Title != "Track One" {
		t.Fatalf("get a1 = %+v ok=%v", got, ok)
	}
	if _, ok := lib.Get("missing"); ok {
		t.Fatalf("missing should not be found")
	}
}

func TestInMemoryMediaLibrary_AddReplacesAndValidates(t *testing.T) {
	lib := circleai.NewInMemoryMediaLibrary()
	now := time.Now().UTC()
	_ = lib.Add(mediaAsset("a1", "Old", circleai.MediaKindAudio, now))
	_ = lib.Add(mediaAsset("a1", "New", circleai.MediaKindAudio, now))
	got, _ := lib.Get("a1")
	if got.Title != "New" {
		t.Fatalf("replace by AssetId failed: %q", got.Title)
	}

	if err := lib.Add(mediaAsset("   ", "Blank", circleai.MediaKindAudio, now)); err == nil {
		t.Fatalf("blank AssetId must error")
	}
}

func TestInMemoryMediaLibrary_ListByKind_NewestFirst(t *testing.T) {
	lib := circleai.NewInMemoryMediaLibrary()
	base := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	_ = lib.Add(mediaAsset("a1", "First", circleai.MediaKindAudio, base))
	_ = lib.Add(mediaAsset("a2", "Second", circleai.MediaKindAudio, base.Add(time.Hour)))
	_ = lib.Add(mediaAsset("a3", "Third", circleai.MediaKindAudio, base.Add(2*time.Hour)))
	_ = lib.Add(mediaAsset("v1", "Video", circleai.MediaKindVideo, base.Add(3*time.Hour)))

	audio := lib.ListByKind(circleai.MediaKindAudio)
	if len(audio) != 3 {
		t.Fatalf("want 3 audio, got %d", len(audio))
	}
	if audio[0].AssetId != "a3" || audio[1].AssetId != "a2" || audio[2].AssetId != "a1" {
		t.Fatalf("order = %s,%s,%s (want a3,a2,a1)", audio[0].AssetId, audio[1].AssetId, audio[2].AssetId)
	}
	if v := lib.ListByKind(circleai.MediaKindVideo); len(v) != 1 || v[0].AssetId != "v1" {
		t.Fatalf("video listing = %+v", v)
	}
	if img := lib.ListByKind(circleai.MediaKindImage); len(img) != 0 {
		t.Fatalf("no images expected, got %d", len(img))
	}
}

func TestInMemoryMediaLibrary_ListByKind_DeterministicTies(t *testing.T) {
	// Equal timestamps must break ties deterministically by AssetId asc.
	same := time.Date(2026, 5, 1, 0, 0, 0, 0, time.UTC)
	for iter := 0; iter < 5; iter++ {
		lib := circleai.NewInMemoryMediaLibrary()
		_ = lib.Add(mediaAsset("c", "C", circleai.MediaKindImage, same))
		_ = lib.Add(mediaAsset("a", "A", circleai.MediaKindImage, same))
		_ = lib.Add(mediaAsset("b", "B", circleai.MediaKindImage, same))
		got := lib.ListByKind(circleai.MediaKindImage)
		if len(got) != 3 || got[0].AssetId != "a" || got[1].AssetId != "b" || got[2].AssetId != "c" {
			t.Fatalf("iter %d tie order = %v", iter, []string{got[0].AssetId, got[1].AssetId, got[2].AssetId})
		}
	}
}

func TestInMemoryMediaLibrary_Search(t *testing.T) {
	lib := circleai.NewInMemoryMediaLibrary()
	base := time.Date(2026, 1, 1, 0, 0, 0, 0, time.UTC)
	_ = lib.Add(mediaAsset("a1", "Jazz Night", circleai.MediaKindAudio, base))
	_ = lib.Add(mediaAsset("a2", "Jazzy Morning", circleai.MediaKindAudio, base.Add(time.Hour)))
	_ = lib.Add(mediaAsset("a3", "Rock Anthem", circleai.MediaKindAudio, base.Add(2*time.Hour)))

	// Case-insensitive substring, newest first.
	hits, err := lib.Search("jazz", 20)
	if err != nil {
		t.Fatalf("search: %v", err)
	}
	if len(hits) != 2 || hits[0].AssetId != "a2" || hits[1].AssetId != "a1" {
		t.Fatalf("search jazz = %+v", hits)
	}

	// topK cap.
	capped, _ := lib.Search("jazz", 1)
	if len(capped) != 1 || capped[0].AssetId != "a2" {
		t.Fatalf("capped = %+v", capped)
	}

	// No match.
	none, _ := lib.Search("classical", 20)
	if len(none) != 0 {
		t.Fatalf("expected no hits, got %d", len(none))
	}

	// Invalid topK.
	if _, err := lib.Search("jazz", 0); err == nil {
		t.Fatalf("topK=0 must error")
	}
}

func TestInMemoryMediaLibrary_InterfaceSatisfied(t *testing.T) {
	var _ circleai.MediaLibrary = circleai.NewInMemoryMediaLibrary()
}
