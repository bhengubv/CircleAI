// feedback_training_queue.go
//
// Ports CircleAI.Inference.TrainingSample, IFeedbackTrainingQueue, and
// FileBackedFeedbackTrainingQueue (FeedbackTrainingQueue.cs).
//
// (Phase D2) Append-only queue of user feedback signals that the
// NightlyAdapterTrainer drains into LoRA training batches. Disk-backed so it
// survives process restarts without a database. Each line of the file is one
// JSON-encoded sample. The JSON property names match System.Text.Json's default
// serialisation of the C# record (PascalCase, ISO-8601 timestamp) so the
// on-disk format is cross-language compatible.

package circleai

import (
	"bufio"
	"encoding/json"
	"errors"
	"os"
	"path/filepath"
	"strings"
	"sync"
	"time"
)

// TrainingSample is one feedback-tagged turn that will inform fine-tuning.
// Ports CircleAI.Inference.TrainingSample. JSON tags match the C# record's
// default System.Text.Json shape.
type TrainingSample struct {
	// UserText is what the user said.
	UserText string `json:"UserText"`
	// AssistantText is what we replied (the "current" answer).
	AssistantText string `json:"AssistantText"`
	// PreferredText is the user's correction or the accepted form. Falls back to
	// AssistantText for a thumbs-up.
	PreferredText string `json:"PreferredText"`
	// Polarity is +1 (positive) / -1 (negative) / 0 (correction).
	Polarity int `json:"Polarity"`
	// AtUtc is when the feedback was given.
	AtUtc time.Time `json:"AtUtc"`
}

// IFeedbackTrainingQueue is the append-only feedback queue contract. Ports
// CircleAI.Inference.IFeedbackTrainingQueue.
type IFeedbackTrainingQueue interface {
	// Enqueue appends one sample.
	Enqueue(sample TrainingSample) error
	// Drain removes and returns up to maxSamples oldest samples.
	Drain(maxSamples int) ([]TrainingSample, error)
	// Pending returns the number of queued samples.
	Pending() int
}

// FileBackedFeedbackTrainingQueue is an append-only line-delimited JSON file
// queue. Ports CircleAI.Inference.FileBackedFeedbackTrainingQueue.
type FileBackedFeedbackTrainingQueue struct {
	path      string
	writeLock sync.Mutex
}

// NewFileBackedFeedbackTrainingQueue builds a queue backed by the file at path.
// The parent directory and an empty file are created if absent.
func NewFileBackedFeedbackTrainingQueue(path string) (*FileBackedFeedbackTrainingQueue, error) {
	if strings.TrimSpace(path) == "" {
		return nil, errors.New("path required")
	}
	if dir := filepath.Dir(path); dir != "" {
		if err := os.MkdirAll(dir, 0o755); err != nil {
			return nil, err
		}
	}
	if !fileExists(path) {
		if err := os.WriteFile(path, []byte{}, 0o644); err != nil {
			return nil, err
		}
	}
	return &FileBackedFeedbackTrainingQueue{path: path}, nil
}

// Pending counts the lines in the file. Ports the Pending getter.
func (q *FileBackedFeedbackTrainingQueue) Pending() int {
	f, err := os.Open(q.path)
	if err != nil {
		return 0
	}
	defer f.Close()
	count := 0
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 16*1024*1024)
	for sc.Scan() {
		count++
	}
	return count
}

// Enqueue appends one JSON-encoded sample line. Ports EnqueueAsync.
func (q *FileBackedFeedbackTrainingQueue) Enqueue(sample TrainingSample) error {
	line, err := json.Marshal(sample)
	if err != nil {
		return err
	}
	q.writeLock.Lock()
	defer q.writeLock.Unlock()
	f, err := os.OpenFile(q.path, os.O_APPEND|os.O_CREATE|os.O_WRONLY, 0o644)
	if err != nil {
		return err
	}
	defer f.Close()
	if _, err := f.Write(append(line, '\n')); err != nil {
		return err
	}
	return nil
}

// Drain removes and returns up to maxSamples oldest samples, rewriting the file
// with the remainder. Malformed lines are skipped (as C# does). Ports DrainAsync.
func (q *FileBackedFeedbackTrainingQueue) Drain(maxSamples int) ([]TrainingSample, error) {
	if maxSamples <= 0 {
		return nil, errors.New("maxSamples must be greater than zero")
	}
	q.writeLock.Lock()
	defer q.writeLock.Unlock()

	if !fileExists(q.path) {
		return []TrainingSample{}, nil
	}

	allLines, err := readAllLines(q.path)
	if err != nil {
		return nil, err
	}
	takeCount := maxSamples
	if takeCount > len(allLines) {
		takeCount = len(allLines)
	}
	taken := make([]TrainingSample, 0, takeCount)
	for i := 0; i < takeCount; i++ {
		var s TrainingSample
		if err := json.Unmarshal([]byte(allLines[i]), &s); err != nil {
			// malformed line skipped — matches C# behaviour.
			continue
		}
		taken = append(taken, s)
	}
	remaining := allLines[takeCount:]
	if err := writeAllLines(q.path, remaining); err != nil {
		return nil, err
	}
	return taken, nil
}

// readAllLines reads non-empty-terminated lines, matching File.ReadAllLines.
func readAllLines(path string) ([]string, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	var lines []string
	sc := bufio.NewScanner(f)
	sc.Buffer(make([]byte, 0, 64*1024), 16*1024*1024)
	for sc.Scan() {
		lines = append(lines, sc.Text())
	}
	return lines, sc.Err()
}

// writeAllLines writes each line followed by a newline, matching File.WriteAllLines
// closely enough for round-trip (trailing newline; empty slice → empty file).
func writeAllLines(path string, lines []string) error {
	var sb strings.Builder
	for _, l := range lines {
		sb.WriteString(l)
		sb.WriteByte('\n')
	}
	return os.WriteFile(path, []byte(sb.String()), 0o644)
}

var _ IFeedbackTrainingQueue = (*FileBackedFeedbackTrainingQueue)(nil)
