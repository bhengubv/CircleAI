// inference_mesh_offload.go
//
// Ports the pure mesh-offload heuristic from
// CircleAI.Inference/MnnInteropRtFeatures.cs (RT-12): decide whether to route an
// inference request to a mesh peer when local execution is infeasible or a peer
// is meaningfully faster. The native P/Invoke surface (RT-03 mmap, RT-05
// speculative decoding, RT-10 LoRA) is intentionally NOT ported here — this file
// is dependency-free heuristic logic only.
//
//	MeshPeer / OffloadVerdict (records) -> structs
//	MeshOffloadStrategy + Decide         -> MeshOffloadStrategy.Decide (pure)
//
// TargetPeerID is empty when no peer is chosen (C# nullable string?). The peer
// registry is supplied as a func so the strategy stays pure — hosts wire the
// live registry.

package circleai

import (
	"errors"
	"sort"
	"strings"
)

// MeshPeer is a candidate offload peer. Ports the MeshPeer record.
type MeshPeer struct {
	PeerID          string
	LatencyMs       float64
	RAMBytes        int64
	LoadAvg         float64
	SupportedModels []string
}

// OffloadVerdict is the decision on whether to offload. Ports the OffloadVerdict
// record. TargetPeerID is empty when ShouldOffload is false or no peer fits.
type OffloadVerdict struct {
	ShouldOffload bool
	TargetPeerID  string
	Reason        string
}

// MeshOffloadStrategy decides whether to offload inference to a peer. Ports
// MeshOffloadStrategy — the logic is pure; hosts wire the peer registry via the
// peers func. Construct with NewMeshOffloadStrategy.
type MeshOffloadStrategy struct {
	peers         func() []MeshPeer
	localRAMBytes int64
	localLoadAvg  float64
}

// NewMeshOffloadStrategy constructs the strategy. peers returns the current
// candidate peers. Panics if peers is nil (mirrors the C# ArgumentNullException).
func NewMeshOffloadStrategy(peers func() []MeshPeer, localRAMBytes int64, localLoadAvg float64) *MeshOffloadStrategy {
	if peers == nil {
		panic("peers must not be nil")
	}
	return &MeshOffloadStrategy{peers: peers, localRAMBytes: localRAMBytes, localLoadAvg: localLoadAvg}
}

// Decide returns the offload verdict for a model + its requirements. Ports
// Decide. Returns an error for a blank modelID or non-positive requiredRAMBytes
// (mirrors the C# ArgumentException / ArgumentOutOfRangeException).
//
//  1. If local RAM can't fit the model, offload to the best eligible peer (or
//     report that none fits).
//  2. Else if local is overloaded (loadAvg > 0.85) AND a peer is idle
//     (loadAvg < 0.5) and reachable within 70% of the expected local time,
//     offload to it.
//  3. Otherwise keep it local.
func (s *MeshOffloadStrategy) Decide(modelID string, requiredRAMBytes int64, expectedSecondsLocal float64) (OffloadVerdict, error) {
	if strings.TrimSpace(modelID) == "" {
		return OffloadVerdict{}, errors.New("modelId required")
	}
	if requiredRAMBytes <= 0 {
		return OffloadVerdict{}, errors.New("requiredRamBytes must be positive")
	}

	// 1) Always offload if local can't fit the model.
	if s.localRAMBytes < requiredRAMBytes {
		pick, ok := s.pickBestPeer(modelID, requiredRAMBytes)
		if !ok {
			return OffloadVerdict{ShouldOffload: false, Reason: "Local can't fit; no eligible peer"}, nil
		}
		return OffloadVerdict{ShouldOffload: true, TargetPeerID: pick.PeerID, Reason: "Local RAM insufficient"}, nil
	}

	// 2) Offload if local is overloaded AND a peer can do it noticeably faster.
	if s.localLoadAvg > 0.85 {
		pick, ok := s.pickBestPeer(modelID, requiredRAMBytes)
		if ok && pick.LoadAvg < 0.5 && pick.LatencyMs < expectedSecondsLocal*1000*0.7 {
			return OffloadVerdict{ShouldOffload: true, TargetPeerID: pick.PeerID, Reason: "Local overloaded; peer faster"}, nil
		}
	}

	return OffloadVerdict{ShouldOffload: false, Reason: "Local capacity sufficient"}, nil
}

// pickBestPeer returns the lowest-cost peer that fits the model + supports it.
// Ports PickBestPeer (ordering by LatencyMs + LoadAvg*500). Ties resolve by
// PeerID for determinism.
func (s *MeshOffloadStrategy) pickBestPeer(modelID string, requiredRAMBytes int64) (MeshPeer, bool) {
	eligible := make([]MeshPeer, 0)
	for _, p := range s.peers() {
		if p.RAMBytes < requiredRAMBytes {
			continue
		}
		if !containsFold(p.SupportedModels, modelID) {
			continue
		}
		eligible = append(eligible, p)
	}
	if len(eligible) == 0 {
		return MeshPeer{}, false
	}
	sort.SliceStable(eligible, func(i, j int) bool {
		ci := eligible[i].LatencyMs + eligible[i].LoadAvg*500
		cj := eligible[j].LatencyMs + eligible[j].LoadAvg*500
		if ci != cj {
			return ci < cj
		}
		return eligible[i].PeerID < eligible[j].PeerID
	})
	return eligible[0], true
}
