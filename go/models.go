// models.go
//
// Shared primitive types used across multiple Circle AI modules.
// ChatMessage lives here alongside DownloadProgress so that modules
// that only need the message type don't have to import the full
// inference module.

package circleai

// ChatMessage is a single message in a chat history.
// Role is one of "system", "user", or "assistant".
type ChatMessage struct {
	Role    string
	Content string
}

// DownloadProgress is a progress report for a model or asset download.
type DownloadProgress struct {
	BytesReceived int64
	TotalBytes    *int64 // nil when Content-Length is unknown
}

// Fraction returns the 0.0–1.0 fraction complete, or nil when total is unknown.
func (d DownloadProgress) Fraction() *float64 {
	if d.TotalBytes == nil || *d.TotalBytes == 0 {
		return nil
	}
	f := float64(d.BytesReceived) / float64(*d.TotalBytes)
	return &f
}
