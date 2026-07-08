// shard_kv_codec.go
//
// Ports CircleAI.Core.Compression.ShardKvCodec + ShardCompressedFrame
// (ShardKvCodec.cs).
//
// Shard-style KV-cache compression: K is centred → Hadamard-rotated → projected
// onto top-rank PCA axes → int8-quantised with a float32 scale header; V is
// product-vector-quantised to a codeword index. The encoded wire layout matches
// C# byte-for-byte GIVEN the same PCA axes + V codebook:
//   CompressedK = [float32 scale LE][kRank × int8]
//   CompressedV = uint LE, width 1/2/4 bytes by codebook size
//
// Wire-format note: the byte LAYOUT is identical to C#. The DEFAULT seeded V
// codebook is NOT — C# fills it from System.Random(seed), which has no portable
// Go equivalent. So for cross-language byte parity the host MUST inject a known
// codebook via SetVCodebook (and axes via SetPrincipalAxes). With an injected
// codebook the produced CompressedK/CompressedV bytes are exactly C#'s. The Go
// default SeedCodebook is deterministic within Go (its own PRNG) purely so a
// codec is usable out of the box; encode→decode round-trips regardless.

package circleai

import (
	"encoding/binary"
	"errors"
	"fmt"
	"math"
)

// ShardCompressedFrame is an encoded shard KV pair. Ports ShardCompressedFrame.
type ShardCompressedFrame struct {
	CompressedK    []byte
	CompressedV    []byte
	KPrincipalAxes []float32 // flattened kRank×kDim, row-major
	KOriginalDim   int
	VOriginalDim   int
}

// ShardKvCodec is an online-PCA-on-K + VQ-on-V KV compressor. Ports
// CircleAI.Core.Compression.ShardKvCodec. Not safe for concurrent use.
type ShardKvCodec struct {
	kDim       int
	kRank      int
	vDim       int
	vCodewords int
	vCodebook  [][]float32
	hadScratch []float32
	kCenter    []float32
	kAxes      [][]float32 // kRank × kDim
	samples    int64
}

// NewShardKvCodec builds a codec. kDim>0, 0<kRank<=kDim, vDim>0, and vCodewords
// must be a power of two > 1. Ports the ShardKvCodec ctor including the
// identity-top-rank PCA seed.
func NewShardKvCodec(kDim, kRank, vDim, vCodewords, vCodebookSeed int) (*ShardKvCodec, error) {
	if kDim <= 0 {
		return nil, errors.New("kDim out of range")
	}
	if kRank <= 0 || kRank > kDim {
		return nil, errors.New("kRank out of range")
	}
	if vDim <= 0 {
		return nil, errors.New("vDim out of range")
	}
	if vCodewords <= 1 || (vCodewords&(vCodewords-1)) != 0 {
		return nil, errors.New("codeword count must be a power of two greater than 1")
	}
	c := &ShardKvCodec{
		kDim:       kDim,
		kRank:      kRank,
		vDim:       vDim,
		vCodewords: vCodewords,
		kCenter:    make([]float32, kDim),
		hadScratch: make([]float32, pow2Ceil(kDim)),
	}
	c.kAxes = make([][]float32, kRank)
	for r := 0; r < kRank; r++ {
		c.kAxes[r] = make([]float32, kDim)
	}
	c.vCodebook = seedShardCodebook(vDim, vCodewords, vCodebookSeed)

	// Identity-top-rank PCA seed for sane defaults before training.
	for r := 0; r < kRank; r++ {
		c.kAxes[r][r] = 1
	}
	return c, nil
}

// SamplesObserved is the number of K samples folded into the online mean.
func (c *ShardKvCodec) SamplesObserved() int64 { return c.samples }

// ObserveK updates the online K-mean estimate with one sample. Ports ObserveK.
func (c *ShardKvCodec) ObserveK(k []float32) error {
	if len(k) != c.kDim {
		return errors.New("input dim mismatch")
	}
	c.samples++
	for i := 0; i < c.kDim; i++ {
		c.kCenter[i] += (k[i] - c.kCenter[i]) / float32(c.samples)
	}
	return nil
}

