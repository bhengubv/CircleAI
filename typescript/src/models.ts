// models.ts — shared primitive types used across Circle AI modules

/**
 * A single message in a chat history.
 * role is one of "system", "user", or "assistant".
 */
export interface ChatMessage {
  readonly role: string;
  readonly content: string;
}

/**
 * Progress event emitted during a model or file download.
 */
export interface DownloadProgress {
  /** Bytes received so far. */
  readonly bytesReceived: number;
  /** Total bytes expected, or null when unknown. */
  readonly totalBytes: number | null;
  /** Fraction complete [0.0–1.0], or null when totalBytes is unknown. */
  readonly fraction: number | null;
  /** Human-readable label (e.g. "Downloading gemma-3-1b-it.gguf"). */
  readonly label: string;
}
