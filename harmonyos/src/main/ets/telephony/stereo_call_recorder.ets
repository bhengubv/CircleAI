/**
 * telephony/stereo_call_recorder
 *
 * ArkTS for HarmonyOS. Ported from the TypeScript source of the same module by
 * `harmonyos/tools/gen_arkts.py`: ArkTS is TypeScript without the dynamic
 * parts, so the logic is shared rather than retyped, and what differs is the
 * platform - Node's filesystem, paths and cryptography are reached here through
 * `src/main/ets/platform.ets`, which is over `@ohos.*`.
 *
 * Edit the TypeScript source, not this file.
 */

// telephony/stereo_call_recorder.ts
//
// Interleave inbound (caller) and outbound (agent) PCM-16 mono audio into a
// single stereo WAV. Left channel = caller, right = agent. Faithful port of
// StereoCallRecorder.cs.
//
// The C# writes to an injected `Stream` and backfills the 44-byte RIFF/WAVE
// header on Finalize (when the stream can seek). JavaScript has no ambient
// seekable file stream, so the deterministic, framework-free equivalent buffers
// the interleaved sample bytes in memory and materialises the complete WAV
// (header always backfilled) via {@link toWav} / {@link finalize}. Hosts that
// need to stream to disk write the returned buffer through their own I/O — the
// injected boundary the C# `Stream` represented.

const INT16_BYTES = 2;
const STEREO_FRAME_BYTES = 4; // 2 channels × 2 bytes
const WAV_HEADER_BYTES = 44;

/** Records a call to a stereo PCM-16 WAV. Mirrors `StereoCallRecorder`. */
export class StereoCallRecorder {
  private readonly sampleRateHz: number;
  private readonly samples: number[] = []; // interleaved int16 sample values (L, R, L, R, …)
  private samplesWritten = 0; // total interleaved sample *pairs*
  private finalised = false;

  constructor(sampleRateHz: number) {
    if (sampleRateHz <= 0) throw new RangeError("sampleRateHz");
    this.sampleRateHz = sampleRateHz;
  }

  /** Write inbound (caller) PCM-16 mono audio. Caller side is the left channel. */
  writeCallerFrame(pcmFrame: Uint8Array): void {
    this.writeSide(pcmFrame, true);
  }

  /** Write outbound (agent) PCM-16 mono audio. Agent side is the right channel. */
  writeAgentFrame(pcmFrame: Uint8Array): void {
    this.writeSide(pcmFrame, false);
  }

  /** Finalise the recording. After this, no more writes are allowed. Mirrors `Finalize`. */
  finalize(): void {
    this.finalised = true;
  }

  /** Total interleaved sample pairs written so far. */
  get samplePairs(): number {
    return this.samplesWritten;
  }

  /** Materialise the complete stereo PCM-16 WAV (header + data) as bytes. */
  toWav(): Uint8Array {
    const dataSize = this.samplesWritten * STEREO_FRAME_BYTES;
    const chunkSize = 36 + dataSize;
    const out = new Uint8Array(WAV_HEADER_BYTES + dataSize);
    const view = new DataView(out.buffer);

    // RIFF header.
    out[0] = 0x52; // 'R'
    out[1] = 0x49; // 'I'
    out[2] = 0x46; // 'F'
    out[3] = 0x46; // 'F'
    view.setInt32(4, chunkSize, true);
    out[8] = 0x57; // 'W'
    out[9] = 0x41; // 'A'
    out[10] = 0x56; // 'V'
    out[11] = 0x45; // 'E'
    // fmt  subchunk.
    out[12] = 0x66; // 'f'
    out[13] = 0x6d; // 'm'
    out[14] = 0x74; // 't'
    out[15] = 0x20; // ' '
    view.setInt32(16, 16, true); // Subchunk1Size
    view.setInt16(20, 1, true); // PCM
    view.setInt16(22, 2, true); // channels
    view.setInt32(24, this.sampleRateHz, true);
    view.setInt32(28, this.sampleRateHz * STEREO_FRAME_BYTES, true); // byte rate
    view.setInt16(32, STEREO_FRAME_BYTES, true); // block align
    view.setInt16(34, 16, true); // bits per sample
    // data subchunk.
    out[36] = 0x64; // 'd'
    out[37] = 0x61; // 'a'
    out[38] = 0x74; // 't'
    out[39] = 0x61; // 'a'
    view.setInt32(40, dataSize, true);

    // Interleaved PCM data.
    let offset = WAV_HEADER_BYTES;
    for (const s of this.samples) {
      view.setInt16(offset, s, true);
      offset += INT16_BYTES;
    }
    return out;
  }

  private writeSide(pcmFrame: Uint8Array, isCaller: boolean): void {
    if (pcmFrame.byteLength < INT16_BYTES) return;
    if (this.finalised) return; // no writes after finalize (matches C# header-locked semantics)
    const view = new DataView(pcmFrame.buffer, pcmFrame.byteOffset, pcmFrame.byteLength);
    const sampleCount = Math.trunc(pcmFrame.byteLength / INT16_BYTES);
    for (let i = 0; i < sampleCount; i++) {
      const mono = view.getInt16(i * INT16_BYTES, true);
      if (isCaller) {
        this.samples.push(mono, 0); // left = caller, right = silence
      } else {
        this.samples.push(0, mono); // left = silence, right = agent
      }
      this.samplesWritten++;
    }
  }
}