// SetPrincipalAxes replaces the PCA axes (kRank × kDim, row-major). Ports
// SetPrincipalAxes.
func (c *ShardKvCodec) SetPrincipalAxes(axes [][]float32) error {
	if len(axes) != c.kRank {
		return errors.New("axes shape must be (kRank, kDim)")
	}
	for r := 0; r < c.kRank; r++ {
		if len(axes[r]) != c.kDim {
			return errors.New("axes shape must be (kRank, kDim)")
		}
		copy(c.kAxes[r], axes[r])
	}
	return nil
}

// SetVCodebook replaces the V codebook. Ports SetVCodebook.
func (c *ShardKvCodec) SetVCodebook(codebook [][]float32) error {
	if len(codebook) != c.vCodewords {
		return errors.New("codebook size mismatch")
	}
	for i := range codebook {
		if len(codebook[i]) != c.vDim {
			return errors.New("codeword dim mismatch")
		}
		copy(c.vCodebook[i], codebook[i])
	}
	return nil
}

// Encode compresses one (K, V) pair. Ports Encode.
func (c *ShardKvCodec) Encode(k, v []float32) (ShardCompressedFrame, error) {
	if len(k) != c.kDim {
		return ShardCompressedFrame{}, errors.New("K dim mismatch")
	}
	if len(v) != c.vDim {
		return ShardCompressedFrame{}, errors.New("V dim mismatch")
	}

	// K: centre → Hadamard → project to top-rank axes → int8 quantise.
	centred := make([]float32, c.kDim)
	for i := 0; i < c.kDim; i++ {
		centred[i] = k[i] - c.kCenter[i]
	}
	c.applyHadamardInPlace(centred)

	projected := make([]float32, c.kRank)
	for r := 0; r < c.kRank; r++ {
		var dot float32
		for i := 0; i < c.kDim; i++ {
			dot += centred[i] * c.kAxes[r][i]
		}
		projected[r] = dot
	}

	maxAbs := float32(1e-9)
	for r := 0; r < c.kRank; r++ {
		if a := float32(math.Abs(float64(projected[r]))); a > maxAbs {
			maxAbs = a
		}
	}
	scale := maxAbs / 127

	encodedK := make([]byte, c.kRank+4) // +4 for the float32 scale header
	binary.LittleEndian.PutUint32(encodedK[0:4], math.Float32bits(scale))
	for r := 0; r < c.kRank; r++ {
		q := int(math.Round(float64(projected[r] / scale)))
		q = clampInt(q, -127, 127)
		encodedK[4+r] = byte(int8(q))
	}

	// V: nearest-codeword VQ.
	bestIdx := 0
	bestDist := float32(math.MaxFloat32)
	for cw := 0; cw < c.vCodewords; cw++ {
		var d float32
		word := c.vCodebook[cw]
		for i := 0; i < c.vDim; i++ {
			diff := v[i] - word[i]
			d += diff * diff
		}
		if d < bestDist {
			bestDist = d
			bestIdx = cw
		}
	}

	idxBytes := codewordIndexBytes(c.vCodewords)
	encodedV := make([]byte, idxBytes)
	switch idxBytes {
	case 1:
		encodedV[0] = byte(bestIdx)
	case 2:
		binary.LittleEndian.PutUint16(encodedV, uint16(bestIdx))
	case 4:
		binary.LittleEndian.PutUint32(encodedV, uint32(bestIdx))
	}

	// Materialise the axes into the frame so the decoder can stand alone.
	axesFlat := make([]float32, c.kRank*c.kDim)
	for r := 0; r < c.kRank; r++ {
		for i := 0; i < c.kDim; i++ {
			axesFlat[r*c.kDim+i] = c.kAxes[r][i]
		}
	}
	return ShardCompressedFrame{
		CompressedK:    encodedK,
		CompressedV:    encodedV,
		KPrincipalAxes: axesFlat,
		KOriginalDim:   c.kDim,
		VOriginalDim:   c.vDim,
	}, nil
}

