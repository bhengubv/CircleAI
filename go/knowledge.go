// knowledge.go
//
// Ports CircleAI.Knowledge:
//   YamlFrontmatter          (YamlFrontmatter.cs)            -> yamlFrontmatterWrite/Read
//   KnowledgeNote            (KnowledgeNote.cs)              -> KnowledgeNote (+ ToFileText/ParseKnowledgeNoteFile)
//   IKnowledgeStore          (IKnowledgeStore.cs)           -> IKnowledgeStore
//   FileSystemKnowledgeStore (FileSystemKnowledgeStore.cs)  -> FileSystemKnowledgeStore
//   MarkdownEpisodicMemoryStore (MarkdownEpisodicMemoryStore.cs) -> MarkdownEpisodicMemoryStore
//
// The C# IAsyncEnumerable<KnowledgeNote> streaming surface (SearchByTagAsync /
// EnumerateAllAsync) is ported to slice-returning methods — the callers here
// (MarkdownEpisodicMemoryStore) buffer the whole stream anyway, and Go
// consumers iterate a returned slice. Atomic write-then-rename and per-id
// locking are preserved. Id.ToString("N") (32 hex, no dashes) is the file stem.

package circleai

import (
	"context"
	"encoding/base64"
	"encoding/binary"
	"errors"
	"fmt"
	"math"
	"os"
	"path/filepath"
	"sort"
	"strings"
	"sync"
	"time"
	"unicode"

	"github.com/google/uuid"
)

// ---------------------------------------------------------------------------
// YAML frontmatter (internal, mirrors YamlFrontmatter.cs)
// ---------------------------------------------------------------------------

const yamlFrontmatterDelimiter = "---"

// yamlFrontmatterWrite renders frontmatter into a YAML block followed by body.
// Ports YamlFrontmatter.Write. Keys are emitted in the provided iteration
// order; callers wanting deterministic order pass an ordered sequence.
func yamlFrontmatterWrite(keys []string, frontmatter map[string]string, body string) (string, error) {
	var sb strings.Builder
	sb.WriteString(yamlFrontmatterDelimiter)
	sb.WriteByte('\n')
	for _, k := range keys {
		if err := yamlValidateKey(k); err != nil {
			return "", err
		}
		sb.WriteString(k)
		sb.WriteString(": ")
		sb.WriteString(yamlEncodeValue(frontmatter[k]))
		sb.WriteByte('\n')
	}
	sb.WriteString(yamlFrontmatterDelimiter)
	sb.WriteByte('\n')
	sb.WriteString(body)
	return sb.String(), nil
}

// yamlFrontmatterRead parses text into a frontmatter map and a body string.
// Ports YamlFrontmatter.Read. Returns an error on malformed documents.
func yamlFrontmatterRead(text string) (map[string]string, string, error) {
	// Normalise line endings.
	text = strings.ReplaceAll(text, "\r\n", "\n")
	text = strings.ReplaceAll(text, "\r", "\n")

	if !strings.HasPrefix(text, yamlFrontmatterDelimiter+"\n") {
		return nil, "", errors.New("frontmatter must start with '---' on its own line")
	}

	searchStart := len(yamlFrontmatterDelimiter) + 1
	closingMarker := "\n" + yamlFrontmatterDelimiter + "\n"
	closingIdx := strings.Index(text[searchStart:], closingMarker)
	if closingIdx < 0 {
		return nil, "", errors.New("missing closing '---' line for frontmatter block")
	}
	closingIdx += searchStart

	yamlBlock := text[searchStart:closingIdx]
	body := text[closingIdx+len(closingMarker):]

	dict := make(map[string]string)
	for _, rawLine := range strings.Split(yamlBlock, "\n") {
		if strings.TrimSpace(rawLine) == "" {
			continue
		}
		if rawLine[0] == ' ' || rawLine[0] == '\t' {
			return nil, "", errors.New("nested YAML is not supported")
		}
		if strings.HasPrefix(rawLine, "- ") {
			return nil, "", errors.New("YAML lists are not supported")
		}
		colon := strings.IndexByte(rawLine, ':')
		if colon <= 0 {
			return nil, "", fmt.Errorf("malformed YAML line: '%s'", rawLine)
		}
		key := strings.TrimSpace(rawLine[:colon])
		rest := ""
		if colon+1 < len(rawLine) {
			rest = strings.TrimLeft(rawLine[colon+1:], " \t")
		}
		if err := yamlValidateKey(key); err != nil {
			return nil, "", err
		}
		if strings.HasPrefix(rest, "{") || strings.HasPrefix(rest, "[") {
			return nil, "", errors.New("flow-style YAML structures are not supported")
		}
		decoded, err := yamlDecodeValue(rest)
		if err != nil {
			return nil, "", err
		}
		dict[key] = decoded
	}
	return dict, body, nil
}

