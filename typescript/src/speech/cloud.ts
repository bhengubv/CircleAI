// Cloud speech and image providers, and the rules that keep them last.
//
// SENDING AUDIO IS DIFFERENT FROM SENDING TEXT. A transcript is what somebody
// said; a recording is their VOICE - who they are, who else was in the room,
// and a biometric they cannot change. So every provider here is off by default,
// consented to individually, and named in the result so a person can be told
// where their voice went.
//
// THE ORDER IS ON-DEVICE FIRST, ALWAYS. These exist for the languages and
// accents an on-device model handles badly, and for nothing else. A build that
// reaches for a cloud recogniser because it is more accurate in English has
// sent every South African household's audio to a company to solve a problem
// they did not have.
//
// The keys never appear in a log, an error, a repr or a URL - the same _Secret
// discipline as the chat providers, for the same reason: a key reaches a log
// the ordinary way, and nobody decided that.

// ─────────────────────────────────────────────────────────────────────────────
// Secrets and shared shape

/**
 * Holds a key so it cannot be printed by accident.
 *
 * `toString` and `toJSON` both redact - `toJSON` matters more here than
 * anywhere, because an options object reaches a log through
 * `JSON.stringify(config)` far more often than through a deliberate print.
 */
export class SecretValue {
  constructor(private readonly value = "") {}

  /** The ONE way out, named so it is visible at every call site. */
  reveal(): string {
    return this.value;
  }

  get isSet(): boolean {
    return this.value.length > 0;
  }

  toString(): string {
    return this.isSet ? "<secret set>" : "<secret unset>";
  }

  toJSON(): string {
    return this.toString();
  }
}

/** What every cloud speech provider needs. */
export interface CloudSpeechOptionsBase {
  /** OFF. A build that carries a provider does not use it. */
  readonly enabled: boolean;
  readonly baseUrl: string;
  readonly model: string;
  readonly language: string;
  readonly timeoutSeconds: number;
  readonly apiKey: SecretValue;
}

const speechOptions = (
  defaults: { baseUrl: string; model?: string },
  partial: Partial<CloudSpeechOptionsBase> = {},
): CloudSpeechOptionsBase =>
  Object.freeze({
    enabled: partial.enabled ?? false,
    baseUrl: partial.baseUrl ?? defaults.baseUrl,
    model: partial.model ?? defaults.model ?? "",
    language: partial.language ?? "",
    timeoutSeconds: partial.timeoutSeconds ?? 60,
    apiKey: partial.apiKey ?? new SecretValue(),
  });

export const isSpeechConfigured = (o: CloudSpeechOptionsBase): boolean =>
  o.enabled && o.apiKey.isSet && o.baseUrl.length > 0;

// The per-provider option types. Each carries its own defaults so a host does
// not have to know a base URL, and its own extras where the provider needs one.

export type AssemblyAiOptions = CloudSpeechOptionsBase;
export const assemblyAiOptions = (p: Partial<CloudSpeechOptionsBase> = {}): AssemblyAiOptions =>
  speechOptions({ baseUrl: "https://api.assemblyai.com/v2", model: "best" }, p);

export interface AzureSpeechOptions extends CloudSpeechOptionsBase {
  /** Azure keys are REGION-BOUND. A key from one region against another's
   * endpoint returns 401, which reads exactly like a bad key. */
  readonly region: string;
}
export const azureSpeechOptions = (
  p: Partial<AzureSpeechOptions> = {},
): AzureSpeechOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://REGION.stt.speech.microsoft.com" }, p),
    region: p.region ?? "",
  });

export interface AzureTtsOptions extends CloudSpeechOptionsBase {
  readonly region: string;
  readonly voice: string;
}
export const azureTtsOptions = (p: Partial<AzureTtsOptions> = {}): AzureTtsOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://REGION.tts.speech.microsoft.com" }, p),
    region: p.region ?? "",
    voice: p.voice ?? "",
  });

export type CartesiaSttOptions = CloudSpeechOptionsBase;
export const cartesiaSttOptions = (p: Partial<CloudSpeechOptionsBase> = {}): CartesiaSttOptions =>
  speechOptions({ baseUrl: "https://api.cartesia.ai", model: "ink-whisper" }, p);

export interface CartesiaTtsOptions extends CloudSpeechOptionsBase {
  readonly voiceId: string;
}
export const cartesiaTtsOptions = (p: Partial<CartesiaTtsOptions> = {}): CartesiaTtsOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.cartesia.ai", model: "sonic-2" }, p),
    voiceId: p.voiceId ?? "",
  });

