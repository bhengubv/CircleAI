// inference/index.ets — Contract for an on-device chat-style text generator
// ArkTS port: no `any`, explicit types throughout.

import { ChatMessage } from '../models/index';

/**
 * Knobs for a single generation call.
 * All fields have sensible defaults via defaultGenerationOptions().
 */
export interface GenerationOptions {
  maxTokens: number;
  temperature: number;
  topP: number;
  stopSequences: string[];
  systemPrompt: string;
}

/**
 * Returns a GenerationOptions object with sensible defaults.
 */
export function defaultGenerationOptions(): GenerationOptions {
  return {
    maxTokens:     2048,
    temperature:   0.7,
    topP:          0.9,
    stopSequences: [],
    systemPrompt:  '',
  };
}

/**
 * Contract for an on-device chat-style text generator.
 * Implementations own native model state.
 */
export interface IChatGenerator {
  generate(messages: ChatMessage[], options: GenerationOptions): Promise<string>;
  stream(messages: ChatMessage[], options: GenerationOptions): AsyncGenerator<string>;
}