func yamlValidateKey(key string) error {
	if strings.TrimSpace(key) == "" {
		return errors.New("YAML key cannot be empty")
	}
	for _, ch := range key {
		// isLetterOrDigit (model_download_service.go, ASCII) plus Unicode
		// letters/digits — parity with char.IsLetterOrDigit for frontmatter keys.
		if !(isLetterOrDigit(ch) || unicode.IsLetter(ch) || unicode.IsDigit(ch) || ch == '_' || ch == '-' || ch == '.') {
			return fmt.Errorf("invalid character '%c' in YAML key '%s'", ch, key)
		}
	}
	return nil
}

// yamlEncodeValue mirrors YamlFrontmatter.EncodeValue.
func yamlEncodeValue(value string) string {
	if value == "" {
		return "\"\""
	}
	needsQuoting := false
	for _, ch := range value {
		switch ch {
		case ':', '#', '\n', '\r', '\t', '"', '\\', '\'', '{', '[':
			needsQuoting = true
		}
		if needsQuoting {
			break
		}
	}
	if !needsQuoting && (value[0] == ' ' || value[len(value)-1] == ' ') {
		needsQuoting = true
	}
	if !needsQuoting {
		return value
	}
	var sb strings.Builder
	sb.Grow(len(value) + 2)
	sb.WriteByte('"')
	for _, ch := range value {
		switch ch {
		case '\\':
			sb.WriteString("\\\\")
		case '"':
			sb.WriteString("\\\"")
		case '\n':
			sb.WriteString("\\n")
		case '\r':
			sb.WriteString("\\r")
		case '\t':
			sb.WriteString("\\t")
		default:
			sb.WriteRune(ch)
		}
	}
	sb.WriteByte('"')
	return sb.String()
}

// yamlDecodeValue mirrors YamlFrontmatter.DecodeValue.
func yamlDecodeValue(raw string) (string, error) {
	if raw == "" {
		return "", nil
	}
	if raw[0] != '"' && raw[0] != '\'' {
		if hashIdx := strings.Index(raw, " #"); hashIdx >= 0 {
			raw = strings.TrimRight(raw[:hashIdx], " \t")
		}
		return raw, nil
	}
	if raw[0] == '\'' {
		return "", errors.New("single-quoted YAML scalars are not supported")
	}
	if len(raw) < 2 || raw[len(raw)-1] != '"' {
		return "", errors.New("unterminated double-quoted YAML scalar")
	}
	inner := raw[1 : len(raw)-1]
	var sb strings.Builder
	sb.Grow(len(inner))
	runes := []rune(inner)
	for i := 0; i < len(runes); i++ {
		ch := runes[i]
		if ch != '\\' {
			sb.WriteRune(ch)
			continue
		}
		if i+1 >= len(runes) {
			return "", errors.New("trailing backslash in YAML scalar")
		}
		i++
		switch runes[i] {
		case '\\':
			sb.WriteByte('\\')
		case '"':
			sb.WriteByte('"')
		case 'n':
			sb.WriteByte('\n')
		case 'r':
			sb.WriteByte('\r')
		case 't':
			sb.WriteByte('\t')
		default:
			return "", fmt.Errorf("unsupported YAML escape '\\%c'", runes[i])
		}
	}
	return sb.String(), nil
}

// ---------------------------------------------------------------------------
// KnowledgeNote (KnowledgeNote.cs)
// ---------------------------------------------------------------------------

// KnowledgeNote is a markdown knowledge note: arbitrary frontmatter metadata
// combined with a markdown body. Ports the KnowledgeNote record.
type KnowledgeNote struct {
	ID           uuid.UUID
	Title        string
	BodyMarkdown string
	Frontmatter  map[string]string
	Tags         []string
	CreatedAt    time.Time
	UpdatedAt    time.Time
}

