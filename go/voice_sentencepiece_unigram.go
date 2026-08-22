// voice_sentencepiece_unigram.go
//
// Port of src/CircleAI.Voice/SentencePieceUnigram.cs — SentencePiece unigram
// encoding: Viterbi over the piece lattice, with byte fallback.
//
// Parity is asserted against fixtures/voice_sentencepiece_unigram.json, whose
// vocabulary is deliberately built so GREEDY AND VITERBI DISAGREE. A port that
// takes the greedy shortcut passes a naive test and fails that fixture.

package circleai

import (
	"encoding/json"
	"fmt"
	"os"

	"golang.org/x/text/unicode/norm"
)

// SentencePieceUnigram is a unigram SentencePiece tokeniser.
type SentencePieceUnigram struct {
	ids            map[string]int
	scores         map[string]float32
	maxPieceLength int
}

// voiceFallbackPenalty is the cost charged for falling back to raw bytes.
//
// Any finite penalty works, because fallback only ever competes with "no path
// at all". It must be worse than a real piece so the lattice never prefers it
// where a piece exists, and finite so a path always exists.
const voiceFallbackPenalty float32 = 10.0

// NewSentencePieceUnigram builds a tokeniser from piece→id and piece→score maps.
func NewSentencePieceUnigram(ids map[string]int, scores map[string]float32) *SentencePieceUnigram {
	maxLen := 1
	for piece := range ids {
		if n := len([]rune(piece)); n > maxLen {
			maxLen = n
		}
	}
	return &SentencePieceUnigram{ids: ids, scores: scores, maxPieceLength: maxLen}
}

// LoadSentencePieceUnigram reads a bundle's vocab.json and token_scores.json.
func LoadSentencePieceUnigram(vocabPath, scoresPath string) (*SentencePieceUnigram, error) {
	vocabData, err := os.ReadFile(vocabPath)
	if err != nil {
		return nil, err
	}
	scoresData, err := os.ReadFile(scoresPath)
	if err != nil {
		return nil, err
	}
	var ids map[string]int
	if err := json.Unmarshal(vocabData, &ids); err != nil {
		return nil, err
	}
	var scores map[string]float32
	if err := json.Unmarshal(scoresData, &scores); err != nil {
		return nil, err
	}
	if len(ids) == 0 {
		return nil, fmt.Errorf("%s is empty", vocabPath)
	}
	return NewSentencePieceUnigram(ids, scores), nil
}

// Count returns the number of pieces in the vocabulary.
func (sp *SentencePieceUnigram) Count() int { return len(sp.ids) }

// Encode converts text to token ids.
func (sp *SentencePieceUnigram) Encode(text string) []int {
	if text == "" {
		return []int{}
	}

	// SentencePiece's own normalisation: NFKC, then spaces become U+2581, with
	// one prepended so the first word is marked word-initial too.
	normalised := "▁"
	for _, r := range norm.NFKC.String(text) {
		if r == ' ' {
			normalised += "▁"
		} else {
			normalised += string(r)
		}
	}

	// RUNES, NOT BYTES. Indexing a Go string yields bytes, and a piece boundary
	// landing mid-rune would produce pieces that match nothing and byte-fallback
	// output that decodes to a different character.
	chars := []rune(normalised)
	n := len(chars)

	const unreachable float32 = -1e18
	best := make([]float32, n+1)
	fromIndex := make([]int, n+1)
	piece := make([]string, n+1)
	hasPiece := make([]bool, n+1)
	for i := range best {
		best[i] = unreachable
	}
	best[0] = 0

	for i := 0; i < n; i++ {
		if best[i] <= unreachable/2 {
			continue
		}

		limit := sp.maxPieceLength
		if n-i < limit {
			limit = n - i
		}
		for length := 1; length <= limit; length++ {
			candidate := string(chars[i : i+length])
			if _, ok := sp.ids[candidate]; !ok {
				continue
			}
			score := best[i] + sp.scores[candidate]
			if score > best[i+length] {
				best[i+length] = score
				fromIndex[i+length] = i
				piece[i+length] = candidate
				hasPiece[i+length] = true
			}
		}

		// Byte fallback for this ONE rune, so no input is ever silent.
		end := i + 1
		if fallback := best[i] - voiceFallbackPenalty; fallback > best[end] {
			best[end] = fallback
			fromIndex[end] = i
			hasPiece[end] = false
		}
	}

	reversed := make([]int, 0, n)
	for i := n; i > 0; {
		start := fromIndex[i]
		if hasPiece[i] {
			reversed = append(reversed, sp.ids[piece[i]])
		} else {
			// BACKWARDS, because this whole list is built backwards. The lattice
			// is walked from the end and flipped once at the bottom, so a
			// multi-byte character appended in forward order comes out
			// byte-reversed: é is UTF-8 C3 A9 and would be emitted A9 C3.
			// Nothing errors — those are real pieces with real ids — so the
			// model simply says a different character.
			raw := []byte(string(chars[start:i]))
			for b := len(raw) - 1; b >= 0; b-- {
				if id, ok := sp.ids[fmt.Sprintf("<0x%02X>", raw[b])]; ok {
					reversed = append(reversed, id)
				}
			}
		}
		i = start
	}

	out := make([]int, len(reversed))
	for i, v := range reversed {
		out[len(reversed)-1-i] = v
	}
	return out
}
