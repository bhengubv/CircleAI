// telephony/hold_music_mixer.ts
//
// Background-audio mixer for call-on-hold experiences — faithful port of
// HoldMusicMixer.cs. Loops a music track and mixes the AI's speech on top at
// adjustable gain. Ducks the background automatically when speech frames arrive.
//
// Float gains use Math.fround (C# `float`). PCM-16 samples are read/written
// little-endian. The `bgSample * gain` product is `int`-truncated exactly as
// C# `(int)(bgSample * gain)` before summing.

const INT16_BYTES = 2;
const INT16_MAX = 32767;
const INT16_MIN = -32768;

function clampInt(value: number, min: number, max: number): number {
  return value < min ? min : value > max ? max : value;
}

/** Background audio mixer for hold music. Mirrors `HoldMusicMixer`. */
export class HoldMusicMixer {
  private readonly backgroundLoop: Uint8Array;
  private readonly backgroundView: DataView;
  private readonly backgroundGain: number;
  private readonly duckedGain: number;
  private loopCursor = 0;

  /**
   * @param backgroundLoop PCM-16 mono buffer that the mixer loops over.
   * @param backgroundGain Gain when no speech (0..1). Default 0.6.
   * @param duckedGain Gain while speech is being mixed (0..1). Default 0.15.
   */
  constructor(backgroundLoop: Uint8Array, backgroundGain = 0.6, duckedGain = 0.15) {
    if (backgroundLoop === null || backgroundLoop === undefined) {
      throw new Error("backgroundLoop is required");
    }
    if (backgroundLoop.byteLength < INT16_BYTES) {
      throw new Error("Background loop must contain at least one PCM-16 sample.");
    }
    if (backgroundGain < 0 || backgroundGain > 1) throw new RangeError("backgroundGain");
    if (duckedGain < 0 || duckedGain > 1) throw new RangeError("duckedGain");
    this.backgroundLoop = backgroundLoop;
    this.backgroundView = new DataView(
      backgroundLoop.buffer,
      backgroundLoop.byteOffset,
      backgroundLoop.byteLength,
    );
    this.backgroundGain = Math.fround(backgroundGain);
    this.duckedGain = Math.fround(duckedGain);
  }

  /** Reset the loop cursor to the start. */
  reset(): void {
    this.loopCursor = 0;
  }

  /**
   * Mix `speechFrame` on top of looped background and write the result into
   * `destination`. Pass an empty speech buffer to render plain background.
   * Returns the number of bytes written.
   */
  mixFrame(speechFrame: Uint8Array, destination: Uint8Array): number {
    if (destination.byteLength < INT16_BYTES) return 0;
    const hasSpeech = speechFrame.byteLength >= INT16_BYTES;
    const frameLength = hasSpeech ? speechFrame.byteLength : destination.byteLength;
    if (destination.byteLength < frameLength) {
      throw new Error("destination must be at least as long as the speech frame.");
    }

    const gain = hasSpeech ? this.duckedGain : this.backgroundGain;
    const speechView = hasSpeech
      ? new DataView(speechFrame.buffer, speechFrame.byteOffset, speechFrame.byteLength)
      : null;
    const destView = new DataView(destination.buffer, destination.byteOffset, destination.byteLength);
    const loopLen = this.backgroundLoop.byteLength;

    for (let i = 0; i < frameLength; i += INT16_BYTES) {
      const speechSample = hasSpeech ? speechView!.getInt16(i, true) : 0;

      // Pull background sample from the loop, wrapping as needed.
      const bgSample = this.backgroundView.getInt16(this.loopCursor, true);
      this.loopCursor = (this.loopCursor + INT16_BYTES) % loopLen;
      if (this.loopCursor % 2 !== 0) this.loopCursor--; // align to 16-bit boundary

      const mixed = clampInt(speechSample + Math.trunc(bgSample * gain), INT16_MIN, INT16_MAX);
      destView.setInt16(i, mixed, true);
    }
    return frameLength;
  }
}
