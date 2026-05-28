// models/index.ets — shared primitive types used across Circle AI modules
// ArkTS port: no `any`, explicit types throughout.

/**
 * A single message in a chat history.
 * role is one of "system", "user", or "assistant".
 */
export interface ChatMessage {
  id: string;
  role: string;   // 'user' | 'assistant' | 'system'
  content: string;
  createdAt: number;  // unix ms
}

/**
 * Progress event emitted during a model or file download.
 */
export interface DownloadProgress {
  totalBytes: number;
  downloadedBytes: number;
  filename: string;
}

/**
 * Returns the fraction [0.0–1.0] of a download completed.
 * Returns 0 when totalBytes is zero to avoid division by zero.
 */
export function fractionComplete(p: DownloadProgress): number {
  return p.totalBytes === 0 ? 0 : p.downloadedBytes / p.totalBytes;
}
