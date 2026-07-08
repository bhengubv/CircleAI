// memory_compression.go
//
// TurboQuant embedding compression + the compressed store decorators.
//
// Ported EXACTLY from the C# reference so a payload encoded by any language in
// the SDK decodes byte-identically in every other:
//   • CircleAI.Core.Compression.BitPacker
//   • CircleAI.Core.Compression.OrthogonalRotation (+ SeededGaussian)
//   • CircleAI.Core.Compression.BetaLloydMaxCodebook
//   • CircleAI.Core.Compression.TurboQuantCodec (+ TurboQuantPayload)
//   • CircleAI.Memory.Compression.EmbeddingPayloadCodec
//   • CircleAI.Memory.Compression.CompressedEpisodicMemoryStore
//   • CircleAI.Memory.Compression.CompressedMultimodalMemoryStore
//
// TurboQuant is Google Research's data-oblivious vector quantizer
// (arxiv:2504.19874). Per-vector: norm → unit-normalise → fixed orthogonal
// rotation → per-coordinate Lloyd-Max quantise (codebook optimal for the
// Beta((d-1)/2,(d-1)/2) coordinate distribution of a rotated unit vector) →
// bit-pack. Decode reverses it.
//
// Numeric fidelity notes (why this round-trips bit-for-bit with C#):
//   • The SplitMix64 PRNG state is a native uint64 — the +=/* wrap mod 2^64
//     exactly like C# `ulong` (no BigInt shim needed, unlike the TS port).
//   • Every place C# stores a `float` (norm, matrix cells, centroids, deltas)
//     we use Go float32 so the FP32 rounding matches (Go float32 arithmetic is
//     natively single-precision — no Math.fround shim needed, unlike TS).
//   • The wire format writes float32 little-endian via encoding/binary, same as
//     BinaryPrimitives.WriteSingleLittleEndian.

package circleai

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"sort"
	"sync"
	"time"
)

// ---------------------------------------------------------------------------
// BitPacker — CircleAI.Core.Compression.BitPacker
// ---------------------------------------------------------------------------

// BitPack packs indices at bitsPerIndex into a new byte slice. Indices are
// written least-significant-bit first. bitsPerIndex must be 1..16.
func BitPack(indices []uint16, bitsPerIndex int) ([]byte, error) {
	if err := validateBitWidth(bitsPerIndex); err != nil {
		return nil, err
	}
	totalBits := len(indices) * bitsPerIndex
	packed := make([]byte, (totalBits+7)/8)

	bitPos := 0
	for i := 0; i < len(indices); i++ {
		value := uint32(indices[i])
		if bitsPerIndex < 16 && value >= (1<<uint(bitsPerIndex)) {
			return nil, fmt.Errorf("index %d at position %d exceeds %d-bit range", value, i, bitsPerIndex)
		}

		remaining := bitsPerIndex
		byteIdx := bitPos >> 3
		bitOffset := bitPos & 7

		for remaining > 0 {
			take := remaining
			if 8-bitOffset < take {
				take = 8 - bitOffset
			}
			shift := bitsPerIndex - remaining
			chunk := (value >> uint(shift)) & ((1 << uint(take)) - 1)
			packed[byteIdx] |= byte((chunk << uint(bitOffset)) & 0xff)

			remaining -= take
			bitOffset = 0
			byteIdx++
		}
		bitPos += bitsPerIndex
	}
	return packed, nil
}

// BitUnpack unpacks count indices of bitsPerIndex each from packed.
func BitUnpack(packed []byte, count, bitsPerIndex int) ([]uint16, error) {
	if err := validateBitWidth(bitsPerIndex); err != nil {
		return nil, err
	}
	requiredBytes := (count*bitsPerIndex + 7) / 8
	if len(packed) < requiredBytes {
		return nil, fmt.Errorf("packed buffer too small: need %d bytes, got %d", requiredBytes, len(packed))
	}

	result := make([]uint16, count)
	bitPos := 0
	for i := 0; i < count; i++ {
		remaining := bitsPerIndex
		byteIdx := bitPos >> 3
		bitOffset := bitPos & 7
		var value uint32

		for remaining > 0 {
			take := remaining
			if 8-bitOffset < take {
				take = 8 - bitOffset
			}
			shift := bitsPerIndex - remaining
			chunk := (uint32(packed[byteIdx]) >> uint(bitOffset)) & ((1 << uint(take)) - 1)
			value |= chunk << uint(shift)

			remaining -= take
			bitOffset = 0
			byteIdx++
		}
		result[i] = uint16(value)
		bitPos += bitsPerIndex
	}
	return result, nil
}

func validateBitWidth(bitsPerIndex int) error {
	if bitsPerIndex < 1 || bitsPerIndex > 16 {
		return errors.New("bits per index must be 1..16")
	}
	return nil
}

// ---------------------------------------------------------------------------
// SeededGaussian — SplitMix64 + Box-Muller (internal SeededGaussian in C#)
// ---------------------------------------------------------------------------

// seededGaussian is a deterministic Gaussian sampler — Box-Muller over a seeded
// SplitMix64 PRNG. Hand-rolled (not math/rand) so output is reproducible across
// platforms and byte-identical with the C# SeededGaussian.
type seededGaussian struct {
	state    uint64
	hasSpare bool
	spare    float64
}

