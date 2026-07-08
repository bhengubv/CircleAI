// model_manager.go
//
// Ports CircleAI.Core.IModelManager (IModelManager.cs) and
// CircleAI.Core.LocalModelManager (LocalModelManager.cs).
//
// LocalModelManager resolves a modelId to an on-disk directory, downloading via
// an injected IModelDownloader when the model is not already present, and
// verifies the anchor file (pytorch_model.bin) against an expected SHA-256.

package circleai

import (
	"context"
	"crypto/sha256"
	"errors"
	"fmt"
	"io"
	"os"
	"path/filepath"
	"strings"
)

// modelAnchorFileName is the file LocalModelManager treats as the model's
// existence + checksum anchor. Mirrors the C# "pytorch_model.bin" constant.
const modelAnchorFileName = "pytorch_model.bin"

// IModelManager resolves + verifies model paths. Ports CircleAI.Core.IModelManager.
type IModelManager interface {
	// GetModelPath returns the local path for modelId, downloading it first if
	// absent.
	GetModelPath(ctx context.Context, modelId string) (string, error)

	// VerifyModel returns true iff the model at modelPath hashes to
	// expectedChecksum (SHA-256 of the anchor file).
	VerifyModel(ctx context.Context, modelPath string, expectedChecksum []byte) (bool, error)
}

// LocalModelManager is the disk-backed IModelManager. Ports
// CircleAI.Core.LocalModelManager.
type LocalModelManager struct {
	downloader      IModelDownloader
	modelsDirectory string
	disposed        bool
}

// NewLocalModelManager builds a manager over an injected downloader (which may
// be nil — then GetModelPath fails when a model is missing) and a models
// directory. Ports the C# ctor overloads. The directory is created eagerly.
func NewLocalModelManager(downloader IModelDownloader, modelsDirectory string) (*LocalModelManager, error) {
	if modelsDirectory == "" {
		modelsDirectory = "Models"
	}
	if err := os.MkdirAll(modelsDirectory, 0o755); err != nil {
		return nil, err
	}
	return &LocalModelManager{downloader: downloader, modelsDirectory: modelsDirectory}, nil
}

// GetModelPath returns the local directory for modelId. If the anchor file is
// absent it downloads via the injected downloader; with no downloader
// configured it errors. expectedChecksum is optional and, when non-empty,
// verified against the anchor file after resolution.
func (m *LocalModelManager) GetModelPath(ctx context.Context, modelId string) (string, error) {
	return m.GetModelPathVerified(ctx, modelId, nil)
}

// GetModelPathVerified is GetModelPath with an optional expected checksum.
// Mirrors the C# GetModelPathAsync(modelId, expectedChecksum?, ct) overload.
func (m *LocalModelManager) GetModelPathVerified(ctx context.Context, modelId string, expectedChecksum []byte) (string, error) {
	if m.disposed {
		return "", errors.New("LocalModelManager is disposed")
	}
	modelPath := filepath.Join(m.modelsDirectory, sanitizeModelID(modelId))
	anchor := filepath.Join(modelPath, modelAnchorFileName)

	if !isDir(modelPath) || !fileExists(anchor) {
		if m.downloader == nil {
			return "", errors.New("model not found and no downloader configured")
		}
		if err := m.downloader.DownloadModel(ctx, modelId, modelPath); err != nil {
			return "", err
		}
	}

	if len(expectedChecksum) > 0 {
		actual, err := computeFileChecksum(anchor)
		if err != nil {
			return "", err
		}
		if !bytesEqual(actual, expectedChecksum) {
			return "", fmt.Errorf(
				"model checksum verification failed for %q. The file may be corrupt or tampered with", modelId)
		}
	}
	return modelPath, nil
}

// VerifyModel returns true iff the anchor file under modelPath hashes to
// expectedChecksum. A missing anchor or hash mismatch returns false (no error);
// I/O errors surface as an error.
func (m *LocalModelManager) VerifyModel(_ context.Context, modelPath string, expectedChecksum []byte) (bool, error) {
	if m.disposed {
		return false, errors.New("LocalModelManager is disposed")
	}
	anchor := filepath.Join(modelPath, modelAnchorFileName)
	if !fileExists(anchor) {
		return false, nil
	}
	actual, err := computeFileChecksum(anchor)
	if err != nil {
		return false, err
	}
	if len(expectedChecksum) == 0 {
		return false, nil
	}
	return bytesEqual(actual, expectedChecksum), nil
}

// Close releases the manager and disposes an owned downloader if it is
// closeable. Ports IDisposable.Dispose.
func (m *LocalModelManager) Close() error {
	if m.disposed {
		return nil
	}
	m.disposed = true
	if c, ok := m.downloader.(io.Closer); ok {
		return c.Close()
	}
	return nil
}

func sanitizeModelID(modelId string) string {
	return strings.NewReplacer("/", "_", "\\", "_").Replace(modelId)
}

func computeFileChecksum(path string) ([]byte, error) {
	f, err := os.Open(path)
	if err != nil {
		return nil, err
	}
	defer f.Close()
	h := sha256.New()
	if _, err := io.Copy(h, f); err != nil {
		return nil, err
	}
	return h.Sum(nil), nil
}

func fileExists(path string) bool {
	info, err := os.Stat(path)
	return err == nil && !info.IsDir()
}

func bytesEqual(a, b []byte) bool {
	if len(a) != len(b) {
		return false
	}
	for i := range a {
		if a[i] != b[i] {
			return false
		}
	}
	return true
}

var _ IModelManager = (*LocalModelManager)(nil)
