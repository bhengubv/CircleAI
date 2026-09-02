// sdd_board.go
//
// Ports CircleAI.SDD (Contracts.cs / InMemorySDD.cs / NullImplementations.cs):
//   Specification / SpecValidationResult / ScaffoldedProject
//   ISpecificationStore / ISpecificationValidator / ISpecToScaffold
//   InMemorySpecificationStore / JsonShapeSpecificationValidator / HelloWorldSpecToScaffold
//   NullSpecificationStore / NullSpecificationValidator / NullSpecToScaffold
//
// The validator checks Title/Body presence and (when a schema is supplied)
// that it parses as a JSON object declaring a top-level "type"; the scaffolder
// emits a complete minimal hello-world project for csharp / typescript /
// python. All ported verbatim.

package circleai

import (
	"context"
	"encoding/json"
	"errors"
	"strings"
	"sync"

	"github.com/google/uuid"
)

// Specification is a stored spec. Ports Specification. Schema is a *string;
// Metadata nil == none.
type Specification struct {
	SpecID   string
	Title    string
	Body     string
	Schema   *string
	Metadata map[string]string
}

// SpecValidationResult is a validation outcome. Ports SpecValidationResult.
type SpecValidationResult struct {
	IsValid bool
	Errors  []string
}

// ScaffoldedProject is a generated project. Ports ScaffoldedProject. Files maps
// path -> file bytes.
type ScaffoldedProject struct {
	ProjectID string
	Files     map[string][]byte
}

// ISpecificationStore persists specifications. Ports ISpecificationStore.
type ISpecificationStore interface {
	BackendID() string
	Upsert(ctx context.Context, spec Specification) error
	Get(ctx context.Context, specID string) (Specification, bool, error)
	List(ctx context.Context) ([]Specification, error)
}

// ISpecificationValidator validates a specification. Ports ISpecificationValidator.
type ISpecificationValidator interface {
	BackendID() string
	Validate(ctx context.Context, spec Specification) (SpecValidationResult, error)
}

// ISpecToScaffold turns a spec into a scaffolded project. Ports ISpecToScaffold.
type ISpecToScaffold interface {
	BackendID() string
	Scaffold(ctx context.Context, spec Specification, targetLanguage string) (ScaffoldedProject, error)
}

// ---------------------------------------------------------------------------
// InMemorySpecificationStore
// ---------------------------------------------------------------------------

// InMemorySpecificationStore is a thread-safe spec store. Ports
// InMemorySpecificationStore.
type InMemorySpecificationStore struct {
	mu    sync.Mutex
	items map[string]Specification
}

// NewInMemorySpecificationStore constructs an empty store.
func NewInMemorySpecificationStore() *InMemorySpecificationStore {
	return &InMemorySpecificationStore{items: make(map[string]Specification)}
}

// BackendID returns "in-memory".
func (s *InMemorySpecificationStore) BackendID() string { return "in-memory" }

// Upsert stores (or replaces by SpecId) a spec. Ports UpsertAsync.
func (s *InMemorySpecificationStore) Upsert(ctx context.Context, spec Specification) error {
	if strings.TrimSpace(spec.SpecID) == "" {
		return errors.New("SpecId required")
	}
	s.mu.Lock()
	s.items[spec.SpecID] = spec
	s.mu.Unlock()
	return nil
}

// Get returns the spec for specID. Ports GetAsync.
func (s *InMemorySpecificationStore) Get(ctx context.Context, specID string) (Specification, bool, error) {
	if strings.TrimSpace(specID) == "" {
		return Specification{}, false, errors.New("specId required")
	}
	s.mu.Lock()
	spec, ok := s.items[specID]
	s.mu.Unlock()
	return spec, ok, nil
}

// List returns all specs. Ports ListAsync.
func (s *InMemorySpecificationStore) List(ctx context.Context) ([]Specification, error) {
	s.mu.Lock()
	out := make([]Specification, 0, len(s.items))
	for _, v := range s.items {
		out = append(out, v)
	}
	s.mu.Unlock()
	return out, nil
}

var _ ISpecificationStore = (*InMemorySpecificationStore)(nil)

// ---------------------------------------------------------------------------
// JsonShapeSpecificationValidator
// ---------------------------------------------------------------------------

// JsonShapeSpecificationValidator validates spec shape + optional JSON schema.
// Ports JsonShapeSpecificationValidator.
type JsonShapeSpecificationValidator struct{}

// BackendID returns "json-shape".
func (JsonShapeSpecificationValidator) BackendID() string { return "json-shape" }

// Validate checks Title/Body and (when present) the schema. Ports ValidateAsync.
func (JsonShapeSpecificationValidator) Validate(ctx context.Context, spec Specification) (SpecValidationResult, error) {
	errs := make([]string, 0)
	if strings.TrimSpace(spec.Title) == "" {
		errs = append(errs, "Title is required.")
	}
	if strings.TrimSpace(spec.Body) == "" {
		errs = append(errs, "Body is required.")
	}
	if spec.Schema != nil && strings.TrimSpace(*spec.Schema) != "" {
		var root map[string]json.RawMessage
		if err := json.Unmarshal([]byte(*spec.Schema), &root); err != nil {
			// Distinguish "not an object" from "not valid JSON".
			var any interface{}
			if json.Unmarshal([]byte(*spec.Schema), &any) == nil {
				errs = append(errs, "Schema must be a JSON object.")
			} else {
				errs = append(errs, "Schema is not valid JSON: "+err.Error())
			}
		} else if _, ok := root["type"]; !ok {
			errs = append(errs, "Schema must declare a top-level 'type'.")
		}
	}
	return SpecValidationResult{IsValid: len(errs) == 0, Errors: errs}, nil
}