func newSeededGaussian(seed uint64) *seededGaussian {
	s := seed
	if s == 0 {
		s = 0xDEADBEEFCAFEBABE
	}
	return &seededGaussian{state: s}
}

func (g *seededGaussian) sample() float64 {
	if g.hasSpare {
		g.hasSpare = false
		return g.spare
	}

	// Two uniforms in (0, 1].
	var u, v float64
	for {
		u = g.nextUniform()
		if u > 1e-300 {
			break
		}
	}
	v = g.nextUniform()
	magnitude := math.Sqrt(-2.0 * math.Log(u))
	angle := 2.0 * math.Pi * v
	g.spare = magnitude * math.Sin(angle)
	g.hasSpare = true
	return magnitude * math.Cos(angle)
}

func (g *seededGaussian) nextUniform() float64 {
	// SplitMix64 step. Native uint64 wraps mod 2^64, matching C# ulong.
	g.state += 0x9E3779B97F4A7C15
	z := g.state
	z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9
	z = (z ^ (z >> 27)) * 0x94D049BB133111EB
	z = z ^ (z >> 31)
	// Convert top 53 bits to a double in [0, 1).
	return float64(z>>11) * (1.0 / float64(uint64(1)<<53))
}

// ---------------------------------------------------------------------------
// OrthogonalRotation — CircleAI.Core.Compression.OrthogonalRotation
// ---------------------------------------------------------------------------

// rotationSeed is the fixed seed shared across every CircleAI process so the
// rotation is portable: compress on device A, decode on device B works
// identically.
const rotationSeed uint64 = 0xC1C1EA10C1C1EA10

var (
	rotationCache   = make(map[int][]float32)
	rotationCacheMu sync.Mutex
)

// getRotationMatrix returns the dim×dim orthogonal matrix in row-major layout
// (length dim*dim). Cached after the first call for a given dimension.
func getRotationMatrix(dim int) ([]float32, error) {
	if dim <= 0 {
		return nil, errors.New("dim must be positive")
	}
	rotationCacheMu.Lock()
	defer rotationCacheMu.Unlock()
	if m, ok := rotationCache[dim]; ok {
		return m, nil
	}
	m, err := buildRotationMatrix(dim)
	if err != nil {
		return nil, err
	}
	rotationCache[dim] = m
	return m, nil
}

// rotate computes output[i] = Σ R[i,j] * vector[j]. sum is float32 so each
// multiply-add is single-precision, matching C# `float sum`.
func rotate(dim int, vector []float32, output []float32) error {
	if len(vector) != dim {
		return errors.New("vector length must equal dim")
	}
	if len(output) != dim {
		return errors.New("output length must equal dim")
	}
	matrix, err := getRotationMatrix(dim)
	if err != nil {
		return err
	}
	for i := 0; i < dim; i++ {
		var sum float32
		rowStart := i * dim
		for j := 0; j < dim; j++ {
			sum += matrix[rowStart+j] * vector[j]
		}
		output[i] = sum
	}
	return nil
}

// unrotate multiplies the TRANSPOSE of the rotation matrix by vector. The
// transpose of an orthogonal matrix is its inverse.
func unrotate(dim int, vector []float32, output []float32) error {
	if len(vector) != dim {
		return errors.New("vector length must equal dim")
	}
	if len(output) != dim {
		return errors.New("output length must equal dim")
	}
	matrix, err := getRotationMatrix(dim)
	if err != nil {
		return err
	}
	for i := 0; i < dim; i++ {
		var sum float32
		for j := 0; j < dim; j++ {
			// Transpose: matrix[j, i] instead of matrix[i, j].
			sum += matrix[j*dim+i] * vector[j]
		}
		output[i] = sum
	}
	return nil
}

func buildRotationMatrix(dim int) ([]float32, error) {
	// 1. Generate a seeded Gaussian matrix G (dim × dim).
	gauss := make([]float64, dim*dim)
	rng := newSeededGaussian(rotationSeed)
	for i := range gauss {
		gauss[i] = rng.sample()
	}

	// 2. QR decomposition via modified Gram-Schmidt.
	q, err := modifiedGramSchmidt(gauss, dim)
	if err != nil {
		return nil, err
	}

	// 3. Sign-correct columns so Q is deterministic.
	signCorrectColumns(q, dim)

	// 4. Convert to row-major float32.
	result := make([]float32, dim*dim)
	for i := range result {
		result[i] = float32(q[i])
	}
	return result, nil
}

// modifiedGramSchmidt returns Q (orthonormal columns) in row-major flat layout.
func modifiedGramSchmidt(g []float64, dim int) ([]float64, error) {
	q := make([]float64, dim*dim)

	for j := 0; j < dim; j++ {
		// Copy column j of g into a working vector.
		for i := 0; i < dim; i++ {
			q[i*dim+j] = g[i*dim+j]
		}

		// Subtract projections onto already-processed columns.
		for k := 0; k < j; k++ {
			var dot float64
			for i := 0; i < dim; i++ {
				dot += q[i*dim+j] * q[i*dim+k]
			}
			for i := 0; i < dim; i++ {
				q[i*dim+j] -= dot * q[i*dim+k]
			}
		}

		// Normalise column j.
		var norm float64
		for i := 0; i < dim; i++ {
			norm += q[i*dim+j] * q[i*dim+j]
		}
		norm = math.Sqrt(norm)
		if norm < 1e-15 {
			return nil, fmt.Errorf("Gram-Schmidt produced a near-zero column at j=%d (dim=%d)", j, dim)
		}
		inv := 1.0 / norm
		for i := 0; i < dim; i++ {
			q[i*dim+j] *= inv
		}
	}
	return q, nil
}