export type DeepgramOptions = CloudSpeechOptionsBase;
export const deepgramOptions = (p: Partial<CloudSpeechOptionsBase> = {}): DeepgramOptions =>
  speechOptions({ baseUrl: "https://api.deepgram.com/v1", model: "nova-3" }, p);

export interface DeepgramTtsOptions extends CloudSpeechOptionsBase {
  readonly voice: string;
}
export const deepgramTtsOptions = (p: Partial<DeepgramTtsOptions> = {}): DeepgramTtsOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.deepgram.com/v1", model: "aura-2" }, p),
    voice: p.voice ?? "",
  });

export interface ElevenLabsOptions extends CloudSpeechOptionsBase {
  readonly voiceId: string;
}
export const elevenLabsOptions = (p: Partial<ElevenLabsOptions> = {}): ElevenLabsOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.elevenlabs.io/v1", model: "eleven_multilingual_v2" }, p),
    voiceId: p.voiceId ?? "",
  });

export type GoogleSpeechOptions = CloudSpeechOptionsBase;
export const googleSpeechOptions = (p: Partial<CloudSpeechOptionsBase> = {}): GoogleSpeechOptions =>
  speechOptions({ baseUrl: "https://speech.googleapis.com/v1", model: "latest_long" }, p);

export interface GoogleTtsOptions extends CloudSpeechOptionsBase {
  readonly voice: string;
}
export const googleTtsOptions = (p: Partial<GoogleTtsOptions> = {}): GoogleTtsOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://texttospeech.googleapis.com/v1" }, p),
    voice: p.voice ?? "",
  });

export interface OpenAiVoiceOptions extends CloudSpeechOptionsBase {
  readonly voice: string;
}
export const openAiVoiceOptions = (p: Partial<OpenAiVoiceOptions> = {}): OpenAiVoiceOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.openai.com/v1", model: "whisper-1" }, p),
    voice: p.voice ?? "alloy",
  });

export interface PlayHtOptions extends CloudSpeechOptionsBase {
  /** PlayHT needs a user id ALONGSIDE the key. A request with only the key gets
   * a 403 that says nothing about which of the two is missing. */
  readonly userId: string;
  readonly voice: string;
}
export const playHtOptions = (p: Partial<PlayHtOptions> = {}): PlayHtOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.play.ht/api/v2", model: "PlayDialog" }, p),
    userId: p.userId ?? "",
    voice: p.voice ?? "",
  });

// ─────────────────────────────────────────────────────────────────────────────
// Recognisers

/** What came back from a transcription. */
export interface CloudTranscription {
  readonly text: string;
  /** The provider that heard it. ALWAYS carried, so a caller can tell a person
   * where their voice went. */
  readonly providerId: string;
  /** Undefined when the provider did not say. Zero is a real answer meaning "no
   * idea", and the two must not be confused. */
  readonly confidence: number | undefined;
  readonly language: string;
  readonly error: string;
}

const transcription = (partial: Partial<CloudTranscription> = {}): CloudTranscription =>
  Object.freeze({
    text: partial.text ?? "",
    providerId: partial.providerId ?? "",
    confidence: partial.confidence,
    language: partial.language ?? "",
    error: partial.error ?? "",
  });

/** Turns audio into text, somewhere else. */
export interface CloudSpeechRecognizer {
  readonly providerId: string;
  readonly isAvailable: boolean;
  transcribe(audio: Uint8Array, mimeType: string, language?: string): Promise<CloudTranscription>;
}

/**
 * What the recognisers share.
 *
 * Each subclass overrides the URL, the headers and the shape of the reply, and
 * nothing else. The interesting behaviour - the availability check, the refusal
 * wording, the error that names the provider and not the key - is here, once.
 */