const (
	knowledgeTitleKey   = "title"
	knowledgeCreatedKey = "created_at"
	knowledgeUpdatedKey = "updated_at"
	knowledgeIDKey      = "id"
	knowledgeTagsKey    = "tags"
)

// ToFileText serialises the note to its on-disk text form. Ports ToFileText.
// Well-known fields win over user-supplied frontmatter. Keys are emitted in a
// stable order: user keys (sorted) first, then the canonical fields last, so
// the canonical values (which are appended after) override on parse.
func (n KnowledgeNote) ToFileText() (string, error) {
	merged := make(map[string]string)
	for k, v := range n.Frontmatter {
		merged[k] = v
	}
	merged[knowledgeIDKey] = n.ID.String()
	merged[knowledgeTitleKey] = n.Title
	merged[knowledgeCreatedKey] = formatRoundtrip(n.CreatedAt)
	merged[knowledgeUpdatedKey] = formatRoundtrip(n.UpdatedAt)
	merged[knowledgeTagsKey] = strings.Join(n.Tags, ",")

	// Deterministic key order: user (sorted) then canonical fields.
	canonical := map[string]struct{}{
		knowledgeIDKey: {}, knowledgeTitleKey: {}, knowledgeCreatedKey: {},
		knowledgeUpdatedKey: {}, knowledgeTagsKey: {},
	}
	userKeys := make([]string, 0, len(merged))
	for k := range merged {
		if _, ok := canonical[k]; !ok {
			userKeys = append(userKeys, k)
		}
	}
	sort.Strings(userKeys)
	keys := append(userKeys, knowledgeIDKey, knowledgeTitleKey, knowledgeCreatedKey, knowledgeUpdatedKey, knowledgeTagsKey)
	return yamlFrontmatterWrite(keys, merged, n.BodyMarkdown)
}

// ParseKnowledgeNoteFile parses on-disk text back into a KnowledgeNote. Ports
// KnowledgeNote.ParseFile.
func ParseKnowledgeNoteFile(text string) (KnowledgeNote, error) {
	frontmatter, body, err := yamlFrontmatterRead(text)
	if err != nil {
		return KnowledgeNote{}, err
	}
	idRaw, ok := frontmatter[knowledgeIDKey]
	if !ok {
		return KnowledgeNote{}, errors.New("knowledge note frontmatter missing or invalid 'id'")
	}
	id, perr := uuid.Parse(idRaw)
	if perr != nil {
		return KnowledgeNote{}, errors.New("knowledge note frontmatter missing or invalid 'id'")
	}

	title := frontmatter[knowledgeTitleKey]
	created := parseKnowledgeTimestamp(frontmatter, knowledgeCreatedKey)
	updated := parseKnowledgeTimestamp(frontmatter, knowledgeUpdatedKey)

	var tags []string
	if rawTags, ok := frontmatter[knowledgeTagsKey]; ok && strings.TrimSpace(rawTags) != "" {
		for _, t := range strings.Split(rawTags, ",") {
			trimmed := strings.TrimSpace(t)
			if trimmed != "" {
				tags = append(tags, trimmed)
			}
		}
	}
	if tags == nil {
		tags = []string{}
	}

	userFront := make(map[string]string)
	for k, v := range frontmatter {
		switch k {
		case knowledgeIDKey, knowledgeTitleKey, knowledgeCreatedKey, knowledgeUpdatedKey, knowledgeTagsKey:
			continue
		}
		userFront[k] = v
	}

	return KnowledgeNote{
		ID:           id,
		Title:        title,
		BodyMarkdown: body,
		Frontmatter:  userFront,
		Tags:         tags,
		CreatedAt:    created,
		UpdatedAt:    updated,
	}, nil
}

func parseKnowledgeTimestamp(m map[string]string, key string) time.Time {
	raw, ok := m[key]
	if !ok || strings.TrimSpace(raw) == "" {
		return time.Now().UTC()
	}
	if t, err := parseRoundtrip(raw); err == nil {
		return t
	}
	return time.Now().UTC()
}

// formatRoundtrip renders a time in the .NET "O" (round-trip) format.
func formatRoundtrip(t time.Time) string {
	return t.UTC().Format("2006-01-02T15:04:05.0000000Z07:00")
}