func signCorrectColumns(q []float64, dim int) {
	for j := 0; j < dim; j++ {
		// Diagonal-based sign convention: ensure q[j,j] >= 0.
		if q[j*dim+j] < 0.0 {
			for i := 0; i < dim; i++ {
				q[i*dim+j] = -q[i*dim+j]
			}
		}
	}
}

// ---------------------------------------------------------------------------
// BetaLloydMaxCodebook — CircleAI.Core.Compression.BetaLloydMaxCodebook
// ---------------------------------------------------------------------------

// betaCodebook is a Lloyd-Max codebook for Beta((d-1)/2,(d-1)/2) on [-1, 1].
// boundaries has length 2^bits-1; centroids has length 2^bits.
type betaCodebook struct {
	boundaries []float32
	centroids  []float32
}

var (
	codebookCache   = make(map[[2]int]betaCodebook)
	codebookCacheMu sync.Mutex
)

// getCodebook returns the codebook for the given bit width and dimension,
// computing it on first request. Cached by (bits, dim).
func getCodebook(bits, dim int) (betaCodebook, error) {
	if bits < 1 || bits > 8 {
		return betaCodebook{}, errors.New("bits must be in 1..8")
	}
	if dim <= 1 {
		return betaCodebook{}, errors.New("dim must be > 1")
	}
	key := [2]int{bits, dim}
	codebookCacheMu.Lock()
	defer codebookCacheMu.Unlock()
	if cb, ok := codebookCache[key]; ok {
		return cb, nil
	}
	cb := computeCodebook(bits, dim, 200, 1e-12)
	codebookCache[key] = cb
	return cb, nil
}

// binFor returns the bin index for value against boundaries (linear scan).
func binFor(value float32, boundaries []float32) uint16 {
	for i := 0; i < len(boundaries); i++ {
		if value < boundaries[i] {
			return uint16(i)
		}
	}
	return uint16(len(boundaries))
}

func computeCodebook(bits, dim, maxIter int, tol float64) betaCodebook {
	a := (float64(dim) - 1.0) / 2.0
	nLevels := 1 << uint(bits)

	// Initial centroids: evenly spaced across ±3σ of the Beta-on-[-1,1].
	std := math.Sqrt((2.0 * a) / ((2.0*a + 1.0) * 4.0 * a))
	spread := 3.0 * std
	centroids := make([]float64, nLevels)
	for i := 0; i < nLevels; i++ {
		centroids[i] = -spread + (2.0*spread*float64(i))/float64(nLevels-1)
	}

	for iter := 0; iter < maxIter; iter++ {
		// Boundaries = midpoints between adjacent centroids.
		boundaries := make([]float64, nLevels-1)
		for i := 0; i < nLevels-1; i++ {
			boundaries[i] = (centroids[i] + centroids[i+1]) / 2.0
		}

		edges := make([]float64, nLevels+1)
		edges[0] = -1.0
		for i := 0; i < len(boundaries); i++ {
			edges[i+1] = boundaries[i]
		}
		edges[nLevels] = 1.0

		newCentroids := make([]float64, nLevels)
		for i := 0; i < nLevels; i++ {
			lo := edges[i]
			hi := edges[i+1]
			cdfLo := betaCdfSymmetric(a, (lo+1.0)/2.0)
			cdfHi := betaCdfSymmetric(a, (hi+1.0)/2.0)
			prob := cdfHi - cdfLo

			if prob < 1e-15 {
				newCentroids[i] = centroids[i]
			} else {
				mean := adaptiveSimpson(
					func(x float64) float64 {
						return x * betaPdfSymmetric(a, (x+1.0)/2.0) / 2.0
					},
					lo, hi, 1e-14, 50,
				)
				newCentroids[i] = mean / prob
			}
		}

		maxChange := 0.0
		for i := 0; i < nLevels; i++ {
			if c := math.Abs(centroids[i] - newCentroids[i]); c > maxChange {
				maxChange = c
			}
		}
		centroids = newCentroids

		if maxChange < tol {
			break
		}
	}

	finalBoundaries := make([]float32, nLevels-1)
	for i := 0; i < nLevels-1; i++ {
		finalBoundaries[i] = float32((centroids[i] + centroids[i+1]) / 2.0)
	}
	finalCentroids := make([]float32, nLevels)
	for i := 0; i < nLevels; i++ {
		finalCentroids[i] = float32(centroids[i])
	}
	return betaCodebook{boundaries: finalBoundaries, centroids: finalCentroids}
}

// ── Beta(a, a) PDF / CDF on [0, 1] ──────────────────────────────────────────
// The "Symmetric" suffix is a reminder that we always use shape Beta(a, a).

func betaPdfSymmetric(a, x float64) float64 {
	if x <= 0.0 || x >= 1.0 {
		return 0.0
	}
	// f(x) = x^(a-1) * (1-x)^(a-1) / B(a, a); log-space for stability at large a.
	logPdf := (a-1.0)*math.Log(x) + (a-1.0)*math.Log(1.0-x) - logBeta(a, a)
	return math.Exp(logPdf)
}

