// telephony/sentence_chunker.ts
//
// Stream-friendly sentence chunker — faithful port of SentenceChunker.cs.
// Accepts streamed LLM tokens and emits whole sentences as soon as they're
// complete, so TTS can speak them before the full response finishes — cuts
// time-to-first-audio dramatically.

const TERMINAL_PUNCTUATION: ReadonlySet<string> = new Set([".", "!", "?", "。", "！", "？"]);

function isWhiteSpace(ch: string): boolean {
  return /\s/.test(ch);
}

/** Streaming sentence chunker. Mirrors `SentenceChunker`. */
export class SentenceChunker {
  private buffer = "";
  private readonly minSentenceLength: number;

  /**
   * @param minSentenceLength Sentences below this character count are buffered
   *   with the next one (avoids "1." / "Mr." splits). Default 4.
   */
  constructor(minSentenceLength = 4) {
    this.minSentenceLength = minSentenceLength;
  }

  /** Push a token; receive any complete sentences ready to emit. */
  pushToken(token: string): string[] {
    const ready: string[] = [];
    if (!token) return ready;

    this.buffer += token;
    for (;;) {
      const { chunk, kept } = this.extractNext(this.buffer);
      if (chunk === null) break;
      this.buffer = kept;
      ready.push(chunk);
    }
    return ready;
  }

  /** Flush whatever's buffered as a final fragment, regardless of punctuation. */
  flush(): string {
    const s = this.buffer;
    this.buffer = "";
    return s;
  }

  private extractNext(buffer: string): { chunk: string | null; kept: string } {
    let searchFrom = 0;
    while (searchFrom < buffer.length) {
      const idx = SentenceChunker.indexOfAny(buffer, searchFrom);
      if (idx < 0) return { chunk: null, kept: buffer };

      // Consume any trailing whitespace + closing quotes after the punctuation.
      let end = idx + 1;
      while (
        end < buffer.length &&
        (isWhiteSpace(buffer[end]!) ||
          buffer[end] === '"' ||
          buffer[end] === "'" ||
          buffer[end] === ")")
      ) {
        end++;
      }

      const candidate = buffer.slice(0, end).trim();
      if (candidate.length >= this.minSentenceLength) {
        return { chunk: candidate, kept: buffer.slice(end) };
      }
      // Too short — keep extending past this punctuation.
      searchFrom = end;
    }
    return { chunk: null, kept: buffer };
  }

  private static indexOfAny(buffer: string, from: number): number {
    for (let i = from; i < buffer.length; i++) {
      if (TERMINAL_PUNCTUATION.has(buffer[i]!)) return i;
    }
    return -1;
  }
}
