// shard_kv_codec_test.go
//
// Verifies ShardKvCodec (ported from ShardKvCodec.cs):
//   - K-path wire bytes match the ground-truth fixture (float32 scale LE +
//     int8 quantised components), the load-bearing cross-language format.
//   - Encode→Decode round-trips K approximately (quantisation is lossy) and V
//     exactly to the selected codeword.
//   - VQ picks the nearest codeword; the returned V equals that codeword.
//   - The V index byte-width follows the codebook size (1/2/4 bytes).
//   - Construction + dim guards reject bad shapes.

package circleai_test

import (
	"bytes"
	"encoding/hex"
	"math"
	"testing"

	circleai "github.com/bhengubv/CircleAI/go"
)

type shardEncodeFixture struct {
	Cases []struct {
		ID          string    `json:"id"`
		KDim        int       `json:"kDim"`
		KRank       int       `json:"kRank"`
		VDim        int       `json:"vDim"`
		VCodewords  int       `json:"vCodewords"`
		K           []float32 `json:"k"`
		Scale       float32   `json:"scale"`
		Projected   []float32 `json:"projected"`
		EncodedKHex string    `json:"encodedK_hex"`
		EncodedVLen int       `json:"encodedV_len"`
	} `json:"cases"`
}

// identityAxes builds the kRank×kDim identity-top-rank axes the ShardKvCodec
// ctor seeds, so the test pins bytes against a known, C#-matching basis.
func identityAxes(kRank, kDim int) [][]float32 {
	axes := make([][]float32, kRank)
	for r := 0; r < kRank; r++ {
		axes[r] = make([]float32, kDim)
		axes[r][r] = 1
	}
	return axes
}

func TestShardKvCodec_EncodeKBytes_Fixture(t *testing.T) {
	var fix shardEncodeFixture
	readLocalFixture(t, "shard_kv_encode.json", &fix)
	if len(fix.Cases) == 0 {
		t.Fatal("no shard cases")
	}
	for _, c := range fix.Cases {
		c := c
		t.Run(c.ID, func(t *testing.T) {
			codec, err := circleai.NewShardKvCodec(c.KDim, c.KRank, c.VDim, c.VCodewords, 0)
			if err != nil {
				t.Fatalf("ctor: %v", err)
			}
			// Axes = identity-top-rank (the ctor already seeds this, but set it
			// explicitly so the intent is clear and independent of ctor state).
			if err := codec.SetPrincipalAxes(identityAxes(c.KRank, c.KDim)); err != nil {
				t.Fatalf("set axes: %v", err)
			}
			v := make([]float32, c.VDim) // all-zero V → deterministic nearest codeword

			frame, err := codec.Encode(c.K, v)
			if err != nil {
				t.Fatalf("encode: %v", err)
			}
			want, _ := hex.DecodeString(c.EncodedKHex)
			if !bytes.Equal(frame.CompressedK, want) {
				t.Errorf("CompressedK bytes mismatch:\n got %x\nwant %x", frame.CompressedK, want)
			}
			if len(frame.CompressedV) != c.EncodedVLen {
				t.Errorf("CompressedV length: got %d want %d", len(frame.CompressedV), c.EncodedVLen)
			}
			if frame.KOriginalDim != c.KDim || frame.VOriginalDim != c.VDim {
				t.Errorf("frame dims: got (%d,%d) want (%d,%d)", frame.KOriginalDim, frame.VOriginalDim, c.KDim, c.VDim)
			}
		})
	}
}

func TestShardKvCodec_RoundTrip_KApprox_VExact(t *testing.T) {
	codec, err := circleai.NewShardKvCodec(8, 8, 4, 16, 0)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	// Full-rank identity axes so K reconstruction is exact up to int8 quantisation.
	codec.SetPrincipalAxes(identityAxes(8, 8))

	// Inject a known codebook so V decode is deterministic and independent of
	// any RNG. Row i is the constant vector (i/10).
	cb := make([][]float32, 16)
	for i := range cb {
		cb[i] = []float32{float32(i) / 10, float32(i) / 10, float32(i) / 10, float32(i) / 10}
	}
	if err := codec.SetVCodebook(cb); err != nil {
		t.Fatalf("set codebook: %v", err)
	}

	k := []float32{0.9, -0.4, 0.2, 0.05, -0.7, 0.33, 0.1, -0.15}
	// V closest to codeword 5 (=0.5 everywhere).
	v := []float32{0.51, 0.49, 0.5, 0.52}

	frame, err := codec.Encode(k, v)
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	gotK, gotV, err := codec.Decode(frame)
	if err != nil {
		t.Fatalf("decode: %v", err)
	}

	// K: lossy — require close reconstruction (int8 over an 8-dim vector).
	for i := range k {
		if d := math.Abs(float64(gotK[i] - k[i])); d > 0.03 {
			t.Errorf("K[%d] reconstruction off: got %v want ~%v (|d|=%v)", i, gotK[i], k[i], d)
		}
	}
	// V: exact codeword copy — codeword 5.
	for i := range gotV {
		if math.Abs(float64(gotV[i]-0.5)) > 1e-6 {
			t.Errorf("V[%d]: got %v want 0.5 (nearest codeword)", i, gotV[i])
		}
	}
}