func betaCdfSymmetric(a, x float64) float64 {
	if x <= 0.0 {
		return 0.0
	}
	if x >= 1.0 {
		return 1.0
	}
	return regularizedIncompleteBeta(a, a, x)
}

func logBeta(a, b float64) float64 {
	return logGamma(a) + logGamma(b) - logGamma(a+b)
}

// lanczosG7 holds the Lanczos coefficients for g = 7.
var lanczosG7 = [9]float64{
	0.99999999999980993, 676.5203681218851, -1259.1392167224028,
	771.32342877765313, -176.61502916214059, 12.507343278686905,
	-0.13857109526572012, 9.9843695780195716e-6, 1.5056327351493116e-7,
}

// logGamma returns log Γ(x) for x > 0 via the Lanczos approximation (g=7, n=9).
func logGamma(x float64) float64 {
	if x < 0.5 {
		// Reflection: Γ(x)Γ(1-x) = π/sin(πx).
		return math.Log(math.Pi/math.Sin(math.Pi*x)) - logGamma(1.0-x)
	}
	x -= 1.0
	t := x + 7.5
	sum := lanczosG7[0]
	for i := 1; i < len(lanczosG7); i++ {
		sum += lanczosG7[i] / (x + float64(i))
	}
	return 0.5*math.Log(2.0*math.Pi) + (x+0.5)*math.Log(t) - t + math.Log(sum)
}

// regularizedIncompleteBeta returns I_x(a, b) (Numerical Recipes 6.4).
func regularizedIncompleteBeta(a, b, x float64) float64 {
	if x < 0.0 || x > 1.0 {
		panic("x must be in [0, 1]")
	}
	if x == 0.0 || x == 1.0 {
		return x
	}

	bt := math.Exp(
		logGamma(a+b) - logGamma(a) - logGamma(b) +
			a*math.Log(x) + b*math.Log(1.0-x),
	)
	if x < (a+1.0)/(a+b+2.0) {
		return bt * betaContinuedFraction(a, b, x) / a
	}
	return 1.0 - bt*betaContinuedFraction(b, a, 1.0-x)/b
}

func betaContinuedFraction(a, b, x float64) float64 {
	const maxIter = 200
	const eps = 3e-15
	const fpmin = 1e-300

	qab := a + b
	qap := a + 1.0
	qam := a - 1.0
	c := 1.0
	d := 1.0 - qab*x/qap
	if math.Abs(d) < fpmin {
		d = fpmin
	}
	d = 1.0 / d
	h := d

	for m := 1; m <= maxIter; m++ {
		m2 := float64(2 * m)
		fm := float64(m)
		aa := fm * (b - fm) * x / ((qam + m2) * (a + m2))
		d = 1.0 + aa*d
		if math.Abs(d) < fpmin {
			d = fpmin
		}
		c = 1.0 + aa/c
		if math.Abs(c) < fpmin {
			c = fpmin
		}
		d = 1.0 / d
		h *= d * c

		aa = -(a + fm) * (qab + fm) * x / ((a + m2) * (qap + m2))
		d = 1.0 + aa*d
		if math.Abs(d) < fpmin {
			d = fpmin
		}
		c = 1.0 + aa/c
		if math.Abs(c) < fpmin {
			c = fpmin
		}
		d = 1.0 / d
		delta := d * c
		h *= delta
		if math.Abs(delta-1.0) < eps {
			return h
		}
	}
	return h // best effort if no convergence
}

// ── Adaptive Simpson integration ────────────────────────────────────────────

func adaptiveSimpson(f func(float64) float64, a, b, tol float64, maxDepth int) float64 {
	mid := (a + b) / 2.0
	fa := f(a)
	fb := f(b)
	fm := f(mid)
	whole := (b - a) / 6.0 * (fa + 4.0*fm + fb)
	return adaptiveSimpsonRec(f, a, b, fa, fb, fm, whole, tol, maxDepth)
}

func adaptiveSimpsonRec(f func(float64) float64, a, b, fa, fb, fm, whole, tol float64, depth int) float64 {
	mid := (a + b) / 2.0
	m1 := (a + mid) / 2.0
	m2 := (mid + b) / 2.0
	fm1 := f(m1)
	fm2 := f(m2)
	left := (mid - a) / 6.0 * (fa + 4.0*fm1 + fm)
	right := (b - mid) / 6.0 * (fm + 4.0*fm2 + fb)
	refined := left + right

	if depth == 0 || math.Abs(refined-whole) < 15.0*tol {
		return refined + (refined-whole)/15.0
	}
	return adaptiveSimpsonRec(f, a, mid, fa, fm, fm1, left, tol/2.0, depth-1) +
		adaptiveSimpsonRec(f, mid, b, fm, fb, fm2, right, tol/2.0, depth-1)
}

// ---------------------------------------------------------------------------
// TurboQuantCodec — CircleAI.Core.Compression.TurboQuantCodec
// ---------------------------------------------------------------------------

// TurboQuantPayload is the output of TurboQuantEncode.
//   - Norm: L2 norm of the original vector — needed to reconstruct magnitude.
//   - PackedIndices: bit-packed Lloyd-Max bin indices, one per dimension.
type TurboQuantPayload struct {
	Norm          float32
	PackedIndices []byte
}