// parseRoundtrip parses a .NET round-trippable timestamp (best-effort across
// the common RFC3339 variants .NET emits).
func parseRoundtrip(s string) (time.Time, error) {
	layouts := []string{
		"2006-01-02T15:04:05.0000000Z07:00",
		time.RFC3339Nano,
		time.RFC3339,
		"2006-01-02T15:04:05.999999999",
	}
	var lastErr error
	for _, l := range layouts {
		if t, err := time.Parse(l, s); err == nil {
			return t.UTC(), nil
		} else {
			lastErr = err
		}
	}
	return time.Time{}, lastErr
}

// ---------------------------------------------------------------------------
// IKnowledgeStore (IKnowledgeStore.cs)
// ---------------------------------------------------------------------------

// IKnowledgeStore is a persistent store for KnowledgeNote documents. Ports the
// IKnowledgeStore interface. The IAsyncEnumerable streaming methods are
// modelled as slice-returning methods.
type IKnowledgeStore interface {
	// Get loads the note with the given id, or (zero,false,nil) when absent.
	Get(ctx context.Context, id uuid.UUID) (KnowledgeNote, bool, error)
	// Save persists note; the returned record has a refreshed UpdatedAt.
	Save(ctx context.Context, note KnowledgeNote) (KnowledgeNote, error)
	// Delete removes the note; no-op if it does not exist.
	Delete(ctx context.Context, id uuid.UUID) error
	// SearchByTag returns notes carrying tag (case-insensitive).
	SearchByTag(ctx context.Context, tag string) ([]KnowledgeNote, error)
	// EnumerateAll returns every note currently stored.
	EnumerateAll(ctx context.Context) ([]KnowledgeNote, error)
}

// ---------------------------------------------------------------------------
// FileSystemKnowledgeStore (FileSystemKnowledgeStore.cs)
// ---------------------------------------------------------------------------

// FileSystemKnowledgeStore stores each note as {root}/{id-no-dashes}.md. Writes
// are atomic (write-to-tmp + rename); access is serialised per-id. Ports
// FileSystemKnowledgeStore.
type FileSystemKnowledgeStore struct {
	rootDirectory string
	locksMu       sync.Mutex
	locks         map[uuid.UUID]*sync.Mutex
}

// NewFileSystemKnowledgeStore creates a store rooted at rootDirectory, creating
// the directory if needed. Returns an error on empty root / mkdir failure.
func NewFileSystemKnowledgeStore(rootDirectory string) (*FileSystemKnowledgeStore, error) {
	if strings.TrimSpace(rootDirectory) == "" {
		return nil, errors.New("rootDirectory required")
	}
	if err := os.MkdirAll(rootDirectory, 0o755); err != nil {
		return nil, err
	}
	return &FileSystemKnowledgeStore{
		rootDirectory: rootDirectory,
		locks:         make(map[uuid.UUID]*sync.Mutex),
	}, nil
}

func (s *FileSystemKnowledgeStore) lockFor(id uuid.UUID) *sync.Mutex {
	s.locksMu.Lock()
	defer s.locksMu.Unlock()
	g, ok := s.locks[id]
	if !ok {
		g = &sync.Mutex{}
		s.locks[id] = g
	}
	return g
}

func (s *FileSystemKnowledgeStore) notePath(id uuid.UUID) string {
	return filepath.Join(s.rootDirectory, uuidNoDashes(id)+".md")
}

// uuidNoDashes renders a UUID as 32 lowercase hex chars (C# "N" format).
func uuidNoDashes(id uuid.UUID) string {
	return strings.ReplaceAll(id.String(), "-", "")
}

// Get loads the note for id. Ports GetAsync.
func (s *FileSystemKnowledgeStore) Get(ctx context.Context, id uuid.UUID) (KnowledgeNote, bool, error) {
	path := s.notePath(id)
	if _, err := os.Stat(path); errors.Is(err, os.ErrNotExist) {
		return KnowledgeNote{}, false, nil
	}
	gate := s.lockFor(id)
	gate.Lock()
	defer gate.Unlock()
	data, err := os.ReadFile(path)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return KnowledgeNote{}, false, nil
		}
		return KnowledgeNote{}, false, err
	}
	note, perr := ParseKnowledgeNoteFile(string(data))
	if perr != nil {
		return KnowledgeNote{}, false, perr
	}
	return note, true, nil
}