var _ ISpecificationValidator = JsonShapeSpecificationValidator{}

// ---------------------------------------------------------------------------
// HelloWorldSpecToScaffold
// ---------------------------------------------------------------------------

// HelloWorldSpecToScaffold emits a minimal compilable project per language.
// Ports HelloWorldSpecToScaffold.
type HelloWorldSpecToScaffold struct{}

// BackendID returns "hello-world".
func (HelloWorldSpecToScaffold) BackendID() string { return "hello-world" }

// Scaffold produces project files for the target language. Ports ScaffoldAsync.
func (HelloWorldSpecToScaffold) Scaffold(ctx context.Context, spec Specification, targetLanguage string) (ScaffoldedProject, error) {
	if strings.TrimSpace(targetLanguage) == "" {
		return ScaffoldedProject{}, errors.New("targetLanguage required")
	}
	files := make(map[string][]byte)
	lang := strings.ToLower(targetLanguage)
	name := sanitizeScaffoldName(spec.SpecID)
	title := escapeScaffoldText(spec.Title)
	body := escapeScaffoldText(spec.Body)

	switch lang {
	case "csharp", "c#":
		files["Program.cs"] = []byte("Console.WriteLine(\"" + name + ": " + title + "\");\n")
		files[name+".csproj"] = []byte("<Project Sdk=\"Microsoft.NET.Sdk\">\n  <PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net9.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup>\n</Project>\n")
		files["README.md"] = []byte("# " + title + "\n\n" + body + "\n")
	case "typescript", "ts":
		files["index.ts"] = []byte("console.log(\"" + name + ": " + title + "\");\n")
		files["package.json"] = []byte("{\"name\":\"" + name + "\",\"version\":\"0.1.0\",\"main\":\"index.ts\",\"scripts\":{\"start\":\"ts-node index.ts\"}}\n")
		files["tsconfig.json"] = []byte("{\"compilerOptions\":{\"strict\":true,\"target\":\"ES2022\",\"module\":\"commonjs\"}}\n")
		files["README.md"] = []byte("# " + title + "\n\n" + body + "\n")
	case "python", "py":
		files["main.py"] = []byte("def main():\n    print(\"" + name + ": " + title + "\")\n\nif __name__ == \"__main__\":\n    main()\n")
		files["pyproject.toml"] = []byte("[project]\nname = \"" + name + "\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\n")
		files["README.md"] = []byte("# " + title + "\n\n" + body + "\n")
	default:
		return ScaffoldedProject{}, errors.New("language '" + targetLanguage + "' is not supported by HelloWorldSpecToScaffold (csharp / typescript / python)")
	}
	return ScaffoldedProject{ProjectID: name + "-" + lang, Files: files}, nil
}

func sanitizeScaffoldName(id string) string {
	if strings.TrimSpace(id) == "" {
		return "project"
	}
	var sb strings.Builder
	for _, ch := range id {
		if (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_' || ch == '-' {
			sb.WriteRune(ch)
		}
	}
	if sb.Len() == 0 {
		return "project"
	}
	return sb.String()
}

func escapeScaffoldText(s string) string {
	s = strings.ReplaceAll(s, "\\", "\\\\")
	s = strings.ReplaceAll(s, "\"", "\\\"")
	s = strings.ReplaceAll(s, "\n", "\\n")
	return s
}

var _ ISpecToScaffold = HelloWorldSpecToScaffold{}

// ---------------------------------------------------------------------------
// Null implementations
// ---------------------------------------------------------------------------

// NullSpecificationStore is a fail-safe store. Ports NullSpecificationStore.
type NullSpecificationStore struct{}

// NullSpecificationStoreInstance is the shared singleton.
var NullSpecificationStoreInstance = NullSpecificationStore{}

func (NullSpecificationStore) BackendID() string                           { return "null" }
func (NullSpecificationStore) Upsert(context.Context, Specification) error { return nil }
func (NullSpecificationStore) Get(context.Context, string) (Specification, bool, error) {
	return Specification{}, false, nil
}
func (NullSpecificationStore) List(context.Context) ([]Specification, error) {
	return []Specification{}, nil
}

// NullSpecificationValidator always fails validation. Ports
// NullSpecificationValidator.
type NullSpecificationValidator struct{}

// NullSpecificationValidatorInstance is the shared singleton.
var NullSpecificationValidatorInstance = NullSpecificationValidator{}

func (NullSpecificationValidator) BackendID() string { return "null" }
func (NullSpecificationValidator) Validate(context.Context, Specification) (SpecValidationResult, error) {
	return SpecValidationResult{IsValid: false, Errors: []string{"No real validator wired."}}, nil
}

// NullSpecToScaffold produces an empty scaffold. Ports NullSpecToScaffold.
type NullSpecToScaffold struct{}

// NullSpecToScaffoldInstance is the shared singleton.
var NullSpecToScaffoldInstance = NullSpecToScaffold{}

func (NullSpecToScaffold) BackendID() string { return "null" }
func (NullSpecToScaffold) Scaffold(context.Context, Specification, string) (ScaffoldedProject, error) {
	return ScaffoldedProject{ProjectID: uuid.Nil.String(), Files: map[string][]byte{}}, nil
}

var (
	_ ISpecificationStore     = NullSpecificationStore{}
	_ ISpecificationValidator = NullSpecificationValidator{}
	_ ISpecToScaffold         = NullSpecToScaffold{}
)