// TurboQuantEncode encodes a float vector at bitsPerDim bits per dimension.
// Higher bits = better fidelity, larger payload. Typical: 2 bits (16×),
// 3 bits (~10×).
func TurboQuantEncode(vector []float32, bitsPerDim int) (TurboQuantPayload, error) {
	if len(vector) <= 1 {
		return TurboQuantPayload{}, errors.New("vector must have length > 1")
	}
	if bitsPerDim < 1 || bitsPerDim > 8 {
		return TurboQuantPayload{}, errors.New("bitsPerDim must be 1..8")
	}

	dim := len(vector)

	// 1. Norm — accumulate in float64 like C# `(double)vector[i] * vector[i]`.
	var sumSq float64
	for i := 0; i < dim; i++ {
		sumSq += float64(vector[i]) * float64(vector[i])
	}
	norm := float32(math.Sqrt(sumSq))

	// Edge case — zero vector. Round-trip preserves the all-zero shape.
	if norm < 1e-20 {
		allZeros := make([]byte, (dim*bitsPerDim+7)/8)
		return TurboQuantPayload{Norm: 0, PackedIndices: allZeros}, nil
	}

	// 2. Unit-normalise (float32 arithmetic, matching C#).
	unit := make([]float32, dim)
	invNorm := float32(1) / norm
	for i := 0; i < dim; i++ {
		unit[i] = vector[i] * invNorm
	}

	// 3. Rotate.
	rotated := make([]float32, dim)
	if err := rotate(dim, unit, rotated); err != nil {
		return TurboQuantPayload{}, err
	}

	// 4. Quantize per-coordinate.
	codebook, err := getCodebook(bitsPerDim, dim)
	if err != nil {
		return TurboQuantPayload{}, err
	}
	indices := make([]uint16, dim)
	for i := 0; i < dim; i++ {
		indices[i] = binFor(rotated[i], codebook.boundaries)
	}

	// 5. Pack.
	packed, err := BitPack(indices, bitsPerDim)
	if err != nil {
		return TurboQuantPayload{}, err
	}
	return TurboQuantPayload{Norm: norm, PackedIndices: packed}, nil
}

// TurboQuantDecode decodes a TurboQuant payload back into the original-magnitude
// vector (modulo quantization error).
func TurboQuantDecode(payload TurboQuantPayload, dim, bitsPerDim int) ([]float32, error) {
	if dim <= 1 {
		return nil, errors.New("dim must be > 1")
	}
	if bitsPerDim < 1 || bitsPerDim > 8 {
		return nil, errors.New("bitsPerDim must be 1..8")
	}

	result := make([]float32, dim)
	if payload.Norm == 0 {
		return result, nil // all zeros
	}

	// 1. Unpack indices.
	indices, err := BitUnpack(payload.PackedIndices, dim, bitsPerDim)
	if err != nil {
		return nil, err
	}

	// 2. Map indices → centroids (rotated-space reconstruction).
	rotated := make([]float32, dim)
	codebook, err := getCodebook(bitsPerDim, dim)
	if err != nil {
		return nil, err
	}
	for i := 0; i < dim; i++ {
		rotated[i] = codebook.centroids[indices[i]]
	}

	// 3. Inverse rotation.
	unit := make([]float32, dim)
	if err := unrotate(dim, rotated, unit); err != nil {
		return nil, err
	}

	// 4. Scale by stored norm.
	scale := payload.Norm
	for i := 0; i < dim; i++ {
		result[i] = unit[i] * scale
	}
	return result, nil
}

// TurboQuantRoundTrip encodes then decodes, returning the reconstruction.
func TurboQuantRoundTrip(vector []float32, bitsPerDim int) ([]float32, error) {
	encoded, err := TurboQuantEncode(vector, bitsPerDim)
	if err != nil {
		return nil, err
	}
	return TurboQuantDecode(encoded, len(vector), bitsPerDim)
}

// TurboQuantPayloadByteCount returns the bytes-per-vector required at the given
// dim and bitsPerDim (excluding the 4-byte norm header).
func TurboQuantPayloadByteCount(dim, bitsPerDim int) int {
	return (dim*bitsPerDim + 7) / 8
}

// TurboQuantCompressionRatio returns the compression ratio vs raw FP32 (vector
// bytes / encoded bytes incl. norm).
func TurboQuantCompressionRatio(dim, bitsPerDim int) float64 {
	raw := dim * 4
	encoded := TurboQuantPayloadByteCount(dim, bitsPerDim) + 4 // norm
	return float64(raw) / float64(encoded)
}

// ---------------------------------------------------------------------------
// EmbeddingPayloadCodec — CircleAI.Memory.Compression.EmbeddingPayloadCodec
// ---------------------------------------------------------------------------
//
// Wire format (binary):
//   bytes [0..3]   = magic "TQ3\1" (0x54 0x51 0x33 0x01)
//   bytes [4..7]   = bit-width as uint32 little-endian
//   bytes [8..11]  = dimension as uint32 little-endian
//   bytes [12..15] = norm as float32 little-endian
//   bytes [16..]   = packed indices
// Base64-encoded for tag storage. Bit-width + dim are embedded so callers can
// decode without out-of-band metadata.

// embeddingMagic holds the magic header bytes that identify a TurboQuant blob.
var embeddingMagic = []byte{0x54, 0x51, 0x33, 0x01} // "TQ3\1"

// EmbeddingMagic returns a copy of the magic header bytes.
func EmbeddingMagic() []byte {
	out := make([]byte, len(embeddingMagic))
	copy(out, embeddingMagic)
	return out
}