// Save persists note (refreshing UpdatedAt) atomically. Ports SaveAsync.
func (s *FileSystemKnowledgeStore) Save(ctx context.Context, note KnowledgeNote) (KnowledgeNote, error) {
	refreshed := note
	refreshed.UpdatedAt = time.Now().UTC()
	if refreshed.Frontmatter == nil {
		refreshed.Frontmatter = map[string]string{}
	}
	if refreshed.Tags == nil {
		refreshed.Tags = []string{}
	}
	target := s.notePath(refreshed.ID)
	tmp := target + "." + uuidNoDashes(uuid.New()) + ".tmp"

	text, err := refreshed.ToFileText()
	if err != nil {
		return KnowledgeNote{}, err
	}

	gate := s.lockFor(refreshed.ID)
	gate.Lock()
	defer gate.Unlock()

	if err := os.WriteFile(tmp, []byte(text), 0o644); err != nil {
		_ = os.Remove(tmp)
		return KnowledgeNote{}, err
	}
	if err := os.Rename(tmp, target); err != nil {
		_ = os.Remove(tmp)
		return KnowledgeNote{}, err
	}
	return refreshed, nil
}

// Delete removes the note for id. Ports DeleteAsync.
func (s *FileSystemKnowledgeStore) Delete(ctx context.Context, id uuid.UUID) error {
	path := s.notePath(id)
	gate := s.lockFor(id)
	gate.Lock()
	defer gate.Unlock()
	if err := os.Remove(path); err != nil && !errors.Is(err, os.ErrNotExist) {
		return err
	}
	return nil
}

// SearchByTag returns notes carrying tag (case-insensitive). Ports
// SearchByTagAsync.
func (s *FileSystemKnowledgeStore) SearchByTag(ctx context.Context, tag string) ([]KnowledgeNote, error) {
	if strings.TrimSpace(tag) == "" {
		return nil, errors.New("tag required")
	}
	all, err := s.EnumerateAll(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]KnowledgeNote, 0)
	for _, note := range all {
		for _, t := range note.Tags {
			if strings.EqualFold(t, tag) {
				out = append(out, note)
				break
			}
		}
	}
	return out, nil
}

// EnumerateAll returns every stored note, skipping files that are not in the
// knowledge-note format. Ports EnumerateAllAsync.
func (s *FileSystemKnowledgeStore) EnumerateAll(ctx context.Context) ([]KnowledgeNote, error) {
	out := make([]KnowledgeNote, 0)
	entries, err := os.ReadDir(s.rootDirectory)
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return out, nil
		}
		return nil, err
	}
	for _, entry := range entries {
		if ctx.Err() != nil {
			return nil, ctx.Err()
		}
		if entry.IsDir() || !strings.HasSuffix(entry.Name(), ".md") {
			continue
		}
		data, rerr := os.ReadFile(filepath.Join(s.rootDirectory, entry.Name()))
		if rerr != nil {
			continue
		}
		note, perr := ParseKnowledgeNoteFile(string(data))
		if perr != nil {
			continue
		}
		out = append(out, note)
	}
	return out, nil
}

var _ IKnowledgeStore = (*FileSystemKnowledgeStore)(nil)

// ---------------------------------------------------------------------------
// MarkdownEpisodicMemoryStore (MarkdownEpisodicMemoryStore.cs)
// ---------------------------------------------------------------------------

const (
	episodeIDKey       = "episode_id"
	episodeRecordedKey = "recorded_at"
	episodeAppCtxKey   = "app_context"
	episodeEmbedKey    = "embedding"
	episodeEmbedDimKey = "embedding_dims"
	episodeTagPrefix   = "tag_"
)

// MarkdownEpisodicMemoryStore is a markdown-on-disk IEpisodicMemoryStore backed
// by an IKnowledgeStore. Ports MarkdownEpisodicMemoryStore.
type MarkdownEpisodicMemoryStore struct {
	store IKnowledgeStore
}

// NewMarkdownEpisodicMemoryStore constructs the store over a knowledge store.
// Panics if store is nil (mirrors the C# ArgumentNullException guard).
func NewMarkdownEpisodicMemoryStore(store IKnowledgeStore) *MarkdownEpisodicMemoryStore {
	if store == nil {
		panic("store must not be nil")
	}
	return &MarkdownEpisodicMemoryStore{store: store}
}