abstract class CloudSpeechRecognizerBase implements CloudSpeechRecognizer {
  constructor(
    readonly providerId: string,
    protected readonly options: CloudSpeechOptionsBase,
    protected readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: Uint8Array,
    ) => Promise<Record<string, unknown>>,
  ) {}

  /** Configured AND given a transport. A recogniser with a key and no way to
   * send it is not available, and reporting otherwise makes a caller choose a
   * provider that then fails. */
  get isAvailable(): boolean {
    return isSpeechConfigured(this.options) && this.post !== undefined;
  }

  protected abstract url(language: string): string;
  protected abstract headers(mimeType: string): Record<string, string>;
  protected abstract parse(raw: Record<string, unknown>): CloudTranscription;

  async transcribe(audio: Uint8Array, mimeType: string, language = ""): Promise<CloudTranscription> {
    if (!this.isAvailable) {
      // Names what is missing WITHOUT naming the key, and says "not configured"
      // rather than "auth failed" - the second sends somebody to rotate a
      // credential that was never the problem.
      return transcription({
        providerId: this.providerId,
        error: `${this.providerId} is not configured on this device`,
      });
    }
    if (audio.length === 0) {
      return transcription({ providerId: this.providerId, error: "there is no audio to send" });
    }
    try {
      const raw = await this.post!(
        this.url(language || this.options.language),
        this.headers(mimeType),
        audio,
      );
      return this.parse(raw);
    } catch (error) {
      return transcription({
        providerId: this.providerId,
        error: `${this.providerId} did not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/** OpenAI's transcription endpoint. */
export class OpenAiSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(options: OpenAiVoiceOptions = openAiVoiceOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("openai", options, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/audio/transcriptions`;
  }
  protected headers(mimeType: string): Record<string, string> {
    return {
      Authorization: `Bearer ${this.options.apiKey.reveal()}`,
      "Content-Type": mimeType,
    };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    return transcription({ text: String(raw.text ?? ""), providerId: this.providerId });
  }
}

/** Deepgram. */
export class DeepgramSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(options: DeepgramOptions = deepgramOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("deepgram", options, post);
  }
  protected url(language: string): string {
    // Deepgram takes its options in the QUERY STRING, which is why the language
    // is here and not in a body. No key ever goes in a query string.
    const params = new URLSearchParams({ model: this.options.model, smart_format: "true" });
    if (language) params.set("language", language);
    return `${this.options.baseUrl}/listen?${params.toString()}`;
  }
  protected headers(mimeType: string): Record<string, string> {
    return { Authorization: `Token ${this.options.apiKey.reveal()}`, "Content-Type": mimeType };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    const results = raw.results as { channels?: { alternatives?: { transcript?: string; confidence?: number }[] }[] } | undefined;
    const best = results?.channels?.[0]?.alternatives?.[0];
    return transcription({
      text: best?.transcript ?? "",
      providerId: this.providerId,
      confidence: best?.confidence,
    });
  }
}

/** AssemblyAI. */
export class AssemblyAiSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(options: AssemblyAiOptions = assemblyAiOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("assemblyai", options, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/transcript`;
  }
  protected headers(): Record<string, string> {
    // `authorization` bare, not a bearer. Sending a bearer gets a 401 that
    // reads exactly like a bad key.
    return { authorization: this.options.apiKey.reveal(), "content-type": "application/json" };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    return transcription({
      text: String(raw.text ?? ""),
      providerId: this.providerId,
      confidence: typeof raw.confidence === "number" ? raw.confidence : undefined,
    });
  }
}

/** Azure. */
export class AzureSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(private readonly azure: AzureSpeechOptions = azureSpeechOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("azure", azure, post);
  }
  /** Needs a REGION as well as a key. Without one the endpoint is a template. */
  get isAvailable(): boolean {
    return super.isAvailable && this.azure.region.length > 0;
  }
  protected url(language: string): string {
    const base = this.options.baseUrl.replace("REGION", this.azure.region);
    return `${base}/speech/recognition/conversation/cognitiveservices/v1?language=${language || "en-US"}`;
  }
  protected headers(mimeType: string): Record<string, string> {
    return {
      "Ocp-Apim-Subscription-Key": this.options.apiKey.reveal(),
      "Content-Type": mimeType,
    };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    return transcription({ text: String(raw.DisplayText ?? ""), providerId: this.providerId });
  }
}

/** Google. */
export class GoogleSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(options: GoogleSpeechOptions = googleSpeechOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("google", options, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/speech:recognize`;
  }
  protected headers(): Record<string, string> {
    // A HEADER, never `?key=` in the URL. A key in a query string reaches every
    // proxy log and browser history between here and there.
    return {
      "x-goog-api-key": this.options.apiKey.reveal(),
      "Content-Type": "application/json",
    };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    const results = raw.results as { alternatives?: { transcript?: string; confidence?: number }[] }[] | undefined;
    const best = results?.[0]?.alternatives?.[0];
    return transcription({
      text: best?.transcript ?? "",
      providerId: this.providerId,
      confidence: best?.confidence,
    });
  }
}

/** Cartesia. */
export class CartesiaSpeechRecognizer extends CloudSpeechRecognizerBase {
  constructor(options: CartesiaSttOptions = cartesiaSttOptions(), post?: CloudSpeechRecognizerBase["post"]) {
    super("cartesia", options, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/stt`;
  }
  protected headers(mimeType: string): Record<string, string> {
    return {
      "X-API-Key": this.options.apiKey.reveal(),
      // Cartesia pins an API VERSION by date and rejects a request without it.
      // Pinned rather than tracking latest, so a change on their side never
      // changes what this build sends.
      "Cartesia-Version": "2024-06-10",
      "Content-Type": mimeType,
    };
  }
  protected parse(raw: Record<string, unknown>): CloudTranscription {
    return transcription({ text: String(raw.text ?? ""), providerId: this.providerId });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Synthesisers

/** What came back from a synthesis. */
export interface CloudSpeechAudio {
  readonly audio: Uint8Array;
  readonly mimeType: string;
  readonly providerId: string;
  readonly error: string;
}

const speechAudio = (partial: Partial<CloudSpeechAudio> = {}): CloudSpeechAudio =>
  Object.freeze({
    audio: partial.audio ?? new Uint8Array(0),
    mimeType: partial.mimeType ?? "audio/mpeg",
    providerId: partial.providerId ?? "",
    error: partial.error ?? "",
  });

/** Turns text into audio, somewhere else. */
export interface CloudSpeechSynthesizer {
  readonly providerId: string;
  readonly isAvailable: boolean;
  synthesize(text: string, language?: string): Promise<CloudSpeechAudio>;
}

abstract class CloudSpeechSynthesizerBase implements CloudSpeechSynthesizer {
  constructor(
    readonly providerId: string,
    protected readonly options: CloudSpeechOptionsBase,
    protected readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: string,
    ) => Promise<Uint8Array>,
  ) {}

  get isAvailable(): boolean {
    return isSpeechConfigured(this.options) && this.post !== undefined;
  }

  protected abstract url(): string;
  protected abstract headers(): Record<string, string>;
  protected abstract body(text: string, language: string): string;
  protected mimeType(): string {
    return "audio/mpeg";
  }

  async synthesize(text: string, language = ""): Promise<CloudSpeechAudio> {
    if (!this.isAvailable) {
      return speechAudio({
        providerId: this.providerId,
        error: `${this.providerId} is not configured on this device`,
      });
    }
    if (!text.trim()) {
      // Empty text is EMPTY AUDIO, not an error. A caller synthesising a
      // silence should get a silence.
      return speechAudio({ providerId: this.providerId });
    }
    try {
      return speechAudio({
        audio: await this.post!(this.url(), this.headers(), this.body(text, language || this.options.language)),
        mimeType: this.mimeType(),
        providerId: this.providerId,
      });
    } catch (error) {
      return speechAudio({
        providerId: this.providerId,
        error: `${this.providerId} did not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/** OpenAI. */
export class OpenAiSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly voiceOptions: OpenAiVoiceOptions = openAiVoiceOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("openai", voiceOptions, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/audio/speech`;
  }
  protected headers(): Record<string, string> {
    return {
      Authorization: `Bearer ${this.options.apiKey.reveal()}`,
      "Content-Type": "application/json",
    };
  }
  protected body(text: string): string {
    return JSON.stringify({ model: "tts-1", input: text, voice: this.voiceOptions.voice });
  }
}

/** ElevenLabs. */
export class ElevenLabsSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly eleven: ElevenLabsOptions = elevenLabsOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("elevenlabs", eleven, post);
  }
  /** Needs a VOICE as well as a key: the voice id is in the path, so without
   * one there is no endpoint to call. */
  get isAvailable(): boolean {
    return super.isAvailable && this.eleven.voiceId.length > 0;
  }
  protected url(): string {
    return `${this.options.baseUrl}/text-to-speech/${this.eleven.voiceId}`;
  }
  protected headers(): Record<string, string> {
    return { "xi-api-key": this.options.apiKey.reveal(), "Content-Type": "application/json" };
  }
  protected body(text: string): string {
    return JSON.stringify({ text, model_id: this.options.model });
  }
}

/** Deepgram. */
export class DeepgramSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly deepgram: DeepgramTtsOptions = deepgramTtsOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("deepgram", deepgram, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/speak?model=${this.deepgram.voice || this.options.model}`;
  }
  protected headers(): Record<string, string> {
    return { Authorization: `Token ${this.options.apiKey.reveal()}`, "Content-Type": "application/json" };
  }
  protected body(text: string): string {
    return JSON.stringify({ text });
  }
}

/** Google. */
export class GoogleSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly google: GoogleTtsOptions = googleTtsOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("google", google, post);
  }
  protected url(): string {
    return `${this.options.baseUrl}/text:synthesize`;
  }
  protected headers(): Record<string, string> {
    return { "x-goog-api-key": this.options.apiKey.reveal(), "Content-Type": "application/json" };
  }
  protected body(text: string, language: string): string {
    return JSON.stringify({
      input: { text },
      // Google requires a languageCode even when a voice name is given, and
      // rejects the request without one - so it is defaulted rather than left
      // out.
      voice: { languageCode: language || "en-US", name: this.google.voice || undefined },
      audioConfig: { audioEncoding: "MP3" },
    });
  }
}

/** Azure. */
export class AzureSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly azure: AzureTtsOptions = azureTtsOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("azure", azure, post);
  }
  get isAvailable(): boolean {
    return super.isAvailable && this.azure.region.length > 0;
  }
  protected url(): string {
    return `${this.options.baseUrl.replace("REGION", this.azure.region)}/cognitiveservices/v1`;
  }
  protected headers(): Record<string, string> {
    return {
      "Ocp-Apim-Subscription-Key": this.options.apiKey.reveal(),
      "Content-Type": "application/ssml+xml",
      // Azure needs the output format as a HEADER and returns a 400 without it.
      "X-Microsoft-OutputFormat": "audio-24khz-48kbitrate-mono-mp3",
    };
  }
  /**
   * SSML, and the text is ESCAPED before it goes in.
   *
   * This text came from a model, which got it from a person, so an unescaped
   * ampersand breaks the document and an unescaped angle bracket lets somebody
   * change the voice by typing a tag.
   */
  protected body(text: string, language: string): string {
    const escaped = text
      .replace(/&/g, "&amp;")
      .replace(/</g, "&lt;")
      .replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;");
    return `<speak version='1.0' xml:lang='${language || "en-US"}'><voice name='${this.azure.voice}'>${escaped}</voice></speak>`;
  }
}