// EmbeddingEncode encodes vector at bitsPerDim bits per coordinate into a
// self-describing byte payload.
func EmbeddingEncode(vector []float32, bitsPerDim int) ([]byte, error) {
	if len(vector) <= 1 {
		return nil, errors.New("vector must have length > 1")
	}

	payload, err := TurboQuantEncode(vector, bitsPerDim)
	if err != nil {
		return nil, err
	}
	buf := make([]byte, len(embeddingMagic)+4+4+4+len(payload.PackedIndices))
	o := 0
	copy(buf[o:], embeddingMagic)
	o += len(embeddingMagic)
	binary.LittleEndian.PutUint32(buf[o:], uint32(bitsPerDim))
	o += 4
	binary.LittleEndian.PutUint32(buf[o:], uint32(len(vector)))
	o += 4
	binary.LittleEndian.PutUint32(buf[o:], math.Float32bits(payload.Norm))
	o += 4
	copy(buf[o:], payload.PackedIndices)
	return buf, nil
}

// EmbeddingDecode decodes a byte payload produced by EmbeddingEncode back into a
// float slice.
func EmbeddingDecode(bytes []byte) ([]float32, error) {
	if len(bytes) < len(embeddingMagic)+12 {
		return nil, errors.New("payload too short")
	}
	if !hasMagic(bytes) {
		return nil, errors.New("magic header missing — not a TurboQuant payload")
	}

	o := len(embeddingMagic)
	bitsPerDim := int(binary.LittleEndian.Uint32(bytes[o:]))
	o += 4
	dim := int(binary.LittleEndian.Uint32(bytes[o:]))
	o += 4
	norm := math.Float32frombits(binary.LittleEndian.Uint32(bytes[o:]))
	o += 4
	packed := make([]byte, len(bytes)-o)
	copy(packed, bytes[o:])
	return TurboQuantDecode(TurboQuantPayload{Norm: norm, PackedIndices: packed}, dim, bitsPerDim)
}

// EmbeddingIsEncoded reports whether the byte span begins with the TurboQuant
// magic header.
func EmbeddingIsEncoded(bytes []byte) bool {
	return len(bytes) >= len(embeddingMagic) && hasMagic(bytes)
}

// EmbeddingEncodeBase64 encodes + base64-stringifies for tag-style storage.
func EmbeddingEncodeBase64(vector []float32, bitsPerDim int) (string, error) {
	b, err := EmbeddingEncode(vector, bitsPerDim)
	if err != nil {
		return "", err
	}
	return base64.StdEncoding.EncodeToString(b), nil
}

// EmbeddingDecodeBase64 base64-decodes + decodes.
func EmbeddingDecodeBase64(b64 string) ([]float32, error) {
	raw, err := base64.StdEncoding.DecodeString(b64)
	if err != nil {
		return nil, err
	}
	return EmbeddingDecode(raw)
}

func hasMagic(bytes []byte) bool {
	return bytes[0] == embeddingMagic[0] &&
		bytes[1] == embeddingMagic[1] &&
		bytes[2] == embeddingMagic[2] &&
		bytes[3] == embeddingMagic[3]
}

// CompressedTagKey is the tag key under which the compressed embedding is
// stored.
const CompressedTagKey = "x-tq-embedding"

// ---------------------------------------------------------------------------
// CompressedEpisodicMemoryStore — CircleAI.Memory.Compression
// ---------------------------------------------------------------------------

// CompressedEpisodicMemoryStore wraps any IEpisodicMemoryStore and stores its
// embeddings in TurboQuant-compressed form. Default 2 bits per dim (~16×
// shrink).
//
// The inner store sees Embedding = nil; the compressed base64 payload lives in
// the entry's tags under CompressedTagKey. Reads rehydrate the embedding by
// decoding the tag, and Search rebuilds embeddings on the read path so cosine
// ranking works against the reconstructed vectors.
type CompressedEpisodicMemoryStore struct {
	inner      IEpisodicMemoryStore
	bitsPerDim int
}

// NewCompressedEpisodicMemoryStore wraps inner, compressing embeddings at
// bitsPerDim (1..8).
func NewCompressedEpisodicMemoryStore(inner IEpisodicMemoryStore, bitsPerDim int) (*CompressedEpisodicMemoryStore, error) {
	if inner == nil {
		return nil, errors.New("inner required")
	}
	if bitsPerDim < 1 || bitsPerDim > 8 {
		return nil, errors.New("bitsPerDim must be 1..8")
	}
	return &CompressedEpisodicMemoryStore{inner: inner, bitsPerDim: bitsPerDim}, nil
}

// Add compresses the entry's embedding into a tag and drops the float array,
// then delegates to the inner store. Entries with a length ≤ 1 embedding pass
// through unchanged.
func (s *CompressedEpisodicMemoryStore) Add(ctx context.Context, entry EpisodicMemoryEntry) error {
	if len(entry.Embedding) > 1 {
		tags, err := s.copyTagsWithCompressed(entry.Tags, entry.Embedding)
		if err != nil {
			return err
		}
		rewritten := EpisodicMemoryEntry{
			ID:            entry.ID,
			RecordedAtUTC: entry.RecordedAtUTC,
			UserText:      entry.UserText,
			AssistantText: entry.AssistantText,
			AppContext:    entry.AppContext,
			Embedding:     nil, // dropped — lives in tags
			Tags:          tags,
		}
		return s.inner.Add(ctx, rewritten)
	}
	return s.inner.Add(ctx, entry)
}