// Add persists an entry as a knowledge note. Ports AddAsync.
func (m *MarkdownEpisodicMemoryStore) Add(ctx context.Context, entry EpisodicMemoryEntry) error {
	note := episodicToNote(entry)
	_, err := m.store.Save(ctx, note)
	return err
}

// Search returns the topK entries by cosine similarity, or by recency when the
// query embedding is nil/empty. Ports SearchAsync.
func (m *MarkdownEpisodicMemoryStore) Search(ctx context.Context, queryEmbedding []float32, topK int) ([]EpisodicMemoryEntry, error) {
	snapshot, err := m.enumerateEntries(ctx)
	if err != nil {
		return nil, err
	}
	if len(queryEmbedding) == 0 {
		sort.SliceStable(snapshot, func(i, j int) bool {
			return snapshot[i].RecordedAtUTC.After(snapshot[j].RecordedAtUTC)
		})
		return takeEntries(snapshot, topK), nil
	}

	type scored struct {
		entry EpisodicMemoryEntry
		score float32
	}
	hits := make([]scored, 0)
	for _, e := range snapshot {
		if e.Embedding != nil && len(e.Embedding) == len(queryEmbedding) {
			hits = append(hits, scored{entry: e, score: episodicCosine(queryEmbedding, e.Embedding)})
		}
	}
	sort.SliceStable(hits, func(i, j int) bool { return hits[i].score > hits[j].score })
	out := make([]EpisodicMemoryEntry, 0, len(hits))
	for _, h := range hits {
		out = append(out, h.entry)
	}
	return takeEntries(out, topK), nil
}

// GetRecent returns the most recent count entries newest-first. Ports
// GetRecentAsync.
func (m *MarkdownEpisodicMemoryStore) GetRecent(ctx context.Context, count int) ([]EpisodicMemoryEntry, error) {
	snapshot, err := m.enumerateEntries(ctx)
	if err != nil {
		return nil, err
	}
	sort.SliceStable(snapshot, func(i, j int) bool {
		return snapshot[i].RecordedAtUTC.After(snapshot[j].RecordedAtUTC)
	})
	return takeEntries(snapshot, count), nil
}

// Count returns the number of stored entries. Ports CountAsync.
func (m *MarkdownEpisodicMemoryStore) Count(ctx context.Context) (int, error) {
	all, err := m.store.EnumerateAll(ctx)
	if err != nil {
		return 0, err
	}
	return len(all), nil
}

// PruneOlderThan deletes entries older than cutoff, returning the count removed.
// Ports PruneOlderThanAsync.
func (m *MarkdownEpisodicMemoryStore) PruneOlderThan(ctx context.Context, cutoff time.Time) (int, error) {
	all, err := m.store.EnumerateAll(ctx)
	if err != nil {
		return 0, err
	}
	doomed := make([]uuid.UUID, 0)
	for _, note := range all {
		entry := episodicFromNote(note)
		if entry.RecordedAtUTC.Before(cutoff) {
			doomed = append(doomed, note.ID)
		}
	}
	for _, id := range doomed {
		if derr := m.store.Delete(ctx, id); derr != nil {
			return 0, derr
		}
	}
	return len(doomed), nil
}

func (m *MarkdownEpisodicMemoryStore) enumerateEntries(ctx context.Context) ([]EpisodicMemoryEntry, error) {
	notes, err := m.store.EnumerateAll(ctx)
	if err != nil {
		return nil, err
	}
	out := make([]EpisodicMemoryEntry, 0, len(notes))
	for _, note := range notes {
		out = append(out, episodicFromNote(note))
	}
	return out, nil
}

// (takeEntries is shared with memory_stores.go — reused here.)

