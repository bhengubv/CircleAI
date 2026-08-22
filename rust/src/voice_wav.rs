//! voice_wav — minimal RIFF/WAVE reading and PCM-16 packing.
//!
//! Port of `src/CircleAI.Voice/WavIo.cs`, so a reference recording can become
//! the float samples a voice needs.
//!
//! Parity is asserted against `fixtures/voice_wav_io.json`.

/// Mimi's sample rate — what [`read_mono_24k`] resamples to.
pub const TARGET_RATE: u32 = 24_000;

#[derive(Debug)]
pub enum WavError {
    NotRiff,
    NoUsableChunk,
    Unsupported { format: u16, bits: u16 },
}

impl std::fmt::Display for WavError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        match self {
            WavError::NotRiff => write!(f, "not a RIFF/WAVE file"),
            WavError::NoUsableChunk => write!(f, "no usable fmt/data chunk"),
            WavError::Unsupported { format, bits } => {
                write!(f, "WAV format {format} at {bits} bits is not decoded by this reader")
            }
        }
    }
}

impl std::error::Error for WavError {}

/// Decoded WAV: interleaved float samples, plus rate and channel count.
pub struct Wav {
    pub samples: Vec<f32>,
    pub rate: u32,
    pub channels: u16,
}

/// Parse a RIFF/WAVE buffer.
pub fn parse(raw: &[u8]) -> Result<Wav, WavError> {
    if raw.len() < 12
        || be32(raw, 0) != 0x5249_4646  // "RIFF"
        || be32(raw, 8) != 0x5741_5645
    // "WAVE"
    {
        return Err(WavError::NotRiff);
    }

    let (mut format, mut channels, mut rate, mut bits) = (0u16, 0u16, 0u32, 0u16);
    let mut data: &[u8] = &[];
    let mut offset = 12usize;

    // WALK THE CHUNKS. A WAV written by anything other than the simplest encoder
    // carries LIST/fact/cue chunks before the data, and assuming data starts at
    // byte 44 reads metadata as audio — which sounds like a short burst of noise
    // before the real recording.
    while offset + 8 <= raw.len() {
        let id = be32(raw, offset);
        let declared = i32::from_le_bytes([
            raw[offset + 4],
            raw[offset + 5],
            raw[offset + 6],
            raw[offset + 7],
        ]);
        let body = offset + 8;
        let size = if declared < 0 || body + declared as usize > raw.len() {
            raw.len() - body
        } else {
            declared as usize
        };

        match id {
            0x666D_7420 => {
                // "fmt "
                format = le16(raw, body);
                channels = le16(raw, body + 2);
                rate = le32(raw, body + 4);
                bits = le16(raw, body + 14);
            }
            0x6461_7461 => data = &raw[body..body + size], // "data"
            _ => {}
        }

        offset = body + size + (size & 1); // chunks are word-aligned
    }

    if channels == 0 || rate == 0 || data.is_empty() {
        return Err(WavError::NoUsableChunk);
    }

    // 3 is IEEE float; 0xFFFE is WAVE_FORMAT_EXTENSIBLE, whose real format lives
    // in a sub-chunk — treated as PCM here, which is what it is in every file
    // the voice stack has met.
    let pcm = format == 1 || format == 0xFFFE;
    let samples: Vec<f32> = match (pcm, format, bits) {
        (true, _, 8) => data.iter().map(|&b| (b as i32 - 128) as f32 / 128.0).collect(),
        (true, _, 16) => data
            .chunks_exact(2)
            .map(|c| i16::from_le_bytes([c[0], c[1]]) as f32 / 32768.0)
            .collect(),
        (true, _, 24) => data
            .chunks_exact(3)
            .map(|c| {
                let v = (c[0] as i32) | (c[1] as i32) << 8 | (c[2] as i32) << 16;
                ((v << 8) >> 8) as f32 / 8_388_608.0
            })
            .collect(),
        (true, _, 32) => data
            .chunks_exact(4)
            .map(|c| i32::from_le_bytes([c[0], c[1], c[2], c[3]]) as f32 / 2_147_483_648.0)
            .collect(),
        (_, 3, 32) => data
            .chunks_exact(4)
            .map(|c| f32::from_le_bytes([c[0], c[1], c[2], c[3]]))
            .collect(),
        _ => return Err(WavError::Unsupported { format, bits }),
    };

    Ok(Wav { samples, rate, channels })
}

/// Read a WAV file as mono float samples at 24 kHz, resampling if needed.
pub fn read_mono_24k(path: &str, max_seconds: usize) -> Result<Vec<f32>, Box<dyn std::error::Error>> {
    let raw = std::fs::read(path)?;
    let wav = parse(&raw)?;
    Ok(to_mono_24k(&wav, max_seconds))
}

/// Downmix to mono, resample to 24 kHz, and cap the length.
pub fn to_mono_24k(wav: &Wav, max_seconds: usize) -> Vec<f32> {
    let channels = wav.channels as usize;
    let mut samples = if channels > 1 {
        wav.samples
            .chunks_exact(channels)
            .map(|frame| frame.iter().sum::<f32>() / channels as f32)
            .collect()
    } else {
        wav.samples.clone()
    };

    if wav.rate != TARGET_RATE {
        samples = resample(&samples, wav.rate, TARGET_RATE);
    }

    let cap = max_seconds * TARGET_RATE as usize;
    if samples.len() > cap {
        samples.truncate(cap);
    }
    samples
}

/// Pack float samples in [-1,1] as little-endian signed 16-bit PCM.
pub fn to_pcm16(samples: &[f32]) -> Vec<u8> {
    let mut out = Vec::with_capacity(samples.len() * 2);
    for &s in samples {
        let v = (s.clamp(-1.0, 1.0) * i16::MAX as f32) as i16;
        out.extend_from_slice(&v.to_le_bytes());
    }
    out
}

/// Linear resample. Adequate here: the target is a speaker embedding, not playback.
fn resample(input: &[f32], from: u32, to: u32) -> Vec<f32> {
    if input.is_empty() {
        return Vec::new();
    }
    let count = ((input.len() as f64) * to as f64 / from as f64).round() as usize;
    let count = count.max(1);
    let step = (input.len() - 1) as f64 / (count.saturating_sub(1)).max(1) as f64;
    (0..count)
        .map(|i| {
            let x = i as f64 * step;
            let lo = x as usize;
            let hi = (lo + 1).min(input.len() - 1);
            (input[lo] as f64 + (input[hi] as f64 - input[lo] as f64) * (x - lo as f64)) as f32
        })
        .collect()
}

fn be32(b: &[u8], i: usize) -> u32 {
    u32::from_be_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}
fn le32(b: &[u8], i: usize) -> u32 {
    u32::from_le_bytes([b[i], b[i + 1], b[i + 2], b[i + 3]])
}
fn le16(b: &[u8], i: usize) -> u16 {
    u16::from_le_bytes([b[i], b[i + 1]])
}