// Decode reconstructs approximate K and V from a frame. Ports Decode.
func (c *ShardKvCodec) Decode(frame ShardCompressedFrame) (kOut, vOut []float32, err error) {
	if frame.KOriginalDim != c.kDim {
		return nil, nil, errors.New("codec K-dim does not match frame")
	}
	if frame.VOriginalDim != c.vDim {
		return nil, nil, errors.New("codec V-dim does not match frame")
	}
	if len(frame.CompressedK) < 4+c.kRank {
		return nil, nil, fmt.Errorf("compressed K too short: %d", len(frame.CompressedK))
	}
	if len(frame.KPrincipalAxes) < c.kRank*c.kDim {
		return nil, nil, errors.New("frame axes too short")
	}

	// K decode: int8 + scale → projected → un-rotate via axes → un-Hadamard → recentre.
	scale := math.Float32frombits(binary.LittleEndian.Uint32(frame.CompressedK[0:4]))
	projected := make([]float32, c.kRank)
	for r := 0; r < c.kRank; r++ {
		projected[r] = float32(int8(frame.CompressedK[4+r])) * scale
	}

	k := make([]float32, c.kDim)
	for i := 0; i < c.kDim; i++ {
		var acc float32
		for r := 0; r < c.kRank; r++ {
			acc += projected[r] * frame.KPrincipalAxes[r*c.kDim+i]
		}
		k[i] = acc
	}
	c.applyHadamardInPlace(k) // Hadamard is self-inverse up to the 1/n scale.
	for i := 0; i < c.kDim; i++ {
		k[i] = k[i]/float32(c.kDim) + c.kCenter[i]
	}

	// V decode: read index, copy codeword.
	idxBytes := codewordIndexBytes(c.vCodewords)
	if len(frame.CompressedV) < idxBytes {
		return nil, nil, fmt.Errorf("compressed V too short: %d", len(frame.CompressedV))
	}
	var idx int
	switch idxBytes {
	case 1:
		idx = int(frame.CompressedV[0])
	case 2:
		idx = int(binary.LittleEndian.Uint16(frame.CompressedV))
	case 4:
		idx = int(binary.LittleEndian.Uint32(frame.CompressedV))
	}
	v := make([]float32, c.vDim)
	copy(v, c.vCodebook[idx])
	return k, v, nil
}

// applyHadamardInPlace runs the fast Walsh-Hadamard transform on the
// next-power-of-two scratch and copies min(len,n) back. Ports ApplyHadamardInPlace.
func (c *ShardKvCodec) applyHadamardInPlace(buffer []float32) {
	n := len(c.hadScratch)
	for i := range c.hadScratch {
		c.hadScratch[i] = 0
	}
	m := len(buffer)
	if m > n {
		m = n
	}
	copy(c.hadScratch[:m], buffer[:m])

	for h := 1; h < n; h <<= 1 {
		for i := 0; i < n; i += h * 2 {
			for j := i; j < i+h; j++ {
				x := c.hadScratch[j]
				y := c.hadScratch[j+h]
				c.hadScratch[j] = x + y
				c.hadScratch[j+h] = x - y
			}
		}
	}
	copy(buffer[:m], c.hadScratch[:m])
}

func pow2Ceil(v int) int {
	p := 1
	for p < v {
		p <<= 1
	}
	return p
}

func codewordIndexBytes(codewords int) int {
	switch {
	case codewords <= 256:
		return 1
	case codewords <= 65536:
		return 2
	default:
		return 4
	}
}

func clampInt(x, lo, hi int) int {
	if x < lo {
		return lo
	}
	if x > hi {
		return hi
	}
	return x
}

// seedShardCodebook builds a deterministic default V codebook in [-1,1]. It uses
// a local LCG (NOT .NET System.Random) — see the wire-format note above.
func seedShardCodebook(dim, count, seed int) [][]float32 {
	rng := newShardLCG(uint64(uint32(seed)))
	cb := make([][]float32, count)
	for c := 0; c < count; c++ {
		word := make([]float32, dim)
		for i := 0; i < dim; i++ {
			word[i] = float32(rng()*2.0 - 1.0)
		}
		cb[c] = word
	}
	return cb
}

// newShardLCG is a small deterministic [0,1) generator for the default codebook.
func newShardLCG(seed uint64) func() float64 {
	state := seed*2862933555777941757 + 3037000493
	return func() float64 {
		state = state*2862933555777941757 + 3037000493
		// Top 53 bits → [0,1).
		return float64(state>>11) / float64(uint64(1)<<53)
	}
}