// episodicToNote maps an EpisodicMemoryEntry to a KnowledgeNote. Ports ToNote.
func episodicToNote(entry EpisodicMemoryEntry) KnowledgeNote {
	frontmatter := map[string]string{
		episodeIDKey:       entry.ID.String(),
		episodeRecordedKey: formatRoundtrip(entry.RecordedAtUTC),
	}
	if entry.AppContext != nil && strings.TrimSpace(*entry.AppContext) != "" {
		frontmatter[episodeAppCtxKey] = *entry.AppContext
	}
	if len(entry.Embedding) > 0 {
		bytes := make([]byte, len(entry.Embedding)*4)
		for i, f := range entry.Embedding {
			binary.LittleEndian.PutUint32(bytes[i*4:], math.Float32bits(f))
		}
		frontmatter[episodeEmbedKey] = base64.StdEncoding.EncodeToString(bytes)
		frontmatter[episodeEmbedDimKey] = fmt.Sprintf("%d", len(entry.Embedding))
	}
	tags := make([]string, 0, len(entry.Tags))
	// Deterministic tag ordering for stable output.
	tagKeys := make([]string, 0, len(entry.Tags))
	for k := range entry.Tags {
		tagKeys = append(tagKeys, k)
	}
	sort.Strings(tagKeys)
	for _, k := range tagKeys {
		frontmatter[episodeTagPrefix+k] = entry.Tags[k]
		tags = append(tags, k)
	}

	body := "## User\n\n" + entry.UserText + "\n\n" + "## Assistant\n\n" + entry.AssistantText

	id := entry.ID
	if id == uuid.Nil {
		id = uuid.New()
	}
	return KnowledgeNote{
		ID:           id,
		Title:        truncateForTitle(entry.UserText),
		BodyMarkdown: body,
		Frontmatter:  frontmatter,
		Tags:         tags,
		CreatedAt:    entry.RecordedAtUTC,
		UpdatedAt:    entry.RecordedAtUTC,
	}
}

// episodicFromNote is the inverse of episodicToNote. Ports FromNote.
func episodicFromNote(note KnowledgeNote) EpisodicMemoryEntry {
	episodeID := note.ID
	if raw, ok := note.Frontmatter[episodeIDKey]; ok {
		if parsed, err := uuid.Parse(raw); err == nil {
			episodeID = parsed
		}
	}

	recordedAt := note.CreatedAt
	if rawWhen, ok := note.Frontmatter[episodeRecordedKey]; ok {
		if when, err := parseRoundtrip(rawWhen); err == nil {
			recordedAt = when
		}
	}

	var appContext *string
	if ctx, ok := note.Frontmatter[episodeAppCtxKey]; ok {
		v := ctx
		appContext = &v
	}

	var embedding []float32
	if b64, ok := note.Frontmatter[episodeEmbedKey]; ok && strings.TrimSpace(b64) != "" {
		if bytes, err := base64.StdEncoding.DecodeString(b64); err == nil {
			embedding = make([]float32, len(bytes)/4)
			for i := range embedding {
				embedding[i] = math.Float32frombits(binary.LittleEndian.Uint32(bytes[i*4:]))
			}
		}
	}

	userText, assistantText := splitEpisodicBody(note.BodyMarkdown)

	var tagsOut map[string]string
	for k, v := range note.Frontmatter {
		if !strings.HasPrefix(k, episodeTagPrefix) {
			continue
		}
		if tagsOut == nil {
			tagsOut = make(map[string]string)
		}
		tagsOut[k[len(episodeTagPrefix):]] = v
	}

	return EpisodicMemoryEntry{
		ID:            episodeID,
		RecordedAtUTC: recordedAt,
		UserText:      userText,
		AssistantText: assistantText,
		AppContext:    appContext,
		Embedding:     embedding,
		Tags:          tagsOut,
	}
}

func splitEpisodicBody(body string) (string, string) {
	if body == "" {
		return "", ""
	}
	normal := strings.ReplaceAll(body, "\r\n", "\n")
	const userMarker = "## User\n\n"
	const assistantMarker = "\n\n## Assistant\n\n"
	userIdx := strings.Index(normal, userMarker)
	assistantIdx := strings.Index(normal, assistantMarker)
	if userIdx < 0 || assistantIdx <= userIdx {
		return normal, ""
	}
	userText := normal[userIdx+len(userMarker) : assistantIdx]
	assistantText := normal[assistantIdx+len(assistantMarker):]
	return userText, assistantText
}

func truncateForTitle(source string) string {
	if strings.TrimSpace(source) == "" {
		return "(untitled)"
	}
	single := strings.TrimSpace(strings.ReplaceAll(strings.ReplaceAll(source, "\n", " "), "\r", " "))
	runes := []rune(single)
	if len(runes) <= 64 {
		return single
	}
	return string(runes[:64])
}

func episodicCosine(a, b []float32) float32 {
	var dot float32
	for i := range a {
		dot += a[i] * b[i]
	}
	return dot
}

var _ IEpisodicMemoryStore = (*MarkdownEpisodicMemoryStore)(nil)