// Search loads recent entries from the inner store, rehydrates their embeddings,
// then ranks by cosine here (the inner store only sees nil embeddings). A nil
// query returns recency.
func (s *CompressedEpisodicMemoryStore) Search(ctx context.Context, queryEmbedding []float32, topK int) ([]EpisodicMemoryEntry, error) {
	all, err := s.inner.GetRecent(ctx, maxInt)
	if err != nil {
		return nil, err
	}
	rehydrated := make([]EpisodicMemoryEntry, len(all))
	for i, e := range all {
		rehydrated[i] = rehydrateEpisodic(e)
	}

	if queryEmbedding == nil {
		return takeEntries(rehydrated, topK), nil
	}

	type scored struct {
		entry EpisodicMemoryEntry
		score float64
	}
	var candidates []scored
	for _, e := range rehydrated {
		if len(e.Embedding) > 0 {
			candidates = append(candidates, scored{entry: e, score: cosineScore(queryEmbedding, e.Embedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool {
		return candidates[i].score > candidates[j].score
	})
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	if limit < 0 {
		limit = 0
	}
	out := make([]EpisodicMemoryEntry, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].entry)
	}
	return out, nil
}

// GetRecent returns the most recent count entries with embeddings rehydrated.
func (s *CompressedEpisodicMemoryStore) GetRecent(ctx context.Context, count int) ([]EpisodicMemoryEntry, error) {
	recent, err := s.inner.GetRecent(ctx, count)
	if err != nil {
		return nil, err
	}
	out := make([]EpisodicMemoryEntry, len(recent))
	for i, e := range recent {
		out[i] = rehydrateEpisodic(e)
	}
	return out, nil
}

// Count delegates to the inner store.
func (s *CompressedEpisodicMemoryStore) Count(ctx context.Context) (int, error) {
	return s.inner.Count(ctx)
}

// PruneOlderThan delegates to the inner store.
func (s *CompressedEpisodicMemoryStore) PruneOlderThan(ctx context.Context, cutoff time.Time) (int, error) {
	return s.inner.PruneOlderThan(ctx, cutoff)
}

func (s *CompressedEpisodicMemoryStore) copyTagsWithCompressed(src map[string]string, embedding []float32) (map[string]string, error) {
	dict := make(map[string]string, len(src)+1)
	for k, v := range src {
		dict[k] = v
	}
	b64, err := EmbeddingEncodeBase64(embedding, s.bitsPerDim)
	if err != nil {
		return nil, err
	}
	dict[CompressedTagKey] = b64
	return dict, nil
}

func rehydrateEpisodic(e EpisodicMemoryEntry) EpisodicMemoryEntry {
	if len(e.Embedding) > 0 {
		return e // never compressed
	}
	if e.Tags == nil {
		return e
	}
	b64, ok := e.Tags[CompressedTagKey]
	if !ok {
		return e
	}
	floats, err := EmbeddingDecodeBase64(b64)
	if err != nil {
		// Malformed tag — return entry as-is so the caller can still see it.
		return e
	}
	return EpisodicMemoryEntry{
		ID:            e.ID,
		RecordedAtUTC: e.RecordedAtUTC,
		UserText:      e.UserText,
		AssistantText: e.AssistantText,
		AppContext:    e.AppContext,
		Embedding:     floats,
		Tags:          e.Tags,
	}
}

// Compile-time assertion that the decorator satisfies the interface.
var _ IEpisodicMemoryStore = (*CompressedEpisodicMemoryStore)(nil)

// ---------------------------------------------------------------------------
// CompressedMultimodalMemoryStore — CircleAI.Memory.Compression
// ---------------------------------------------------------------------------

// CompressedMultimodalMemoryStore wraps any IMultimodalMemoryStore and stores
// its embeddings in TurboQuant-compressed form. Same wire format + tag key as
// the episodic decorator.
type CompressedMultimodalMemoryStore struct {
	inner      IMultimodalMemoryStore
	bitsPerDim int
}

// NewCompressedMultimodalMemoryStore wraps inner, compressing embeddings at
// bitsPerDim (1..8).
func NewCompressedMultimodalMemoryStore(inner IMultimodalMemoryStore, bitsPerDim int) (*CompressedMultimodalMemoryStore, error) {
	if inner == nil {
		return nil, errors.New("inner required")
	}
	if bitsPerDim < 1 || bitsPerDim > 8 {
		return nil, errors.New("bitsPerDim must be 1..8")
	}
	return &CompressedMultimodalMemoryStore{inner: inner, bitsPerDim: bitsPerDim}, nil
}

// Add compresses the entry's embedding into a tag and drops the float array,
// then delegates to the inner store. Entries with a length ≤ 1 embedding pass
// through unchanged.
func (s *CompressedMultimodalMemoryStore) Add(ctx context.Context, entry MultimodalMemoryEntry) error {
	if len(entry.Embedding) > 1 {
		rewritten, err := s.compress(entry)
		if err != nil {
			return err
		}
		return s.inner.Add(ctx, rewritten)
	}
	return s.inner.Add(ctx, entry)
}

// GetByHash returns the entry with the given hash (embedding rehydrated), or nil.
func (s *CompressedMultimodalMemoryStore) GetByHash(ctx context.Context, sourceSha256 string) (*MultimodalMemoryEntry, error) {
	got, err := s.inner.GetByHash(ctx, sourceSha256)
	if err != nil {
		return nil, err
	}
	if got == nil {
		return nil, nil
	}
	r := rehydrateMultimodal(*got)
	return &r, nil
}

// Reinforce delegates to the inner store.
func (s *CompressedMultimodalMemoryStore) Reinforce(ctx context.Context, sourceSha256 string) error {
	return s.inner.Reinforce(ctx, sourceSha256)
}

// Search loads recent entries, rehydrates, then ranks by cosine here. A nil
// query returns recency.
func (s *CompressedMultimodalMemoryStore) Search(ctx context.Context, queryEmbedding []float32, topK int) ([]MultimodalMemoryEntry, error) {
	all, err := s.inner.GetRecent(ctx, maxInt)
	if err != nil {
		return nil, err
	}
	rehydrated := make([]MultimodalMemoryEntry, len(all))
	for i, e := range all {
		rehydrated[i] = rehydrateMultimodal(e)
	}
	if queryEmbedding == nil {
		return takeMultimodal(rehydrated, topK), nil
	}

	type scored struct {
		entry MultimodalMemoryEntry
		score float64
	}
	var candidates []scored
	for _, e := range rehydrated {
		if len(e.Embedding) > 0 {
			candidates = append(candidates, scored{entry: e, score: cosineScore(queryEmbedding, e.Embedding)})
		}
	}
	sort.SliceStable(candidates, func(i, j int) bool {
		return candidates[i].score > candidates[j].score
	})
	limit := topK
	if limit > len(candidates) {
		limit = len(candidates)
	}
	if limit < 0 {
		limit = 0
	}
	out := make([]MultimodalMemoryEntry, 0, limit)
	for i := 0; i < limit; i++ {
		out = append(out, candidates[i].entry)
	}
	return out, nil
}

// GetRecent returns the most recent count entries with embeddings rehydrated.
func (s *CompressedMultimodalMemoryStore) GetRecent(ctx context.Context, count int) ([]MultimodalMemoryEntry, error) {
	recent, err := s.inner.GetRecent(ctx, count)
	if err != nil {
		return nil, err
	}
	out := make([]MultimodalMemoryEntry, len(recent))
	for i, e := range recent {
		out[i] = rehydrateMultimodal(e)
	}
	return out, nil
}

// PruneOlderThan delegates to the inner store.
func (s *CompressedMultimodalMemoryStore) PruneOlderThan(ctx context.Context, cutoff time.Time) (int, error) {
	return s.inner.PruneOlderThan(ctx, cutoff)
}

// Count delegates to the inner store.
func (s *CompressedMultimodalMemoryStore) Count(ctx context.Context) (int, error) {
	return s.inner.Count(ctx)
}

func (s *CompressedMultimodalMemoryStore) compress(entry MultimodalMemoryEntry) (MultimodalMemoryEntry, error) {
	tags := make(map[string]string, len(entry.Tags)+1)
	for k, v := range entry.Tags {
		tags[k] = v
	}
	b64, err := EmbeddingEncodeBase64(entry.Embedding, s.bitsPerDim)
	if err != nil {
		return MultimodalMemoryEntry{}, err
	}
	tags[CompressedTagKey] = b64

	return MultimodalMemoryEntry{
		ID:              entry.ID,
		RecordedAtUTC:   entry.RecordedAtUTC,
		Modality:        entry.Modality,
		Caption:         entry.Caption,
		Embedding:       nil,
		SourceSha256:    entry.SourceSha256,
		SourceMimeType:  entry.SourceMimeType,
		SourceByteCount: entry.SourceByteCount,
		SourceURI:       entry.SourceURI,
		WidthPx:         entry.WidthPx,
		HeightPx:        entry.HeightPx,
		DurationMs:      entry.DurationMs,
		ReferenceCount:  entry.ReferenceCount,
		Tags:            tags,
	}, nil
}

func rehydrateMultimodal(e MultimodalMemoryEntry) MultimodalMemoryEntry {
	if len(e.Embedding) > 0 {
		return e
	}
	if e.Tags == nil {
		return e
	}
	b64, ok := e.Tags[CompressedTagKey]
	if !ok {
		return e
	}
	floats, err := EmbeddingDecodeBase64(b64)
	if err != nil {
		return e
	}
	return MultimodalMemoryEntry{
		ID:              e.ID,
		RecordedAtUTC:   e.RecordedAtUTC,
		Modality:        e.Modality,
		Caption:         e.Caption,
		Embedding:       floats,
		SourceSha256:    e.SourceSha256,
		SourceMimeType:  e.SourceMimeType,
		SourceByteCount: e.SourceByteCount,
		SourceURI:       e.SourceURI,
		WidthPx:         e.WidthPx,
		HeightPx:        e.HeightPx,
		DurationMs:      e.DurationMs,
		ReferenceCount:  e.ReferenceCount,
		Tags:            e.Tags,
	}
}

// Compile-time assertion that the decorator satisfies the interface.
var _ IMultimodalMemoryStore = (*CompressedMultimodalMemoryStore)(nil)

// The compressed-search read path requests "all" entries from the inner store
// via maxInt (defined in memory_consolidation.go — int.MaxValue equivalent,
// mirroring the C# int.MaxValue and the TS Number.MAX_SAFE_INTEGER).