/** Cartesia. */
export class CartesiaSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly cartesia: CartesiaTtsOptions = cartesiaTtsOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("cartesia", cartesia, post);
  }
  get isAvailable(): boolean {
    return super.isAvailable && this.cartesia.voiceId.length > 0;
  }
  protected url(): string {
    return `${this.options.baseUrl}/tts/bytes`;
  }
  protected headers(): Record<string, string> {
    return {
      "X-API-Key": this.options.apiKey.reveal(),
      "Cartesia-Version": "2024-06-10",
      "Content-Type": "application/json",
    };
  }
  protected body(text: string, language: string): string {
    return JSON.stringify({
      model_id: this.options.model,
      transcript: text,
      voice: { mode: "id", id: this.cartesia.voiceId },
      language: language || undefined,
      output_format: { container: "mp3", sample_rate: 44100, bit_rate: 128000 },
    });
  }
}

/** PlayHT. */
export class PlayHtSpeechSynthesizer extends CloudSpeechSynthesizerBase {
  constructor(private readonly play: PlayHtOptions = playHtOptions(), post?: CloudSpeechSynthesizerBase["post"]) {
    super("playht", play, post);
  }
  /** Needs a USER ID alongside the key. A request with only the key gets a 403
   * that says nothing about which of the two is missing. */
  get isAvailable(): boolean {
    return super.isAvailable && this.play.userId.length > 0;
  }
  protected url(): string {
    return `${this.options.baseUrl}/tts/stream`;
  }
  protected headers(): Record<string, string> {
    return {
      Authorization: `Bearer ${this.options.apiKey.reveal()}`,
      "X-USER-ID": this.play.userId,
      "Content-Type": "application/json",
      Accept: "audio/mpeg",
    };
  }
  protected body(text: string): string {
    return JSON.stringify({ text, voice: this.play.voice, voice_engine: this.options.model });
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Intent

/** Something a person can ask for by name. */
export interface VoiceIntent {
  readonly name: string;
  /** Phrases that mean it, in whatever languages the device covers. Several per
   * intent, because people do not say the same thing twice. */
  readonly phrases: readonly string[];
  readonly requiresConfirmation: boolean;
}

/** What was matched, and how well. */
export interface VoiceIntentMatch {
  readonly intent: VoiceIntent;
  readonly matchedPhrase: string;
  /** 0..1. Below the router's floor nothing is returned at all. */
  readonly score: number;
  /** What was left over after the phrase - usually the actual subject. "Call
   * Thabo" matches "call" and leaves "Thabo", and dropping the remainder is how
   * an intent router becomes a keyword detector. */
  readonly remainder: string;
}

/** Routes what was said to what was meant. */
export interface VoiceIntentRouter {
  route(text: string): VoiceIntentMatch | undefined;
}

/** Routes nothing. The default: a device without configured intents falls
 * through to the model rather than guessing. */
export class NullVoiceIntentRouter implements VoiceIntentRouter {
  route(): VoiceIntentMatch | undefined {
    return undefined;
  }
}

/**
 * Matches on phrases, on the device.
 *
 * ON THE DEVICE because the alternative is sending every utterance to a
 * classifier, and the things people say to an assistant most often - call
 * somebody, set a timer, what is the time - are exactly the things that should
 * never leave.
 *
 * A LONGER PHRASE WINS. "Call" and "call an ambulance" are both matches for the
 * second, and returning the shorter one dials somebody named "an ambulance".
 */
export class KeywordVoiceIntentRouter implements VoiceIntentRouter {
  /** Below this nothing is returned, so a near-miss falls through to the model
   * rather than doing the wrong thing confidently. */
  static readonly SCORE_FLOOR = 0.6;

  constructor(private readonly intents: readonly VoiceIntent[] = []) {}

  /** Normalised: case folded, punctuation dropped, spaces collapsed. */
  static normalise(text: string): string {
    return [...text.toLowerCase()]
      .map((c) => (/[\p{L}\p{N}\s]/u.test(c) ? c : " "))
      .join("")
      .split(/\s+/)
      .filter(Boolean)
      .join(" ");
  }

  route(text: string): VoiceIntentMatch | undefined {
    const said = KeywordVoiceIntentRouter.normalise(text);
    if (!said) return undefined;

    let best: VoiceIntentMatch | undefined;
    for (const intent of this.intents) {
      for (const phrase of intent.phrases) {
        const wanted = KeywordVoiceIntentRouter.normalise(phrase);
        if (!wanted || !said.startsWith(wanted)) continue;
        // Scored by how much of what was said the phrase accounts for, so a
        // long phrase matched in full beats a short one matched in full.
        const score = wanted.length / said.length;
        const candidate: VoiceIntentMatch = Object.freeze({
          intent,
          matchedPhrase: phrase,
          score: Math.max(KeywordVoiceIntentRouter.SCORE_FLOOR, score),
          remainder: said.slice(wanted.length).trim(),
        });
        if (!best || wanted.length > KeywordVoiceIntentRouter.normalise(best.matchedPhrase).length) {
          best = candidate;
        }
      }
    }
    return best;
  }
}

// ─────────────────────────────────────────────────────────────────────────────
// Images

/** The image generators a host may consent to. */
export class GeneratorIds {
  static readonly LOCAL = "local";
  static readonly OPENAI_IMAGE = "openai-image";
  static readonly STABILITY = "stability";
  static readonly REPLICATE = "replicate";

  static readonly ALL = Object.freeze([
    GeneratorIds.LOCAL, GeneratorIds.OPENAI_IMAGE, GeneratorIds.STABILITY, GeneratorIds.REPLICATE,
  ]);

  /**
   * Which ones keep the prompt on the device.
   *
   * Worth its own function because it is the question that decides whether a
   * person needs to be asked - every other generator in the list sends the
   * prompt, and often a reference image, to somebody else.
   */
  static isLocal(generatorId: string): boolean {
    return generatorId.trim().toLowerCase() === GeneratorIds.LOCAL;
  }
}

/** What to draw. */
export interface ImageGenerationRequest {
  readonly prompt: string;
  readonly width: number;
  readonly height: number;
  /** A reference image, when the request is an edit. Sending one sends a
   * picture - often of a person - so it is carried explicitly rather than
   * hidden in the prompt. */
  readonly reference?: Uint8Array;
  readonly seed?: number;
}

/** A generated image. */
export interface ImageArtifact {
  readonly bytes: Uint8Array;
  readonly mimeType: string;
  readonly width: number;
  readonly height: number;
  /** Which generator made it. Carried so a person can be told, and so a picture
   * made in the cloud can be labelled as such. */
  readonly generatorId: string;
  readonly error: string;
}

const imageArtifact = (partial: Partial<ImageArtifact> = {}): ImageArtifact =>
  Object.freeze({
    bytes: partial.bytes ?? new Uint8Array(0),
    mimeType: partial.mimeType ?? "image/png",
    width: partial.width ?? 0,
    height: partial.height ?? 0,
    generatorId: partial.generatorId ?? "",
    error: partial.error ?? "",
  });

/** Draws a picture. */
export interface ImageGenerator {
  readonly generatorId: string;
  readonly isAvailable: boolean;
  generate(request: ImageGenerationRequest): Promise<ImageArtifact>;
}

/** Draws nothing. */
export class NullImageGenerator implements ImageGenerator {
  readonly generatorId = "none";
  readonly isAvailable = false;
  async generate(): Promise<ImageArtifact> {
    return imageArtifact({ error: "no image generator is available on this device" });
  }
}

export interface OpenAiImageOptions extends CloudSpeechOptionsBase {
  readonly size: string;
}
export const openAiImageOptions = (p: Partial<OpenAiImageOptions> = {}): OpenAiImageOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.openai.com/v1", model: "gpt-image-1" }, p),
    size: p.size ?? "1024x1024",
  });

