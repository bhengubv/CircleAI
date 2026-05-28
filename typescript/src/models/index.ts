// models/index.ts
// Core message and progress types shared across Circle AI modules.

/**
 * A single message in a chat conversation.
 * Role is one of "system" | "user" | "assistant".
 */
export interface ChatMessage {
  readonly role: string;
  readonly content: string;
}

/**
 * Progress report for a file download operation.
 */
export interface DownloadProgress {
  readonly fileName: string;
  readonly bytesReceived: number;
  readonly totalBytes: number;
  readonly bytesPerSecond: number;
  /** Estimated seconds remaining. */
  readonly estimatedTimeRemaining: number;
}