func TestShardKvCodec_VQ_PicksNearestCodeword(t *testing.T) {
	codec, err := circleai.NewShardKvCodec(4, 4, 3, 4, 0)
	if err != nil {
		t.Fatalf("ctor: %v", err)
	}
	cb := [][]float32{
		{0, 0, 0},
		{1, 1, 1},
		{-1, -1, -1},
		{5, 5, 5},
	}
	codec.SetVCodebook(cb)

	// Query nearest codeword 1 = (1,1,1).
	frame, err := codec.Encode([]float32{0, 0, 0, 0}, []float32{0.9, 1.1, 1.0})
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	if got := int(frame.CompressedV[0]); got != 1 {
		t.Errorf("expected codeword index 1, got %d", got)
	}
}

func TestShardKvCodec_VIndexByteWidth(t *testing.T) {
	// 256 codewords → 1 byte; 512 → 2 bytes. (4-byte path needs >65536 which is
	// impractical to allocate here; the 1/2 boundary exercises the switch.)
	cases := []struct {
		codewords int
		wantBytes int
	}{
		{codewords: 256, wantBytes: 1},
		{codewords: 512, wantBytes: 2},
	}
	for _, c := range cases {
		codec, err := circleai.NewShardKvCodec(4, 4, 2, c.codewords, 0)
		if err != nil {
			t.Fatalf("ctor codewords=%d: %v", c.codewords, err)
		}
		frame, err := codec.Encode([]float32{0, 0, 0, 0}, []float32{0, 0})
		if err != nil {
			t.Fatalf("encode codewords=%d: %v", c.codewords, err)
		}
		if len(frame.CompressedV) != c.wantBytes {
			t.Errorf("codewords=%d: CompressedV len got %d want %d", c.codewords, len(frame.CompressedV), c.wantBytes)
		}
	}
}

func TestShardKvCodec_ObserveK_UpdatesMean(t *testing.T) {
	codec, _ := circleai.NewShardKvCodec(3, 3, 2, 4, 0)
	if codec.SamplesObserved() != 0 {
		t.Fatalf("fresh codec should have 0 samples")
	}
	codec.ObserveK([]float32{2, 4, 6})
	codec.ObserveK([]float32{4, 8, 12})
	if codec.SamplesObserved() != 2 {
		t.Errorf("expected 2 samples, got %d", codec.SamplesObserved())
	}
	// Mean of the two samples is (3,6,9); with a non-zero centre, encoding a
	// vector equal to the mean projects (after Hadamard) to ~0, so the scale
	// floors at ~1e-9 and all int8 components are 0.
	codec.SetPrincipalAxes(identityAxes(3, 3))
	frame, err := codec.Encode([]float32{3, 6, 9}, []float32{0, 0})
	if err != nil {
		t.Fatalf("encode: %v", err)
	}
	for i := 4; i < len(frame.CompressedK); i++ {
		if frame.CompressedK[i] != 0 {
			t.Errorf("component %d should be 0 when input equals mean, got %d", i-4, int8(frame.CompressedK[i]))
		}
	}
}

func TestShardKvCodec_ConstructionGuards(t *testing.T) {
	if _, err := circleai.NewShardKvCodec(0, 1, 4, 4, 0); err == nil {
		t.Error("kDim=0 should error")
	}
	if _, err := circleai.NewShardKvCodec(4, 5, 4, 4, 0); err == nil {
		t.Error("kRank>kDim should error")
	}
	if _, err := circleai.NewShardKvCodec(4, 2, 0, 4, 0); err == nil {
		t.Error("vDim=0 should error")
	}
	if _, err := circleai.NewShardKvCodec(4, 2, 4, 3, 0); err == nil {
		t.Error("non-power-of-two codewords should error")
	}
	if _, err := circleai.NewShardKvCodec(4, 2, 4, 1, 0); err == nil {
		t.Error("codewords=1 should error")
	}
}

func TestShardKvCodec_DimMismatchGuards(t *testing.T) {
	codec, _ := circleai.NewShardKvCodec(4, 2, 3, 4, 0)
	if _, err := codec.Encode([]float32{1, 2, 3}, []float32{0, 0, 0}); err == nil {
		t.Error("wrong K dim should error")
	}
	if _, err := codec.Encode([]float32{1, 2, 3, 4}, []float32{0, 0}); err == nil {
		t.Error("wrong V dim should error")
	}
	if err := codec.ObserveK([]float32{1, 2}); err == nil {
		t.Error("ObserveK wrong dim should error")
	}
	if err := codec.SetPrincipalAxes([][]float32{{1, 0, 0, 0}}); err == nil {
		t.Error("wrong axes shape should error")
	}
	if err := codec.SetVCodebook([][]float32{{0, 0, 0}}); err == nil {
		t.Error("wrong codebook size should error")
	}
}