export interface StabilityImageOptions extends CloudSpeechOptionsBase {
  readonly outputFormat: string;
}
export const stabilityImageOptions = (p: Partial<StabilityImageOptions> = {}): StabilityImageOptions =>
  Object.freeze({
    ...speechOptions({ baseUrl: "https://api.stability.ai/v2beta", model: "core" }, p),
    outputFormat: p.outputFormat ?? "png",
  });

/** OpenAI's image endpoint. */
export class OpenAiImageGenerator implements ImageGenerator {
  readonly generatorId = GeneratorIds.OPENAI_IMAGE;

  constructor(
    private readonly options: OpenAiImageOptions = openAiImageOptions(),
    private readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: string,
    ) => Promise<Record<string, unknown>>,
  ) {}

  get isAvailable(): boolean {
    return isSpeechConfigured(this.options) && this.post !== undefined;
  }

  async generate(request: ImageGenerationRequest): Promise<ImageArtifact> {
    if (!this.isAvailable) {
      return imageArtifact({
        generatorId: this.generatorId,
        error: `${this.generatorId} is not configured on this device`,
      });
    }
    try {
      const raw = await this.post!(
        `${this.options.baseUrl}/images/generations`,
        {
          Authorization: `Bearer ${this.options.apiKey.reveal()}`,
          "Content-Type": "application/json",
        },
        JSON.stringify({
          model: this.options.model,
          prompt: request.prompt,
          size: `${request.width}x${request.height}` || this.options.size,
          n: 1,
        }),
      );
      const data = raw.data as { b64_json?: string }[] | undefined;
      const b64 = data?.[0]?.b64_json ?? "";
      return imageArtifact({
        bytes: decodeBase64(b64),
        generatorId: this.generatorId,
        width: request.width,
        height: request.height,
      });
    } catch (error) {
      return imageArtifact({
        generatorId: this.generatorId,
        error: `${this.generatorId} did not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/** Stability. */
export class StabilityImageGenerator implements ImageGenerator {
  readonly generatorId = GeneratorIds.STABILITY;

  constructor(
    private readonly options: StabilityImageOptions = stabilityImageOptions(),
    private readonly post?: (
      url: string,
      headers: Record<string, string>,
      body: string,
    ) => Promise<Uint8Array>,
  ) {}

  get isAvailable(): boolean {
    return isSpeechConfigured(this.options) && this.post !== undefined;
  }

  async generate(request: ImageGenerationRequest): Promise<ImageArtifact> {
    if (!this.isAvailable) {
      return imageArtifact({
        generatorId: this.generatorId,
        error: `${this.generatorId} is not configured on this device`,
      });
    }
    try {
      return imageArtifact({
        bytes: await this.post!(
          `${this.options.baseUrl}/stable-image/generate/${this.options.model}`,
          {
            Authorization: `Bearer ${this.options.apiKey.reveal()}`,
            // Stability returns JSON unless told otherwise, so this asks for
            // the bytes directly rather than a base64 round trip.
            Accept: "image/*",
          },
          JSON.stringify({ prompt: request.prompt, output_format: this.options.outputFormat }),
        ),
        mimeType: `image/${this.options.outputFormat}`,
        generatorId: this.generatorId,
        width: request.width,
        height: request.height,
      });
    } catch (error) {
      return imageArtifact({
        generatorId: this.generatorId,
        error: `${this.generatorId} did not answer: ${error instanceof Error ? error.message : String(error)}`,
      });
    }
  }
}

/**
 * Tries generators in order until one produces a picture.
 *
 * ORDERED WITH LOCAL FIRST, and the order is not by quality. A local generator
 * that produces a worse picture and keeps the prompt on the device is the right
 * first choice, and a chain sorted by quality quietly makes the cloud the
 * default.
 */
export class ImageGeneratorFallbackChain implements ImageGenerator {
  readonly generatorId = "chain";

  constructor(private readonly generators: readonly ImageGenerator[] = []) {}

  get isAvailable(): boolean {
    return this.generators.some((g) => g.isAvailable);
  }

  async generate(request: ImageGenerationRequest): Promise<ImageArtifact> {
    const reasons: string[] = [];
    for (const generator of this.generators) {
      if (!generator.isAvailable) {
        reasons.push(`${generator.generatorId}: not configured`);
        continue;
      }
      const result = await generator.generate(request);
      if (!result.error && result.bytes.length > 0) return result;
      reasons.push(`${generator.generatorId}: ${result.error || "produced nothing"}`);
    }
    // EVERY reason is reported, not just the last. A chain that says only "no
    // generator worked" leaves somebody guessing which of four to configure.
    return imageArtifact({
      generatorId: this.generatorId,
      error: reasons.length ? reasons.join("; ") : "no image generators are configured",
    });
  }
}

/** Decodes base64 without assuming a runtime helper exists. */
function decodeBase64(text: string): Uint8Array {
  if (!text) return new Uint8Array(0);
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
  const clean = text.replace(/[^A-Za-z0-9+/]/g, "");
  const out = new Uint8Array(Math.floor((clean.length * 3) / 4));
  let bits = 0;
  let value = 0;
  let at = 0;
  for (const ch of clean) {
    const index = alphabet.indexOf(ch);
    if (index < 0) continue;
    value = (value << 6) | index;
    bits += 6;
    if (bits >= 8) {
      bits -= 8;
      out[at++] = (value >> bits) & 0xff;
    }
  }
  return out.subarray(0, at);
}

// ─────────────────────────────────────────────────────────────────────────────
// Wiring

/**
 * Wires the speech providers a host has consented to.
 *
 * BOTH configured AND consented, not either. A configured provider nobody
 * agreed to is the failure this whole file exists to prevent - and for audio it
 * is a person's voice rather than their words.
 */
export class SpeechCloudServiceCollectionExtensions {
  static addRecognizers(
    candidates: readonly CloudSpeechRecognizer[],
    consented: readonly string[],
  ): readonly CloudSpeechRecognizer[] {
    const allowed = new Set(consented.map((c) => c.trim().toLowerCase()).filter(Boolean));
    return Object.freeze(
      candidates.filter((c) => allowed.has(c.providerId.toLowerCase()) && c.isAvailable),
    );
  }

  static addSynthesizers(
    candidates: readonly CloudSpeechSynthesizer[],
    consented: readonly string[],
  ): readonly CloudSpeechSynthesizer[] {
    const allowed = new Set(consented.map((c) => c.trim().toLowerCase()).filter(Boolean));
    return Object.freeze(
      candidates.filter((c) => allowed.has(c.providerId.toLowerCase()) && c.isAvailable),
    );
  }

  /** What a person is shown before any audio leaves the device. */
  static describe(providers: readonly { providerId: string }[]): string {
    if (providers.length === 0) return "no audio would leave this device";
    return `if this device cannot hear or speak, it would ask: ${providers.map((p) => p.providerId).join(", ")}`;
  }
}

/** Wires the image generators a host has consented to. */
export class VisionCloudServiceCollectionExtensions {
  static addGenerators(
    candidates: readonly ImageGenerator[],
    consented: readonly string[],
  ): ImageGenerator {
    const allowed = new Set(consented.map((c) => c.trim().toLowerCase()).filter(Boolean));
    const wired = candidates.filter(
      // A LOCAL generator does not need consent, because nothing leaves. Every
      // other one does.
      (g) => GeneratorIds.isLocal(g.generatorId) || allowed.has(g.generatorId.toLowerCase()),
    );
    return wired.length ? new ImageGeneratorFallbackChain(wired) : new NullImageGenerator();
  }
}

// The C# spellings, kept so the two trees line up.
export type IVoiceIntentRouter = VoiceIntentRouter;
export type IImageGenerator = ImageGenerator;
export type ISpeechRecognizer = CloudSpeechRecognizer;
export type ISpeechSynthesizer = CloudSpeechSynthesizer;
