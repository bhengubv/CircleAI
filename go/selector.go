// selector.go
//
// ChatCapability flags + ModelSelection + IModelSelector contract +
// DeviceAwareModelSelector implementation. Port of the 1.5.0 surface.

package circleai

import (
	"fmt"
	"sort"
	"strings"
)

// ChatCapability is a bit-flag enum.
type ChatCapability int

const (
	CapNone        ChatCapability = 0
	CapDefault     ChatCapability = 1
	CapTools       ChatCapability = 2
	CapVision      ChatCapability = 4
	CapLongContext ChatCapability = 8
	CapReasoning   ChatCapability = 16
)

// HasAll returns true if `set` contains every flag in `required`.
func (set ChatCapability) HasAll(required ChatCapability) bool {
	return (set & required) == required
}

// DeviceTier classifies the host into a tier for default-derivation.
type DeviceTier int

const (
	DeviceTierWearable    DeviceTier = 0
	DeviceTierPhone       DeviceTier = 1
	DeviceTierTablet      DeviceTier = 2
	DeviceTierDesktop     DeviceTier = 3
	DeviceTierWorkstation DeviceTier = 4
)

// ModelSelection is one selector result.
type ModelSelection struct {
	ModelID          string
	RequiresDownload bool
	EstimatedBytes   int64
	Tier             DeviceTier
}

// IModelSelector picks a model that fits the device + capabilities.
type IModelSelector interface {
	BestFit(probe DeviceProbe, required ChatCapability) (ModelSelection, error)
	AllCandidates(probe DeviceProbe) []ModelSelection
}

// DeviceAwareModelSelector is the default IModelSelector.
type DeviceAwareModelSelector struct {
	Registry *ModelRegistryService
}

// BestFit picks the highest-quality entry that satisfies every flag in
// `required` AND has MinRamGB <= probe RAM AND MinStorageGB <= free.
func (s *DeviceAwareModelSelector) BestFit(probe DeviceProbe, required ChatCapability) (ModelSelection, error) {
	if s.Registry == nil {
		return ModelSelection{}, fmt.Errorf("selector: registry is nil")
	}
	entries := s.Registry.AllModels()
	if len(entries) == 0 {
		return ModelSelection{}, fmt.Errorf("selector: registry is empty")
	}

	ramGB := float64(probe.RAMAvailableBytes) / (1024 * 1024 * 1024)
	storageGB := float64(probe.StorageFreeBytes) / (1024 * 1024 * 1024)

	// 1. Filter by capability.
	var capabilityOk []ModelEntry
	for _, e := range entries {
		if satisfiesCapability(e, required) {
			capabilityOk = append(capabilityOk, e)
		}
	}
	if len(capabilityOk) == 0 {
		return ModelSelection{}, fmt.Errorf("selector: no model satisfies required capabilities %d", required)
	}

	// 2. Filter by device fit — advisory.
	var deviceOk []ModelEntry
	for _, e := range capabilityOk {
		if e.MinRAMGB <= ramGB+1e-4 &&
			(storageGB <= 0 || e.MinStorageGB <= storageGB+1e-4) {
			deviceOk = append(deviceOk, e)
		}
	}
	candidates := deviceOk
	if len(candidates) == 0 {
		candidates = capabilityOk
	}

	// Higher QualityRank wins, smaller MinRAMGB breaks ties.
	sort.SliceStable(candidates, func(i, j int) bool {
		if candidates[i].QualityRank != candidates[j].QualityRank {
			return candidates[i].QualityRank > candidates[j].QualityRank
		}
		return candidates[i].MinRAMGB < candidates[j].MinRAMGB
	})

	winner := candidates[0]
	return ModelSelection{
		ModelID:          winner.Name,
		RequiresDownload: true,
		EstimatedBytes:   winner.TotalBytes,
		Tier:             probe.Classify(),
	}, nil
}

// AllCandidates returns every selection candidate, highest QualityRank first.
func (s *DeviceAwareModelSelector) AllCandidates(probe DeviceProbe) []ModelSelection {
	if s.Registry == nil {
		return nil
	}
	entries := s.Registry.AllModels()
	sort.SliceStable(entries, func(i, j int) bool {
		return entries[i].QualityRank > entries[j].QualityRank
	})
	tier := probe.Classify()
	out := make([]ModelSelection, 0, len(entries))
	for _, e := range entries {
		out = append(out, ModelSelection{
			ModelID:          e.Name,
			RequiresDownload: true,
			EstimatedBytes:   e.TotalBytes,
			Tier:             tier,
		})
	}
	return out
}

func satisfiesCapability(e ModelEntry, required ChatCapability) bool {
	if required == CapNone {
		return true
	}
	declared := ParseCapabilities(e.Capabilities)
	return declared.HasAll(required)
}

// ParseCapabilities parses a registry capability list into a ChatCapability set.
// Empty list returns CapDefault.
func ParseCapabilities(labels []string) ChatCapability {
	if len(labels) == 0 {
		return CapDefault
	}
	var result ChatCapability
	for _, l := range labels {
		l = strings.TrimSpace(l)
		if l == "" {
			continue
		}
		key := strings.ToUpper(strings.ReplaceAll(l, " ", "_"))
		switch key {
		case "DEFAULT":
			result |= CapDefault
		case "TOOLS":
			result |= CapTools
		case "VISION":
			result |= CapVision
		case "LONGCONTEXT", "LONG_CONTEXT":
			result |= CapLongContext
		case "REASONING":
			result |= CapReasoning
		}
	}
	if result == CapNone {
		return CapDefault
	}
	return result
}
